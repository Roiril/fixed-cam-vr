"""登録済み Quest と博士用タブレットを起動する。接続設定は消さない。"""
from __future__ import annotations

import json
import re
import subprocess
import time
import urllib.request
import uuid
from datetime import datetime
from pathlib import Path

from android_devices import adb_command, list_android_devices, resolve_adb

ROOT = Path(__file__).resolve().parents[1]
FLEET = ROOT / 'tools/web-compositor/operations-fleet.json'
SETTINGS = {'stay_on_while_plugged_in': ('global', '3'),
            'screen_off_timeout': ('system', '1800000'),
            'accelerometer_rotation': ('system', '0'),
            'user_rotation': ('system', '1')}
DESK_DEVICES = 'http://127.0.0.1:8099/unity/devices'
TABLET_PACKAGE = 'com.roiril.mawarimi.tablet'
TABLET_COMPONENT = TABLET_PACKAGE + '/.MainActivity'
TABLET_APK = ROOT / 'Builds/doctor-tablet.apk'
TABLET_BUILD_COMMAND = 'py -3.11 tablet/build.py'
TABLET_LOG_TAG = 'DoctorTablet'


def display_state(power, policy):
    awake = bool(re.search(r'mWakefulness=Awake\b', power))
    unlocked = bool(re.search(r'mIsShowing=false\b', policy))
    return {'awake': awake, 'unlocked': unlocked}


def run(cmd, timeout=30):
    result = subprocess.run(cmd, capture_output=True, text=True, encoding='utf-8',
                            errors='replace', timeout=timeout)
    return result.returncode, result.stdout, result.stderr


def get_status(url):
    try:
        with urllib.request.urlopen(url, timeout=3) as response:
            return json.load(response)
    except (OSError, ValueError):
        return None


def validate_portal(quest, portal_status, unity_devices):
    """Quest の口と卓 heartbeat が、同じ起動中の登録機を示すか検査する。"""
    expected_id = quest.get('deviceId')
    session = portal_status.get('portalSessionId') if isinstance(portal_status, dict) else None
    if not expected_id:
        return False, '固定登録の deviceId が空です'
    if not session:
        return False, 'Quest /status に portalSessionId がありません'
    devices = unity_devices.get('devices') if isinstance(unity_devices, dict) else None
    if not isinstance(devices, list):
        return False, '卓の heartbeat を取得できません'
    matched = [d for d in devices if isinstance(d, dict) and d.get('deviceId') == expected_id]
    if len(matched) != 1:
        return False, '登録した deviceId の heartbeat が 1 台に定まりません'
    device = matched[0]
    if device.get('localIp') != quest.get('host'):
        return False, 'heartbeat の localIp が固定登録と違います'
    try:
        age = float(device.get('ageSec'))
    except (TypeError, ValueError):
        return False, 'heartbeat の ageSec がありません'
    if device.get('alive') is not True or not 0 <= age < 6:
        return False, 'heartbeat が新しくありません'
    status = device.get('status') if isinstance(device.get('status'), dict) else {}
    visitor = status.get('visitorPortal') if isinstance(status.get('visitorPortal'), dict) else {}
    if visitor.get('portalSessionId') != session:
        return False, 'Quest /status と heartbeat の portalSessionId が違います'
    return True, device


def wait_portal(url, quest, timeout=25):
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        status = get_status(url.rstrip('/') + '/status')
        valid, _ = validate_portal(quest, status, get_status(DESK_DEVICES))
        if valid:
            return status
        time.sleep(.5)
    return None


def choose_devices(fleet, devices, selection):
    """IP や機種名だけで起動対象を増やさない。"""
    quests = [q for q in fleet.get('quests', [])
              if selection == 'all' or q.get('id') == selection]
    selected = {q.get('id') for q in quests}
    tablets = [t for t in fleet.get('tablets', []) if t.get('questId', t.get('target')) in selected]
    available = {d['physicalSerial']: d for d in devices if d['state'] == 'device'}
    return quests, tablets, available


