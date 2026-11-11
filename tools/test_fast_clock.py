"""固定刻み（--fast）の走行で、実時間の量を測る判定を対象外にする（analyze-xp-log.py）。"""

import importlib.util
import sys
import unittest
from pathlib import Path

TOOLS = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("analyze_xp_log", TOOLS / "analyze-xp-log.py")
ax = importlib.util.module_from_spec(spec)
sys.modules["analyze_xp_log"] = ax
spec.loader.exec_module(ax)


class FastClock(unittest.TestCase):
    def test_the_clock_row_marks_a_fast_run(self):
        self.assertTrue(ax.is_fast_clock([{"ev": "clock", "mode": "fast", "step": "0.01111"}]))

    def test_a_real_run_and_an_old_log_are_not_fast(self):
        self.assertFalse(ax.is_fast_clock([{"ev": "clock", "mode": "real", "step": "0.00000"}]))
        self.assertFalse(ax.is_fast_clock([{"ev": "boot"}]))

    def test_wall_time_quantities_are_not_judged(self):
        for text in ("動画カットの停滞（最大 484ms）が最短カット 0.80s の 60% を食う",
                     "表示 fps が 72 を割った", "最悪フレーム時間 120ms"):
            self.assertTrue(ax.FAST_NOT_APPLICABLE.search(text), text)

    def test_what_the_show_did_is_still_judged(self):
        for text in ("結末が Trapped（想定は Released）", "1 周目 カメラ 0 の録画が 0 枚",
                     "録画カットが著作されているのに、録画が 1 度も再生されていない"):
            self.assertFalse(ax.FAST_NOT_APPLICABLE.search(text), text)


if __name__ == "__main__":
    unittest.main()
