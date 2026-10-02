"""当日の固定登録と、映像まで確かめる診断。ネットワーク I/O はこのモジュールに閉じる。"""

import concurrent.futures
import json
import os
import re
import subprocess
import sys
import threading
import time
import urllib.request

sys.path.insert(0, os.path.dirname(os.path.dirname(__file__)))
from android_devices import resolve_adb

FLEET_FILE = os.path.join(os.path.dirname(__file__), 'operations-fleet.json')
with open(FLEET_FILE, encoding='utf-8') as _file:
    FLEET = json.load(_file)
if FLEET.get('schema') != 1:
    raise ValueError('unsupported operations fleet schema')

_cache_cond = threading.Condition()
_cached = None
_cached_until = 0.0
_cached_beacons = None
_checking = False
_restart_lock = threading.Lock()
_last_restart = {}
_pool = concurrent.futures.ThreadPoolExecutor(max_workers=5, thread_name_prefix='ops-probe')


def normalize_show(show):
    """固定の接続値だけを投影し、カメラの著作値や D は残す。"""
    changed = False
    cams = show.setdefault('cameras', [])
    fixed_ids = [fixed['id'] for fixed in FLEET['cameras']]
    for index, cam in enumerate(cams):
        cid = cam.get('id')
        if index < len(fixed_ids) and cid not in fixed_ids[:index + 1]:
            raise ValueError('fixed camera order must be A/B/C')
        if cid in fixed_ids and fixed_ids.index(cid) != index:
            raise ValueError('fixed camera order must be A/B/C')
    for index, fixed in enumerate(FLEET['cameras']):
        if index >= len(cams):
            cam = {'id': fixed['id']}
            cams.append(cam)
            changed = True
        else:
            cam = cams[index]
            if cam.get('id') != fixed['id']:
                raise ValueError('fixed camera order must be A/B/C')
        for key, value in (('host', fixed['host']), ('port', fixed['port']), ('pinned', True)):
            if cam.get(key) != value:
                cam[key] = value
                changed = True
    ctrl = show.setdefault('control', {})
    for key in ('autoFollow', 'discoveryEnabled'):
        if ctrl.get(key) is not False:
            ctrl[key] = False
            changed = True
    return changed


def conflicts_fixed_patch(patch, current):
    """POST /state の固定欄への変更要求だけを弾く。"""
    if 'cameras' in patch:
        proposed = patch['cameras']
        if not isinstance(proposed, list) or len(proposed) < len(FLEET['cameras']):
            return True
        for index, fixed in enumerate(FLEET['cameras']):
            cam = proposed[index]
            if not isinstance(cam, dict) or cam.get('id') != fixed['id'] or any(
                    cam.get(k) != v for k, v in
                    (('host', fixed['host']), ('port', fixed['port']), ('pinned', True))):
                return True
        if any(isinstance(c, dict) and c.get('id') in {f['id'] for f in FLEET['cameras']}
               for c in proposed[len(FLEET['cameras']):]):
            return True
    if 'control' in patch:
        ctrl = patch['control']
        if not isinstance(ctrl, dict) or ctrl.get('autoFollow') is not False or ctrl.get('discoveryEnabled') is not False:
            return True
    return False


def _json_get(host, port, path, timeout=1.2):
    with urllib.request.urlopen(f'http://{host}:{port}{path}', timeout=timeout) as response:
        return json.loads(response.read(65536))


