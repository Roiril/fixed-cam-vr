"""長押しの間だけ人形の下からライブが透ける演出（take の markBleed）の、ログの書き手と読み手を突き合わせる。

- 書き手は C# の ShowTelemetryHost（ev=bleed take= max=）、読み手は tools/analyze-xp-log.py の live_bleed_findings。
  片方だけ直すと判定が黙って空振りする
- 台本の旗の名前は C# の ShowTakeDef.markBleed・卓の timeline-model.js・解析器の 3 か所で同じ

    py -3.11 tools/test_live_bleed_log.py
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


ARMED = [{"id": "L3C1#0", "markBleed": True}, {"id": "L3C0#0", "markBleed": False}]


class LiveBleedAnalyzerTests(unittest.TestCase):
    def levels(self, events, takes=ARMED):
        _, verdicts = xp.live_bleed_findings(events, takes)
        return [level for level, _ in verdicts]

    def plate_mark(self, bleed_max):
        return [ev("step", 10, take="L3C1#0", i=0, src="rec", played=1),
                ev("step", 14, take="L3C1#0", i=1, src="plate", played=1),
                ev("mark", 16, n=3, take="L3C1#0"),
                ev("bleed", 16.5, take="L3C1#0", max=bleed_max)]

    def test_bleed_seen_during_doll_cut_passes(self):
        self.assertEqual(self.levels(self.plate_mark("0.42")), ["OK"])

    def test_mark_on_doll_cut_without_bleed_fails(self):
        self.assertEqual(self.levels(self.plate_mark("0.01")), ["FAIL"])
        events = self.plate_mark("0.42")[:-1]   # ev=bleed が 1 行も無い
        self.assertEqual(self.levels(events), ["FAIL"])

    def test_mark_only_on_rec_cut_is_not_a_failure(self):
        events = [ev("step", 10, take="L3C1#0", i=0, src="rec", played=1),
                  ev("mark", 11, n=1, take="L3C1#0")]
        self.assertEqual(self.levels(events), ["WARN"])

    def test_script_without_flag_says_nothing(self):
        lines, verdicts = xp.live_bleed_findings(self.plate_mark("0.0"), [{"id": "L3C1#0", "markBleed": False}])
        self.assertEqual((lines, verdicts), ([], []))

    def test_run_start_edge_is_not_a_mark(self):
        events = [ev("step", 14, take="L3C1#0", i=1, src="plate", played=1),
                  ev("mark", 14.1, n=0, take="L3C1#0")]
        self.assertEqual(self.levels(events), ["WARN"])

    def test_expected_takes_carry_the_flag(self):
        show = {"timeline": {"segments": [{"lap": 3, "camera": 1, "takes": [
            {"id": "L3C1#0", "markBleed": True, "steps": []}, {"id": "L3C1#1", "steps": []}]}]}}
        takes = xp.expected_from_show(show)["takes"]
        self.assertEqual([t["markBleed"] for t in takes], [True, False])


class LiveBleedWriterReaderTests(unittest.TestCase):
    def test_csharp_writes_what_the_analyzer_reads(self):
        host = read("Assets/Scripts/Diagnostics/ShowTelemetryHost.cs")
        self.assertRegex(host, r'Emit\(\$"ev=bleed take=\{')
        self.assertIn('max={_overlay.BleedLastHoldMax:F2}', host)

    def test_flag_name_is_the_same_everywhere(self):
        self.assertRegex(read("Assets/Scripts/Streaming/ShowTakeSchema.cs"), r"public bool markBleed;")
        model = read("tools/web-compositor/timeline-model.js")
        self.assertEqual(len(re.findall(r"\bmarkBleed:", model)), 3, "既定・保存・読み込みの 3 か所")

    def test_threshold_is_below_what_a_full_hold_reaches(self):
        logic = read("Assets/Scripts/Streaming/LiveBleedLogic.cs")
        m = re.search(r"const float MaxBleed = ([0-9.]+)f;", logic)
        self.assertIsNotNone(m)
        self.assertLess(xp.LIVE_BLEED_MIN_MAX, float(m.group(1)) * 0.5)


if __name__ == "__main__":
    unittest.main()
