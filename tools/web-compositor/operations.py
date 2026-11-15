"""当日の固定登録と、映像まで確かめる診断。ネットワーク I/O はこのモジュールに閉じる。"""

import concurrent.futures
import json
import os
import re
import socket
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

# 配信スマホを最後に起こし直した時刻。`onsite.py watch` と卓の `/ops/restart` が同じ 1 つを読み書きし、
# 同じ端末を数秒差で 2 回起こさない（host がキー。壁時計の秒）。
RESTART_LEDGER = os.path.join(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))),
                              'Logs', 'onsite', 'restart-ledger.json')
RESTART_COOLDOWN_SEC = 90.0
_ledger_lock = threading.RLock()
_RECENT_ELSEWHERE = '自動監視か別の画面が直前に起こし直しました。90 秒経ってから再試行してください'


def _read_restart_ledger():
    """読めない・壊れているときは記録なし（空）として返す。"""
    try:
        with open(RESTART_LEDGER, encoding='utf-8') as source:
            ledger = json.load(source)
    except (OSError, ValueError):
        return {}
    hosts = ledger.get('hosts') if isinstance(ledger, dict) else None
    return hosts if isinstance(hosts, dict) else {}


def last_restart_at(host):
    entry = _read_restart_ledger().get(host)
    at = entry.get('at') if isinstance(entry, dict) else None
    if isinstance(at, bool) or not isinstance(at, (int, float)):
        return None
    return float(at)


def recently_restarted(host, now=None):
    """`RESTART_COOLDOWN_SEC` 以内に誰かがこの端末を起こし直したか。

    時計が戻って記録が未来になったときも、ずれが待ちの幅を超えれば撃てる（永久に止めない）。
    """
    at = last_restart_at(host)
    if at is None:
        return False
    return abs((time.time() if now is None else now) - at) < RESTART_COOLDOWN_SEC


def record_restart(host, by, now=None):
    """起こし直した時刻を書く。一時ファイル → `os.replace` で置き換える。書けたら True。"""
    path = RESTART_LEDGER
    with _ledger_lock:
        hosts = dict(_read_restart_ledger())
        hosts[host] = {'at': time.time() if now is None else now, 'by': by}
        tmp = f'{path}.{os.getpid()}.{threading.get_ident()}.tmp'
        try:
            os.makedirs(os.path.dirname(path), exist_ok=True)
            with open(tmp, 'w', encoding='utf-8', newline='\n') as sink:
                json.dump({'schema': 1, 'hosts': hosts}, sink, ensure_ascii=False)
            # Windows では相手が読んでいる瞬間の置き換えが拒まれることがあるので、数回だけ待つ。
            for attempt in range(5):
                try:
                    os.replace(tmp, path)
                    return True
                except PermissionError:
                    if attempt == 4:
                        raise
                    time.sleep(0.05)
        except OSError:
            try:
                os.remove(tmp)
            except OSError:
                pass
            return False


def claim_restart(host, by, now=None):
    """直近に誰も起こし直していなければ記録して True。待つべきなら False。"""
    with _ledger_lock:
        if recently_restarted(host, now):
            return False
        record_restart(host, by, now)
        return True


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


class _ChunkDecoder:
    """Transfer-Encoding: chunked を少しずつ解く。配信アプリ（NanoHTTPD）の /video はこの形。"""

    def __init__(self):
        self.raw = bytearray()
        self.left = 0
        self.crlf = False
        self.done = False

    def feed(self, chunk):
        self.raw.extend(chunk)
        out = bytearray()
        while not self.done:
            if self.left:
                take = min(self.left, len(self.raw))
                if not take:
                    break
                out += self.raw[:take]
                del self.raw[:take]
                self.left -= take
                self.crlf = not self.left
                continue
            if self.crlf:
                if len(self.raw) < 2:
                    break
                if self.raw[:2] != b'\r\n':
                    raise ValueError('broken chunk terminator')
                del self.raw[:2]
                self.crlf = False
                continue
            end = self.raw.find(b'\r\n')
            if end < 0:
                if len(self.raw) > 1024:
                    raise ValueError('broken chunk size line')
                break
            size = int(bytes(self.raw[:end]).split(b';')[0].strip(), 16)
            del self.raw[:end + 2]
            if size == 0:
                self.done = True
            else:
                self.left = size
        return bytes(out)