def probe_stream(host, port):
    """最大 3 秒 / 4 MiB。Content-Length が成立した JPEG と連番の進行を要求。"""
    started = time.monotonic()
    result = {'ok': False, 'frames': 0, 'firstSeq': None, 'lastSeq': None,
              'elapsedSec': None, 'bytes': 0}
    deadline = started + 3.0
    data = bytearray()
    seqs = []
    try:
        with urllib.request.urlopen(f'http://{host}:{port}/video', timeout=0.6) as response:
            if 'multipart/' not in response.headers.get('Content-Type', '').lower():
                return result
            while time.monotonic() < deadline and result['bytes'] < 4 * 1024 * 1024:
                try:
                    chunk = response.read1(16384)
                except TimeoutError:
                    continue
                if not chunk:
                    break
                result['bytes'] += len(chunk)
                data.extend(chunk)
                while True:
                    start = data.find(b'\r\n\r\n')
                    if start < 0:
                        if len(data) > 65536 and b'Content-Length:' not in data[-65536:]:
                            del data[:-65536]
                        break
                    header = bytes(data[:start]).split(b'\r\n')
                    length = next((int(m.group(1)) for line in header
                                   if (m := re.fullmatch(rb'Content-Length:\s*(\d+)', line, re.I))), None)
                    if length is None or length < 4 or length > 2 * 1024 * 1024:
                        del data[:start + 4]
                        continue
                    end = start + 4 + length
                    if len(data) < end:
                        break
                    frame = bytes(data[start + 4:end])
                    seq = next((int(m.group(1)) for line in header
                                if (m := re.fullmatch(rb'X-Frame-Seq:\s*(\d+)', line, re.I))), None)
                    del data[:end]
                    if frame.startswith(b'\xff\xd8') and frame.endswith(b'\xff\xd9'):
                        result['frames'] += 1
                        if seq is not None:
                            seqs.append(seq)
                        if len(seqs) >= 2 and seqs[-1] > seqs[0]:
                            result.update(ok=True, firstSeq=seqs[0], lastSeq=seqs[-1])
                            return result
    except (OSError, ValueError):
        pass
    finally:
        result['elapsedSec'] = round(time.monotonic() - started, 2)
    if seqs:
        result.update(firstSeq=seqs[0], lastSeq=seqs[-1])
    return result


def _issue(status, code, title, action, detail=''):
    return {'status': status, 'code': code, 'title': title, 'detail': detail, 'action': action}


def _heartbeat_value(heartbeat, key):
    """現行の root 値を優先し、旧 APK の nested status も読む。"""
    if key in heartbeat:
        return heartbeat[key]
    nested = heartbeat.get('status')
    return nested.get(key) if isinstance(nested, dict) else None


def _wireless_adb(fixed, runner=subprocess.run):
    """登録済みの物理 serial と無線 ADB の接続先が同じ端末かを確認する。"""
    expected = (fixed.get('serial') or '').strip()
    result = {'state': 'serial_unregistered', 'reachable': False, 'identityOk': False,
              'expectedSerial': expected, 'observedSerial': ''}
    if not expected:
        return result
    transport = fixed['host'] + ':5555'
    try:
        adb_path = resolve_adb()
        connected = runner([adb_path, 'connect', transport], capture_output=True, text=True,
                           timeout=20, check=False)
        if connected.returncode != 0 or 'connected' not in connected.stdout.lower():
            result['state'] = 'unreachable'
            return result
        result['reachable'] = True
        observed = runner([adb_path, '-s', transport, 'shell', 'getprop', 'ro.serialno'],
                          capture_output=True, text=True, timeout=20, check=False)
    except (OSError, RuntimeError, subprocess.TimeoutExpired):
        result['state'] = 'unreachable'
        return result
    if observed.returncode != 0:
        result['state'] = 'identity_unavailable'
        return result
    result['observedSerial'] = observed.stdout.strip().splitlines()[0] if observed.stdout.strip() else ''
    result['identityOk'] = result['observedSerial'] == expected
    result['state'] = 'verified' if result['identityOk'] else 'identity_mismatch'
    return result


