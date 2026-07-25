"""capture-server.py の焼き込み走査（cue 参照 / 演出素材）を stdlib unittest で固定する。

実行: python -m unittest discover -s tools/web-compositor -p "test_*.py"

なぜテストするか: v3（takes[].steps[]）を走査し漏れると「現地 PC 不在の APK で演出の映像だけ出ない」
という、卓では気づけない事故になる（Web プレビューは実 URL で動くため）。
"""
import importlib.util
import os
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))


def _load_server_module():
    """ハイフン入りファイル名なので importlib で読む（__main__ ガードがあるのでサーバは起動しない）。"""
    path = os.path.join(_HERE, 'capture-server.py')
    spec = importlib.util.spec_from_file_location('capture_server', path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


_srv = _load_server_module()
Handler = _srv.Handler


def _v3_show():
    return {
        'cues': [{'id': 'cg_doll_B'}, {'id': 'unused_cue'}],
        'timeline': {
            'rev': 1, 'schema': 3,
            'segments': [{
                'lap': 3, 'camera': 1,
                'takes': [{
                    'id': 'L3C1#0',
                    'steps': [
                        {'source': 'live', 'camera': 3, 'assetUrl': ''},
                        {'source': 'clip', 'assetUrl': '/captures/pre_01.mp4'},
                        {'source': 'clip', 'assetUrl': '/recordings/rec_lap1_B.mp4', 'cueId': 'cg_doll_B'},
                    ],
                }],
            }],
        },
        'control': {},
    }


class ReferencedCueIdsTest(unittest.TestCase):
    def test_v3_take_step_cue_is_referenced(self):
        ids = Handler._referenced_cue_ids(_v3_show())
        self.assertIn('cg_doll_B', ids, 'takes[].steps[].cueId を走査していない（dangling 誤検出になる）')
        self.assertNotIn('unused_cue', ids)

    def test_v2_keys_still_referenced(self):
        show = {
            'timeline': {'segments': [{
                'lap': 1, 'camera': 0,
                'cues': [{'cueId': 'legacy_cue'}],
                'hasInsert': True, 'insert': {'cueId': 'legacy_insert'},
            }]},
            'schedule': {'entries': [{'cueId': 'sched_cue'}]},
            'control': {'activeCue': 'live_cue'},
        }
        ids = Handler._referenced_cue_ids(show)
        self.assertEqual(ids, {'legacy_cue', 'legacy_insert', 'sched_cue', 'live_cue'})

    def test_insert_without_present_flag_is_ignored(self):
        show = {'timeline': {'segments': [{
            'lap': 1, 'camera': 0, 'hasInsert': False, 'insert': {'cueId': 'ghost'},
        }]}, 'control': {}}
        self.assertEqual(Handler._referenced_cue_ids(show), set())

    def test_empty_show_is_safe(self):
        self.assertEqual(Handler._referenced_cue_ids({}), set())


class StepAssetSlotsTest(unittest.TestCase):
    def test_collects_only_steps_with_asset_url(self):
        slots = Handler._step_asset_slots(_v3_show())
        self.assertEqual([s['assetUrl'] for s in slots],
                         ['/captures/pre_01.mp4', '/recordings/rec_lap1_B.mp4'])

    def test_slots_are_live_references(self):
        # export-build は返ってきた dict をその場で書き換えるので、show 本体に反映される必要がある。
        show = _v3_show()
        for step in Handler._step_asset_slots(show):
            step['assetUrl'] = 'sa://assets/x.mp4'
        steps = show['timeline']['segments'][0]['takes'][0]['steps']
        self.assertEqual(steps[1]['assetUrl'], 'sa://assets/x.mp4')
        self.assertEqual(steps[2]['assetUrl'], 'sa://assets/x.mp4')
        self.assertEqual(steps[0]['assetUrl'], '', '空の assetUrl は触らない')

    def test_v2_only_show_has_no_slots(self):
        show = {'timeline': {'segments': [{'lap': 1, 'camera': 0, 'cues': [{'cueId': 'c'}]}]}}
        self.assertEqual(Handler._step_asset_slots(show), [])

    def test_empty_show_is_safe(self):
        self.assertEqual(Handler._step_asset_slots({}), [])


if __name__ == '__main__':
    unittest.main()
