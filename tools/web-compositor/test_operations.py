import importlib.util
import json
import os
import tempfile
import threading
import time
import unittest
import urllib.error
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from unittest.mock import patch

HERE = Path(__file__).parent
spec = importlib.util.spec_from_file_location('operations', HERE / 'operations.py')
ops = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ops)


class CameraServer:
    def __init__(self, camera_id='A', uuid=None, show='mawarimi', video='moving',
                 registration=None, health=None):
        self.camera_id = camera_id
        self.uuid = uuid or ops.FLEET['cameras'][0]['uuid']
        self.show = show
        self.video = video
        self.registration = registration
        self.health = health
        owner = self

        class Handler(BaseHTTPRequestHandler):
            def do_GET(self):
                if self.path == '/info':
                    info = {'cameraId': owner.camera_id, 'uuid': owner.uuid,
                            'show': owner.show, 'lensFovDeg': 104.3}
                    info.update(owner.registration if owner.registration is not None else {
                        'expectedIp': '127.0.0.1', 'identityLocked': True,
                        'identityState': 'registered', 'localIp': '127.0.0.1'})
                    body = json.dumps(info).encode()
                    self.send_response(200)
                    self.send_header('Content-Length', str(len(body)))
                    self.end_headers()
                    self.wfile.write(body)
                elif self.path == '/health':
                    if owner.video == 'info_only':
                        self.send_error(404)
                        return
                    body = json.dumps(owner.health or {'fps': 30, 'totalFrames': 20,
                                                        'latestFrameAgeMs': 20, 'aeLock': True}).encode()
                    self.send_response(200)
                    self.send_header('Content-Length', str(len(body)))
                    self.end_headers()
                    self.wfile.write(body)
                elif self.path == '/video':
                    if owner.video == 'info_only':
                        self.send_error(404)
                        return
                    self.send_response(200)
                    self.send_header('Content-Type', 'multipart/x-mixed-replace; boundary=frame')
                    self.end_headers()
                    if owner.video != 'none':
                        for seq in ([1] if owner.video == 'stalled' else [1, 2]):
                            jpeg = b'\xff\xd8abcdef\xff\xd9'
                            part = (b'--frame\r\nContent-Type: image/jpeg\r\nContent-Length: 10\r\n'
                                    + f'X-Frame-Seq: {seq}\r\n\r\n'.encode() + jpeg + b'\r\n')
                            self.wfile.write(part)
                            self.wfile.flush()
                    if owner.video in ('none', 'stalled'):
                        time.sleep(3.2)
                else:
                    self.send_error(404)

            def log_message(self, *_args):
                pass

        self.server = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)

    def __enter__(self):
        self.thread.start()
        return self

    def __exit__(self, *_args):
        self.server.shutdown()
        self.server.server_close()

    def fixed(self):
        return {'id': 'A', 'host': '127.0.0.1', 'port': self.server.server_port,
                'uuid': ops.FLEET['cameras'][0]['uuid']}


