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
    {"id": "L2C1#0", "skipIfDismissed": "L2C0#0", "lap": 2, "camera": 1,
     "steps": [{"cueId": "", "cg": "doll", "cgMode": "trail", "markClear": True}, {"cueId": "backrooms_B_in"}]},
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

    def test_trail_doll_cleared_passes_when_the_doll_went_away(self):
        # 0382: 2-B の後ろをついてくる人形を報告で消す。人形の書き手は ShowTelemetryHost の ev=trail（vis=0 で消えた）
        events = [ev("markclear", 20.0, take="L2C1#0", step=0, n=1),
                  ev("trail", 20.01, station=-1, x=0, z=0, vis=0, gap=0.8)]
        self.assertEqual(levels(xp.mark_clear_findings, events), ["OK"])

    def test_trail_doll_cleared_but_still_visible_fails(self):
        events = [ev("markclear", 20.0, take="L2C1#0", step=0, n=1),
                  ev("trail", 20.5, station=33, x=-0.8, z=-0.6, vis=1, gap=0.8)]
        self.assertEqual(levels(xp.mark_clear_findings, events), ["FAIL"])
        host = read("Assets/Scripts/Streaming/TakeRunner.cs")
        self.assertIn("_cgLayer.Hide();", host[host.index("private void ClearStepMaterial"):])

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
    """2-A の人形を報告で畳んだ回は、2-B の演出を人形だけ外して出す（0378 / 0399）。"""

    def test_doll_withheld_and_take_shown_passes(self):
        events = [ev("take", 20.0, id="L2C0#0", st="end", why="mark"),
                  ev("take", 25.0, id="L2C1#0", st="begin"),
                  ev("takeskip", 25.0, take="L2C1#0", by="L2C0#0", n=1),
                  ev("take", 40.0, id="L2C1#0", st="end", why="done")]
        self.assertEqual(levels(xp.take_skip_findings, events), ["OK"])

    def test_withheld_without_partner_dismissed_fails(self):
        events = [ev("take", 20.0, id="L2C0#0", st="end", why="yield"),
                  ev("take", 25.0, id="L2C1#0", st="begin"),
                  ev("takeskip", 25.0, take="L2C1#0", by="L2C0#0", n=1)]
        self.assertEqual(levels(xp.take_skip_findings, events), ["FAIL"])

    def test_withheld_but_take_never_began_fails(self):
        # 2026-11-18 より前の版の形（演出ごと出さなかった）
        events = [ev("take", 20.0, id="L2C0#0", st="end", why="mark"),
                  ev("takeskip", 20.0, take="L2C1#0", by="L2C0#0", n=1)]
        self.assertIn("FAIL", levels(xp.take_skip_findings, events))

    def test_doll_shown_while_withheld_fails(self):
        events = [ev("take", 20.0, id="L2C0#0", st="end", why="mark"),
                  ev("take", 25.0, id="L2C1#0", st="begin"),
                  ev("takeskip", 25.0, take="L2C1#0", by="L2C0#0", n=1),
                  ev("trail", 25.5, station=0, vis=1),
                  ev("take", 40.0, id="L2C1#0", st="end", why="done")]
        self.assertEqual(levels(xp.take_skip_findings, events), ["FAIL"])

    def test_partner_dismissed_but_take_began_without_skip_fails(self):
        events = [ev("take", 20.0, id="L2C0#0", st="end", why="mark"),
                  ev("take", 25.0, id="L2C1#0", st="begin")]
        self.assertEqual(levels(xp.take_skip_findings, events), ["FAIL"])

    def test_report_inside_2b_during_hold_then_take_missing_fails(self):
        # β の実機（2026-11-18）: 2-A の人形が付いて来る間に 2-B で報告し、子供部屋の演出が出なかった
        events = [ev("seg", 201.8, lap=2, cam=1, plap=2),
                  ev("take", 202.3, id="L2C0#0", st="end", why="mark"),
                  ev("takeskip", 202.3, take="L2C1#0", by="L2C0#0", n=1),
                  ev("seg", 209.1, lap=2, cam=2, plap=2)]
        self.assertIn("FAIL", levels(xp.take_skip_findings, events))

    def test_partner_not_dismissed_and_take_began_says_nothing(self):
        events = [ev("take", 20.0, id="L2C0#0", st="end", why="yield"),
                  ev("take", 25.0, id="L2C1#0", st="begin")]
        self.assertEqual(xp.take_skip_findings(events, TAKES), ([], []))


