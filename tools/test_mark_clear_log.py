"""報告で素材だけを消す（markClear）・長押しの開始で進める（markStartAdvance）・相手が畳まれたら出さない
（skipIfDismissed）の、ログの書き手と読み手を突き合わせる（canon/LEDGER.md 0378）。

- 書き手は C# の ShowTelemetryHost（ev=markclear / ev=markstartadv / ev=takeskip）
- 判断は TakeRunnerLogic（MarkResult.Cleared・NotifyMarkStarted・ArmEnterTakes の skipIfDismissed）
- 読み手は tools/analyze-xp-log.py の mark_clear_findings / mark_start_advance_findings / take_skip_findings
- 旗の名前は C# の ShowStepDef / ShowTakeDef と卓の timeline-model.js の白名簿で揃える

    py -3.11 tools/test_mark_clear_log.py
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


def ev(name, t, **kw):
    return dict(ev=name, t=str(t), _cams=[], **{k: str(v) for k, v in kw.items()})


TAKES = [
    {"id": "L2C0#0", "skipIfDismissed": "",
     "steps": [{"cueId": "cue_stain_A", "markClear": True}, {"cueId": "right", "markStartAdvance": True},
               {"cueId": "burst"}]},
    {"id": "L2C1#0", "skipIfDismissed": "L2C0#0", "steps": [{"cueId": "inherit"}]},
]


def levels(fn, events, takes=TAKES):
    _, verdicts = fn(events, takes)
    return [level for level, _ in verdicts]


class MarkClearAnalyzerTests(unittest.TestCase):
    def test_nothing_happened_says_nothing(self):
        self.assertEqual(xp.mark_clear_findings([ev("step", 1, take="L2C0#0", i=0)], TAKES), ([], []))

    def test_material_went_off_and_take_went_on_passes(self):
        events = [ev("markclear", 10.0, take="L2C0#0", step=0, n=1),
                  ev("cue", 10.02, id="cue_stain_A", st="off")]
        self.assertEqual(levels(xp.mark_clear_findings, events), ["OK"])

    def test_runtime_cue_id_is_take_and_step(self):
        # 実機・ツインの ev=cue id= は TakeRunner.StepCueKey（<take>#<step>）。台本の cueId では出ない
        events = [ev("markclear", 10.0, take="L2C0#0", step=0, n=1),
                  ev("cue", 10.24, id="L2C0#0#0", st="off")]
        self.assertEqual(levels(xp.mark_clear_findings, events), ["OK"])
        host = read("Assets/Scripts/Streaming/TakeRunner.cs")
        self.assertIn('StepCueKey(string takeId, int stepIndex) => $"{takeId}#{stepIndex}"', host)

    def test_take_folded_by_the_same_report_fails(self):
        events = [ev("markclear", 10.0, take="L2C0#0", step=0, n=1),
                  ev("cue", 10.02, id="cue_stain_A", st="off"),
                  ev("take", 10.03, id="L2C0#0", st="end", why="mark")]
        self.assertEqual(levels(xp.mark_clear_findings, events), ["FAIL"])

    def test_material_left_on_screen_fails(self):
        events = [ev("markclear", 10.0, take="L2C0#0", step=0, n=1)]
        self.assertEqual(levels(xp.mark_clear_findings, events), ["FAIL"])

    def test_later_report_fold_is_not_counted(self):
        events = [ev("markclear", 10.0, take="L2C0#0", step=0, n=1),
                  ev("cue", 10.02, id="cue_stain_A", st="off"),
                  ev("take", 20.0, id="L2C0#0", st="end", why="mark")]
        self.assertEqual(levels(xp.mark_clear_findings, events), ["OK"])

    def test_without_script_only_checks_the_fold(self):
        events = [ev("markclear", 10.0, take="L2C0#0", step=0, n=1)]
        self.assertEqual(levels(xp.mark_clear_findings, events, takes=None), ["OK"])


class MarkStartAdvanceAnalyzerTests(unittest.TestCase):
    def test_next_step_follows_passes(self):
        events = [ev("markstartadv", 12.0, take="L2C0#0", step=1, n=1),
                  ev("step", 12.02, take="L2C0#0", i=2, played=1)]
        self.assertEqual(levels(xp.mark_start_advance_findings, events), ["OK"])

    def test_no_next_step_fails(self):
        events = [ev("markstartadv", 12.0, take="L2C0#0", step=1, n=1)]
        self.assertEqual(levels(xp.mark_start_advance_findings, events), ["FAIL"])

    def test_plain_markadv_is_not_read_as_start_advance(self):
        events = [ev("markadv", 12.0, take="L2C0#0", step=1, n=1)]
        self.assertEqual(xp.mark_start_advance_findings(events, TAKES), ([], []))


class TakeSkipAnalyzerTests(unittest.TestCase):
    def test_skipped_after_partner_dismissed_passes(self):
        events = [ev("take", 20.0, id="L2C0#0", st="end", why="mark"),
                  ev("takeskip", 25.0, take="L2C1#0", by="L2C0#0", n=1)]
        self.assertEqual(levels(xp.take_skip_findings, events), ["OK"])

    def test_skipped_without_partner_dismissed_fails(self):
        events = [ev("take", 20.0, id="L2C0#0", st="end", why="yield"),
                  ev("takeskip", 25.0, take="L2C1#0", by="L2C0#0", n=1)]
        self.assertEqual(levels(xp.take_skip_findings, events), ["FAIL"])

    def test_skipped_take_began_later_fails(self):
        events = [ev("take", 20.0, id="L2C0#0", st="end", why="mark"),
                  ev("takeskip", 25.0, take="L2C1#0", by="L2C0#0", n=1),
                  ev("take", 30.0, id="L2C1#0", st="begin")]
        self.assertEqual(levels(xp.take_skip_findings, events), ["FAIL"])

    def test_partner_dismissed_but_take_began_without_skip_fails(self):
        events = [ev("take", 20.0, id="L2C0#0", st="end", why="mark"),
                  ev("take", 25.0, id="L2C1#0", st="begin")]
        self.assertEqual(levels(xp.take_skip_findings, events), ["FAIL"])

    def test_partner_not_dismissed_and_take_began_says_nothing(self):
        events = [ev("take", 20.0, id="L2C0#0", st="end", why="yield"),
                  ev("take", 25.0, id="L2C1#0", st="begin")]
        self.assertEqual(xp.take_skip_findings(events, TAKES), ([], []))


class WriterReaderTests(unittest.TestCase):
    def test_csharp_writes_the_events(self):
        host = read("Assets/Scripts/Diagnostics/ShowTelemetryHost.cs")
        self.assertIn('Emit($"ev=markclear take=', host)
        self.assertIn('$"step={_takes.LastMarkClearStep} n={_takes.MarkClearCount}"', host)
        self.assertIn('Emit($"ev=markstartadv take=', host)
        self.assertIn('$"step={_takes.LastMarkStartAdvanceStep} n={_takes.MarkStartAdvanceCount}"', host)
        self.assertIn('Emit($"ev=takeskip take=', host)
        self.assertIn('$"by={', host)

    def test_logic_has_the_three_decisions(self):
        logic = read("Assets/Scripts/Streaming/TakeRunnerLogic.cs")
        self.assertRegex(logic, r"Cleared = 4,")
        self.assertIn("public bool NotifyMarkStarted(float now)", logic)
        self.assertIn("skipIfDismissedTake1", logic)

    def test_flag_names_match_schema_and_desk(self):
        schema = read("Assets/Scripts/Streaming/ShowTakeSchema.cs")
        self.assertRegex(schema, r"public bool markClear;")
        self.assertRegex(schema, r"public bool markStartAdvance;")
        self.assertRegex(schema, r'public string skipIfDismissed = "";')
        model = read("tools/web-compositor/timeline-model.js")
        self.assertIn("markClear: !!s.markClear", model)
        self.assertIn("markStartAdvance: !!s.markStartAdvance", model)
        self.assertIn("skipIfDismissed: t.skipIfDismissed || ''", model)

    def test_analyzer_reads_the_partner_from_the_script(self):
        src = read("tools/analyze-xp-log.py")
        self.assertIn('"skipIfDismissed": str(t.get("skipIfDismissed") or "")', src)


if __name__ == "__main__":
    unittest.main()
