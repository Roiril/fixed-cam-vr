"""焼き込み（export_build.py）の書き出しを stdlib unittest で固定する。

実行: py -3.11 -m unittest discover -s tools/web-compositor -p "test_*.py"

走査の網羅は `test_export_refs.py` が持っている。ここで固定するのは**書き出した結果**:
URL が `sa://` に変わったか / 実ファイルが assets/ へ入ったか / 前回の置き土産が消えたか /
解決できない URL を素通ししていないか。

なぜテストするか: 2026-09-03 に焼き込みを `tools/unity.ps1 build fixedcam` の自動工程にした。
**押し忘れないぶん、間違ったまま毎回焼かれる**ようになったので、書き出しの形はここで留める。
"""
import importlib.util
import io
import json
import os
import shutil
import tempfile
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))


def _load_export_module():
    path = os.path.join(_HERE, 'export_build.py')
    spec = importlib.util.spec_from_file_location('export_build_undertest', path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


_MOD = _load_export_module()


class ExportBuildTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix='exportbuild_')
        self.addCleanup(shutil.rmtree, self.tmp, ignore_errors=True)
        self.src = os.path.join(self.tmp, 'compositor')
        self.out = os.path.join(self.tmp, 'baked')
        for d in ('masks', 'captures', 'recordings', 'static-inputs', 'audio', 'testassets'):
            os.makedirs(os.path.join(self.src, d))
        os.makedirs(os.path.join(self.src, 'eyejack', 'norm'))
        self.dirs = _MOD.local_url_dirs(self.src)

    def _put(self, rel, data=b'x'):
        p = os.path.join(self.src, rel)
        with open(p, 'wb') as f:
            f.write(data)
        return p

    def _run(self, show):
        return _MOD.export_build(show, self.tmp, self.dirs, out_dir=self.out)

    def _baked(self):
        with io.open(os.path.join(self.out, 'show.json'), encoding='utf-8') as f:
            return json.load(f)

    # --- 書き換えとコピー ---

    def test_local_urls_become_sa_and_files_are_copied(self):
        self._put('masks/m.png')
        self._put('captures/c.mp4')
        res = self._run({'cues': [{'id': 'a', 'maskUrl': '/masks/m.png', 'sourceUrl': '/captures/c.mp4'}]})
        cue = self._baked()['cues'][0]
        self.assertEqual(cue['maskUrl'], 'sa://assets/m.png')
        self.assertEqual(cue['sourceUrl'], 'sa://assets/c.mp4')
        self.assertTrue(os.path.isfile(os.path.join(self.out, 'assets', 'm.png')))
        self.assertEqual(res['count'], 2)

    def test_external_urls_are_rejected(self):
        show = {'cues': [{'id': 'a', 'sourceUrl': 'http://example.com/x.mp4'},
                         {'id': 'b', 'sourceUrl': 'sa://assets/already.png'}]}
        with self.assertRaises(ValueError):
            self._run(show)
        self.assertFalse(os.path.exists(self.out))

    def test_bgm_and_step_assets_and_eyejack_are_baked(self):
        """cue 以外の 3 経路。どれかを歩き漏らすと「そこだけ無映像 / 無音」になる。"""
        self._put('audio/b.mp3')
        self._put('recordings/pov.mp4')
        self._put('eyejack/norm/p.jpg')
        show = {
            'bgmTracks': [{'id': 't', 'url': '/audio/b.mp3'}],
            'bgm': {'action': 'play', 'trackId': 't'},
            'eyejack': {'photos': ['/eyejack/norm/p.jpg']},
            'timeline': {'segments': [{'takes': [{'steps': [{'assetUrl': '/recordings/pov.mp4'}]}]}]},
        }
        self._run(show)
        b = self._baked()
        self.assertEqual(b['bgmTracks'][0]['url'], 'sa://assets/b.mp3')
        self.assertEqual(b['eyejack']['photos'], ['sa://assets/p.jpg'])
        self.assertEqual(b['timeline']['segments'][0]['takes'][0]['steps'][0]['assetUrl'],
                         'sa://assets/pov.mp4')

    def test_missing_file_preserves_previous_export(self):
        self._run({'cues': []})
        with open(os.path.join(self.out, 'manifest.json'), 'rb') as stream:
            previous = stream.read()
        with self.assertRaises(ValueError):
            self._run({'cues': [{'id': 'a', 'sourceUrl': '/captures/gone.mp4'}]})
        with open(os.path.join(self.out, 'manifest.json'), 'rb') as stream:
            self.assertEqual(stream.read(), previous)

    def test_take_and_step_only_music_is_baked_and_missing_track_rejected(self):
        self._put('audio/take.mp3', b'take music')
        self._put('audio/step.mp3', b'step music')
        show = {'bgmTracks': [{'id': 'take', 'url': '/audio/take.mp3'},
                              {'id': 'step', 'url': '/audio/step.mp3'}],
                'timeline': {'schema': 3, 'segments': [{'takes': [
                    {'hasBgm': True, 'bgm': {'action': 'play', 'trackId': 'take'},
                     'steps': [{'hasBgm': True, 'bgm': {'action': 'play', 'trackId': 'step'}}]}]}]}}
        self.assertEqual(self._run(show)['count'], 2)
        self.assertEqual({t['url'] for t in self._baked()['bgmTracks']},
                         {'sa://assets/take.mp3', 'sa://assets/step.mp3'})
        show['bgmTracks'].pop()
        with self.assertRaisesRegex(ValueError, 'step'):
            self._run(show)

    def test_same_file_is_copied_once_and_different_files_do_not_collide(self):
        self._put('masks/same.png')
        self._put('captures/dup.png', b'aa')
        self._put('recordings/dup.png', b'bbb')
        res = self._run({'cues': [
            {'id': 'a', 'maskUrl': '/masks/same.png', 'sourceUrl': '/masks/same.png'},
            {'id': 'b', 'sourceUrl': '/captures/dup.png'},
            {'id': 'c', 'sourceUrl': '/recordings/dup.png'},
        ]})
        self.assertEqual(res['count'], 3, '同一ファイルは 1 回だけコピーする')
        cues = self._baked()['cues']
        self.assertEqual(cues[1]['sourceUrl'], 'sa://assets/dup.png')
        self.assertEqual(cues[2]['sourceUrl'], 'sa://assets/dup_1.png', '同名の別ファイルは連番で分ける')

    def test_previous_export_is_swept(self):
        """置き土産を残すと APK が太り続け、消した素材が生き残る。"""
        os.makedirs(os.path.join(self.out, 'assets'))
        orphan = os.path.join(self.out, 'assets', 'old.png')
        with open(orphan, 'wb') as f:
            f.write(b'x')
        self._run({'cues': []})
        self.assertFalse(os.path.exists(orphan))

    def test_traversal_is_refused(self):
        secret = os.path.join(self.tmp, 'secret.txt')
        with open(secret, 'wb') as f:
            f.write(b'x')
        with self.assertRaises(ValueError):
            self._run({'cues': [{'id': 'a', 'sourceUrl': '/masks/../../secret.txt'}]})
        self.assertFalse(os.path.exists(os.path.join(self.out, 'assets', 'secret.txt')))

    # --- 報告 ---

    def test_dangling_cue_and_track_references_are_rejected(self):
        show = {
            'cues': [{'id': 'known'}],
            'bgmTracks': [{'id': 'bgm_known'}],
            'timeline': {'segments': [
                {'takes': [{'steps': [{'cueId': 'known'}, {'cueId': 'gone'}]}],
                 'hasBgm': True, 'bgm': {'action': 'play', 'trackId': 'bgm_gone'}},
            ]},
        }
        with self.assertRaisesRegex(ValueError, 'gone'):
            self._run(show)

    def test_manifest_hashes_and_revision_only_change(self):
        self._put('masks/m.png', b'asset')
        show = {'rev': 1, 'cues': [{'id': 'a', 'maskUrl': '/masks/m.png'}]}
        first = self._run(show)['contentId']
        with open(os.path.join(self.out, 'manifest.json'), encoding='utf-8') as stream:
            manifest = json.load(stream)
        self.assertEqual(manifest['schema'], 1)
        self.assertEqual(manifest['policy'], 'baked-only-v1')
        self.assertEqual(len(manifest['assets']), 1)
        self.assertEqual(first, self._run({'rev': 2, 'cues': show['cues']})['contentId'])

    def test_revs_are_reported_for_the_build_log(self):
        """どの著作を焼いたかは rev でしか分からない（CLI がこれを出す）。"""
        res = self._run({'rev': 1044, 'timeline': {'rev': 44, 'segments': []}})
        self.assertEqual((res['showRev'], res['timelineRev']), (1044, 44))

    def test_written_json_is_utf8_lf(self):
        """Unity の JsonUtility が読む契約。CRLF で書くと Windows の Python は黙って混ぜる。"""
        self._run({'cues': [{'id': 'あ'}]})
        with io.open(os.path.join(self.out, 'show.json'), 'rb') as f:
            raw = f.read()
        self.assertNotIn(b'\r\n', raw)
        self.assertIn('あ'.encode('utf-8'), raw)


if __name__ == '__main__':
    unittest.main()
