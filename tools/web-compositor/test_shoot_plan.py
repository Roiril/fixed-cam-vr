"""撮影の指示（GET /shoot/plan）が配る判定を stdlib unittest で固定する。

実行: py -3.11 -m unittest discover -s tools/web-compositor -p "test_*.py"

なぜテストするか: この判定は **配信スマホの画面にそのまま出る**。
撮る人はここに書かれた「頭 N 秒」と「✅/⚠/—」だけを見て、撮り直すかを決める。
黙って古い答えを配ると、現場では「撮ったのに実機で出ない」としてしか現れない。

⚠ `neededHeadSec` / `cutCount` は shoot-model.js（ブラウザ）からの移植。
  **同じフィクスチャ（shoot-fixture.json）を node のテストも食う**ので、
  片方だけ直せばどちらかが落ちる。
"""
import importlib.util
import json
import os
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))


def _load_server_module():
    path = os.path.join(_HERE, 'capture-server.py')
    spec = importlib.util.spec_from_file_location('capture_server', path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


_srv = _load_server_module()

with open(os.path.join(_HERE, 'shoot-fixture.json'), encoding='utf-8') as _f:
    FIXTURE = json.load(_f)


class NeededHeadSecTest(unittest.TestCase):
    """ブラウザ側（shoot-model.js）と同じ答えを出す。"""

    def test_matches_shared_fixture(self):
        segs = FIXTURE['segments']
        for cue_id, want in FIXTURE['expect']['neededHeadSec'].items():
            self.assertEqual(_srv.needed_head_sec(segs, cue_id), want,
                             f'needed_head_sec({cue_id})')
        for cue_id, want in FIXTURE['expect']['cutCount'].items():
            self.assertEqual(_srv.cut_count(segs, cue_id), want, f'cut_count({cue_id})')

    def test_empty_and_missing(self):
        self.assertIsNone(_srv.needed_head_sec([], 'pov_1'))
        self.assertIsNone(_srv.needed_head_sec(None, 'pov_1'))
        self.assertIsNone(_srv.needed_head_sec(FIXTURE['segments'], ''))
        self.assertEqual(_srv.cut_count(None, 'pov_1'), 0)

    def test_trim_start_negative_means_inherit_not_negative_offset(self):
        """trimStartSec の -1 は「cue から継承」。**負の頭合わせではない**ので 0 として数える。"""
        segs = [{'takes': [{'steps': [
            {'cueId': 'x', 'trimStartSec': -1, 'durSec': 2.0},
        ]}]}]
        self.assertEqual(_srv.needed_head_sec(segs, 'x'), 2.0)


class ShotStatusTest(unittest.TestCase):
    """スマホに出る ✅ / ⚠ / — の中身。"""

    def test_no_cue(self):
        level, reason, _ = _srv.shot_status(None, 1.0, None)
        self.assertEqual(level, 'ng')
        self.assertIn('show.json に無い', reason)

    def test_not_adopted(self):
        level, reason, fix = _srv.shot_status({'id': 'pov_1', 'sourceUrl': ''}, 1.0, None)
        self.assertEqual(level, 'ng')
        self.assertEqual(reason, '素材が未採用')
        self.assertIn('卓へ送って採用', fix)

    def test_too_short_is_ng(self):
        cue = {'id': 'pov_4', 'sourceUrl': '/recordings/a.mp4'}
        level, reason, fix = _srv.shot_status(cue, 1.4, 1.0)
        self.assertEqual(level, 'ng')
        self.assertIn('0.4s 足りない', reason)
        self.assertIn('1.4s', fix)

    def test_thin_margin_is_warn(self):
        cue = {'id': 'pov_4', 'sourceUrl': '/recordings/a.mp4'}
        level, reason, _ = _srv.shot_status(cue, 1.4, 1.6)
        self.assertEqual(level, 'warn')
        self.assertIn('余裕が 0.2s', reason)

    def test_enough_is_ok(self):
        cue = {'id': 'pov_4', 'sourceUrl': '/recordings/a.mp4'}
        self.assertEqual(_srv.shot_status(cue, 1.4, 3.1)[0], 'ok')

    def test_unknown_duration_does_not_fail(self):
        """尺が分からないテイク（台帳に無い）で ❌ にしない。判定できないだけ。"""
        cue = {'id': 'pov_4', 'sourceUrl': '/recordings/a.mp4'}
        self.assertEqual(_srv.shot_status(cue, 1.4, None)[0], 'ok')
        self.assertEqual(_srv.shot_status(cue, 1.4, 0)[0], 'ok')

    def test_margin_boundary_is_ok(self):
        """余裕がちょうど閾値なら ok（境界で赤くしない）。"""
        cue = {'id': 'x', 'sourceUrl': '/recordings/a.mp4'}
        self.assertEqual(_srv.shot_status(cue, 1.0, 1.0 + _srv.SHORT_MARGIN_SEC)[0], 'ok')


class ShotsDefTest(unittest.TestCase):
    """shots.json（3 者が読む単一の正）が Python からも同じ形で読める。"""

    def test_reads_shots_json(self):
        shots = _srv._shots_def()
        self.assertTrue(shots, 'shots.json が読めていない')
        for s in shots:
            self.assertTrue(s.get('cueId'))
            self.assertTrue(s.get('label'), f"{s.get('cueId')} に label（スマホに出る）")
            self.assertTrue(s.get('hint'), f"{s.get('cueId')} に hint（スマホに出る撮影指示）")
            self.assertIn(s.get('dev'), ('pov', 'cam'))

    def test_shots_json_matches_browser_side(self):
        """ブラウザ側の定義（shoot-model.js が読む同じファイル）と同一実体であること。"""
        with open(os.path.join(_HERE, 'shots.json'), encoding='utf-8') as f:
            raw = json.load(f)
        self.assertEqual([s['cueId'] for s in _srv._shots_def()],
                         [s['cueId'] for s in raw['shots']])


class PlanPayloadContractTest(unittest.TestCase):
    """
    配信スマホ（`ShootPlan.kt`）が読む欄を固定する。

    ⚠ **ここは 2 つの言語にまたがる契約**。Python 側で欄の名前を変えると、
      スマホは黙って既定値（`status:"ng"` / `needSec:0`）を読み、
      現場では「なぜか全部 — のまま」「短いのに緑」としてしか現れない。
    """

    # ShootPlan.kt の Shot が読む欄。増やすときは向こうも同時に直す。
    SHOT_KEYS = {
        'cueId', 'label', 'hint', 'recSec', 'countdownSec', 'dev', 'forThisDevice',
        'needSec', 'cuts', 'status', 'reason', 'fix', 'adoptedName', 'collected',
    }

    def _plan(self, cam='A'):
        handler = _srv.Handler.__new__(_srv.Handler)   # __init__（ソケット）を通さない
        return _srv.Handler._shoot_plan(handler, cam)

    def test_plan_shape(self):
        plan = self._plan()
        self.assertTrue(plan['ok'])
        for key in ('desk', 'rev', 'nowIso', 'shots'):
            self.assertIn(key, plan)
        self.assertTrue(plan['shots'])
        for shot in plan['shots']:
            self.assertEqual(self.SHOT_KEYS, set(shot.keys()))
            self.assertIn(shot['status'], ('ok', 'warn', 'ng'))
            self.assertIsInstance(shot['collected'], list)
            self.assertTrue(shot['needSec'] is None or isinstance(shot['needSec'], (int, float)))

    def test_needSec_is_null_not_zero_when_unused(self):
        """台本が使っていないショットは **null**。0 にすると「0 秒でよい」に化ける。"""
        plan = self._plan()
        for shot in plan['shots']:
            if shot['cuts'] == 0:
                self.assertIsNone(shot['needSec'], shot['cueId'])

    def test_plan_matches_the_shots_file_order(self):
        self.assertEqual([s['cueId'] for s in self._plan()['shots']],
                         [s['cueId'] for s in _srv._shots_def()])


if __name__ == '__main__':
    unittest.main()
