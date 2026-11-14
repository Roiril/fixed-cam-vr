"""歩いた跡の上に立つ人形の歩き出し（step の trailFromX / trailFromZ）の、ログの書き手と読み手を突き合わせる。

- 書き手は C# の ShowTelemetryHost（ev=trail station= x= z= vis= gap= from=）。from は ShowCgLayer.TrailFromActive
- 読み手は tools/analyze-xp-log.py の trail_findings。from=1 の station は hop の回数、from=0 / 無しは gap の飛び石
- 旗の名前（trailFromX / trailFromZ / trailHopSec）は C# の ShowStepDef と卓の timeline-model.js の白名簿で揃える

    py -3.11 tools/test_trail_from_log.py
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


def trail(t, station, frm=None, vis=1):
    kw = dict(station=station, x="0.10", z="1.20", vis=vis, gap="1.80")
    if frm is not None:
        kw["from"] = frm
    return ev("trail", t, **kw)


FROM_STEP = {"cueId": "", "cg": "doll", "cgMode": "trail", "trailFromX": 0.4, "trailFromZ": 1.1}
TAKES = [{"id": "L2C0#0", "steps": [{"cueId": "a"}, FROM_STEP]}]


class TrailFindingsTests(unittest.TestCase):
    def levels(self, events, takes=TAKES):
        _, verdicts = xp.trail_findings(events, takes)
        return [level for level, _ in verdicts]

    def test_nothing_says_nothing(self):
        self.assertEqual(xp.trail_findings([], [{"id": "x", "steps": [{"cg": "doll", "cgMode": "trail"}]}]), ([], []))

    def test_old_log_without_from_counts_as_gap_stations(self):
        lines, verdicts = xp.trail_findings([trail(1, 3), trail(2, 4)], None)
        self.assertEqual(verdicts, [])
        self.assertIn("立った飛び石 2 個", lines[0])
        self.assertIn("歩き出しの hop 0 個", lines[0])

    def test_hops_and_gap_stations_are_counted_apart(self):
        rows = [trail(1, 0, 1), trail(1.5, 1, 1), trail(2, 2, 1), trail(2.5, 2, 0), trail(3, 3, 0)]
        lines, _ = xp.trail_findings(rows, None)
        self.assertIn("立った飛び石 2 個", lines[0])
        self.assertIn("歩き出しの hop 3 個", lines[0])
        self.assertTrue(lines[-1].endswith("from=0"))

    def test_from_cut_shown_with_from_rows_passes(self):
        events = [ev("step", 10, take="L2C0#0", i=1, played=1), trail(10, 0, 1), trail(10.6, 1, 1)]
        self.assertEqual(self.levels(events), ["OK"])

    def test_from_cut_shown_without_from_rows_warns(self):
        events = [ev("step", 10, take="L2C0#0", i=1, played=1), trail(10, 7, 0)]
        self.assertEqual(self.levels(events), ["WARN"])

    def test_from_cut_not_reached_warns(self):
        self.assertEqual(self.levels([ev("step", 10, take="L2C0#0", i=1, played=0)]), ["WARN"])

    def test_half_authored_point_is_not_a_from_cut(self):
        takes = [{"id": "L2C0#0", "steps": [{"cg": "doll", "cgMode": "trail", "trailFromX": 0.4}]}]
        self.assertEqual(xp.trail_findings([], takes), ([], []))

    def test_expected_takes_carry_the_point(self):
        show = {"timeline": {"segments": [{"lap": 2, "camera": 0, "takes": [
            {"id": "L2C0#0", "steps": [dict(FROM_STEP, source="live")]}]}]}}
        takes = xp.expected_from_show(show)["takes"]
        _, verdicts = xp.trail_findings([ev("step", 1, take="L2C0#0", i=0, played=1)], takes)
        self.assertEqual([level for level, _ in verdicts], ["WARN"])


class TrailFromWriterReaderTests(unittest.TestCase):
    def test_csharp_writes_from(self):
        host = read("Assets/Scripts/Diagnostics/ShowTelemetryHost.cs")
        self.assertIn('Emit($"ev=trail station={trailStation} x={st.x:F2} z={st.y:F2} vis={(trailShown ? 1 : 0)} "', host)
        self.assertIn('from={(trailFrom ? 1 : 0)}");', host)
        self.assertIn("tcg!.TrailFromActive", host)

    def test_field_names_match_schema_and_desk(self):
        schema = read("Assets/Scripts/Streaming/ShowTakeSchema.cs")
        self.assertIn("public float trailFromX = float.NaN;", schema)
        self.assertIn("public float trailFromZ = float.NaN;", schema)
        self.assertIn("public float trailHopSec;", schema)
        model = read("tools/web-compositor/timeline-model.js")
        for key in ("trailFromX", "trailFromZ", "trailHopSec"):
            self.assertIn(key, model)
        ribbon = read("tools/web-compositor/ribbon.js")
        for cls in ("rb-s-tfx", "rb-s-tfz", "rb-s-thop"):
            self.assertIn(cls, ribbon)

    def test_runner_passes_the_point_to_the_layer(self):
        runner = read("Assets/Scripts/Streaming/TakeRunner.cs")
        self.assertIn("step.trailFromX, step.trailFromZ, step.trailHopSec);", runner)


if __name__ == "__main__":
    unittest.main()
