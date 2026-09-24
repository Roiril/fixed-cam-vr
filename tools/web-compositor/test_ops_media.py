import copy
import http.client
import importlib.util
import json
import os
import shutil
import tempfile
import threading
import unittest
import zipfile

import export_build
import release_inventory


HERE = os.path.dirname(os.path.abspath(__file__))


def load_server():
    spec = importlib.util.spec_from_file_location(
        'capture_server_ops_media', os.path.join(HERE, 'capture-server.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class MediaFixture(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix='ops_media_')
        self.addCleanup(shutil.rmtree, self.tmp, ignore_errors=True)
        for name in ('captures', 'masks', 'recordings'):
            os.makedirs(os.path.join(self.tmp, name))
        self.dirs = export_build.local_url_dirs(self.tmp)
        self.shared = os.path.join(self.tmp, 'captures', 'shared.png')
        self.stable_mask = os.path.join(self.tmp, 'masks', 'stable.png')
        self.old_only = os.path.join(self.tmp, 'captures', 'old.mp4')
        with open(self.shared, 'wb') as stream:
            stream.write(b'old-image')
        with open(self.stable_mask, 'wb') as stream:
            stream.write(b'stable-mask')
        with open(self.old_only, 'wb') as stream:
            stream.write(b'old-video')
        self.old_show = {
            'rev': 1,
            'control': {'slots': [{'name': 'active', 'url': '/captures/shared.png'}]},
            'cues': [
                {'id': 'slot-cue', 'name': 'スロット演出', 'sourceUrl': 'slot://active',
                 'maskUrl': '/masks/stable.png'},
                {'id': 'old-cue', 'name': '旧演出', 'sourceUrl': '/captures/old.mp4'},
            ],
            'timeline': {'schema': 3, 'segments': [
                {'takes': [{'id': 'L1C1#0', 'steps': [{'cueId': 'slot-cue'}]}]},
                {'takes': [{'id': 'L2C0#0', 'steps': [{'cueId': 'old-cue'}]}]},
            ]},
        }
        self.bundle = os.path.join(self.tmp, 'bundle')
        export_build.export_build(self.old_show, self.tmp, self.dirs, self.bundle)
        self.apk = os.path.join(self.tmp, 'mawarimi.apk')
        with zipfile.ZipFile(self.apk, 'w') as apk:
            for base, _, files in os.walk(self.bundle):
                for name in files:
                    path = os.path.join(base, name)
                    rel = os.path.relpath(path, self.bundle).replace('\\', '/')
                    apk.write(path, 'assets/show/' + rel)
        release_inventory._apk_cache.clear()

    def current_show(self):
        show = copy.deepcopy(self.old_show)
        show['cues'] = [show['cues'][0],
                        {'id': 'new-cue', 'name': '新演出',
                         'sourceUrl': '/captures/new.mp4'},
                        {'id': 'missing-cue', 'name': '未準備演出',
                         'sourceUrl': '/captures/missing.mp4'}]
        show['timeline']['segments'] = [show['timeline']['segments'][0],
                                        {'takes': [{'id': 'L3C1#0', 'steps': [
                                            {'cueId': 'new-cue'}]}]},
                                        {'takes': [{'id': 'L3C2#0', 'steps': [
                                            {'cueId': 'missing-cue'}]}]}]
        with open(os.path.join(self.tmp, 'captures', 'new.mp4'), 'wb') as stream:
            stream.write(b'new-video')
        with open(self.shared, 'wb') as stream:
            stream.write(b'new-image')
        return show


class MediaLibraryTest(MediaFixture):
    def test_compares_bytes_and_keeps_old_only_effect(self):
        data = release_inventory.media_library(self.current_show(), self.dirs, self.apk, now=123)
        self.assertTrue(data['apkVerified'])
        by_id = {effect['id']: effect for effect in data['effects']}
        slot = by_id['L1C1#0']
        self.assertEqual(slot['name'], 'スロット演出')
        self.assertEqual(slot['segmentName'], '1周目B')
        source = next(a for a in slot['assets'] if a['key'].endswith(':sourceUrl'))
        self.assertEqual(source['current']['url'], '/captures/shared.png')
        self.assertEqual(source['current']['name'], source['built']['name'])
        self.assertEqual(source['status'], 'changed')
        mask = next(a for a in slot['assets'] if a['key'].endswith(':maskUrl'))
        self.assertEqual(mask['status'], 'same')
        self.assertEqual(by_id['L2C0#0']['assets'][0]['status'], 'removed')
        self.assertEqual(by_id['L3C1#0']['assets'][0]['status'], 'unbuilt')
        self.assertEqual(by_id['L3C2#0']['assets'][0]['status'], 'unknown')
        self.assertEqual(data['preparedContentId'], '')

    def test_unverified_apk_never_claims_a_difference(self):
        with open(self.apk, 'wb') as stream:
            stream.write(b'not an apk')
        release_inventory._apk_cache.clear()
        data = release_inventory.media_library(self.current_show(), self.dirs, self.apk)
        self.assertFalse(data['apkVerified'])
        self.assertTrue(all(asset['status'] == 'unknown'
                            for effect in data['effects'] for asset in effect['assets']))

    def test_asset_reader_rejects_stale_id_and_traversal(self):
        verified = release_inventory._verified_apk(self.apk)
        path = next(iter(verified['assets']))
        self.assertEqual(release_inventory.read_apk_asset(self.apk, 'stale', path)[0], 409)
        self.assertEqual(release_inventory.read_apk_asset(
            self.apk, verified['contentId'], 'assets/../show.json')[0], 404)
        code, raw, record = release_inventory.read_apk_asset(
            self.apk, verified['contentId'], path)
        self.assertEqual(code, 200)
        self.assertEqual(len(raw), record['size'])


class OpsHttpTest(MediaFixture):
    def setUp(self):
        super().setUp()
        self.server_module = load_server()
        self.server_module.REPO_ROOT = self.tmp
        self.server_module.RECORDINGS = os.path.join(self.tmp, 'recordings')
        self.server_module.LOCAL_URL_DIRS = self.dirs
        self.server_module.SHOW_FILE = os.path.join(self.tmp, 'show.json')
        self.server_module._show = copy.deepcopy(self.old_show)
        self.server_module._show['cameras'] = [
            {'id': 'A', 'host': '127.0.0.1', 'port': 18080}]
        os.makedirs(os.path.join(self.tmp, 'Builds'))
        shutil.copyfile(self.apk, os.path.join(self.tmp, 'Builds', 'mawarimi.apk'))
        release_inventory._apk_cache.clear()
        self.httpd = self.server_module.ThreadingHTTPServer(
            ('127.0.0.1', 0), self.server_module.Handler)
        self.thread = threading.Thread(target=self.httpd.serve_forever, daemon=True)
        self.thread.start()
        self.addCleanup(self.stop_server)
        self.old_env = {key: os.environ.get(key) for key in
                        ('FIXEDCAM_AUTHORING', 'FIXEDCAM_PREPARATION')}
        self.addCleanup(self.restore_env)

    def stop_server(self):
        self.httpd.shutdown()
        self.thread.join(timeout=5)
        self.httpd.server_close()

    def restore_env(self):
        for key, value in self.old_env.items():
            if value is None:
                os.environ.pop(key, None)
            else:
                os.environ[key] = value

    def request(self, method, path, body=None, headers=None):
        conn = http.client.HTTPConnection('127.0.0.1', self.httpd.server_port, timeout=5)
        payload = json.dumps(body).encode() if body is not None else None
        request_headers = dict(headers or {})
        if body is not None and 'Content-Type' not in request_headers:
            request_headers['Content-Type'] = 'application/json'
        conn.request(method, path, payload, request_headers)
        response = conn.getresponse()
        raw = response.read()
        result = response.status, dict(response.getheaders()), raw
        conn.close()
        return result

    def test_apk_asset_supports_exact_bytes_head_and_range(self):
        verified = release_inventory._verified_apk(
            os.path.join(self.tmp, 'Builds', 'mawarimi.apk'))
        path = next(iter(verified['assets']))
        query = ('/ops/apk-asset?contentId=' + verified['contentId']
                 + '&path=' + path.replace('/', '%2F'))
        status, _, raw = self.request('GET', query)
        self.assertEqual(status, 200)
        self.assertEqual(raw, release_inventory.read_apk_asset(
            os.path.join(self.tmp, 'Builds', 'mawarimi.apk'), verified['contentId'], path)[1])
        status, headers, raw = self.request('GET', query, headers={'Range': 'bytes=1-3'})
        self.assertEqual((status, raw), (206, release_inventory.read_apk_asset(
            os.path.join(self.tmp, 'Builds', 'mawarimi.apk'), verified['contentId'], path)[1][1:4]))
        self.assertIn('bytes 1-3/', headers['Content-Range'])
        status, _, raw = self.request('HEAD', query)
        self.assertEqual((status, raw), (200, b''))
        status, _, _ = self.request('GET', query.replace(verified['contentId'], 'stale'))
        self.assertEqual(status, 409)
        traversal = ('/ops/apk-asset?contentId=' + verified['contentId']
                     + '&path=assets%2F..%2Fshow.json')
        status, _, _ = self.request('GET', traversal)
        self.assertEqual(status, 404)

    def test_preparation_mode_allows_only_local_fixed_camera_and_pov_adoption(self):
        os.environ.pop('FIXEDCAM_AUTHORING', None)
        os.environ['FIXEDCAM_PREPARATION'] = '1'
        self.server_module._shoot_get = lambda *args, **kwargs: (True, {'accepted': True})
        status, _, raw = self.request('GET', '/ops/capabilities')
        self.assertEqual(status, 200)
        self.assertEqual(json.loads(raw)['mode'], 'preparation')
        status, _, _ = self.request('POST', '/shoot/start', {
            'host': '127.0.0.1', 'port': 18080, 'shot': 'pov_0'})
        self.assertEqual(status, 200)
        status, _, _ = self.request('POST', '/shoot/start', {
            'host': '127.0.0.2', 'port': 18080, 'shot': 'pov_0'})
        self.assertEqual(status, 403)
        adopted = os.path.join(self.tmp, 'recordings', 'take.mp4')
        with open(adopted, 'wb') as stream:
            stream.write(b'video')
        self.server_module._show['cues'].append({'id': 'pov_0', 'sourceUrl': ''})
        status, _, _ = self.request('POST', '/shoot/adopt', {
            'cueId': 'pov_0', 'url': '/recordings/take.mp4'})
        self.assertEqual(status, 200)
        status, _, _ = self.request('POST', '/shoot/adopt', {
            'cueId': 'slot-cue', 'url': '/recordings/take.mp4'})
        self.assertEqual(status, 403)
        status, _, _ = self.request('POST', '/shoot/adopt', {
            'cueId': 'pov_0', 'url': '/captures/shared.png'})
        self.assertEqual(status, 403)
        status, _, _ = self.request('POST', '/export-build', {})
        self.assertEqual(status, 403)

    def test_preparation_mode_rejects_bad_headers(self):
        os.environ.pop('FIXEDCAM_AUTHORING', None)
        os.environ['FIXEDCAM_PREPARATION'] = '1'
        status, _, _ = self.request('POST', '/shoot/start', {}, {
            'Content-Type': 'text/plain'})
        self.assertEqual(status, 415)
        status, _, _ = self.request('POST', '/shoot/start', {
            'host': '127.0.0.1', 'port': 18080}, {'Host': 'example.test'})
        self.assertEqual(status, 403)
        host = f'127.0.0.1:{self.httpd.server_port}'
        status, _, _ = self.request('POST', '/shoot/start', {
            'host': '127.0.0.1', 'port': 18080},
            {'Host': host, 'Origin': 'http://localhost:1'})
        self.assertEqual(status, 403)

    def test_handheld_shooter_requires_registered_discovery_uuid(self):
        module = self.server_module
        module._disc.clear()
        shooter = module._ops.FLEET['handheldShooters'][0]
        module._disc[shooter['uuid']] = {
            'role': 'camera', 'id': shooter['id'], 'ip': '127.0.0.2',
            'port': 8080, 'uuid': shooter['uuid'], 'lastSeen': module.time.time(),
        }
        cameras = module.Handler._shoot_cams(None)
        self.assertIn(('P', '127.0.0.2'), {(c['id'], c['host']) for c in cameras})
        module._disc[shooter['uuid']]['uuid'] = 'wrong-device'
        cameras = module.Handler._shoot_cams(None)
        self.assertNotIn('P', {c['id'] for c in cameras})

    def test_preparation_collect_requires_registered_source_and_pov(self):
        os.environ.pop('FIXEDCAM_AUTHORING', None)
        os.environ['FIXEDCAM_PREPARATION'] = '1'
        self.server_module.Handler._shoot_collect = (
            lambda handler, body, host, port: handler._json({'ok': True, 'host': host}))
        status, _, raw = self.request('POST', '/shoot/collect', {
            'name': 'take.mp4', 'shot': 'pov_0', 'port': 18080})
        self.assertEqual(status, 200)
        self.assertEqual(json.loads(raw)['host'], '127.0.0.1')
        status, _, _ = self.request('POST', '/shoot/collect', {
            'name': 'take.mp4', 'shot': 'slot-cue', 'port': 18080})
        self.assertEqual(status, 403)
        status, _, _ = self.request('POST', '/shoot/collect', {
            'name': 'take.mp4', 'shot': 'pov_0', 'host': '127.0.0.2', 'port': 18080})
        self.assertEqual(status, 403)


if __name__ == '__main__':
    unittest.main()