def required_setup_serials(fleet, selection):
    quests = [q for q in fleet.get('quests', [])
              if selection == 'all' or q.get('id') == selection]
    selected = {q.get('id') for q in quests}
    tablets = [t for t in fleet.get('tablets', [])
               if t.get('questId', t.get('target')) in selected]
    return {item.get('serial') for item in quests + tablets if item.get('serial')}


def scan_setup_devices(adb, fleet, selection):
    """登録機が一時的に消えた場合だけ、0.5 秒後に一度再列挙する。"""
    known_quests = [q.get('serial') for q in fleet.get('quests', [])]
    devices = list_android_devices(run, adb, known_quests)
    required = required_setup_serials(fleet, selection)
    present = {d.get('physicalSerial') for d in devices if d.get('state') == 'device'}
    if not required.issubset(present):
        time.sleep(.5)
        devices = list_android_devices(run, adb, known_quests)
    return devices


def tablet_artifact(evidence, physical_serial, suffix):
    """登録物理 serial だけを証拠名に使い、evidence 外への逸脱を拒否する。"""
    evidence = Path(evidence).resolve()
    if not re.fullmatch(r'[A-Za-z0-9._-]+', physical_serial or ''):
        raise ValueError('登録物理 serial を証拠ファイル名に使えません')
    target = (evidence / ('tablet-' + physical_serial + suffix)).resolve()
    if target.parent != evidence:
        raise ValueError('証拠ファイルの移動先が evidence 外です')
    return target


def tablet_install_instruction(adb, transport):
    return (TABLET_BUILD_COMMAND + '\n' + str(adb) + ' -s ' + transport
            + ' install -r ' + str(TABLET_APK))


def parse_tablet_render(log_text):
    events = []
    for line in log_text.splitlines():
        if 'onPageFinished' not in line:
            continue
        sequence = re.search(r'\bseq(?:uence)?\s*[=:]\s*(\d+)', line)
        elapsed = re.search(r'\belapsed(?:Ms)?\s*[=:]\s*(\d+)', line)
        if sequence and elapsed:
            events.append({'sequence': int(sequence.group(1)),
                           'elapsedMs': int(elapsed.group(1)), 'log': line.strip()})
    if not events:
        return None
    if any(current['sequence'] <= previous['sequence']
           or current['elapsedMs'] < previous['elapsedMs']
           for previous, current in zip(events, events[1:])):
        return None
    return events[-1]


def log_suffix(before, after):
    before_lines = before.splitlines()
    after_lines = after.splitlines()
    if before_lines and after_lines[:len(before_lines)] == before_lines:
        after_lines = after_lines[len(before_lines):]
    return '\n'.join(after_lines)


