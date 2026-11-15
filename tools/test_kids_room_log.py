"""2 周目 B の別の場所（子供部屋の動画）の音の、ログの書き手と読み手を突き合わせる（canon/LEDGER.md 0374）。

- 書き手は C# の ShowTelemetryHost（ev=sfx id=<SoundCue>）、鳴らすのは TakeRunner（KidsRoomCueId のカットの頭）
- 読み手は tools/analyze-xp-log.py の kids_room_findings。cue id（KIDS_ROOM_CUE）は C# の
  TakeRunner.KidsRoomCueId と対。片方だけ直すと判定が黙って空振りする
- 押し始めで人形の落ちる所へ飛ぶ・報告を最後のコマまで保つ（0375）は ev=kidsjump / kidsland / kidshold。
  読み手は kids_jump_findings、落ちる所の秒（KIDS_ROOM_FALL_SEC）は C# の TakeRunner.KidsRoomFallSec と対

    py -3.11 tools/test_kids_room_log.py
"""

import importlib.util
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
TOOLS = ROOT / "tools"

spec = importlib.util.spec_from_file_location("xp_analyzer", TOOLS / "analyze-xp-log.py")
xp = importlib.util.module_from_spec(spec)
spec.loader.exec_module(xp)


def read(rel):
    return (ROOT / rel).read_text(encoding="utf-8")


def ev(name, t, **kw):
    return dict(ev=name, t=str(t), _cams=[], **{k: str(v) for k, v in kw.items()})


TAKES = [{"id": "L2C1#0", "steps": [{"cueId": "pov_x"}, {"cueId": "backrooms_B_in"}, {"cueId": "backrooms_B"}]},
         {"id": "L1C0#0", "steps": [{"cueId": ""}]}]


class KidsRoomAnalyzerTests(unittest.TestCase):
    def levels(self, events, takes=TAKES):
        _, verdicts = xp.kids_room_findings(events, takes)
        return [level for level, _ in verdicts]

    def shown(self, played=1):
        return [ev("step", 40, take="L2C1#0", i=0, src="plate", played=1),
                ev("step", 41, take="L2C1#0", i=1, src="plate", played=played),
                ev("step", 46.9, take="L2C1#0", i=2, src="plate", played=1)]

    def test_cut_shown_with_sound_passes(self):
        events = self.shown() + [ev("sfx", 41.02, id="KidsRoom", n=5)]
        self.assertEqual(self.levels(events), ["OK"])

    def test_cut_shown_without_sound_fails(self):
        events = self.shown() + [ev("sfx", 41.02, id="Glitch", n=5)]
        self.assertEqual(self.levels(events), ["FAIL"])

    def test_skipped_cut_without_sound_is_not_reached(self):
        self.assertEqual(self.levels(self.shown(played=0)), ["WARN"])

    def test_sound_without_the_cut_fails(self):
        events = self.shown(played=0) + [ev("sfx", 41.02, id="KidsRoom", n=5)]
        self.assertEqual(self.levels(events), ["FAIL"])

    def test_still_cut_alone_does_not_count_as_the_video(self):
        events = [ev("step", 46.9, take="L2C1#0", i=2, src="plate", played=1)]
        self.assertEqual(self.levels(events), ["WARN"], "静止画のカット（backrooms_B）では鳴らさない")

    def test_sound_without_the_cue_in_the_script_fails(self):
        events = [ev("sfx", 41.02, id="KidsRoom", n=5)]
        self.assertEqual(self.levels(events, takes=[{"id": "L2C1#0", "steps": [{"cueId": "backrooms_B"}]}]),
                         ["FAIL"])

    def test_script_without_the_cue_and_no_sound_says_nothing(self):
        self.assertEqual(xp.kids_room_findings([], [{"id": "x", "steps": []}]), ([], []))

    def test_no_script_reports_count_only(self):
        lines, verdicts = xp.kids_room_findings([ev("sfx", 1, id="KidsRoom", n=1)], None)
        self.assertEqual(len(lines), 1)
        self.assertEqual(verdicts, [])

    def test_expected_takes_carry_the_cue_id(self):
        show = {"timeline": {"segments": [{"lap": 2, "camera": 1, "takes": [
            {"id": "L2C1#0", "steps": [{"source": "plate", "cueId": "backrooms_B_in"}]}]}]}}
        takes = xp.expected_from_show(show)["takes"]
        _, verdicts = xp.kids_room_findings([ev("step", 1, take="L2C1#0", i=0, src="plate", played=1)], takes)
        self.assertEqual([level for level, _ in verdicts], ["FAIL"])