class OperationsTests(unittest.TestCase):
    def setUp(self):
        resolver = patch.object(ops, 'resolve_adb', return_value='fixture-adb')
        resolver.start()
        self.addCleanup(resolver.stop)

    def test_disconnected_registered_camera_keeps_restart_available(self):
        fixed = {'id': 'A', 'host': '127.0.0.1', 'port': 1, 'serial': 'KNOWN', 'uuid': 'known'}
        with patch.object(ops, '_wireless_adb', return_value={'identityOk': True, 'state': 'verified'}):
            row = ops._camera(fixed, [])
        self.assertTrue(row['canRestart'])
        self.assertEqual(row['status'], 'error')

    def test_video_needs_new_complete_frames(self):
        with CameraServer() as server:
            row = ops._camera(server.fixed(), [])
            self.assertEqual(row['status'], 'ok')
            self.assertEqual((row['stream']['firstSeq'], row['stream']['lastSeq']), (1, 2))
        with CameraServer(video='none') as server:
            self.assertEqual(ops._camera(server.fixed(), [])['code'], 'frame_stalled')
        with CameraServer(video='info_only') as server:
            self.assertEqual(ops._camera(server.fixed(), [])['code'], 'frame_stalled')
        with CameraServer(video='stalled') as server:
            self.assertEqual(ops._camera(server.fixed(), [])['code'], 'frame_stalled')

    def test_identity_and_conflict(self):
        for values in ({'camera_id': 'B'}, {'uuid': 'wrong'}, {'show': 'wrong'}):
            with self.subTest(values=values), CameraServer(**values) as server:
                self.assertEqual(ops._camera(server.fixed(), [])['code'], 'identity_mismatch')
        with CameraServer() as server:
            beacons = [{'id': 'A', 'uuid': server.uuid}, {'id': 'A', 'uuid': 'other'}]
            self.assertEqual(ops._camera(server.fixed(), beacons)['code'], 'duplicate_id')

    def test_disconnected_camera_requires_manual_open_until_serial_is_verified(self):
        fixed = {'id': 'A', 'host': '127.0.0.1', 'port': 1,
                 'uuid': ops.FLEET['cameras'][0]['uuid']}
        row = ops._camera(fixed, [])
        self.assertEqual(row['code'], 'disconnected')
        self.assertEqual(row['action'], '端末で配信アプリを開いてください')
        self.assertEqual(row['wirelessAdb']['state'], 'serial_unregistered')
        self.assertFalse(row['canRestart'])

    def test_wireless_adb_accepts_only_registered_physical_serial(self):
        class Result:
            returncode = 0
            stderr = ''

            def __init__(self, stdout):
                self.stdout = stdout

        def runner(args, **_kwargs):
            return Result('connected') if args[1] == 'connect' else Result('CAM-001\n')

        fixed = {'host': '192.168.10.21', 'serial': 'CAM-001'}
        self.assertEqual(ops._wireless_adb(fixed, runner)['state'], 'verified')
        fixed['serial'] = 'CAM-002'
        self.assertEqual(ops._wireless_adb(fixed, runner)['state'], 'identity_mismatch')

    def test_quest_stale_missing_and_collision(self):
        now = time.time()
        fixed = ops.FLEET['quests'][0]
        self.assertEqual(ops._quest(fixed, {}, now)['code'], 'heartbeat_missing')
        self.assertEqual(ops._quest(fixed, {'old': {'localIp': fixed['host'], 'at': now - 7}}, now)['code'],
                         'heartbeat_missing')
        base = {'localIp': fixed['host'], 'at': now, 'visitorPort': 8090,
                'cameraCount': 3, 'activeCamera': 'A', 'recvFps': 30, 'sourceAgeMs': 50}
        devices = {fixed['deviceId']: dict(base), 'two': dict(base)}
        self.assertEqual(ops._quest(fixed, devices, now)['code'], 'ip_collision')
        self.assertEqual(ops._quest(fixed, {fixed['deviceId']: devices[fixed['deviceId']]}, now)['status'], 'ok')
        self.assertEqual(ops._quest(fixed, {'future': {'localIp': fixed['host'],
                                                       'at': now + 30}}, now)['code'],
                         'heartbeat_future')
        healthy = {'localIp': fixed['host'], 'at': now, 'visitorPort': 8090,
                   'cameraCount': 3, 'activeCamera': 'A', 'recvFps': 30, 'sourceAgeMs': 50}
        row = ops._quest(fixed, {fixed['deviceId']: healthy}, now)
        self.assertEqual(row['title'], 'Quest から応答があります')
        self.assertEqual(row['code'], 'ok')
        self.assertIn('A/B/C', row['action'])
        healthy['recvFps'] = 0
        self.assertEqual(ops._quest(fixed, {fixed['deviceId']: healthy}, now)['code'], 'active_stream_stalled')
        healthy['activeCamera'] = 'Phone 01'
        healthy['activeIndex'] = 0
        self.assertEqual(ops._quest(fixed, {fixed['deviceId']: healthy}, now)['code'], 'active_stream_stalled')

    def test_quest_requires_identity_registration_and_all_stream_diagnostics(self):
        now = time.time()
        registered = ops.FLEET['quests'][0]
        healthy = {'localIp': registered['host'], 'at': now, 'visitorPort': 8090,
                   'cameraCount': 3, 'activeCamera': 'A', 'recvFps': 30, 'sourceAgeMs': 50}
        for missing in ('cameraCount', 'activeCamera', 'recvFps', 'sourceAgeMs'):
            heartbeat = dict(healthy)
            del heartbeat[missing]
            row = ops._quest(registered, {registered['deviceId']: heartbeat}, now)
            self.assertNotEqual(row['status'], 'ok', missing)
            self.assertEqual(row['code'], 'diagnostics_missing', missing)
        unregistered = dict(ops.FLEET['quests'][1])
        row = ops._quest(unregistered, {'observed-beta': dict(healthy, localIp=unregistered['host'])}, now)
        self.assertEqual(row['status'], 'unknown')
        self.assertEqual(row['code'], 'identity_unregistered')

    def test_status_keeps_all_fixed_rows_and_rejects_missing_camera(self):
        with CameraServer() as server:
            fixed = [server.fixed(), {'id': 'B', 'host': '127.0.0.1', 'port': 1, 'uuid': 'b'},
                     {'id': 'C', 'host': '127.0.0.1', 'port': 1, 'uuid': 'c'}]
            with patch.dict(ops.FLEET, {'cameras': fixed}):
                ops.invalidate()
                result = ops.status({}, [], 8)
                self.assertEqual([c['id'] for c in result['cameras']], ['A', 'B', 'C'])
                self.assertEqual([c['status'] for c in result['cameras']], ['ok', 'error', 'error'])
                self.assertEqual(len(result['quests']), 2)
                self.assertEqual([t['id'] for t in result['tablets']], ['alpha', 'beta'])
                self.assertEqual(result['config']['revision'], 8)
                fresh = ops.status({}, [{'id': 'A', 'uuid': 'other'},
                                        {'id': 'A', 'uuid': server.uuid}], 8)
                self.assertEqual(fresh['cameras'][0]['code'], 'duplicate_id')

    def test_tablet_connection_and_reflection_from_quest_heartbeat(self):
        now = time.time()
        fixed = ops.FLEET['quests'][0]
        portal = {'schema': 1, 'listening': True, 'portalSessionId': 'quest-run',
                  'tablets': [{'tabletSessionId': 'page-1', 'ip': '192.168.10.50', 'ageSec': 2}],
                  'lastRequest': {'tabletSessionId': 'page-1', 'seq': 4, 'lang': 'en',
                                  'relief': True},
                  'appliedSeq': 4, 'applyCount': 3, 'received': 4,
                  'lang': 'en', 'relief': True, 'pendingSeq': 4, 'consumedSeq': 4}
        heartbeat = {'localIp': fixed['host'], 'at': now - 3, 'visitorPortal': portal}
        devices = {fixed['deviceId']: heartbeat}

        def check(expected, changed=None, at=None):
            current = json.loads(json.dumps(heartbeat))
            if changed:
                current['visitorPortal'].update(changed)
            if at is not None:
                current['at'] = at
            return ops._tablet(fixed, {fixed['deviceId']: current}, now)

        row = check('ok')
        self.assertEqual((row['status'], row['portalStatus'], row['connectionStatus'],
                          row['reflectionStatus']), ('ok', 'ok', 'ok', 'ok'))
        self.assertEqual((row['portalSessionId'], row['tabletSessionId'], row['tabletIp']),
                         ('quest-run', 'page-1', '192.168.10.50'))
        self.assertEqual((row['ageSec'], row['sentSeq'], row['appliedSeq'], row['received'],
                          row['applyCount'], row['requestedLang'], row['requestedRelief'],
                          row['lang'], row['relief'], row['activePages']),
                         (5, 4, 4, 4, 3, 'en', True, 'en', True, 1))
        self.assertEqual(check('unknown', {'lastRequest': None})['title'],
                         '設定の送信はまだ確認できません')
        self.assertEqual(check('unknown', at=now - 7)['status'], 'unknown')
        self.assertEqual(check('unknown', at=now + 1)['status'], 'unknown')
        self.assertEqual(check('warning', {'tablets': portal['tablets'] + [
            {'tabletSessionId': 'page-2', 'ip': '192.168.10.51', 'ageSec': 1}]})['title'],
            '複数のページが開いています')
        self.assertEqual(check('warning', {'lastRequest': dict(portal['lastRequest'],
            tabletSessionId='page-2')})['reflectionStatus'], 'warning')
        self.assertEqual(check('warning', {'lang': 'ja'})['reflectionStatus'], 'warning')
        self.assertEqual(check('warning', {'appliedSeq': 3, 'pendingSeq': 4})['reflectionStatus'],
                         'warning')
        self.assertEqual(check('unknown', {'tablets': []})['action'],
                         '画面を手前に開いてください')
        self.assertEqual(check('unknown', {'tablets': [dict(portal['tablets'][0], ageSec=29)]})[
            'activePages'], 0)
        self.assertEqual(check('unknown', {'schema': 0})['portalStatus'], 'unknown')
        self.assertEqual(check('unknown', {'received': None})['portalStatus'], 'unknown')
        self.assertEqual(check('error', {'listening': False})['portalStatus'], 'error')
        collision = dict(devices, other=heartbeat)
        self.assertEqual(ops._tablet(fixed, collision, now)['status'], 'error')
        with patch.dict(ops.FLEET, {'quests': [dict(fixed, deviceId='expected')]}):
            self.assertEqual(ops._tablet(ops.FLEET['quests'][0], devices, now)['status'], 'error')

    def test_status_refreshes_tablets_during_camera_cache(self):
        now = time.time()
        fixed = ops.FLEET['quests'][0]
        with patch.object(ops, '_camera', return_value={'id': 'A', 'issues': []}), \
             patch.dict(ops.FLEET, {'cameras': [{'id': 'A', 'host': 'unused'}]}):
            ops.invalidate()
            first = ops.status({}, [], 943)
            self.assertEqual([row['id'] for row in first['tablets']], ['alpha', 'beta'])
            second = ops.status({fixed['deviceId']: {'localIp': fixed['host'], 'at': now,
                                                     'visitorPortal': {'schema': 0}}}, [], 943)
            self.assertEqual(second['tablets'][0]['title'], 'タブレットの診断情報がありません')

    def test_fixed_show_guards(self):
        show = {'rev': 10, 'cameras': [{'id': 'A', 'host': 'wrong', 'pose': {'x': 1}},
                                      {'id': 'B'}, {'id': 'C'},
                                      {'id': 'D', 'host': 'optional'}],
                'control': {'autoFollow': True, 'runEpoch': 9}}
        self.assertTrue(ops.normalize_show(show))
        self.assertEqual(show['cameras'][0]['pose'], {'x': 1})
        self.assertEqual(next(c for c in show['cameras'] if c['id'] == 'D')['host'], 'optional')
        self.assertFalse(show['control']['discoveryEnabled'])
        self.assertFalse(ops.normalize_show(show))
        self.assertTrue(ops.conflicts_fixed_patch({'cameras': [{'id': 'A', 'host': 'wrong'}]}, show))
        self.assertTrue(ops.conflicts_fixed_patch({'control': {'autoFollow': True}}, show))
        self.assertFalse(ops.conflicts_fixed_patch({'post': {'exposure': 1}}, show))
        reordered = [show['cameras'][1], show['cameras'][0], *show['cameras'][2:]]
        self.assertTrue(ops.conflicts_fixed_patch({'cameras': reordered}, show))
        with self.assertRaises(ValueError):
            ops.normalize_show({'cameras': reordered, 'control': {}})
        missing = {'cameras': [], 'control': {}}
        self.assertTrue(ops.normalize_show(missing))
        self.assertEqual([c['id'] for c in missing['cameras']], ['A', 'B', 'C'])

    def test_old_health_cannot_override_live_video_and_registration_is_checked(self):
        with CameraServer(health={'encodeIdle': True, 'latestFrameAgeMs': 120000,
                                  'totalFrames': 50, 'clientCount': 0}) as server:
            self.assertEqual(ops._camera(server.fixed(), [])['status'], 'ok')
        with CameraServer(registration={}) as server:
            self.assertEqual(ops._camera(server.fixed(), [])['code'], 'identity_unsupported')
        for registration in ({'identityState': 'registration_required', 'identityLocked': False,
                              'localIp': '127.0.0.1'},
                             {'identityState': 'registered', 'identityLocked': True,
                              'expectedIp': '127.0.0.2', 'localIp': '127.0.0.1'},
                             {'identityState': 'registered', 'identityLocked': True,
                              'expectedIp': '127.0.0.1', 'localIp': '127.0.0.2'}):
            with self.subTest(registration=registration), CameraServer(registration=registration) as server:
                row = ops._camera(server.fixed(), [])
                self.assertEqual(row['status'], 'error')
                self.assertFalse(row['identityOk'])

    def test_state_http_rejects_fixed_host_and_keeps_other_edit(self):
        with tempfile.TemporaryDirectory() as temp:
            path = os.path.join(temp, 'show.json')
            with open(path, 'w', encoding='utf-8') as output:
                json.dump({'rev': 3, 'cameras': [], 'control': {}, 'post': {'exposure': 0}}, output)
            with patch.dict(os.environ, {'FIXEDCAM_SHOW_FILE': path, 'FIXEDCAM_VERIFY': '1'}):
                server_spec = importlib.util.spec_from_file_location('ops_test_server', HERE / 'capture-server.py')
                module = importlib.util.module_from_spec(server_spec)
                server_spec.loader.exec_module(module)
            http = ThreadingHTTPServer(('127.0.0.1', 0), module.Handler)
            thread = threading.Thread(target=http.serve_forever, daemon=True)
            thread.start()
            base = f'http://127.0.0.1:{http.server_port}'
            with self.assertRaises(urllib.error.HTTPError) as error:
                urllib.request.urlopen(urllib.request.Request(
                    base + '/state', b'{}', {'Content-Type': 'application/json'}))
            self.assertEqual(error.exception.code, 403)
            authoring = patch.dict(os.environ, {'FIXEDCAM_AUTHORING': '1'})
            authoring.start()
            try:
                with urllib.request.urlopen(base + '/state') as response:
                    state = json.load(response)
                self.assertEqual(state['rev'], 4)
                self.assertEqual([c['id'] for c in state['cameras'][:3]], ['A', 'B', 'C'])
                bad = json.dumps({'cameras': [{'id': 'A', 'host': 'wrong'}]}).encode()
                with self.assertRaises(urllib.error.HTTPError) as error:
                    urllib.request.urlopen(urllib.request.Request(base + '/state', bad,
                                                                  {'Content-Type': 'application/json'}))
                self.assertEqual(error.exception.code, 409)
                reordered = [state['cameras'][1], state['cameras'][0], state['cameras'][2]]
                bad_order = json.dumps({'cameras': reordered}).encode()
                with self.assertRaises(urllib.error.HTTPError) as error:
                    urllib.request.urlopen(urllib.request.Request(base + '/state', bad_order,
                                                                  {'Content-Type': 'application/json'}))
                self.assertEqual(error.exception.code, 409)
                with self.assertRaises(urllib.error.HTTPError) as error:
                    urllib.request.urlopen(urllib.request.Request(
                        base + '/ops/restart', b'{"cameraId":"A"}',
                        {'Content-Type': 'application/json', 'Origin': 'http://outside.invalid'}))
                self.assertEqual(error.exception.code, 403)
                with self.assertRaises(urllib.error.HTTPError) as error:
                    urllib.request.urlopen(urllib.request.Request(base + '/ops/restart',
                                                                  b'{"cameraId":"A"}'))
                self.assertEqual(error.exception.code, 415)
                good = json.dumps({'post': {'exposure': 1}}).encode()
                with urllib.request.urlopen(urllib.request.Request(base + '/state', good,
                                                                    {'Content-Type': 'application/json'})) as response:
                    self.assertEqual(json.load(response)['rev'], 5)
                with open(path, encoding='utf-8') as source:
                    saved = json.load(source)
                self.assertEqual(saved['post']['exposure'], 1)
                self.assertEqual(saved['cameras'][0]['host'], '192.168.10.21')
            finally:
                authoring.stop()
                http.shutdown()
                http.server_close()

    def test_restart_never_runs_on_mismatch_or_reports_adb_failure_as_success(self):
        with CameraServer(camera_id='B') as server:
            fixed = server.fixed()
            with patch.dict(ops.FLEET, {'cameras': [fixed]}):
                calls = []
                code, result = ops.restart('A', {}, [], 1, runner=lambda *a, **k: calls.append(a))
                self.assertEqual(code, 409)
                self.assertFalse(result['ok'])
                self.assertEqual(calls, [])
        with CameraServer(registration={'identityState': 'device_mismatch',
                                        'identityLocked': True, 'localIp': '127.0.0.1'}) as server:
            fixed = server.fixed()
            with patch.dict(ops.FLEET, {'cameras': [fixed]}):
                calls = []
                code, result = ops.restart('A', {}, [], 1, runner=lambda *a, **k: calls.append(a))
                self.assertEqual(code, 409)
                self.assertFalse(result['ok'])
                self.assertEqual(calls, [])
        with CameraServer() as server:
            fixed = dict(server.fixed(), serial='CAMERA-SERIAL')
            class Failed:
                returncode = 1
                stdout = ''
                stderr = 'failed'
            class Passed:
                returncode = 0
                stderr = ''

                def __init__(self, stdout):
                    self.stdout = stdout

            def runner(args, **_kwargs):
                if args[1] == 'connect':
                    return Passed('connected')
                if args[-2:] == ['getprop', 'ro.serialno']:
                    return Passed('CAMERA-SERIAL\n')
                return Failed()
            with patch.dict(ops.FLEET, {'cameras': [fixed]}):
                code, result = ops.restart('A', {}, [], 1, runner=runner)
                self.assertEqual(code, 502)
                self.assertFalse(result['ok'])


if __name__ == '__main__':
    unittest.main()