def probe_stream(host, port):
    """最大 3 秒 / 4 MiB。Content-Length が成立した JPEG と連番の進行を要求。

    接続は 0.6 秒で諦める（居ない機に長く待たない）。接続後は読むたびにソケットの待ちを
    3 秒の締切までの残りに設定し直し、待ちが切れたら締切に達したとして抜ける。
    ⚠ urllib の応答は 1 度タイムアウトすると以後の読み取りが
    `OSError("cannot read from timed out object")` になる。フレームの間が 0.6 秒空いただけで
    止まったと判定していたので、ソケットを自分で持つ。
    """
    started = time.monotonic()
    result = {'ok': False, 'frames': 0, 'firstSeq': None, 'lastSeq': None,
              'elapsedSec': None, 'bytes': 0}
    deadline = started + 3.0
    data = bytearray()
    seqs = []

    def receive(sock):
        """締切までの残りだけ待って読む。締切に達したら None。"""
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            return None
        sock.settimeout(max(0.05, remaining))
        try:
            return sock.recv(16384)
        except socket.timeout:
            return None

    try:
        with socket.create_connection((host, port), timeout=0.6) as sock:
            sock.sendall(f'GET /video HTTP/1.1\r\nHost: {host}:{port}\r\n'
                         'Accept: multipart/x-mixed-replace\r\nConnection: close\r\n\r\n'.encode('ascii'))
            head = bytearray()
            while b'\r\n\r\n' not in head:
                if len(head) > 65536:
                    return result
                piece = receive(sock)
                if not piece:
                    return result
                head.extend(piece)
            split = head.index(b'\r\n\r\n')
            lines = bytes(head[:split]).decode('iso-8859-1').split('\r\n')
            status = re.fullmatch(r'HTTP/1\.[01] (\d{3})(?: .*)?', lines[0])
            if status is None or not 200 <= int(status.group(1)) < 300:
                return result
            headers = {}
            for line in lines[1:]:
                name, sep, value = line.partition(':')
                if sep:
                    headers[name.strip().lower()] = value.strip()
            if 'multipart/' not in headers.get('content-type', '').lower():
                return result
            decoder = _ChunkDecoder() if 'chunked' in headers.get('transfer-encoding', '').lower() else None
            pending = bytes(head[split + 4:])
            while result['bytes'] < 4 * 1024 * 1024:
                if pending:
                    raw, pending = pending, b''
                else:
                    raw = receive(sock)
                    if not raw:
                        break
                chunk = decoder.feed(raw) if decoder else raw
                if decoder and decoder.done and not chunk:
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


# adb の出力は UTF-8 で読む。届かない端末への `adb connect` は Windows の日本語の失敗文を返し、
# 既定（cp932）で読むと読み取りスレッドが落ちて stdout が None になる。そのまま `.lower()` を呼んで
# `/ops/status` が丸ごと落ちていた（カメラが 1 台でも届かないと、画面が「卓に届かない」と出る形）。
_ADB_TEXT = {'capture_output': True, 'text': True, 'encoding': 'utf-8', 'errors': 'replace', 'check': False}


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
        connected = runner([adb_path, 'connect', transport], timeout=20, **_ADB_TEXT)
        if connected.returncode != 0 or 'connected' not in (connected.stdout or '').lower():
            result['state'] = 'unreachable'
            return result
        result['reachable'] = True
        observed = runner([adb_path, '-s', transport, 'shell', 'getprop', 'ro.serialno'],
                          timeout=20, **_ADB_TEXT)
    except (OSError, RuntimeError, subprocess.TimeoutExpired):
        result['state'] = 'unreachable'
        return result
    if observed.returncode != 0:
        result['state'] = 'identity_unavailable'
        return result
    serial_text = (observed.stdout or '').strip()
    result['observedSerial'] = serial_text.splitlines()[0] if serial_text else ''
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


def _number(value):
    return value if isinstance(value, (int, float)) and not isinstance(value, bool) else None


def _flag(value):
    return value if isinstance(value, bool) else None