class KidsRoomWriterReaderTests(unittest.TestCase):
    def test_cue_id_matches_the_runner(self):
        runner = read("Assets/Scripts/Streaming/TakeRunner.cs")
        m = re.search(r'public const string KidsRoomCueId = "([^"]+)";', runner)
        self.assertIsNotNone(m)
        self.assertEqual(m.group(1), xp.KIDS_ROOM_CUE)
        prefix = re.search(r'public const string OtherworldCuePrefix = "([^"]+)";', runner)
        self.assertIsNotNone(prefix)
        self.assertTrue(xp.KIDS_ROOM_CUE.startswith(prefix.group(1)), "風と時計の ? もこのカットで立つ")

    def test_csharp_writes_the_cue_name(self):
        host = read("Assets/Scripts/Diagnostics/ShowTelemetryHost.cs")
        self.assertIn('Emit($"ev=sfx id={_sound.LastCue} n={spots} "', host)
        logic = read("Assets/Scripts/Streaming/Sound/SoundCueLogic.cs")
        self.assertRegex(logic, r"\bKidsRoom,")
        self.assertIn('case SoundCue.KidsRoom: return "sfx_kidsroom";', logic)

    def test_runner_plays_it_from_the_cut_head(self):
        runner = read("Assets/Scripts/Streaming/TakeRunner.cs")
        self.assertIn("if (IsKidsRoomCue(step.cueId)) PlayKidsRoomSound();", runner)
        self.assertIn("_sound.PlaySpot(SoundCue.KidsRoom)", runner)
        self.assertIn("_sound.StopSpot(SoundCue.KidsRoom, KidsRoomStopFadeSec)", runner)


class KidsJumpAnalyzerTests(unittest.TestCase):
    """押し始めで人形の落ちる所へ飛ぶ・報告を最後のコマまで保つ（canon/LEDGER.md 0375）。"""

    def levels(self, events):
        _, verdicts = xp.kids_jump_findings(events)
        return [level for level, _ in verdicts]

    def take(self, t0, t1, why, *inner):
        return [ev("take", t0, id="L2C1#0", st="begin"),
                ev("step", t0, take="L2C1#0", i=0, src="inherit", played=1),
                *inner,
                ev("take", t1, id="L2C1#0", st="end", why=why)]

    def jump(self, t, frm=1.3, to=4.2, mode="xfade", n=1, by="mark"):
        return ev("kidsjump", t, take="L2C1#0", **{"from": frm}, to=to, mode=mode, by=by, n=n)

    def land(self, t, wait=0.05, timed_out=0, n=1):
        return ev("kidsland", t, wait=wait, timedOut=timed_out, n=n)

    def hold(self, t, step=1, until=2, n=1):
        return ev("kidshold", t, take="L2C1#0", step=step, until=until, n=n)

    def test_nothing_happened_says_nothing(self):
        self.assertEqual(xp.kids_jump_findings(self.take(40, 50, "done")), ([], []))

    def test_jump_then_land_passes(self):
        events = self.take(40, 46, "done", self.jump(41.3), self.land(41.35))
        self.assertEqual(self.levels(events), ["OK"])

    def test_jump_without_land_fails(self):
        events = self.take(40, 46, "done", self.jump(41.3))
        self.assertEqual(self.levels(events), ["FAIL"])

    def test_jump_without_land_before_walking_out_is_a_warning(self):
        events = self.take(40, 41.4, "yield", self.jump(41.3))
        self.assertEqual(self.levels(events), ["WARN"])

    def test_jump_after_the_fall_point_fails(self):
        events = self.take(40, 46, "done", self.jump(41.3, frm=4.5), self.land(41.35))
        self.assertIn("FAIL", self.levels(events))

    def test_fall_point_mismatch_fails(self):
        events = self.take(40, 46, "done", self.jump(41.3, to=4.3), self.land(41.35))
        self.assertIn("FAIL", self.levels(events))

    def test_two_jumps_in_one_take_fail(self):
        events = self.take(40, 46, "done", self.jump(41.3), self.land(41.35), self.jump(42, n=2), self.land(42.1, n=2))
        self.assertIn("FAIL", self.levels(events))

    def test_timed_out_land_is_a_warning(self):
        events = self.take(40, 46, "done", self.jump(41.3), self.land(42.3, wait=1.0, timed_out=1))
        self.assertEqual(self.levels(events), ["WARN"])

    def test_held_report_folded_after_the_last_frame_passes(self):
        events = self.take(40, 48.6, "mark",
                           ev("step", 41, take="L2C1#0", i=1, src="plate", played=1),
                           self.hold(42),
                           ev("step", 46.9, take="L2C1#0", i=2, src="plate", played=1))
        self.assertEqual(self.levels(events), ["OK"])

    def test_held_report_folded_before_the_last_frame_fails(self):
        events = self.take(40, 42.1, "mark",
                           ev("step", 41, take="L2C1#0", i=1, src="plate", played=1),
                           self.hold(42))
        self.assertEqual(self.levels(events), ["FAIL"])

    def test_held_report_that_ran_to_the_end_fails(self):
        events = self.take(40, 48.6, "done",
                           ev("step", 41, take="L2C1#0", i=1, src="plate", played=1),
                           self.hold(42),
                           ev("step", 46.9, take="L2C1#0", i=2, src="plate", played=1))
        self.assertEqual(self.levels(events), ["FAIL"])

    def test_line_jump_then_land_passes(self):
        events = self.take(40, 46, "done", self.jump(41.3, by="line"), self.land(41.35))
        self.assertEqual(self.levels(events), ["OK"])

    def test_unknown_jump_trigger_fails(self):
        events = self.take(40, 46, "done", self.jump(41.3, by="walk"), self.land(41.35))
        self.assertIn("FAIL", self.levels(events))

    # 台本つき: 子供部屋の動画が出たのに飛ばず、人形が入る前に区間を出た（0380）。
    TAKES = [{"id": "L2C1#0", "steps": [{"cueId": ""}, {"cueId": "backrooms_B_in"}, {"cueId": "backrooms_B"}]}]

    def test_walked_out_before_the_doll_fell_warns(self):
        events = self.take(40, 44.2, "yield", ev("step", 41, take="L2C1#0", i=1, src="plate", played=1))
        _, verdicts = xp.kids_jump_findings(events, self.TAKES)
        self.assertEqual([lv for lv, _ in verdicts], ["WARN"])
        self.assertIn("kids_B_fall", verdicts[0][1])

    def test_walked_out_after_the_doll_fell_passes(self):
        events = self.take(40, 46.0, "yield", ev("step", 41, take="L2C1#0", i=1, src="plate", played=1))
        _, verdicts = xp.kids_jump_findings(events, self.TAKES)
        self.assertEqual([lv for lv, _ in verdicts], ["OK"])

    def test_walked_out_after_a_line_jump_is_not_a_miss(self):
        events = self.take(40, 43.0, "yield", ev("step", 41, take="L2C1#0", i=1, src="plate", played=1),
                           self.jump(41.6, frm=0.5, by="line"), self.land(41.65))
        _, verdicts = xp.kids_jump_findings(events, self.TAKES)
        self.assertEqual([lv for lv, _ in verdicts], ["OK"])

    def test_held_report_cut_by_walking_out_says_nothing_bad(self):
        events = self.take(40, 43, "yield",
                           ev("step", 41, take="L2C1#0", i=1, src="plate", played=1),
                           self.hold(42))
        self.assertNotIn("FAIL", self.levels(events))


