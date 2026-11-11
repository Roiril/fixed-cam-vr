"""2-C の乗っ取りが出なかった走行の判定を、人工イベントで校正する。

乗っ取りが無いと顔の人形化と字化けもまとめて起きない。そのときは原因の 1 行だけを出し、
顔（_FaceMix）と字（ApplyCorruption）の FAIL で原因を取り違えさせない。
"""

import importlib.util
from pathlib import Path
import sys
import unittest


TOOLS = Path(__file__).resolve().parent
sys.path.insert(0, str(TOOLS))
spec = importlib.util.spec_from_file_location("analyze_xp_log", TOOLS / "analyze-xp-log.py")
xp = importlib.util.module_from_spec(spec)
spec.loader.exec_module(xp)

MISSING = "2-C の乗っ取りが出なかった"
FACE = "_FaceMix を書けていない疑い"
CX = "ApplyCorruption が文面へ届いていない疑い"


class TakeoverMissingLogTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        # show.json は git 管理外なので、ここでは空の台本から期待値を作る（判定は台本に依らない）。
        cls.exp = xp.expected_from_show({})

    def fails(self, events):
        return [text for level, text in xp.analyze(events, [], self.exp)[1] if level == "FAIL"]

    @staticmethod
    def run_events(plap3, takeover):
        events = [
            {"ev": "comms", "t": "1", "id": "Begin", "n": "1", "chars": "0"},
            {"ev": "seg", "t": "2", "lap": "2", "cam": "2", "plap": "2"},
            # 字の強さは上がったのに、顔は人形へ混ざらず、字も 1 つも化けていない。
            {"ev": "sum", "t": "3", "_cams": [], "commsFace": "1/2/0.80/0.00",
             "commsGl": "0.50", "commsCx": "0"},
        ]
        if takeover:
            events.append({"ev": "comms", "t": "4", "id": "Takeover", "n": "2", "chars": "0"})
        if plap3:
            events.append({"ev": "seg", "t": "5", "lap": "3", "cam": "0", "plap": "3"})
        events.append({"ev": "sum", "t": "6", "_cams": [], "commsFace": "1/2/0.80/0.00",
                       "commsGl": "0.50", "commsCx": "0"})
        return events

    def test_third_lap_without_takeover_fails_once_and_hides_face_and_text_fails(self):
        fails = self.fails(self.run_events(plap3=True, takeover=False))
        self.assertEqual(1, sum(MISSING in f for f in fails), fails)
        self.assertFalse(any(FACE in f for f in fails), fails)
        self.assertFalse(any(CX in f for f in fails), fails)

    def test_run_with_takeover_keeps_the_face_and_text_checks(self):
        fails = self.fails(self.run_events(plap3=True, takeover=True))
        self.assertFalse(any(MISSING in f for f in fails), fails)
        self.assertTrue(any(FACE in f for f in fails), fails)

    def test_run_that_never_reached_the_third_lap_is_not_judged_missing(self):
        fails = self.fails(self.run_events(plap3=False, takeover=False))
        self.assertFalse(any(MISSING in f for f in fails), fails)
        self.assertTrue(any(FACE in f for f in fails), fails)
        self.assertTrue(any(CX in f for f in fails), fails)

    def test_backtracked_segment_lap_still_counts_the_progress_lap(self):
        events = self.run_events(plap3=False, takeover=False)
        events.append({"ev": "seg", "t": "7", "lap": "2", "cam": "2", "plap": "3"})
        fails = self.fails(events)
        self.assertEqual(1, sum(MISSING in f for f in fails), fails)


if __name__ == "__main__":
    unittest.main()
