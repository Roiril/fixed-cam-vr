"""Camera decode telemetry: exercise the real analyzer with positive and negative observations."""
import importlib.util
from pathlib import Path
import unittest


spec = importlib.util.spec_from_file_location("xp_analyzer", Path(__file__).with_name("analyze-xp-log.py"))
xp = importlib.util.module_from_spec(spec)
spec.loader.exec_module(xp)


class DecodeLogTests(unittest.TestCase):
    def levels(self, *cams):
        events = [dict(ev="sum", t=str(i), _t=float(i), _cams=[dict(cam, _idx="0", con="1")]) for i, cam in enumerate(cams)]
        exp = xp.expected_from_show({"cameras": [{"host": "192.0.2.1"}]})
        _, verdicts = xp.analyze(events, [], exp)
        return [(level, text) for level, text in verdicts if "展開" in text]

    def test_frames_arriving_and_decoding_is_quiet(self):
        self.assertEqual(self.levels(*[dict(rx="30", dfps="30", dfail="0")] * 5), [])

    def test_throttled_background_camera_is_quiet(self):
        self.assertEqual(self.levels(*[dict(rx="30", dfps="3", dfail="0")] * 5), [])

    def test_frames_arriving_but_never_decoding_fails(self):
        found = self.levels(*[dict(rx="30", dfps="0", dfail="0")] * 5)
        self.assertEqual([level for level, _ in found], ["FAIL"])

    def test_a_brief_gap_does_not_fail(self):
        cams = [dict(rx="30", dfps="0", dfail="0")] * 2 + [dict(rx="30", dfps="30", dfail="0")] * 3
        self.assertEqual(self.levels(*cams), [])

    def test_no_frames_arriving_is_not_a_decode_problem(self):
        self.assertEqual(self.levels(*[dict(rx="0", dfps="0", dfail="0")] * 5), [])

    def test_decode_failures_warn(self):
        found = self.levels(*[dict(rx="30", dfps="30", dfail="3")] * 5)
        self.assertEqual([level for level, _ in found], ["WARN"])

    def test_old_log_without_the_fields_gets_no_verdict(self):
        self.assertEqual(self.levels(*[dict(rx="30")] * 5), [])


if __name__ == "__main__":
    unittest.main()