def _quest_run(hb):
    """体験の進み具合。heartbeat の値をそのまま写す（判定はしない）。"""
    lap = _heartbeat_value(hb, 'lap')
    total = _heartbeat_value(hb, 'totalLaps')
    return {'phase': hb.get('phase') if hb.get('phase') in ('INTRO', 'RUN', 'END') else None,
            'titleStage': hb.get('titleStage') if isinstance(hb.get('titleStage'), str) else '',
            'lap': lap if isinstance(lap, int) and not isinstance(lap, bool) and lap > 0 else None,
            'totalLaps': total if isinstance(total, int) and not isinstance(total, bool) and total > 0 else None,
            'runSec': _number(_heartbeat_value(hb, 'runSec')),
            'introSec': _number(_heartbeat_value(hb, 'introSec')),
            'targetSec': _number(_heartbeat_value(hb, 'targetSec')),
            'endHolding': _flag(_heartbeat_value(hb, 'endHolding'))}


def _quest_facts(hb):
    """被らないと分からないことのうち、Quest 自身が申告している分。

    ⚠ ここは行の status に混ぜない。右コントローラが机で寝ているだけで行が要確認になると、
    本当に直すべき行が埋もれる。画面は詳細としてだけ出す。
    """
    def controller(side):
        connected = _flag(_heartbeat_value(hb, f'ctrl{side}Connected'))
        tracked = _flag(_heartbeat_value(hb, f'ctrl{side}Tracked'))
        return None if connected is None else {'connected': connected, 'tracked': tracked is True}

    resolved = _heartbeat_value(hb, 'sndResolved')
    missing = _heartbeat_value(hb, 'sndMissing')
    sound = None
    if all(isinstance(v, int) and not isinstance(v, bool) and v >= 0 for v in (resolved, missing)):
        sound = {'resolved': resolved, 'missing': missing,
                 'audible': _number(_heartbeat_value(hb, 'sndAudible'))}
    registered = _flag(_heartbeat_value(hb, 'registered'))
    return {'registered': registered,
            'needsReRegistration': _flag(_heartbeat_value(hb, 'needsReReg')) if registered is not None else None,
            'controllerLeft': controller('L'), 'controllerRight': controller('R'), 'sound': sound,
            'cameraCount': _number(_heartbeat_value(hb, 'cameraCount')),
            'activeCamera': _heartbeat_value(hb, 'activeCamera') or None,
            'recvFps': _number(_heartbeat_value(hb, 'recvFps')),
            'contentVerified': _flag(_heartbeat_value(hb, 'contentVerified')),
            'lang': hb.get('lang') if isinstance(hb.get('lang'), str) else None,
            'relief': _flag(hb.get('relief'))}


def desk_status(addresses=None):
    """卓の PC が展示の網の固定 IP を持っているか。

    全台が同時に NG になったとき、まず疑うのはここ（有線の IP を失って 169.254 へ落ちた実績）。
    点検は「卓から見えるか」しか測れないので、卓が網から外れた姿と現場が全滅した姿は同じ顔で出る。
    """
    expected = (FLEET.get('desk') or {}).get('host') or '192.168.10.10'
    if addresses is None:
        try:
            infos = socket.getaddrinfo(socket.gethostname(), None, socket.AF_INET)
            addresses = sorted({info[4][0] for info in infos})
        except OSError:
            addresses = None
    row = {'expectedHost': expected, 'addresses': addresses or [], 'status': 'unknown',
           'code': 'unconfirmed', 'title': 'この PC の IP を調べられません',
           'action': 'PC のネットワーク設定を確認してください'}
    if addresses is None:
        return row
    if expected in addresses:
        row.update(status='ok', code='ok', title=f'この PC は {expected} です', action='')
    elif any(a.startswith('169.254.') for a in addresses):
        row.update(status='error', code='link_local', title='この PC が展示の網から外れています',
                   action='LAN ケーブルとルーターの電源を確認し、PowerShell で ipconfig /renew "イーサネット" を実行してください')
    else:
        row.update(status='error', code='wrong_address', title=f'この PC が {expected} を持っていません',
                   action='PC を展示ルーターへ有線でつなぎ、固定 IP を確認してください')
    return row


WATCH_LATEST = os.path.join(os.path.dirname(RESTART_LEDGER), 'watch-latest.json')
WATCH_MAX_AGE_SEC = 90.0