def _registration_issues(info, host):
    fields = ('identityState', 'identityLocked', 'expectedIp', 'localIp')
    present = [key for key in fields if key in info]
    if not present:
        return [_issue('warning', 'identity_unsupported', '端末登録を確認できません',
                       '登録情報を表示できる配信アプリへ更新してください')]
    issues = []
    if 'identityState' in info and info['identityState'] != 'registered':
        issues.append(_issue('error', 'registration_required', '端末の登録が必要です',
                             '保守担当に端末登録を依頼してください'))
    if 'identityLocked' in info and info['identityLocked'] is not True:
        issues.append(_issue('error', 'identity_unlocked', '端末の登録が固定されていません',
                             '保守担当に端末登録を依頼してください'))
    if 'expectedIp' in info and info['expectedIp'] != host:
        issues.append(_issue('error', 'registered_ip_mismatch', '登録 IP が違います',
                             '端末の登録内容を確認してください'))
    if 'localIp' in info and info['localIp'] != host:
        issues.append(_issue('error', 'local_ip_mismatch', '端末の実 IP が違います',
                             'Wi-Fi の固定 IP を確認してください'))
    if 'identityState' in info and 'localIp' not in info:
        issues.append(_issue('error', 'wifi_disconnected', '端末の Wi-Fi が未接続です',
                             '現場の Wi-Fi に接続してください'))
    if len(present) < len(fields) and not issues:
        issues.append(_issue('warning', 'identity_unsupported', '端末登録を十分に確認できません',
                             '登録情報を表示できる配信アプリへ更新してください'))
    return issues