def prepare_tablet(adb, transport, physical_serial, quest_id, evidence):
    evidence = Path(evidence).resolve()
    evidence.mkdir(parents=True, exist_ok=True)
    code, out, err = run(adb_command(adb, transport, 'shell', 'pm', 'path', TABLET_PACKAGE))
    if code or not any(line.startswith('package:') for line in out.splitlines()):
        return {'verified': False, 'installed': False,
                'instruction': tablet_install_instruction(adb, transport)}
    backup_path = tablet_artifact(evidence, physical_serial, '-settings.json')
    if not backup_path.exists():
        previous = {}
        for key, (namespace, value) in SETTINGS.items():
            code, out, err = run(adb_command(adb, transport, 'shell', 'settings', 'get', namespace, key))
            if code:
                raise RuntimeError(err or out)
            previous[key] = {'namespace': namespace, 'value': out.strip()}
        backup_path.write_text(json.dumps(previous, ensure_ascii=False, indent=2), encoding='utf-8')
    for key, (namespace, value) in SETTINGS.items():
        code, out, err = run(adb_command(adb, transport, 'shell', 'settings', 'put', namespace, key, value))
        if code:
            raise RuntimeError(err or out)
    _, before_log, _ = run(adb_command(adb, transport, 'logcat', '-d', '-v', 'monotonic',
                                        '-s', TABLET_LOG_TAG + ':I', '*:S'))
    for args in [('input', 'keyevent', 'KEYCODE_WAKEUP'), ('wm', 'dismiss-keyguard'),
                 ('am', 'force-stop', TABLET_PACKAGE),
                 ('am', 'start', '-n', TABLET_COMPONENT, '--es', 'quest', quest_id)]:
        code, out, err = run(adb_command(adb, transport, 'shell', *args))
        if code:
            raise RuntimeError(err or out)
    _, power, _ = run(adb_command(adb, transport, 'shell', 'dumpsys', 'power'))
    _, policy, _ = run(adb_command(adb, transport, 'shell', 'dumpsys', 'window', 'policy'))
    display = display_state(power, policy)
    app = {'verified': False, 'installed': True, 'package': TABLET_PACKAGE,
           'activityComponent': None, 'quest': quest_id, **display}
    if not display['unlocked']:
        app['instruction'] = '本体でロックを解除してから setup を再実行する。PIN をこのツールへ渡さない'
    activity = ''
    render = None
    if display['awake'] and display['unlocked']:
        for _ in range(10):
            _, activity, _ = run(adb_command(adb, transport, 'shell', 'dumpsys', 'activity', 'activities'))
            _, after_log, _ = run(adb_command(adb, transport, 'logcat', '-d', '-v', 'monotonic',
                                               '-s', TABLET_LOG_TAG + ':I', '*:S'))
            render = parse_tablet_render(log_suffix(before_log, after_log))
            if TABLET_COMPONENT in activity and render:
                break
            time.sleep(.5)
    if TABLET_COMPONENT in activity:
        app['activityComponent'] = TABLET_COMPONENT
    if render:
        app['onPageFinished'] = render
    remote = '/sdcard/fixedcam-setup.png'
    code, out, err = run(adb_command(adb, transport, 'shell', 'screencap', '-p', remote))
    if code == 0:
        screenshot = tablet_artifact(evidence, physical_serial, '.png')
        code, out, err = run(adb_command(adb, transport, 'pull', remote, str(screenshot)))
        if code == 0 and screenshot.is_file():
            app['screenshot'] = str(screenshot)
    app['verified'] = bool(app.get('activityComponent') == TABLET_COMPONENT
                           and app.get('onPageFinished') and app.get('screenshot')
                           and display['awake'] and display['unlocked'])
    if not app['verified'] and 'instruction' not in app:
        app['instruction'] = '博士タブレットの画面と DoctorTablet logcat を確認する'
    return app


def restore_tablet(adb, transport, physical_serial, evidence):
    evidence = Path(evidence).resolve()
    source = tablet_artifact(evidence, physical_serial, '-settings.json')
    previous = json.loads(source.read_text(encoding='utf-8'))
    if set(previous) != set(SETTINGS):
        raise ValueError('保存された設定の項目が揃っていません')
    for key, saved in previous.items():
        if key not in SETTINGS or saved.get('namespace') != SETTINGS[key][0]:
            raise ValueError('保存された設定の項目が一致しません')
    for key, saved in previous.items():
        args = ('delete', saved['namespace'], key) if saved['value'] == 'null' else (
            'put', saved['namespace'], key, saved['value'])
        code, out, err = run(adb_command(adb, transport, 'shell', 'settings', *args))
        if code:
            raise RuntimeError(err or out)
    stamp = datetime.now().strftime('%Y%m%d_%H%M%S')
    archive = tablet_artifact(
        evidence, physical_serial,
        '-settings.restored-' + stamp + '-' + uuid.uuid4().hex + '.json')
    source.replace(archive)
    return archive


