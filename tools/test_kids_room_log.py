"""2 周目 B の別の場所（子供部屋の動画）の音の、ログの書き手と読み手を突き合わせる（canon/LEDGER.md 0374）。

- 書き手は C# の ShowTelemetryHost（ev=sfx id=<SoundCue>）、鳴らすのは TakeRunner（KidsRoomCueId のカットの頭）
- 読み手は tools/analyze-xp-log.py の kids_room_findings。cue id（KIDS_ROOM_CUE）は C# の
  TakeRunner.KidsRoomCueId と対。片方だけ直すと判定が黙って空振りする

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


if __name__ == "__main__":
    unittest.main()