def _camera(fixed, beacons, show_camera=None):
    cid, host, port = fixed['id'], fixed['host'], fixed['port']
    row = {'id': cid, 'label': 'カメラ ' + cid, 'host': host, 'port': port,
           'expectedUuid': fixed['uuid'], 'observedUuid': None, 'observedId': None,
           'status': 'unknown', 'code': 'unconfirmed', 'title': '未確認',
           'action': '配信端末を確認してください', 'httpOk': False, 'identityOk': False,
           'stream': {'ok': False, 'frames': 0, 'firstSeq': None, 'lastSeq': None,
                      'elapsedSec': None, 'bytes': 0},
           'metrics': {'fps': None, 'batteryPct': None, 'batteryTempC': None,
                       'clientCount': None, 'aeLock': None, 'awbLock': None,
                       'lensFovDeg': None}, 'issues': [], 'canRestart': False,
           'wirelessAdb': {'state': 'serial_unregistered', 'reachable': False,
                           'identityOk': False, 'expectedSerial': fixed.get('serial') or '',
                           'observedSerial': ''}}
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as probe_pool:
        info_future = probe_pool.submit(_json_get, host, port, '/info')
        health_future = probe_pool.submit(_json_get, host, port, '/health')
        try:
            info = info_future.result()
            if not isinstance(info, dict):
                raise ValueError('invalid info')
        except (OSError, ValueError) as exc:
            info = None
            info_error = str(exc)
        try:
            health = health_future.result()
            if not isinstance(health, dict):
                health = {}
        except (OSError, ValueError):
            health = {}
    if info is None:
        row['wirelessAdb'] = _wireless_adb(fixed)
        row['canRestart'] = row['wirelessAdb']['identityOk']
        row['issues'].append(_issue('error', 'disconnected', '通信できません',
                                    '端末で配信アプリを開いてください', info_error))
        return _finish(row)
    row['httpOk'] = True
    row['observedId'], row['observedUuid'] = info.get('cameraId'), info.get('uuid')
    row['metrics']['lensFovDeg'] = info.get('lensFovDeg')
    if (info.get('cameraId'), info.get('uuid'), info.get('show')) != (cid, fixed['uuid'], FLEET['show']):
        row['issues'].append(_issue('error', 'identity_mismatch', '登録と違う端末です',
                                    'スマホの担当表示と設置場所を確認してください。端末交換後なら保守用の登録を更新します'))
        return _finish(row)
    row['identityOk'] = True
    registration_issues = _registration_issues(info, host)
    row['issues'].extend(registration_issues)
    if any(issue['status'] == 'error' for issue in registration_issues):
        row['identityOk'] = False
    same_id = [b for b in beacons if b.get('id') == cid]
    if any(b.get('uuid') != fixed['uuid'] for b in same_id):
        row['issues'].append(_issue('error', 'duplicate_id', '同じ担当の端末が複数あります',
                                    '登録した 1 台だけを使ってください。担当は入れ替えません'))
    if not health:
        row['issues'].append(_issue('warning', 'health_unavailable', '健康情報を取得できません',
                                    '配信端末を確認してください'))
    for source, target in (('fps', 'fps'), ('batteryPct', 'batteryPct'),
                           ('batteryTempC', 'batteryTempC'), ('clientCount', 'clientCount'),
                           ('aeLock', 'aeLock'), ('awbLock', 'awbLock')):
        row['metrics'][target] = health.get(source)
    row['stream'] = probe_stream(host, port)
    if not row['stream']['ok']:
        row['issues'].append(_issue('error', 'frame_stalled', '映像が進んでいません',
                                    '配信アプリを画面に出して再確認してください'))
    total_frames = health.get('totalFrames')
    frame_age = health.get('latestFrameAgeMs')
    if not row['stream']['ok'] and (health.get('encodeIdle') is True or
                                  (isinstance(total_frames, (int, float)) and total_frames > 0 and
                                   isinstance(frame_age, (int, float)) and frame_age > 3000)):
        row['issues'].append(_issue('error', 'encode_idle', '撮影が止まっています',
                                    '端末の画面ロックを解除してください'))
    if isinstance(health.get('throttleStage'), (int, float)) and health['throttleStage'] > 0:
        row['issues'].append(_issue('warning', 'thermal', '熱で画質が下がっています',
                                    '端末を冷ましてください'))
    if isinstance(health.get('batteryPct'), (int, float)) and health['batteryPct'] < 20:
        row['issues'].append(_issue('warning', 'battery_low', '電池残量が少なくなっています',
                                    '電源を確認してください'))
    if isinstance(health.get('batteryTempC'), (int, float)) and health['batteryTempC'] >= 43:
        row['issues'].append(_issue('warning', 'battery_hot', '端末が熱くなっています',
                                    '端末を冷ましてください'))
    if health and (health.get('aeLock') is False or health.get('awbLock') is False):
        row['issues'].append(_issue('warning', 'exposure_unlocked', '露出と色が自動調整中です',
                                    '設営後に端末の露出と色を固定してください'))
    fov = info.get('lensFovDeg')
    if isinstance(fov, (int, float)) and abs(fov - 104.3) > 2.0:
        row['issues'].append(_issue('warning', 'lens_fov', 'レンズの画角が違います',
                                    '端末のレンズを超広角に戻してください'))
    calib = (show_camera or {}).get('calib') or {}
    if info.get('tiltState') == 'ok' and all(isinstance(info.get(k), (int, float)) for k in
                                             ('tiltPitchDeg', 'tiltRollDeg')):
        if isinstance(calib.get('pitchDeg'), (int, float)) and isinstance(calib.get('rollDeg'), (int, float)):
            difference = max(abs(info['tiltPitchDeg'] - calib['pitchDeg']),
                             abs(info['tiltRollDeg'] - calib['rollDeg']))
            if difference > 15:
                row['issues'].append(_issue('error', 'calibration_mismatch', '較正時から端末が傾いています',
                                            'カメラの向きを戻すか較正を取り直してください'))
            elif difference > 5:
                row['issues'].append(_issue('warning', 'calibration_shift', '較正時から傾きが変わりました',
                                            'カメラの向きと較正を確認してください'))
    if info.get('tiltState') not in (None, '', 'ok', 'stable'):
        row['issues'].append(_issue('warning', 'tilt', '端末の傾きを確認してください',
                                    '設置位置と水平を確認してください'))
    row['wirelessAdb'] = _wireless_adb(fixed)
    row['canRestart'] = (row['identityOk'] and row['wirelessAdb']['identityOk'] and
                         not any(i['code'] == 'duplicate_id' for i in row['issues']))
    return _finish(row)


def _finish(row):
    issue = next((i for i in row['issues'] if i['status'] == 'error'), None)
    issue = issue or next(iter(row['issues']), None)
    if issue:
        row.update({k: issue[k] for k in ('status', 'code', 'title', 'action')})
    else:
        row.update(status='ok', code='ok', title='映像を確認しました', action='')
    return row