def _watch_events(folder, limit=40):
    """いちばん新しい `watch-*.log` の末尾。行は `MM-DD HH:MM:SS  本文`。"""
    try:
        names = sorted(name for name in os.listdir(folder)
                       if re.fullmatch(r'watch-\d{8}_\d{6}\.log', name))
        if not names:
            return []
        with open(os.path.join(folder, names[-1]), encoding='utf-8', errors='replace') as source:
            lines = source.read().splitlines()[-limit:]
    except OSError:
        return []
    events = []
    for line in lines:
        match = re.fullmatch(r'(\d{2}-\d{2} \d{2}:\d{2}:\d{2})\s+(.*\S)\s*', line)
        if match:
            events.append({'at': match.group(1), 'text': match.group(2)})
    return events


def watch_status(now=None, path=None):
    """`onsite.py watch` が回っているか。毎巡書く `watch-latest.json` の時刻で見る。

    `events` は見張りが書いた変わり目の記録。画面を開いていない間のことも、どの端末から見ても同じものが出る。
    """
    row = {'status': 'unknown', 'running': False, 'ageSec': None,
           'title': '自動の見張りは動いていません',
           'action': '会期中は py -3.11 tools/onsite.py watch を動かしてください',
           'events': _watch_events(os.path.dirname(path or WATCH_LATEST))}
    try:
        with open(path or WATCH_LATEST, encoding='utf-8') as source:
            at = time.mktime(time.strptime(json.load(source)['at'], '%Y-%m-%dT%H:%M:%S'))
    except (OSError, ValueError, KeyError, TypeError, OverflowError):
        return row
    age = (time.time() if now is None else now) - at
    row['ageSec'] = round(age, 1)
    if 0 <= age <= WATCH_MAX_AGE_SEC:
        row.update(status='ok', running=True, title='自動の見張りが動いています', action='')
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
           'phase': None, 'visitorPort': None, 'run': None, 'facts': None,
           'standby': fixed.get('standby') is True, 'issues': []}
    if len(matches) > 1:
        row['issues'].append(_issue('error', 'ip_collision', '同じ IP の Quest が複数あります',
                                    'Quest の IP 設定を確認してください'))
    elif matches:
        did, hb = matches[0]
        row.update(ageSec=round(now - float(hb['at']), 1), deviceId=did,
                   phase=hb.get('phase'), visitorPort=hb.get('visitorPort'),
                   run=_quest_run(hb), facts=_quest_facts(hb))
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
    quest_label = {'alpha': 'α', 'beta': 'β'}.get(fixed['id'], fixed['id'])
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
           'tabletPackage': tablet.get('package') if tablet else None,
           'registered': tablet is not None, 'standby': fixed.get('standby') is True}
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
                   action=f'タブレットの「廻リ視 博士」を起動し、スタッフ設定でクエスト{quest_label}を選んでください')
        return row
    else:
        row.update(status='warning', connectionStatus='warning',
                   title='複数のタブレットが応答しています', action='使用する博士アプリを一台だけ起動してください')
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
                   action='現在起動している博士アプリから設定を送り直してください')
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
                   action='「廻リ視 博士」を起動したままにしてください')
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
        if time.monotonic() - _last_restart.get(camera_id, float('-inf')) < RESTART_COOLDOWN_SEC:
            return 409, {'ok': False, 'cameraId': camera_id, 'message': '90 秒経ってから再試行してください'}
        if recently_restarted(fixed['host']):
            return 409, {'ok': False, 'cameraId': camera_id, 'message': _RECENT_ELSEWHERE}
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
        # 端末の確認に数秒かかるあいだに watch が起こし直していることがあるので、撃つ直前に取り直す。
        if not claim_restart(fixed['host'], 'desk'):
            return 409, {'ok': False, 'cameraId': camera_id, 'message': _RECENT_ELSEWHERE}
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
                done = runner(args, timeout=20, **_ADB_TEXT)
            except (OSError, subprocess.TimeoutExpired) as exc:
                return 502, {'ok': False, 'cameraId': camera_id, 'message': 'ADB に失敗しました: ' + str(exc)}
            out = done.stdout or ''
            if (done.returncode != 0 or
                    (args[1] == 'connect' and 'connected' not in out.lower()) or
                    ('am' in args and 'start' in args and 'Error' in out)):
                return 502, {'ok': False, 'cameraId': camera_id,
                             'message': 'ADB に失敗しました: ' + (done.stderr or out).strip()[:120]}
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
