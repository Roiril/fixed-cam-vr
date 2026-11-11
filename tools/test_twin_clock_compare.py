"""tools/twin-clock-compare.py の突き合わせ（固定刻みの校正）。Unity を起動しない。"""

import importlib.util
import sys
import unittest
from pathlib import Path

TOOLS = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("twin_clock_compare", TOOLS / "twin-clock-compare.py")
cc = importlib.util.module_from_spec(spec)
sys.modules["twin_clock_compare"] = cc
spec.loader.exec_module(cc)


def run(offset: float = 0.0, stretch: float = 1.0, drop: int = 0) -> list[dict]:
    ev = [{"t": i * 2.0 * stretch + offset, "ev": "take", "text": f"id=L{i // 10 + 1}C{i % 3}#0 st=begin sec={i * 0.37:.2f}"}
          for i in range(100)]
    return ev[drop:]


class Compare(unittest.TestCase):
    def test_same_run_passes(self):
        r = cc.compare(run(), run())
        self.assertEqual(r["match"], 1.0)
        self.assertEqual(cc.verdict(r), [])

    def test_a_constant_offset_is_not_a_clock_error(self):
        # t の 0 点は録画の開始で、走行ごとに 1 秒弱動く。全体が同じだけずれるのは時計の狂いではない。
        r = cc.compare(run(), run(offset=0.6))
        self.assertAlmostEqual(r["offset"], 0.6, places=3)
        self.assertEqual(cc.verdict(r), [])

    def test_a_stretched_clock_fails(self):
        # 片方の時計だけ 1 割速い（時計の系統が混ざった）と、後ろへ行くほど差が開く。
        bad = cc.verdict(cc.compare(run(), run(stretch=1.1)))
        self.assertTrue(any("95%" in b for b in bad), bad)

    def test_a_huge_offset_fails(self):
        self.assertTrue(any("全体のずれ" in b for b in cc.verdict(cc.compare(run(), run(offset=5.0)))))

    def test_missing_events_fail(self):
        bad = cc.verdict(cc.compare(run(), run(drop=20)))
        self.assertTrue(any("一致率" in b for b in bad), bad)

    def test_numbers_in_the_text_do_not_break_the_match(self):
        a, b = run(), run()
        for e in b:
            e["text"] = e["text"].replace("sec=", "sec=9")
        self.assertEqual(cc.compare(a, b)["match"], 1.0)

    def test_wall_clock_only_rows_are_left_out(self):
        d = [{"t": 0.0, "ev": "clock", "text": "mode=fast"}, {"t": 1.0, "ev": "sum", "text": "fps=90"}]
        self.assertTrue(all(e["ev"] not in cc.SKIP_EV for e in run()))
        self.assertEqual([e for e in d if e["ev"] not in cc.SKIP_EV], [])


if __name__ == "__main__":
    unittest.main()