def _quest(fixed, devices, now):
    matches = [(did, hb) for did, hb in devices.items()
               if hb.get('localIp') == fixed['host'] and
               0 <= now - float(hb.get('at') or 0) < 6]
    row = {'id': fixed['id'], 'label': fixed['label'], 'host': fixed['host'],
           'status': 'unknown', 'code': 'unconfirmed', 'title': '未確認',
           'action': 'Quest の起動を確認してください', 'ageSec': None, 'deviceId': None,
           'expectedDeviceId': fixed.get('deviceId') or '',
           'shortId': fixed.get('shortId') or '', 'serial': fixed.get('serial') or '',
           'phase': None, 'visitorPort': None, 'issues': []}
    if len(matches) > 1:
        row['issues'].append(_issue('error', 'ip_collision', '同じ IP の Quest が複数あります',
                                    'Quest の IP 設定を確認してください'))
    elif matches:
        did, hb = matches[0]
        row.update(ageSec=round(now - float(hb['at']), 1), deviceId=did,
                   phase=hb.get('phase'), visitorPort=hb.get('visitorPort'))
        if not fixed.get('deviceId'):
            row['issues'].append(_issue('unknown', 'identity_unregistered', '端末 ID が未登録です',
                                        'Quest の端末 ID を実測して固定台帳へ登録してください'))
        elif did != fixed['deviceId']:
            row['issues'].append(_issue('error', 'identity_mismatch', '登録と違う Quest です',
                                        'Quest の端末 ID を確認してください'))
        if not _heartbeat_value(hb, 'visitorPort'):
            row['issues'].append(_issue('warning', 'visitor_closed', '受付口が閉じています',
                                        'Quest の受付画面を確認してください'))
        required = ('cameraCount', 'activeCamera', 'recvFps', 'sourceAgeMs')
        missing = [key for key in required if _heartbeat_value(hb, key) is None]
        if missing:
            row['issues'].append(_issue('unknown', 'diagnostics_missing', '映像の診断情報がありません',
                                        '診断情報に対応した Quest アプリを起動してください',
                                        ', '.join(missing)))
        count = _heartbeat_value(hb, 'cameraCount')
        if count is not None and (not isinstance(count, int) or isinstance(count, bool) or count < 3):
            row['issues'].append(_issue('error', 'camera_count', '受信カメラが足りません',
                                        'Quest のカメラ設定を確認してください'))
        active = _heartbeat_value(hb, 'activeCamera')
        active_index = _heartbeat_value(hb, 'activeIndex')
        fps = _heartbeat_value(hb, 'recvFps')
        fixed_active = (active in ('A', 'B', 'C') or
                        (isinstance(active_index, int) and 0 <= active_index <= 2))
        if fixed_active and isinstance(fps, (int, float)) and fps <= 0:
            row['issues'].append(_issue('error', 'active_stream_stalled', '表示中の映像が止まっています',
                                        'Quest の映像接続を確認してください'))
        age = _heartbeat_value(hb, 'sourceAgeMs')
        if isinstance(age, (int, float)) and age > 3000:
            row['issues'].append(_issue('error', 'source_stale', '表示中の映像が古くなっています',
                                        'Quest の映像接続を確認してください'))
    else:
        future = any(hb.get('localIp') == fixed['host'] and float(hb.get('at') or 0) > now
                     for hb in devices.values())
        if future:
            row['issues'].append(_issue('unknown', 'heartbeat_future', 'Quest の時刻が未来です',
                                        'Quest の時刻設定を確認してください'))
        else:
            row['issues'].append(_issue('error', 'heartbeat_missing', 'Quest から連絡がありません',
                                        'Quest の起動と Wi-Fi を確認してください'))
    _finish(row)
    if matches and not row['issues']:
        row['title'] = 'Quest から応答があります'
        row['action'] = '装着して A/B/C の映像と音を確認してください'
    return row


