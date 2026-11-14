"""人形の笑い声 1 回（カットの dollLaugh）の、ログの書き手と読み手を突き合わせる。

- 書き手は C# の ShowTelemetryHost（ev=sfx id=<SoundCue>）、鳴らすのは TakeRunner.PlayDollLaugh（dollCall と同じ行）
- 読み手は tools/analyze-xp-log.py の doll_laugh_findings。名前（DOLL_LAUGH_SFX）は C# の SoundCue.DollLaughOnce と対
- 音源は tools/ingest-doll-laugh.py が bed_doll_one.wav から切り出す（頭 2.4 秒・尻 0.5 秒を聴感直線で退かせる）

    py -3.11 tools/test_doll_laugh_log.py
"""

import array
import importlib.util
import re
import unittest
import wave
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
TOOLS = ROOT / "tools"

spec = importlib.util.spec_from_file_location("xp_analyzer", TOOLS / "analyze-xp-log.py")
xp = importlib.util.module_from_spec(spec)
spec.loader.exec_module(xp)

ingest_spec = importlib.util.spec_from_file_location("ingest_doll_laugh", TOOLS / "ingest-doll-laugh.py")
ingest = importlib.util.module_from_spec(ingest_spec)
ingest_spec.loader.exec_module(ingest)


def read(rel):
    return (ROOT / rel).read_text(encoding="utf-8")


def ev(name, t, **kw):
    return dict(ev=name, t=str(t), _cams=[], **{k: str(v) for k, v in kw.items()})


TAKES = [{"id": "L2C0#0", "steps": [{"cueId": "a"}, {"cueId": "b", "dollLaugh": True}, {"cueId": "c"}]},
         {"id": "L1C0#0", "steps": [{"cueId": ""}]}]


class DollLaughAnalyzerTests(unittest.TestCase):
    def levels(self, events, takes=TAKES):
        _, verdicts = xp.doll_laugh_findings(events, takes)
        return [level for level, _ in verdicts]

    def shown(self, played=1):
        return [ev("step", 40, take="L2C0#0", i=0, played=1),
                ev("step", 41, take="L2C0#0", i=1, played=played)]

    def test_cut_shown_with_laugh_passes(self):
        self.assertEqual(self.levels(self.shown() + [ev("sfx", 41.01, id="DollLaughOnce", n=3)]), ["OK"])

    def test_cut_shown_without_laugh_fails(self):
        self.assertEqual(self.levels(self.shown() + [ev("sfx", 41.01, id="Glitch", n=3)]), ["FAIL"])

    def test_skipped_cut_without_laugh_is_not_reached(self):
        self.assertEqual(self.levels(self.shown(played=0)), ["WARN"])

    def test_laugh_without_the_cut_fails(self):
        self.assertEqual(self.levels(self.shown(played=0) + [ev("sfx", 41.01, id="DollLaughOnce", n=3)]), ["FAIL"])

    def test_laugh_without_the_flag_in_the_script_fails(self):
        takes = [{"id": "L2C0#0", "steps": [{"cueId": "b"}]}]
        self.assertEqual(self.levels([ev("sfx", 1, id="DollLaughOnce", n=1)], takes=takes), ["FAIL"])

    def test_script_without_the_flag_and_no_laugh_says_nothing(self):
        self.assertEqual(xp.doll_laugh_findings([], [{"id": "x", "steps": []}]), ([], []))

    def test_no_script_reports_count_only(self):
        lines, verdicts = xp.doll_laugh_findings([ev("sfx", 1, id="DollLaughOnce", n=1)], None)
        self.assertEqual(len(lines), 1)
        self.assertEqual(verdicts, [])

    def test_expected_takes_carry_the_flag(self):
        show = {"timeline": {"segments": [{"lap": 2, "camera": 0, "takes": [
            {"id": "L2C0#0", "steps": [{"source": "plate", "cueId": "x", "dollLaugh": True}]}]}]}}
        takes = xp.expected_from_show(show)["takes"]
        _, verdicts = xp.doll_laugh_findings([ev("step", 1, take="L2C0#0", i=0, played=1)], takes)
        self.assertEqual([level for level, _ in verdicts], ["FAIL"])


