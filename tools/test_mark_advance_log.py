"""報告でカットを進める（step の markAdvance）の、ログの書き手と読み手を突き合わせる。

- 書き手は C# の ShowTelemetryHost（ev=markadv take= step= n=）、数えるのは TakeRunner.MarkAdvanceCount
- 判断は TakeRunnerLogic.NotifyMarkPressed（MarkResult.Advanced・そのカットを今で終える）
- 読み手は tools/analyze-xp-log.py の mark_advance_findings。進めた後 MARK_ADVANCE_NEXT_SEC 以内に
  ev=step take=<id> i=<step+1> が続かなければ FAIL
- 旗の名前（markAdvance）は C# の ShowStepDef と卓の timeline-model.js の白名簿で揃える

    py -3.11 tools/test_mark_advance_log.py
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


TAKES = [{"id": "L2C0#0", "steps": [{"cueId": "a", "markAdvance": True}, {"cueId": "b"}, {"cueId": "c"}]}]


class MarkAdvanceAnalyzerTests(unittest.TestCase):
    def levels(self, events, takes=TAKES):
        _, verdicts = xp.mark_advance_findings(events, takes)
        return [level for level, _ in verdicts]

    def test_nothing_happened_says_nothing(self):
        self.assertEqual(xp.mark_advance_findings([ev("step", 1, take="L2C0#0", i=0, played=1)], TAKES), ([], []))

    def test_next_step_follows_passes(self):
        events = [ev("step", 10, take="L2C0#0", i=0, played=1),
                  ev("markadv", 12.0, take="L2C0#0", step=0, n=1),
                  ev("step", 12.02, take="L2C0#0", i=1, played=1)]
        self.assertEqual(self.levels(events), ["OK"])

    def test_skipped_next_step_still_counts_as_advanced(self):
        events = [ev("markadv", 12.0, take="L2C0#0", step=0, n=1),
                  ev("step", 12.02, take="L2C0#0", i=1, played=0, why="noasset")]
        self.assertEqual(self.levels(events), ["OK"])

    def test_no_next_step_fails(self):
        events = [ev("markadv", 12.0, take="L2C0#0", step=0, n=1)]
        self.assertEqual(self.levels(events), ["FAIL"])

    def test_next_step_too_late_fails(self):
        events = [ev("markadv", 12.0, take="L2C0#0", step=0, n=1),
                  ev("step", 15.5, take="L2C0#0", i=1, played=1)]
        self.assertEqual(self.levels(events), ["FAIL"])

    def test_step_of_another_take_does_not_count(self):
        events = [ev("markadv", 12.0, take="L2C0#0", step=0, n=1),
                  ev("step", 12.02, take="L1C0#0", i=1, played=1)]
        self.assertEqual(self.levels(events), ["FAIL"])

    def test_wrong_step_index_does_not_count(self):
        events = [ev("markadv", 12.0, take="L2C0#0", step=0, n=1),
                  ev("step", 12.02, take="L2C0#0", i=2, played=1)]
        self.assertEqual(self.levels(events), ["FAIL"])

    def test_advancing_the_last_cut_ends_the_take(self):
        events = [ev("markadv", 12.0, take="L2C0#0", step=2, n=1),
                  ev("take", 12.02, id="L2C0#0", st="end", why="done")]
        self.assertEqual(self.levels(events), ["OK"])

    def test_folded_before_the_next_cut_is_a_warning(self):
        events = [ev("markadv", 12.0, take="L2C0#0", step=0, n=1),
                  ev("take", 12.01, id="L2C0#0", st="end", why="yield")]
        self.assertEqual(self.levels(events), ["WARN"])

    def test_without_script_still_checks_the_next_step(self):
        events = [ev("markadv", 12.0, take="L2C0#0", step=0, n=1)]
        self.assertEqual(self.levels(events, takes=None), ["FAIL"])


class MarkAdvanceWriterReaderTests(unittest.TestCase):
    def test_csharp_writes_the_event(self):
        host = read("Assets/Scripts/Diagnostics/ShowTelemetryHost.cs")
        self.assertIn('Emit($"ev=markadv take=', host)
        self.assertIn('$"step={_takes.LastMarkAdvanceStep} n={_takes.MarkAdvanceCount}"', host)

    def test_reader_keys_match_the_writer(self):
        # 読み手が読むキー（take / step / t）と、次のカットの行（ev=step take= i=）の書き方が C# と揃っているか。
        host = read("Assets/Scripts/Diagnostics/ShowTelemetryHost.cs")
        self.assertIn('Emit($"ev=step take={(string.IsNullOrEmpty(takeId) ? "?" : takeId)} i={stepIndex} "', host)

    def test_logic_returns_advanced_and_ends_the_cut(self):
        logic = read("Assets/Scripts/Streaming/TakeRunnerLogic.cs")
        self.assertRegex(logic, r"Advanced = 3,")
        self.assertIn("if (CurrentStepMarkAdvance()) { SetCurrentStepEnd(now); return MarkResult.Advanced; }", logic)

    def test_flag_name_matches_schema_and_desk(self):
        schema = read("Assets/Scripts/Streaming/ShowTakeSchema.cs")
        self.assertRegex(schema, r"public bool markAdvance;")
        model = read("tools/web-compositor/timeline-model.js")
        self.assertIn("markAdvance: !!s.markAdvance", model)
        ribbon = read("tools/web-compositor/ribbon.js")
        self.assertIn("rb-s-markadvance", ribbon)

    def test_next_step_window_is_three_seconds(self):
        self.assertEqual(xp.MARK_ADVANCE_NEXT_SEC, 3.0)


if __name__ == "__main__":
    unittest.main()