def _tablet(fixed, devices, now):
    tablet = next((item for item in FLEET.get('tablets', [])
                   if (item.get('questId') or item.get('target')) == fixed['id']), None)
    row = {'id': fixed['id'], 'host': fixed['host'], 'status': 'unknown',
           'title': 'タブレットを確認できません', 'action': 'Quest の起動と Wi-Fi を確認してください',
           'portalStatus': 'unknown', 'connectionStatus': 'unknown',
           'reflectionStatus': 'unknown', 'portalSessionId': None,
           'tabletSessionId': None, 'tabletIp': None, 'ageSec': None,
           'sentSeq': None, 'appliedSeq': None, 'received': None,
           'applyCount': None, 'requestedLang': None, 'requestedRelief': None,
           'lang': None, 'relief': None, 'activePages': 0,
           'tabletName': tablet.get('label') if tablet else None,
           'tabletModel': tablet.get('model') if tablet else None,
           'tabletSerial': tablet.get('serial') if tablet else None,
           'tabletHost': tablet.get('host') if tablet else None,
           'visitorUrl': (tablet.get('visitorUrl') if tablet else
                          f"http://{fixed['host']}:{fixed.get('port', 8090)}/")}
    matches = []
    for did, hb in devices.items():
        if not isinstance(hb, dict) or hb.get('localIp') != fixed['host']:
            continue
        try:
            age = now - float(hb.get('at') or 0)
        except (TypeError, ValueError):
            continue
        if 0 <= age < 6:
            matches.append((did, hb, age))
    if len(matches) > 1:
        row.update(status='error', title='同じ IP の Quest が複数あります',
                   action='Quest の IP 設定を確認してください')
        return row
    if not matches:
        if any(isinstance(hb, dict) and hb.get('localIp') == fixed['host']
               for hb in devices.values()):
            row.update(title='Quest からの応答が古くなっています',
                       action='Quest の起動と Wi-Fi を確認してください')
        return row
    did, hb, hb_age = matches[0]
    if not fixed.get('deviceId'):
        row.update(title='対応する Quest の端末 ID が未登録です',
                   action='Quest の端末 ID を実測して固定台帳へ登録してください')
        return row
    if did != fixed['deviceId']:
        row.update(status='error', title='登録と違う Quest です',
                   action='Quest の端末 ID を確認してください')
        return row
    portal = _heartbeat_value(hb, 'visitorPortal')
    required = ('listening', 'portalSessionId', 'tablets', 'lastRequest', 'appliedSeq',
                'applyCount', 'received', 'lang', 'relief', 'pendingSeq', 'consumedSeq')
    if (not isinstance(portal, dict) or portal.get('schema') != 1 or
            any(key not in portal for key in required) or
            not isinstance(portal['listening'], bool) or
            not isinstance(portal['portalSessionId'], str) or not portal['portalSessionId'] or
            not isinstance(portal['tablets'], list) or
            any(not isinstance(portal[key], int) or isinstance(portal[key], bool)
                for key in ('appliedSeq', 'applyCount', 'received', 'pendingSeq', 'consumedSeq')) or
            not isinstance(portal['lang'], str) or not isinstance(portal['relief'], bool)):
        row.update(title='タブレットの診断情報がありません',
                   action='診断情報に対応した Quest アプリを確認してください')
        return row
    row.update(portalSessionId=portal['portalSessionId'], appliedSeq=portal['appliedSeq'],
               applyCount=portal['applyCount'], received=portal['received'],
               lang=portal['lang'], relief=portal['relief'])
    if not portal['listening']:
        row.update(status='error', portalStatus='error', title='タブレットの受付が停止しています',
                   action='Quest の受付画面を確認してください')
        return row
    row['portalStatus'] = 'ok'
    pages = []
    for page in portal['tablets']:
        if not isinstance(page, dict) or not isinstance(page.get('tabletSessionId'), str) or not page['tabletSessionId']:
            continue
        age = page.get('ageSec')
        if isinstance(age, bool) or not isinstance(age, (int, float)) or not 0 <= age + hb_age <= 30:
            continue
        pages.append((page, age + hb_age))
    row['activePages'] = len(pages)
    if len(pages) == 1:
        page, age = pages[0]
        row.update(connectionStatus='ok', tabletSessionId=page['tabletSessionId'],
                   tabletIp=page.get('ip'), ageSec=round(age, 1))
    elif not pages:
        row.update(title='タブレットの応答がありません',
                   action='画面を手前に開いてください')
        return row
    else:
        row.update(status='warning', connectionStatus='warning',
                   title='複数のページが開いています', action='使用するページを一つだけ開いてください')
        return row
    request = portal['lastRequest']
    if (request is None or not isinstance(request, dict) or
            not isinstance(request.get('seq'), int) or isinstance(request.get('seq'), bool) or
            request['seq'] <= 0):
        row.update(title='設定の送信はまだ確認できません',
                   action='タブレットから設定を送ってください')
        return row
    if (not isinstance(request.get('tabletSessionId'), str) or
            not isinstance(request.get('lang'), str) or
            not isinstance(request.get('relief'), bool)):
        row.update(title='タブレットの診断情報がありません',
                   action='診断情報に対応した Quest アプリを確認してください')
        return row
    row.update(sentSeq=request['seq'], requestedLang=request.get('lang'),
               requestedRelief=request.get('relief'))
    if request.get('tabletSessionId') != row['tabletSessionId']:
        row.update(status='warning', reflectionStatus='warning',
                   title='別のタブレットからの設定です',
                   action='現在開いているページから設定を送り直してください')
    elif portal['appliedSeq'] != request['seq']:
        row.update(status='warning', reflectionStatus='warning',
                   title='設定の反映を待っています',
                   action='少し待ってから反映を確認してください')
    elif (portal['lang'], portal['relief']) != (request['lang'], request['relief']):
        row.update(status='warning', reflectionStatus='warning',
                   title='設定の反映値が違います',
                   action='タブレットから設定を送り直してください')
    else:
        row.update(status='ok', reflectionStatus='ok', title='タブレットの設定が反映されています',
                   action='')
    return row