class KidsJumpWriterReaderTests(unittest.TestCase):
    def test_fall_point_matches_the_runner(self):
        runner = read("Assets/Scripts/Streaming/TakeRunner.cs")
        m = re.search(r"public const float KidsRoomFallSec = ([0-9.]+)f;", runner)
        self.assertIsNotNone(m)
        self.assertAlmostEqual(float(m.group(1)), xp.KIDS_ROOM_FALL_SEC)

    def test_csharp_writes_the_events(self):
        host = read("Assets/Scripts/Diagnostics/ShowTelemetryHost.cs")
        for key in ('Emit($"ev=kidsjump take=', '$"from={_takes.LastKidsRoomJumpFromSec:F2} to={TakeRunner.KidsRoomFallSec:F2} "',
                    '$"mode={_takes.LastKidsRoomJumpMode} by={_takes.LastKidsRoomJumpBy} n={_takes.KidsRoomJumpCount}"',
                    'Emit($"ev=kidsland wait={_takes.LastKidsRoomLandWaitSec:F2} "',
                    'Emit($"ev=kidshold take=', '$"step={_takes.LastKidsRoomHeldStep} until={_takes.LastKidsRoomHeldUntilStep} "'):
            self.assertIn(key, host)

    def test_mode_tokens_match(self):
        runner = read("Assets/Scripts/Streaming/TakeRunner.cs")
        self.assertIn('LastKidsRoomJumpMode = jump == ScreenOverlayController.SeekJump.Crossfade ? "xfade" : "prep";', runner)

    def test_trigger_tokens_match(self):
        runner = read("Assets/Scripts/Streaming/TakeRunner.cs")
        self.assertIn('LastKidsRoomJumpBy = byLine ? "line" : "mark";', runner)
        self.assertEqual(set(xp.KIDS_JUMP_BY), {"line", "mark"})

    def test_fall_line_id_matches_the_runner(self):
        runner = read("Assets/Scripts/Streaming/TakeRunner.cs")
        self.assertIn(f'public const string KidsRoomFallLineId = "{xp.KIDS_FALL_LINE_ID}";', runner)

    def test_doll_entry_matches_the_csharp_test(self):
        test = read("Assets/Tests/Streaming/KidsRoomJumpTests.cs")
        self.assertIn("private const float DollEntersSec = 112f / 24f;", test)
        self.assertAlmostEqual(xp.KIDS_ROOM_DOLL_IN_SEC, 112 / 24)


if __name__ == "__main__":
    unittest.main()