class DollLaughWriterReaderTests(unittest.TestCase):
    def test_csharp_writes_the_cue_name(self):
        host = read("Assets/Scripts/Diagnostics/ShowTelemetryHost.cs")
        self.assertIn('Emit($"ev=sfx id={_sound.LastCue} n={spots} "', host)
        logic = read("Assets/Scripts/Streaming/Sound/SoundCueLogic.cs")
        self.assertRegex(logic, r"\b" + xp.DOLL_LAUGH_SFX + r",")
        self.assertIn('case SoundCue.DollLaughOnce: return "sfx_doll_laugh_once";', logic)

    def test_runner_plays_it_on_the_dollcall_line(self):
        runner = read("Assets/Scripts/Streaming/TakeRunner.cs")
        self.assertIn("if (step.dollLaugh) PlayDollLaugh();", runner)
        self.assertIn("_sound.PlaySpot(SoundCue.DollLaughOnce)", runner)
        # 呼びかけ・子供部屋の音と同じ並び（画を差し替えるのと同じ行の近く）に居ること。
        call = runner.index("if (step.dollCall) PlayDollCallVoice();")
        laugh = runner.index("if (step.dollLaugh) PlayDollLaugh();")
        self.assertLess(call, laugh)
        self.assertLess(laugh - call, 2000)

    def test_it_is_2d(self):
        spatial = read("Assets/Scripts/Streaming/Sound/SpatialAudio.cs")
        self.assertNotIn("sfx_doll_laugh_once", spatial, "2D で鳴らす（MonoRequired に入れない）")

    def test_mix_and_lint_know_the_file(self):
        self.assertIn('"DollLaughOnce": ("sfx_doll_laugh_once", 1)', read("tools/mix-from-log.py"))
        self.assertIn('"sfx_doll_laugh_once"', read("tools/sound-lint.py"))

    def test_flag_name_matches_schema_and_desk(self):
        self.assertRegex(read("Assets/Scripts/Streaming/ShowTakeSchema.cs"), r"public bool dollLaugh;")
        self.assertIn("dollLaugh: !!s.dollLaugh", read("tools/web-compositor/timeline-model.js"))
        self.assertIn("rb-s-dolllaugh", read("tools/web-compositor/ribbon.js"))


class DollLaughSourceTests(unittest.TestCase):
    """焼いた音源が、取り込みの手順どおりの姿か（取り込み忘れ・別の版の取り違え）。"""

    def load(self, rel):
        with wave.open(str(ROOT / rel), "rb") as w:
            fmt = (w.getnchannels(), w.getframerate(), w.getsampwidth())
            return fmt, array.array("h", w.readframes(w.getnframes()))

    def test_baked_file_is_the_cut_of_the_source(self):
        fmt, out = self.load("Assets/Resources/Sound/sfx_doll_laugh_once.wav")
        self.assertEqual(fmt, (1, 48000, 2))
        self.assertEqual(len(out), int(round(ingest.CUT_SEC * ingest.RATE)), "2.4 秒")
        _, src = self.load("Assets/Resources/Sound/bed_doll_one.wav")
        fade_n = int(round(ingest.FADE_SEC * ingest.RATE))
        body = len(out) - fade_n
        self.assertEqual(out[:body], src[:body], "退く前の 4.2 秒は音量も含めて素のまま")
        self.assertEqual(out[-1], 0, "最後の標本で 0 へ退き切る")
        for i in range(0, fade_n, 997):
            self.assertLessEqual(abs(out[body + i]), abs(src[body + i]), "退きの間に大きくならない")

    def test_fade_curve_is_perceptual_linear(self):
        self.assertAlmostEqual(ingest.fade_gain(0.0), 1.0)
        self.assertAlmostEqual(ingest.fade_gain(1.0), 0.0)
        self.assertAlmostEqual(ingest.fade_gain(0.5), 0.5 ** (1 / 0.6), places=6)
        so = read("Assets/Scripts/Streaming/Sound/SoundFade.cs")
        m = re.search(r"LoudnessExponent = ([0-9.]+)f;", so)
        self.assertIsNotNone(m)
        self.assertAlmostEqual(float(m.group(1)), ingest.LOUDNESS_EXPONENT)


if __name__ == "__main__":
    unittest.main()