def status(devices, beacons, revision, show=None):
    global _cached, _cached_until, _cached_beacons, _checking
    now = time.time()
    beacon_key = tuple(sorted((b.get('id'), b.get('uuid'), b.get('ip')) for b in beacons))
    with _cache_cond:
        while _checking:
            _cache_cond.wait()
        if (_cached is not None and now < _cached_until and
                _cached['config']['revision'] == revision and _cached_beacons == beacon_key):
            cameras = _cached['cameras']
            quests = [_quest(q, devices, now) for q in FLEET['quests']]
            tablets = [_tablet(q, devices, now) for q in FLEET['quests']]
            return {'ok': True, 'observedAt': _cached['observedAt'],
                    'cameras': cameras, 'quests': quests, 'tablets': tablets,
                    'issues': [dict(i, device=row['id']) for row in cameras + quests
                               for i in row['issues']], 'config': _cached['config']}
        _checking = True
    try:
        show_cameras = {c.get('id'): c for c in (show or {}).get('cameras', [])}
        futures = [_pool.submit(_camera, c, beacons, show_cameras.get(c['id']))
                   for c in FLEET['cameras']]
        cameras = [f.result() for f in futures]
        now = time.time()
        quests = [_quest(q, devices, now) for q in FLEET['quests']]
        tablets = [_tablet(q, devices, now) for q in FLEET['quests']]
        result = {'ok': True, 'observedAt': now, 'cameras': cameras, 'quests': quests,
                  'tablets': tablets,
                  'issues': [dict(i, device=row['id']) for row in cameras + quests
                             for i in row['issues']],
                  'config': {'fixed': True, 'revision': revision}}
        with _cache_cond:
            _cached, _cached_until, _cached_beacons = result, time.time() + 5.0, beacon_key
        return result
    finally:
        with _cache_cond:
            _checking = False
            _cache_cond.notify_all()


def invalidate():
    global _cached_until
    with _cache_cond:
        _cached_until = 0


