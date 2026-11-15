"""増えるスクリーンへ配る静止画（scrSnap）と最後の面の笑う人形（scrFace）の、ログの書き手と読み手を突き合わせる
（canon/LEDGER.md 0384 ④⑤）。

- 書き手は C# の ShowTelemetryHost（ScreenSnapshotState / ScreenFaceState）
- 読み手は tools/analyze-xp-log.py の screen_wall_extra_findings

    py -3.11 tools/test_screen_wall_log.py
"""

import importlib.util
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
TOOLS = ROOT / "tools"

spec = importlib.util.spec_from_file_location("xp_analyzer", TOOLS / "analyze-xp-log.py")
xp = importlib.util.module_from_spec(spec)
spec.loader.exec_module(xp)


def read(rel):
    return (ROOT / rel).read_text(encoding="utf-8")


def sum_ev(t, **kw):
    return dict(ev="sum", t=str(t), _cams=[], **{k: str(v) for k, v in kw.items()})


def levels(found):
    return [lv for lv, _ in found]


class WriterMatchesReader(unittest.TestCase):
    def test_keys_are_written_by_the_telemetry(self):
        src = read("Assets/Scripts/Diagnostics/ShowTelemetryHost.cs")
        self.assertIn('" scrSnap="', src)
        self.assertIn('" scrFace="', src)

    def test_the_face_resource_name_matches(self):
        src = read("Assets/Scripts/Streaming/ScreenMultiply.cs")
        self.assertIn('"ScreenMultiply/DollLaughFace"', src)
        self.assertTrue((ROOT / "Assets/Resources/ScreenMultiply/DollLaughFace.png").exists())


class Findings(unittest.TestCase):
    def test_wall_with_snapshots_needs_no_delayed_capture(self):
        # 0384 ⑤: 静止画があるときは遅れた取り込みを回さないので、取り込み 0 回でも面は画を映している。
        self.assertEqual(xp.wall_snapshots([sum_ev(1, scrSnap="9/4/4/1/0")]), 9)
        self.assertEqual(xp.wall_snapshots([sum_ev(1, scrMul="1/3/0")]), 0)
        src = read("tools/analyze-xp-log.py")
        self.assertIn("wall_cap == 0 and wall_snapshots(events) == 0", src)

    def test_snapshots_from_all_three_areas_are_ok(self):
        found = xp.screen_wall_extra_findings([sum_ev(1, scrSnap="10/3/3/4/1")])
        self.assertEqual(levels(found), ["OK"])

    def test_no_snapshot_warns(self):
        found = xp.screen_wall_extra_findings([sum_ev(1, scrSnap="0/0/0/0/2")])
        self.assertEqual(levels(found), ["WARN"])

    def test_a_missing_area_warns(self):
        found = xp.screen_wall_extra_findings([sum_ev(1, scrSnap="8/4/0/4/0")])
        self.assertEqual(levels(found), ["WARN"])
        self.assertIn("B", found[0][1])

    def test_face_with_laugh_is_ok(self):
        found = xp.screen_wall_extra_findings([sum_ev(1, scrFace="1/0/0.00/0"), sum_ev(2, scrFace="1/1/1.00/1")])
        self.assertEqual(levels(found), ["OK"])

    def test_face_without_laugh_fails(self):
        found = xp.screen_wall_extra_findings([sum_ev(2, scrFace="1/1/1.00/0")])
        self.assertEqual(levels(found), ["FAIL"])

    def test_missing_face_texture_fails(self):
        found = xp.screen_wall_extra_findings([sum_ev(2, scrFace="0/0/0.00/0")])
        self.assertEqual(levels(found), ["FAIL"])

    def test_face_laugh_is_not_counted_as_a_cut_laugh(self):
        # 増えるスクリーンの笑い声は別の id（ScreenFaceLaugh）。dollLaugh の笑い声として数えない。
        events = [dict(ev="sfx", t="5", id="ScreenFaceLaugh", _cams=[]), sum_ev(6, scrFace="1/1/1.00/1")]
        takes = [dict(id="L2C0#0", steps=[dict(dollLaugh=True)])]
        _, verdicts = xp.doll_laugh_findings(events, takes)
        self.assertNotIn("FAIL", levels(verdicts))
        _, verdicts = xp.doll_laugh_findings(events, [dict(id="X", steps=[dict()])])
        self.assertNotIn("FAIL", levels(verdicts))

    def test_old_logs_say_nothing(self):
        self.assertEqual(xp.screen_wall_extra_findings([sum_ev(1, scrMul="1/3/20")]), [])


if __name__ == "__main__":
    unittest.main()