SHOW_TAKES = [
    {"id": "L2C0#0", "steps": [{"cueId": "cue_stain_A", "markClear": True}, {"cueId": "right"},
                               {"cueId": "burst", "markShowThrough": True}, {"cueId": "watch"}]},
]


def take_span(t0, t1, why, steps, hold=None):
    out = [ev("take", t0, id="L2C0#0", st="begin")]
    out += [ev("step", t, take="L2C0#0", i=i, played=1) for t, i in steps]
    if hold:
        out.append(ev("showthru", hold[0], take="L2C0#0", step=hold[1], until=2, n=1))
    out.append(ev("take", t1, id="L2C0#0", st="end", why=why))
    return out


class ShowThroughAnalyzerTests(unittest.TestCase):
    """全員に見せたいカット（markShowThrough）を、報告しても見せ切ったか。"""

    def levels(self, events, takes=SHOW_TAKES):
        return [v[0] for v in xp.show_through_findings(events, takes)[1]]

    def test_held_and_shown_through_passes(self):
        events = take_span(0, 16, "mark", [(0, 0), (13, 1), (13.4, 2)], hold=(13.2, 1))
        self.assertEqual(self.levels(events), ["OK"])

    def test_held_but_folded_before_the_cut_fails(self):
        events = take_span(0, 13.5, "mark", [(0, 0), (13, 1)], hold=(13.2, 1))
        self.assertEqual(self.levels(events), ["FAIL"])

    def test_held_but_went_on_past_the_cut_fails(self):
        events = take_span(0, 20, "mark", [(0, 0), (13, 1), (13.4, 2), (15.7, 3)], hold=(13.2, 1))
        self.assertEqual(self.levels(events), ["FAIL"])

    def test_held_but_finished_by_itself_fails(self):
        events = take_span(0, 30, "done", [(0, 0), (13, 1), (13.4, 2), (15.7, 3)], hold=(13.2, 1))
        self.assertEqual(self.levels(events), ["FAIL"])

    def test_folded_by_a_report_before_the_cut_without_hold_fails(self):
        events = take_span(0, 13.5, "mark", [(0, 0), (13, 1)])
        self.assertEqual(self.levels(events), ["FAIL"])

    def test_report_after_the_cut_folds_at_once_and_says_nothing(self):
        events = take_span(0, 17, "mark", [(0, 0), (13, 1), (13.4, 2), (15.7, 3)])
        self.assertEqual(self.levels(events), [])

    def test_leaving_the_zone_while_holding_warns(self):
        events = take_span(0, 14, "yield", [(0, 0), (13, 1), (13.4, 2)], hold=(13.2, 1))
        self.assertEqual(self.levels(events), ["WARN"])

    def test_without_the_flag_nothing_is_said(self):
        events = take_span(0, 13.5, "mark", [(0, 0), (13, 1)])
        self.assertEqual(self.levels(events, takes=[]), [])


class WriterReaderTests(unittest.TestCase):
    def test_show_through_writer_matches_reader(self):
        host = read("Assets/Scripts/Diagnostics/ShowTelemetryHost.cs")
        self.assertIn('Emit($"ev=showthru take=', host)
        self.assertIn('$"step={_takes.LastShowThroughStep} until={_takes.LastShowThroughUntilStep} "', host)
        self.assertIn('e.get("ev") == "showthru"', read("tools/analyze-xp-log.py"))
        self.assertRegex(read("Assets/Scripts/Streaming/ShowTakeSchema.cs"), r"public bool markShowThrough;")
        self.assertIn("markShowThrough: !!s.markShowThrough", read("tools/web-compositor/timeline-model.js"))

    def test_tally_suffix_after_clear_matches_csharp(self):
        tally = read("Assets/Scripts/Streaming/VisitorReportTally.cs")
        self.assertIn(f'public const string ClearedSuffix = "{xp.TALLY_CLEARED_SUFFIX}";', tally)

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
