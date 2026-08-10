"""実測滞在時間（heartbeat dwell[] → 集計）の畳み方を stdlib unittest で固定する。

実行: py -3.11 -m unittest discover -s tools/web-compositor -p "test_*.py"

なぜテストするか: リボン UI の「実測 平均 Ns」は、演出の開始位置（進入 +20s）が
体験者の歩速に対して現実的かを作者に見せる唯一の材料。集計が壊れると
「山場が出ないのに気づけない」という本番事故に直結する（時計飛び等の異常値混入も含む）。
"""
import importlib.util
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


class DwellMergeTest(unittest.TestCase):
    def setUp(self):
        # 集計はメモリ上のみで検証する（tools/web-compositor/dwell_stats.json を汚さない）。
        _srv._dwell_stats['items'] = {}
        _srv._dwell_stats['updatedAt'] = 0
        self._saved = _srv._save_dwell
        _srv._save_dwell = lambda: None

    def tearDown(self):
        _srv._save_dwell = self._saved
        _srv._dwell_stats['items'] = {}

    def test_mean_min_max_and_count(self):
        _srv._merge_dwell([{'lap': 1, 'camera': 0, 'sec': 10.0},
                           {'lap': 1, 'camera': 0, 'sec': 6.0}])
        _srv._merge_dwell([{'lap': 1, 'camera': 0, 'sec': 8.0}])
        e = _srv._dwell_payload()['items']['1:0']
        self.assertEqual(e['n'], 3)
        self.assertAlmostEqual(e['meanSec'], 8.0, places=2)
        self.assertAlmostEqual(e['minSec'], 6.0, places=2)
        self.assertAlmostEqual(e['maxSec'], 10.0, places=2)
        self.assertAlmostEqual(e['lastSec'], 8.0, places=2)

    def test_segments_are_keyed_by_lap_and_camera(self):
        _srv._merge_dwell([{'lap': 1, 'camera': 0, 'sec': 5.0},
                           {'lap': 2, 'camera': 0, 'sec': 9.0}])
        items = _srv._dwell_payload()['items']
        self.assertEqual(sorted(items.keys()), ['1:0', '2:0'])

    def test_bad_values_are_dropped(self):
        added = _srv._merge_dwell([
            {'lap': 0, 'camera': 0, 'sec': 5.0},        # lap は 1 始まり
            {'lap': 1, 'camera': -1, 'sec': 5.0},       # カメラ未確定
            {'lap': 1, 'camera': 0, 'sec': 0.0},        # 0 秒
            {'lap': 1, 'camera': 0, 'sec': 100000.0},   # 時計飛び
            {'lap': 1, 'camera': 0, 'sec': 'x'},        # 型違い
            'nonsense',
            {'lap': 1, 'camera': 0, 'sec': 7.5},        # これだけ有効
        ])
        self.assertEqual(added, 1)
        self.assertEqual(_srv._dwell_payload()['items']['1:0']['n'], 1)

    def test_empty_or_missing_is_noop(self):
        self.assertEqual(_srv._merge_dwell(None), 0)
        self.assertEqual(_srv._merge_dwell([]), 0)
        self.assertEqual(_srv._dwell_payload()['items'], {})


if __name__ == '__main__':
    unittest.main()