def restart(camera_id, devices, beacons, revision, runner=subprocess.run):
    fixed = next((c for c in FLEET['cameras'] if c['id'] == camera_id), None)
    if fixed is None:
        return 400, {'ok': False, 'cameraId': camera_id, 'message': '登録されていないカメラです'}
    if not _restart_lock.acquire(blocking=False):
        return 409, {'ok': False, 'cameraId': camera_id, 'message': '別の起こし直しが進行中です'}
    try:
        if time.monotonic() - _last_restart.get(camera_id, float('-inf')) < 90:
            return 409, {'ok': False, 'cameraId': camera_id, 'message': '90 秒経ってから再試行してください'}
        adb_identity = None
        try:
            info = _json_get(fixed['host'], fixed['port'], '/info')
        except (OSError, ValueError):
            info = None
            adb_identity = _wireless_adb(fixed, runner)
            if not adb_identity['identityOk']:
                return 409, {'ok': False, 'cameraId': camera_id,
                             'message': '端末で配信アプリを開いてください。無線 ADB から端末を確認できません'}
        if info is not None:
            if (info.get('cameraId'), info.get('uuid'), info.get('show')) != (
                    camera_id, fixed['uuid'], FLEET['show']):
                return 409, {'ok': False, 'cameraId': camera_id, 'message': '端末の ID が登録と一致しません'}
            if any(i['status'] == 'error' for i in _registration_issues(info, fixed['host'])):
                return 409, {'ok': False, 'cameraId': camera_id, 'message': '端末の登録状態か IP が一致しません'}
            adb_identity = _wireless_adb(fixed, runner)
            if not adb_identity['identityOk']:
                return 409, {'ok': False, 'cameraId': camera_id,
                             'message': '無線 ADB の端末 ID を確認できません。端末で配信アプリを開いてください'}
        if any(b.get('uuid') != fixed['uuid'] for b in beacons if b.get('id') == camera_id):
            return 409, {'ok': False, 'cameraId': camera_id, 'message': '同じ ID の端末が複数あります'}
        _last_restart[camera_id] = time.monotonic()
        serial = fixed['host'] + ':5555'
        try:
            adb_path = resolve_adb()
        except RuntimeError as error:
            return 502, {'ok': False, 'cameraId': camera_id, 'message': str(error)}
        commands = [[adb_path, 'connect', serial],
                    [adb_path, '-s', serial, 'shell', 'input', 'keyevent', 'KEYCODE_WAKEUP'],
                    [adb_path, '-s', serial, 'shell', 'wm', 'dismiss-keyguard'],
                    [adb_path, '-s', serial, 'shell', 'am', 'force-stop', 'com.fixedcamvr.streamer'],
                    [adb_path, '-s', serial, 'shell', 'am', 'start', '-n',
                     'com.fixedcamvr.streamer/.MainActivity']]
        for args in commands:
            try:
                done = runner(args, capture_output=True, text=True, timeout=20, check=False)
            except (OSError, subprocess.TimeoutExpired) as exc:
                return 502, {'ok': False, 'cameraId': camera_id, 'message': 'ADB に失敗しました: ' + str(exc)}
            if (done.returncode != 0 or
                    (args[1] == 'connect' and 'connected' not in done.stdout.lower()) or
                    ('am' in args and 'start' in args and 'Error' in done.stdout)):
                return 502, {'ok': False, 'cameraId': camera_id,
                             'message': 'ADB に失敗しました: ' + (done.stderr or done.stdout).strip()[:120]}
        recovered = False
        for attempt in range(3):
            if attempt:
                time.sleep(2)
            invalidate()
            check = status(devices, beacons, revision)
            observed = next(c for c in check['cameras'] if c['id'] == camera_id)
            recovered = (observed['identityOk'] and observed['stream']['ok'] and
                         not any(i['code'] == 'duplicate_id' for i in observed['issues']))
            if recovered:
                break
        return 200, {'ok': recovered, 'cameraId': camera_id,
                     'message': '映像が復旧しました' if recovered else '映像の復旧を確認できません。端末を確認してください'}
    finally:
        _restart_lock.release()
