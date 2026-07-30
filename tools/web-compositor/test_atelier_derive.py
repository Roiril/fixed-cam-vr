"""素材台帳の「採用」を show.json から導出する部分を固定する。

実行: python -m unittest discover -s tools/web-compositor -p "test_*.py"

なぜテストするか: 2026-07-30 まで採用は手で押す 3 択の札（verdict）で、**押し忘れると台帳が
現実の逆を言った**。実際、本番に載っていた 2 枚は「未評価」のまま、台帳が持つ唯一の完成品は
どの cue からも参照されていなかった。工房はそれを「素材なし / 採用 0」と表示していた。
導出へ移したので、この対応がずれないことを機械で押さえる。
"""
import importlib.util
import os
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))


def _load_server_module():
    """ハイフン入りファイル名なので importlib で読む（__main__ ガードがあるのでサーバは起動しない）。"""
    path = os.path.join(_HERE, 'capture-server.py')
    spec = importlib.util.spec_from_file_location('capture_server_atelier', path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


_srv = _load_server_module()


def _atelier():
    return {
        'rev': 3,
        'recipes': [{'id': 'r1', 'name': '人影'}, {'id': 'r2', 'name': '無人の異変'}],
        'generations': [
            {'id': 'g1', 'cameraLabel': 'A', 'recipeId': 'r1',
             'outputUrl': '/captures/gen_monsterA_01.png', 'prompt': '人形'},
            {'id': 'g2', 'cameraLabel': 'C', 'recipeId': 'r1',
             'outputUrl': '/captures/gen_camC_doll.png', 'prompt': '人形 2'},
            {'id': 'g3', 'cameraLabel': 'C', 'recipeId': 'r2', 'prompt': 'まだ生成していない'},
        ],
    }


class AtelierDeriveTests(unittest.TestCase):
    def setUp(self):
        self._saved_show = _srv._show

    def tearDown(self):
        _srv._show = self._saved_show

    def test_採用は_cues_の_sourceUrl_から導出する(self):
        _srv._show = {'cues': [
            {'id': 'cue_ningyo_A', 'name': '人形', 'sourceUrl': '/captures/gen_monsterA_01.png'},
        ]}
        view = _srv._atelier_derive(_atelier())
        g = {x['id']: x for x in view['generations']}
        self.assertEqual([c['id'] for c in g['g1']['usedByCues']], ['cue_ningyo_A'])
        self.assertEqual(g['g2']['usedByCues'], [])   # 出力はあるが cue が指していない
        self.assertEqual(g['g3']['usedByCues'], [])   # まだ出力が無い

    def test_percent_encode_されていても同じ素材として突合する(self):
        _srv._show = {'cues': [
            {'id': 'c1', 'sourceUrl': '/captures/gen%5Fcam%43%5Fdoll.png'.replace('%5F', '_').replace('%43', 'C')},
        ]}
        # 実際に卓が書くのは空白や括弧のエンコード。空白入りのファイル名で確かめる。
        at = _atelier()
        at['generations'][0]['outputUrl'] = '/captures/gen A 01.png'
        _srv._show = {'cues': [{'id': 'c1', 'sourceUrl': '/captures/gen%20A%2001.png'}]}
        view = _srv._atelier_derive(at)
        g = {x['id']: x for x in view['generations']}
        self.assertEqual([c['id'] for c in g['g1']['usedByCues']], ['c1'])

    def test_レシピの採用率も導出値になる(self):
        _srv._show = {'cues': [
            {'id': 'c1', 'sourceUrl': '/captures/gen_monsterA_01.png'},
            {'id': 'c2', 'sourceUrl': '/captures/gen_camC_doll.png'},
        ]}
        view = _srv._atelier_derive(_atelier())
        r = {x['id']: x for x in view['recipes']}
        self.assertEqual((r['r1']['usedCount'], r['r1']['keptCount']), (2, 2))
        self.assertEqual((r['r2']['usedCount'], r['r2']['keptCount']), (1, 0))

    def test_台帳に無いまま使われている素材を列挙する(self):
        # これが「工房は素材なしと言うが本番では使っている」を検出する唯一の口。
        _srv._show = {'cues': [
            {'id': 'cue_hand_B', 'name': '手', 'sourceUrl': '/captures/gen_handB_01.png'},
            {'id': 'cue_ningyo_A', 'sourceUrl': '/captures/gen_monsterA_01.png'},
        ]}
        view = _srv._atelier_derive(_atelier())
        self.assertEqual([u['sourceUrl'] for u in view['unlogged']], ['/captures/gen_handB_01.png'])
        self.assertEqual([c['id'] for c in view['unlogged'][0]['cues']], ['cue_hand_B'])

    def test_導出値は元データを汚さない(self):
        _srv._show = {'cues': [{'id': 'c1', 'sourceUrl': '/captures/gen_monsterA_01.png'}]}
        at = _atelier()
        _srv._atelier_derive(at)
        self.assertNotIn('usedByCues', at['generations'][0])
        self.assertNotIn('usedCount', at['recipes'][0])

    def test_旧_verdict_は読まずに落とす(self):
        # 過去の atelier.json には手動の採否札が残っている。導出の答えを上書きさせない。
        _srv._show = {'cues': []}
        at = _atelier()
        at['generations'][0]['verdict'] = 'keep'      # 「採用」と書いてあるが cue は指していない
        view = _srv._atelier_derive(at)
        g = {x['id']: x for x in view['generations']}
        self.assertNotIn('verdict', g['g1'])
        self.assertEqual(g['g1']['usedByCues'], [])

    def test_動作確認用ダミーは台帳外として数えない(self):
        # testassets/ は「演出 A」の文字だけのダミー。作り方を記録する対象ではないので、
        # 混ぜると本物の取りこぼし（外部ツール製の素材）がノイズに埋もれる。
        _srv._show = {'cues': [
            {'id': 'cue_A', 'sourceUrl': '/testassets/test_clip_A.mp4'},
            {'id': 'cue_hand_B', 'sourceUrl': '/captures/gen_handB_01.png'},
        ]}
        view = _srv._atelier_derive(_atelier())
        self.assertEqual([u['sourceUrl'] for u in view['unlogged']], ['/captures/gen_handB_01.png'])

    def test_cues_が空でも落ちない(self):
        _srv._show = {}
        view = _srv._atelier_derive(_atelier())
        self.assertEqual(view['unlogged'], [])
        self.assertTrue(all(g['usedByCues'] == [] for g in view['generations']))


if __name__ == '__main__':
    unittest.main()