def cmd_setup(args):
    evidence = Path(args.evidence).resolve()
    evidence.mkdir(parents=True, exist_ok=True)
    try:
        adb = resolve_adb()
        fleet = json.loads(FLEET.read_text(encoding='utf-8'))
        devices = scan_setup_devices(adb, fleet, args.quests)
        quests, tablets, available = choose_devices(fleet, devices, args.quests)
        if args.restore_tablet:
            tablet = next((t for t in tablets if t.get('serial') == args.restore_tablet), None)
            if not tablet:
                raise ValueError('この展示に登録されたタブレットの serial を指定してください')
            connected = available.get(args.restore_tablet)
            if not connected:
                raise ValueError('登録したタブレットを ADB 接続してから復元してください')
            archive = restore_tablet(adb, connected['serial'], args.restore_tablet, evidence)
            print('タブレットの画面設定を保存値へ戻しました: ' + str(archive))
            return 0
        import onsite
        if onsite.cmd_serve(args):
            return 1
        results = []
        from operations import probe_stream
        for camera in fleet.get('cameras', []):
            transport = available.get(camera.get('serial'))
            if transport:
                for command in [('input', 'keyevent', 'KEYCODE_WAKEUP'),
                                ('am', 'start', '-n', 'com.fixedcamvr.streamer/.MainActivity')]:
                    code, out, err = run(adb_command(adb, transport['serial'], 'shell', *command))
                    if code:
                        raise RuntimeError(err or out)
            url = 'http://' + camera['host'] + ':' + str(camera.get('port', 8080))
            info = None
            for attempt in range(12):
                info = get_status(url + '/info')
                if info:
                    break
                time.sleep(.5)
            identity = bool(info and info.get('uuid') == camera.get('uuid')
                            and info.get('cameraId') == camera.get('id'))
            stream = probe_stream(camera['host'], camera.get('port', 8080)) if identity else {'ok': False}
            results.append({'device': 'カメラ ' + camera['id'], 'started': identity and stream['ok'],
                            'detail': stream if stream['ok'] else '登録した配信端末を USB 接続するか端末で配信アプリを開く'})
        for quest in quests:
            physical_serial = quest.get('serial')
            transport = available.get(physical_serial)
            url = 'http://' + quest['host'] + ':' + str(quest.get('port', 8090)) + '/'
            status = get_status(url + 'status')
            valid, invalid_detail = validate_portal(quest, status, get_status(DESK_DEVICES))
            if not valid:
                status = None
            if transport:
                for command in [('input', 'keyevent', 'KEYCODE_WAKEUP'),
                                ('am', 'start', '-n', 'com.roiril.mawarimi/com.unity3d.player.UnityPlayerActivity')]:
                    code, out, err = run(adb_command(adb, transport['serial'], 'shell', *command))
                    if code:
                        raise RuntimeError(err or out)
                status = wait_portal(url, quest)
                if not status:
                    invalid_detail = '登録機の新しい heartbeat と Quest /status が一致しません'
            results.append({'device': quest['label'], 'started': bool(status),
                            'detail': url if status else invalid_detail})
            if not status:
                continue
            assigned = [t for t in tablets if t.get('questId', t.get('target')) == quest['id']]
            if not assigned:
                results.append({'device': quest['label'] + ' のタブレット', 'started': False,
                                'detail': 'operations-fleet.json に questId と USB serial を登録する'})
            for tablet in assigned:
                physical_serial = tablet.get('serial')
                if physical_serial not in available:
                    results.append({'device': tablet.get('label', physical_serial), 'started': False,
                                    'detail': 'タブレットを USB 接続してデバッグを許可する'})
                    continue
                app = prepare_tablet(adb, available[physical_serial]['serial'],
                                     physical_serial, quest['id'], evidence)
                results.append({'device': tablet.get('label', physical_serial),
                                'started': bool(app.get('verified')),
                                'detail': app})
        payload = {'at': time.strftime('%Y-%m-%dT%H:%M:%S'), 'startupOnly': True, 'results': results}
        (evidence / 'startup.json').write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding='utf-8')
        print(json.dumps(payload, ensure_ascii=False, indent=2))
        print('起動後の開場判定: py -3.11 tools/onsite.py check --quests ' + args.quests)
        return 0 if results and all(r['started'] for r in results) else 1
    except (OSError, RuntimeError, ValueError, subprocess.TimeoutExpired) as error:
        print(str(error))
        return 1
