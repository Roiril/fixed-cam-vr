"""tools/twin-verify.py の判定（周の印・結末）。Unity を起動しない。"""

import importlib.util
import sys
import tempfile
import unittest
from pathlib import Path

TOOLS = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("twin_verify", TOOLS / "twin-verify.py")
tv = importlib.util.module_from_spec(spec)
sys.modules["twin_verify"] = tv
spec.loader.exec_module(tv)


def lap(shown: int, src: str, seg: str) -> dict:
    return {"t": 0.0, "ev": "twinlap", "text": f"shown={shown} src={src} plap=3 seg={seg}"}


GOOD = [lap(2, "rec", "3/0"), lap(3, "live", "3/0"), lap(1, "rec", "3/1"), lap(1, "rec", "3/2")]


class TwinlapCheck(unittest.TestCase):
    def test_expected_laps_pass(self):
        self.assertEqual(tv.twinlap_check(GOOD)[0], "OK")

    def test_tail_of_the_next_lap_is_allowed(self):
        # 録画は区間を出たあとも少し回るので、カットの後ろに次の周の印が映る（実走行で確認）。
        self.assertEqual(tv.twinlap_check(GOOD + [lap(2, "rec", "3/2")])[0], "OK")

    def test_wrong_lap_at_the_head_fails(self):
        level, text = tv.twinlap_check([lap(1, "rec", "3/0"), lap(1, "rec", "3/1"), lap(1, "rec", "3/2")])
        self.assertEqual(level, "FAIL")
        self.assertIn("3-A", text)

    def test_head_correct_but_jumping_two_laps_fails(self):
        self.assertEqual(tv.twinlap_check(GOOD + [lap(3, "rec", "3/2")])[0], "FAIL")

    def test_live_badge_does_not_count_as_recording(self):
        self.assertEqual(tv.twinlap_check([lap(3, "live", "3/0")])[0], "WARN")

    def test_no_badge_is_not_a_pass(self):
        self.assertEqual(tv.twinlap_check([])[0], "WARN")


class EndingChecks(unittest.TestCase):
    def check(self, text: str, calib: str = "twin", nomark: bool = False):
        with tempfile.TemporaryDirectory() as d:
            log = Path(d) / "run_xp.log"
            log.write_text(text, encoding="utf-8")
            return tv.ending_checks(log, calib, nomark)

    def test_released_with_the_requested_calib_passes(self):
        got = self.check("[XP] t=1 ev=config a=1 calib=twin\n"
                         "[XP] t=1.5 ev=farewell phase=turn cg=1 shot=1 level=1 held=4.2\n"
                         "[XP] t=2 ev=outro stage=Report x=1 ending=Released\n")
        self.assertEqual([x[0] for x in got], ["OK", "OK"])

    def test_released_without_the_photo_is_named(self):
        got = self.check("[XP] t=1 ev=config calib=twin\n"
                         "[XP] t=1.5 ev=farewell phase=turn cg=1 shot=0 level=2 held=15.0\n"
                         "[XP] t=2 ev=outro stage=Report ending=Released\n")
        self.assertEqual(got[-1][0], "FAIL")
        self.assertIn("写真", got[-1][1])

    def test_other_calib_is_reported(self):
        got = self.check("[XP] t=1 ev=config calib=show\n[XP] t=2 ev=outro stage=Report ending=Released\n")
        self.assertEqual(got[0][0], "FAIL")
        self.assertIn("calib=twin", got[0][1])

    def test_interrupted_fails_when_a_report_was_made(self):
        got = self.check("[XP] t=1 ev=config calib=twin\n[XP] t=2 ev=outro stage=Report ending=Interrupted\n")
        self.assertEqual(got[0][0], "FAIL")

    def test_nomark_expects_trapped(self):
        got = self.check("[XP] t=1 ev=config calib=twin\n[XP] t=2 ev=outro stage=Report ending=Trapped\n", nomark=True)
        self.assertEqual(got[0][0], "OK")

    def test_unsolved_farewell_is_named(self):
        got = self.check("[XP] t=1 ev=config calib=twin\n"
                         "[XP] t=2 ev=outro stage=Report ending=Interrupted\n"
                         "[EndingFarewell] placement unavailable visitors=325 dolls=6\n")
        self.assertTrue(any("カメラ A" in x[1] for x in got))


class ReadEvents(unittest.TestCase):
    def test_time_starts_at_recording_head_and_samples_are_dropped(self):
        with tempfile.TemporaryDirectory() as d:
            log = Path(d) / "run_xp.log"
            log.write_text("[XP] t=10.00 ev=xprec state=start\n"
                           "[XP] t=11.00 ev=sum fps=90\n"
                           "[XP] t=12.50 ev=mark n=1\n", encoding="utf-8")
            self.assertEqual(tv.read_events(log), [{"t": 2.5, "ev": "mark", "text": "n=1"}])


if __name__ == "__main__":
    unittest.main()
