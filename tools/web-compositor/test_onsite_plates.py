"""設営用の無人画 API を fake MJPEG と一時ディレクトリで検証する。"""

import copy
import http.client
import importlib.util
import io
import json
import os
import shutil
import tempfile
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from PIL import Image


HERE = os.path.dirname(os.path.abspath(__file__))


def load_server():
    spec = importlib.util.spec_from_file_location(
        'capture_server_onsite_plates', os.path.join(HERE, 'capture-server.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def jpeg_bytes():
    output = io.BytesIO()
    Image.new('RGB', (16, 12), (30, 60, 90)).save(output, format='JPEG')
    return output.getvalue()


class FakeMjpegHandler(BaseHTTPRequestHandler):
    frame = jpeg_bytes()
    requests = []

    def do_GET(self):
        type(self).requests.append((self.path, self.headers.get('Authorization')))
        if self.path == '/broken':
            body = b'not a jpeg stream'
            self.send_response(200)
            self.send_header('Content-Type', 'multipart/x-mixed-replace; boundary=frame')
            self.send_header('Content-Length', str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return
        body = (b'--frame\r\nContent-Type: image/jpeg\r\nContent-Length: '
                + str(len(self.frame)).encode() + b'\r\n\r\n' + self.frame + b'\r\n--frame--\r\n')
        self.send_response(200)
        self.send_header('Content-Type', 'multipart/x-mixed-replace; boundary=frame')
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *_args):
        pass


class OnsitePlateApiTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix='onsite_plates_')
        self.addCleanup(shutil.rmtree, self.tmp, ignore_errors=True)
        self.captures = os.path.join(self.tmp, 'captures')
        self.recordings = os.path.join(self.tmp, 'recordings')
        os.makedirs(self.captures)
        os.makedirs(self.recordings)

        FakeMjpegHandler.requests = []
        self.mjpeg = ThreadingHTTPServer(('127.0.0.1', 0), FakeMjpegHandler)
        self.mjpeg_thread = threading.Thread(target=self.mjpeg.serve_forever, daemon=True)
        self.mjpeg_thread.start()
        self.addCleanup(self.stop_mjpeg)

        self.server = load_server()
        self.server.CAPTURES = self.captures
        self.server.RECORDINGS = self.recordings
        self.server.SHOOT_MANIFEST = os.path.join(self.recordings, 'approach-manifest.json')
        self.server.SHOW_FILE = os.path.join(self.tmp, 'show.json')
        self.server.LOCAL_URL_DIRS = {'/captures/': self.captures,
                                      '/recordings/': self.recordings}
        old_path = os.path.join(self.captures, 'old.jpg')
        with open(old_path, 'wb') as stream:
            stream.write(jpeg_bytes())
        self.server._show = {
            'rev': 7,
            'control': {},
            'cameras': [
                {'id': camera_id, 'host': '127.0.0.1', 'port': self.mjpeg.server_port,
                 'path': '/custom', 'auth': 'staff:secret'}
                for camera_id in ('A', 'B', 'C')
            ],
            'cues': [
                {'id': 'plate_A', 'sourceUrl': '/captures/old.jpg'},
                {'id': 'plate_A_right', 'sourceUrl': '/captures/old.jpg'},
                {'id': 'plate_B', 'sourceUrl': '/captures/old.jpg'},
                {'id': 'plate_C', 'sourceUrl': '/captures/old.jpg'},
            ],
        }
        with open(self.server.SHOW_FILE, 'w', encoding='utf-8') as stream:
            json.dump(self.server._show, stream)
        self.old_normalize = self.server._ops.normalize_show
        self.server._ops.normalize_show = lambda _show: False
        self.addCleanup(setattr, self.server._ops, 'normalize_show', self.old_normalize)

        self.old_env = {key: os.environ.get(key) for key in
                        ('FIXEDCAM_AUTHORING', 'FIXEDCAM_PREPARATION')}
        os.environ.pop('FIXEDCAM_AUTHORING', None)
        os.environ['FIXEDCAM_PREPARATION'] = '1'
        self.addCleanup(self.restore_env)
        self.httpd = self.server.ThreadingHTTPServer(
            ('127.0.0.1', 0), self.server.Handler)
        self.thread = threading.Thread(target=self.httpd.serve_forever, daemon=True)
        self.thread.start()
        self.addCleanup(self.stop_server)

    def stop_mjpeg(self):
        self.mjpeg.shutdown()
        self.mjpeg_thread.join(timeout=5)
        self.mjpeg.server_close()

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
        connection = http.client.HTTPConnection(
            '127.0.0.1', self.httpd.server_port, timeout=10)
        payload = json.dumps(body).encode() if body is not None else None
        request_headers = dict(headers or {})
        if body is not None and 'Content-Type' not in request_headers:
            request_headers['Content-Type'] = 'application/json'
        connection.request(method, path, payload, request_headers)
        response = connection.getresponse()
        raw = response.read()
        connection.close()
        return response.status, json.loads(raw)

    def test_capture_inventory_and_atomic_a_adoption(self):
        status, capabilities = self.request('GET', '/ops/capabilities')
        self.assertEqual(status, 200)
        self.assertTrue(capabilities['plateCapture'])

        status, before = self.request('GET', '/shoot/plates')
        self.assertEqual(status, 200)
        self.assertIsInstance(before['observedAt'], int)
        plate_a = before['items'][0]
        self.assertEqual(plate_a['current']['url'], '/captures/old.jpg')
        self.assertFalse(plate_a['ready'])

        status, captured = self.request(
            'POST', '/shoot/plate-capture', {'cameraId': 'A'})
        self.assertEqual(status, 200)
        candidate = captured['candidate']
        self.assertEqual((candidate['width'], candidate['height']), (16, 12))
        self.assertEqual(self.server._show['cues'][0]['sourceUrl'], '/captures/old.jpg')
        self.assertIn(('/custom', 'Basic c3RhZmY6c2VjcmV0'), FakeMjpegHandler.requests)

        status, inventory = self.request('GET', '/shoot/plates')
        self.assertEqual(status, 200)
        plate_a = inventory['items'][0]
        self.assertEqual([item['url'] for item in plate_a['candidates']], [candidate['url']])
        self.assertEqual(plate_a['current']['url'], '/captures/old.jpg')

        status, adopted = self.request('POST', '/shoot/plate-adopt', {
            'cameraId': 'A', 'url': candidate['url']})
        self.assertEqual(status, 200)
        self.assertEqual(adopted['rev'], 8)
        by_id = {cue['id']: cue for cue in self.server._show['cues']}
        self.assertEqual(by_id['plate_A']['sourceUrl'], candidate['url'])
        self.assertEqual(by_id['plate_A_right']['sourceUrl'], candidate['url'])
        self.assertTrue(self.request('GET', '/shoot/plates')[1]['items'][0]['ready'])

        status, _ = self.request('POST', '/shoot/plate-adopt', {
            'cameraId': 'B', 'url': candidate['url']})
        self.assertEqual(status, 403)
        self.server._show['cameras'][0]['path'] = '/other'
        self.assertFalse(self.request('GET', '/shoot/plates')[1]['items'][0]['ready'])
        status, _ = self.request('POST', '/shoot/plate-adopt', {
            'cameraId': 'A', 'url': candidate['url']})
        self.assertEqual(status, 409)
        self.server._show['cameras'][0]['path'] = '/custom'
        with open(os.path.join(self.captures, candidate['name']), 'ab') as stream:
            stream.write(b'tampered')
        status, _ = self.request('POST', '/shoot/plate-adopt', {
            'cameraId': 'A', 'url': candidate['url']})
        self.assertEqual(status, 403)
        self.assertEqual(self.request('GET', '/shoot/plates')[1]['items'][0]['candidates'], [])

    def test_rejects_bad_camera_broken_image_and_missing_a_cue_without_partial_update(self):
        status, _ = self.request('POST', '/shoot/plate-capture', {'cameraId': 'D'})
        self.assertEqual(status, 400)
        self.server._show['cameras'][0]['path'] = '/broken'
        status, _ = self.request('POST', '/shoot/plate-capture', {'cameraId': 'A'})
        self.assertIn(status, (422, 502))
        self.server._show['cameras'][0]['path'] = '/custom'
        status, captured = self.request(
            'POST', '/shoot/plate-capture', {'cameraId': 'A'})
        self.assertEqual(status, 200)
        candidate = captured['candidate']

        self.server._show['cues'] = [cue for cue in self.server._show['cues']
                                     if cue['id'] != 'plate_A_right']
        old_url = next(cue for cue in self.server._show['cues']
                       if cue['id'] == 'plate_A')['sourceUrl']
        status, _ = self.request('POST', '/shoot/plate-adopt', {
            'cameraId': 'A', 'url': candidate['url']})
        self.assertEqual(status, 409)
        self.assertEqual(next(cue for cue in self.server._show['cues']
                              if cue['id'] == 'plate_A')['sourceUrl'], old_url)

    def test_preparation_rejects_nonlocal_headers_and_non_json(self):
        status, _ = self.request('POST', '/shoot/plate-capture', {'cameraId': 'A'},
                                 {'Content-Type': 'text/plain'})
        self.assertEqual(status, 415)
        status, _ = self.request('POST', '/shoot/plate-adopt',
                                 {'cameraId': 'A', 'url': '/captures/old.jpg'},
                                 {'Host': 'example.test'})
        self.assertEqual(status, 403)


class PlateDependentsTest(unittest.TestCase):
    """写真を替えたら作り直す素材を、カメラごとに出す。"""

    def setUp(self):
        spec = importlib.util.spec_from_file_location('onsite_plates_dep', os.path.join(HERE, 'onsite_plates.py'))
        self.plates = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.plates)
        self.tmp = tempfile.mkdtemp(prefix='plate_dep_')
        self.addCleanup(shutil.rmtree, self.tmp, ignore_errors=True)
        for name in ('plate_A_old.jpg', 'plate_A_new.jpg', 'plate_B_old.jpg'):
            with open(os.path.join(self.tmp, name), 'wb') as stream:
                stream.write(jpeg_bytes())
        self.dependents = {
            'groups': {'stain': {'camera': 'A', 'title': 'シミ', 'how': '作り直す', 'needsInternet': True},
                       'hands': {'camera': 'B', 'title': '手形', 'how': '作り直す'}},
            'dependents': {'cue_stain': {'group': 'stain', 'plate': 'plate_A_old.jpg'},
                           'cue_stain_right': {'group': 'stain', 'plate': 'plate_A_old.jpg'},
                           'cue_hands': {'group': 'hands', 'plate': 'plate_B_old.jpg'},
                           'cue_unused': {'group': 'stain', 'plate': 'plate_A_old.jpg'}},
            'independent': {'pov_0': '手持ち'},
        }

    def status(self, current_a, referenced):
        show = {'cues': [{'id': 'plate_A', 'sourceUrl': f'/captures/{current_a}'},
                         {'id': 'plate_A_right', 'sourceUrl': f'/captures/{current_a}'},
                         {'id': 'plate_B', 'sourceUrl': '/captures/plate_B_old.jpg'},
                         {'id': 'plate_C', 'sourceUrl': '/captures/plate_C.jpg'}],
                'cameras': []}
        return self.plates.plate_status(show, {}, self.tmp, observed_at=1,
                                        dependents=self.dependents, referenced=referenced)

    def test_lists_only_cues_the_timeline_uses_grouped_by_how_they_are_made(self):
        result = self.status('plate_A_old.jpg', {'cue_stain', 'cue_stain_right', 'cue_hands', 'pov_0', 'plate_A'})
        items = {item['cameraId']: item for item in result['items']}
        self.assertEqual(len(items['A']['dependents']), 1)
        stain = items['A']['dependents'][0]
        self.assertEqual(stain['cueIds'], ['cue_stain', 'cue_stain_right'])
        self.assertTrue(stain['current'])
        self.assertTrue(stain['plateExists'])
        self.assertTrue(stain['needsInternet'])
        self.assertEqual(items['B']['dependents'][0]['cueIds'], ['cue_hands'])
        self.assertEqual(items['C']['dependents'], [])
        self.assertEqual(result['unclassified'], [])

    def test_a_new_plate_marks_the_materials_made_for_the_old_one(self):
        result = self.status('plate_A_new.jpg', {'cue_stain', 'cue_hands'})
        items = {item['cameraId']: item for item in result['items']}
        self.assertFalse(items['A']['dependents'][0]['current'])
        self.assertEqual(items['A']['dependents'][0]['plateUrl'], '/captures/plate_A_old.jpg')
        self.assertTrue(items['B']['dependents'][0]['current'])

    def test_a_used_cue_missing_from_the_ledger_is_reported(self):
        result = self.status('plate_A_old.jpg', {'cue_stain', 'cue_new_effect'})
        self.assertEqual(result['unclassified'], ['cue_new_effect'])

    def test_without_the_ledger_nothing_is_claimed(self):
        result = self.plates.plate_status({'cues': [], 'cameras': []}, {}, self.tmp, observed_at=1)
        self.assertTrue(all(item['dependents'] is None for item in result['items']))
        self.assertIsNone(result['unclassified'])

    def test_the_real_ledger_covers_every_cue_the_timeline_uses(self):
        ledger = self.plates.load_dependents()
        self.assertIsNotNone(ledger)
        for cue_id, row in ledger['dependents'].items():
            self.assertIn(row['group'], ledger['groups'], cue_id)
            self.assertIn(ledger['groups'][row['group']]['camera'], ('A', 'B', 'C'), cue_id)
            self.assertRegex(row['plate'], r'^plate_[ABC]_.+\.jpg$', cue_id)
        self.assertFalse(set(ledger['dependents']) & set(ledger.get('independent') or {}))
        show_path = os.path.join(HERE, 'show.json')
        if not os.path.isfile(show_path):
            self.skipTest('show.json は現場設定で、この機に無い')
        import export_build
        with open(show_path, encoding='utf-8') as stream:
            show = json.load(stream)
        result = self.plates.plate_status(show, {}, self.tmp, observed_at=1, dependents=ledger,
                                          referenced=export_build.referenced_cue_ids(show))
        self.assertEqual(result['unclassified'], [], '台本が使う cue を plate-dependents.json に書いていない')


if __name__ == '__main__':
    unittest.main()
