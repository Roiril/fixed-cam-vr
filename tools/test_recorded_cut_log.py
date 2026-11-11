"""3 周目の録画カット（歩く速さで切り上げ）と、その後の人形の判定を、人工イベントで校正する。

速く歩く人は録画が終わる前に区間を出て、録画の後の人形（plate のカット）を見ずに畳まれていた。
実機は ev=step に auth / cut / d2 を足して切り上げを報告する。人形が出なかった走行は WARN（FAIL にしない）。
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

MISSING = "録画の後の人形"
SHOWN_SUFFIX = "が出た"
NOT_SHOWN = "1 度も出なかった"


def rec_then_plate():
    return [
        {"source": "rec", "durKind": "untilClipEnd", "camera": 1, "recLap": 1},
        {"source": "plate", "durKind": "untilZoneChange", "transition": "dip"},
    ]


def show(takes):
    return {"timeline": {"segments": [
        {"lap": lap, "camera": cam, "takes": [{"id": tid, "steps": steps}]}
        for tid, lap, cam, steps in takes
    ]}}


class RecordedCutShapeTests(unittest.TestCase):
    def test_lap3_rec_followed_by_plate_is_a_target(self):
        self.assertTrue(xp.is_recorded_cut_before_doll(rec_then_plate(), 0, 3))

    def test_laps_1_and_2_are_not_targets(self):
        self.assertFalse(xp.is_recorded_cut_before_doll(rec_then_plate(), 0, 2))
        self.assertFalse(xp.is_recorded_cut_before_doll(rec_then_plate(), 0, 1))

    def test_3a_line_driven_recording_is_not_a_target(self):
        steps = [
            {"source": "rec", "durKind": "untilLine", "recFollow": True, "lineId": "line_center_A"},
            {"source": "rec", "durKind": "untilClipEnd", "recContinue": True},
            {"source": "plate", "durKind": "untilZoneChange"},
        ]
        for i in range(len(steps)):
            self.assertFalse(xp.is_recorded_cut_before_doll(steps, i, 3), i)

    def test_the_plate_cut_itself_and_rec_without_plate_are_not_targets(self):
        self.assertFalse(xp.is_recorded_cut_before_doll(rec_then_plate(), 1, 3))
        self.assertFalse(xp.is_recorded_cut_before_doll(
            [{"source": "rec", "durKind": "untilClipEnd"}, {"source": "live", "durKind": "sec"}], 0, 3))


class RecordedCutLogTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        # show.json は git 管理外。3-B / 3-C と同じ形の台本を手で組む（判定は id の文字列に依らない）。
        cls.exp = xp.expected_from_show(show([
            ("B3", 3, 1, rec_then_plate()),
            ("C3", 3, 2, rec_then_plate()),
            ("B2", 2, 1, rec_then_plate()),
        ]))

    def verdicts(self, events, exp=None):
        return xp.analyze(events, [], exp if exp is not None else self.exp)[1]

    def about(self, events, tid, exp=None):
        return [(lv, tx) for lv, tx in self.verdicts(events, exp) if tx.startswith(f"{tid}: ") and MISSING in tx]

    @staticmethod
    def step(tid, i, src, played, extra=None):
        e = {"ev": "step", "t": "1", "take": tid, "i": str(i), "src": src, "cam": "1",
             "played": "1" if played else "0", "why": "ok" if played else "norec"}
        e.update(extra or {})
        return e

    def test_fast_walker_who_never_saw_the_doll_is_warned_not_failed(self):
        events = [self.step("B3", 0, "rec", True, {"auth": "8.00", "cut": "3.00", "d2": "4.10"})]
        got = self.about(events, "B3")
        self.assertEqual([lv for lv, _ in got], ["WARN"], got)
        self.assertIn(NOT_SHOWN, got[0][1])
        self.assertFalse(any(lv == "FAIL" and MISSING in tx for lv, tx in self.verdicts(events)))

    def test_doll_shown_after_the_trimmed_recording_is_ok(self):
        events = [
            self.step("C3", 0, "rec", True, {"auth": "8.00", "cut": "5.20", "d2": "7.70"}),
            self.step("C3", 1, "plate", True),
        ]
        got = self.about(events, "C3")
        self.assertEqual([lv for lv, _ in got], ["OK"], got)
        self.assertTrue(got[0][1].endswith(SHOWN_SUFFIX), got)

    def test_report_lists_authored_and_actual_length(self):
        events = [
            self.step("B3", 0, "rec", True, {"auth": "8.00", "cut": "5.20", "d2": "7.70"}),
            self.step("B3", 1, "plate", True),
        ]
        report = "\n".join(xp.analyze(events, [], self.exp)[0])
        self.assertIn("著作 8.00s → 実際 5.20s", report)
        self.assertIn("切り上げた", report)
        self.assertIn("2 周目の滞在 7.70s", report)

    def test_slow_walker_and_unknown_dwell_keep_the_authored_length(self):
        events = [
            self.step("B3", 0, "rec", True, {"auth": "8.00", "cut": "8.00", "d2": "-1.00"}),
            self.step("B3", 1, "plate", True),
        ]
        report = "\n".join(xp.analyze(events, [], self.exp)[0])
        self.assertIn("著作どおり", report)
        self.assertIn("2 周目の滞在 不明", report)

    def test_take_that_never_ran_is_not_judged(self):
        self.assertEqual(self.about([], "C3"), [])

    def test_lap2_rec_then_plate_is_not_judged(self):
        events = [self.step("B2", 0, "rec", True)]
        self.assertEqual(self.about(events, "B2"), [])

    def test_without_show_json_the_target_comes_from_auth_in_the_log(self):
        events = [self.step("B3", 0, "rec", True, {"auth": "8.00", "cut": "3.00", "d2": "4.10"})]
        got = self.about(events, "B3", exp=xp.expected_from_show({}))
        self.assertEqual([lv for lv, _ in got], ["WARN"], got)


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    unittest.main()
