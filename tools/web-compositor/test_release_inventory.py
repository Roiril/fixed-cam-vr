import os
import json
import shutil
import tempfile
import unittest
import zipfile

import export_build
import release_inventory


class ReleaseInventoryTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix='inventory_')
        self.addCleanup(shutil.rmtree, self.tmp, ignore_errors=True)
        self.source = os.path.join(self.tmp, 'source')
        os.makedirs(os.path.join(self.source, 'masks'))
        self.bundle = os.path.join(self.tmp, 'bundle')
        self.apk = os.path.join(self.tmp, 'mawarimi.apk')
        with open(os.path.join(self.source, 'masks', 'm.png'), 'wb') as stream:
            stream.write(b'image')
        self.dirs = export_build.local_url_dirs(self.source)
        self.show = {'rev': 1, 'cues': [{'id': 'a', 'maskUrl': '/masks/m.png'}],
                     'timeline': {'segments': [{'takes': [{
                         'id': 'take-1', 'name': '演出一',
                         'steps': [{'cueId': 'a'}]}]}]}}

    def test_report_start_video_appears_in_current_adoption(self):
        os.makedirs(os.path.join(self.source, 'static-inputs'))
        with open(os.path.join(self.source, 'static-inputs', 'flutter.mp4'), 'wb') as stream:
            stream.write(b'roundtrip')
        self.show['cues'].append({'id': 'flutter', 'name': '幕が動く',
                                  'sourceUrl': '/static-inputs/flutter.mp4'})
        self.show['timeline']['segments'][0]['takes'][0]['markStartCueId'] = 'flutter'
        library = release_inventory.media_library(self.show, self.dirs, self.apk, now=100)
        assets = library['effects'][0]['assets']
        response = next(a for a in assets if a['key'] == 'markStart:sourceUrl')
        self.assertEqual(response['kind'], 'video')
        self.assertEqual(response['cueId'], 'flutter')
        self.assertTrue(response['current']['exists'])

    def test_bundle_apk_and_preparation_match(self):
        content_id = export_build.export_build(self.show, self.tmp, self.dirs,
                                               self.bundle)['contentId']
        expected, _ = release_inventory._planned(self.show, self.dirs)
        self.assertEqual(expected, content_id)
        self.assertEqual(release_inventory.verify_bundle(self.bundle), content_id)
        with zipfile.ZipFile(self.apk, 'w') as apk:
            for base, _, files in os.walk(self.bundle):
                for name in files:
                    path = os.path.join(base, name)
                    relative = os.path.relpath(path, self.bundle).replace('\\', '/')
                    apk.write(path, 'assets/show/' + relative)
        with open(self.apk + '.content.json', 'w', encoding='utf-8') as stream:
            json.dump({'schema': 1, 'policy': 'baked-only-v1', 'contentId': content_id,
                       'buildGuid': 'abcdef01-2345-6789-abcd-ef0123456789',
                       'apkSha256': export_build._sha_file(self.apk)}, stream)
        self.assertEqual(release_inventory.verify_apk(self.apk), content_id)
        status = release_inventory.content_status(self.show, self.dirs, self.bundle, self.apk,
                                                  {}, now=100)
        self.assertEqual(status['preparation']['status'], 'ok')
        self.assertEqual(status['bundle']['status'], 'ok')
        self.assertEqual(status['apk']['status'], 'ok')
        self.assertEqual(status['apk']['buildGuid'], 'abcdef01-2345-6789-abcd-ef0123456789')
        self.assertEqual(status['effects'][0]['name'], '演出一')
        self.assertTrue(status['effects'][0]['inApk'])
        self.assertFalse(status['ready'])
        devices = {key: {'at': 99, 'localIp': host, 'contentId': content_id,
                         'contentPolicy': 'baked-only-v1', 'contentVerified': True,
                         'buildGuid': 'ABCDEF0123456789ABCDEF0123456789'}
                   for key, host in (('a', '192.168.10.31'), ('b', '192.168.10.32'))}
        ready = release_inventory.content_status(self.show, self.dirs, self.bundle, self.apk,
                                                 devices, now=100)
        self.assertTrue(ready['ready'])
        devices['duplicate'] = dict(devices['a'])
        self.assertFalse(release_inventory.content_status(self.show, self.dirs, self.bundle,
                                                         self.apk, devices, now=100)['ready'])
        del devices['duplicate']
        devices['a']['buildGuid'] = 'other'
        stale = release_inventory.content_status(self.show, self.dirs, self.bundle, self.apk,
                                                 devices, now=100)
        self.assertFalse(stale['ready'])
        self.assertEqual(stale['quests'][0]['status'], 'error')
        with open(os.path.join(self.bundle, 'assets', 'm.png'), 'wb') as stream:
            stream.write(b'changed')
        self.assertIsNone(release_inventory.verify_bundle(self.bundle))


if __name__ == '__main__':
    unittest.main()
