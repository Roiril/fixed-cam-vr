"""前へ抜けた後も畳まずに画面を持つ演出（take の exitHoldSec）の、ログの書き手と読み手を突き合わせる。

- 書き手は C# の ShowTelemetryHost（ev=exithold take= sec= n=）
- 判断は TakeRunnerLogic（OnZoneCommitted が持ち始め、Tick が持つ秒の終わりで前進の yield として畳む）
- 読み手は tools/analyze-xp-log.py の exit_hold_findings
- 名前は C# の ShowTakeDef と卓の timeline-model.js の白名簿で揃える

    py -3.11 tools/test_exit_hold_log.py
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
    {"id": "L2C0#0", "policy": "yield", "exitHoldSec": 3.5, "steps": []},
    {"id": "L2C1#0", "policy": "yield", "exitHoldSec": 0.0, "steps": []},
    {"id": "L3C0#0", "policy": "hold", "exitHoldSec": 3.5, "steps": []},
]


def levels(events, takes=TAKES):
    _, verdicts = xp.exit_hold_findings(events, takes)
    return [level for level, _ in verdicts]


class ExitHoldAnalyzerTests(unittest.TestCase):
    def test_nothing_happened_says_nothing(self):
        self.assertEqual(xp.exit_hold_findings([ev("take", 1, st="end", id="L2C0#0", why="yield")], TAKES), ([], []))

    def test_folded_within_the_hold_passes(self):
        events = [ev("exithold", 10.0, take="L2C0#0", sec="3.50", n=1),
                  ev("take", 13.52, st="end", id="L2C0#0", why="yield")]
        self.assertEqual(levels(events), ["OK"])

    def test_completed_during_the_hold_passes(self):
        events = [ev("exithold", 10.0, take="L2C0#0", sec="3.50", n=1),
                  ev("take", 11.0, st="end", id="L2C0#0", why="done")]
        self.assertEqual(levels(events), ["OK"])

    def test_held_too_long_fails(self):
        events = [ev("exithold", 10.0, take="L2C0#0", sec="3.50", n=1),
                  ev("take", 14.2, st="end", id="L2C0#0", why="yield")]
        self.assertEqual(levels(events), ["FAIL"])

    def test_never_ended_fails(self):
        events = [ev("exithold", 10.0, take="L2C0#0", sec="3.50", n=1)]
        self.assertEqual(levels(events), ["FAIL"])

    def test_take_without_exit_hold_in_the_script_fails(self):
        events = [ev("exithold", 10.0, take="L2C1#0", sec="3.50", n=1),
                  ev("take", 11.0, st="end", id="L2C1#0", why="yield")]
        self.assertIn("FAIL", levels(events))

    def test_hold_policy_take_fails(self):
        events = [ev("exithold", 10.0, take="L3C0#0", sec="3.50", n=1),
                  ev("take", 11.0, st="end", id="L3C0#0", why="yield")]
        self.assertIn("FAIL", levels(events))

    def test_end_before_the_hold_is_not_matched(self):
        events = [ev("take", 9.0, st="end", id="L2C0#0", why="yield"),
                  ev("exithold", 10.0, take="L2C0#0", sec="3.50", n=1)]
        self.assertEqual(levels(events), ["FAIL"])


class WriterReaderTests(unittest.TestCase):
    def test_csharp_writes_the_event(self):
        host = read("Assets/Scripts/Diagnostics/ShowTelemetryHost.cs")
        self.assertIn('Emit($"ev=exithold take=', host)
        self.assertIn('$"sec={_takes.LastExitHoldSec.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} n={_takes.ExitHoldCount}"', host)

    def test_logic_holds_and_folds(self):
        logic = read("Assets/Scripts/Streaming/TakeRunnerLogic.cs")
        self.assertIn("public float exitHoldSec;", logic)
        self.assertIn("if (_exitHolding && now >= _exitHoldUntil)", logic)
        self.assertIn("held.yieldedForward = true;", logic)

    def test_field_name_matches_schema_desk_and_analyzer(self):
        self.assertIn("public float exitHoldSec;", read("Assets/Scripts/Streaming/ShowTakeSchema.cs"))
        model = read("tools/web-compositor/timeline-model.js")
        self.assertEqual(model.count("exitHoldSec: num(t.exitHoldSec, 0)"), 2)
        self.assertIn("exitHoldSec: 0,", model)
        src = read("tools/analyze-xp-log.py")
        self.assertIn('"exitHoldSec": float(t.get("exitHoldSec") or 0)', src)
        self.assertIn('"policy": t.get("policy") or "hold"', src)

    def test_dwell_check_allows_the_hold(self):
        src = read("tools/analyze-xp-log.py")
        self.assertIn('hold = float(t.get("exitHoldSec") or 0) if t.get("policy") == "yield" else 0.0', src)
        self.assertIn("elif hold > 0 and over <= hold + EXIT_HOLD_SLACK_SEC:", src)

    def test_end_reason_word_is_yield(self):
        host = read("Assets/Scripts/Diagnostics/ShowTelemetryHost.cs")
        self.assertIn('TakeRunnerLogic.EndReason.Yielded => "yield"', host)


if __name__ == "__main__":
    unittest.main()
