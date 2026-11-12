"""滲んで現れる・長押しで膨らむ（0349）の、ログの書き手と読み手、試し描きと実機の数値を突き合わせる。

- 書き手は C# の ShowTelemetryHost（ev=cue rev= / revMax= / ev=take pressMax= / ev=sum ovlRev= ovlPress=）、
  読み手は tools/analyze-xp-log.py の bleed_findings。片方だけ直すと判定が黙って空振りする
- 試し描き（tools/gen-plate/bleed_preview.py）は実機の式を写したもの。数値が C#（OverlayRevealLogic・
  OverlayPressLogic）やシェーダ（CrtCompositeShared.hlsl）とずれると、試し描きで決めた見た目が実機で出ない

    py -3.11 tools/test_bleed_log.py
"""

import importlib.util
import re
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
TOOLS = ROOT / "tools"

spec = importlib.util.spec_from_file_location("xp_analyzer", TOOLS / "analyze-xp-log.py")
xp = importlib.util.module_from_spec(spec)
spec.loader.exec_module(xp)

sys.path.insert(0, str(TOOLS / "gen-plate"))
import bleed_preview as bp  # noqa: E402


def read(rel):
    return (ROOT / rel).read_text(encoding="utf-8")


def cs_const(text, name):
    m = re.search(r"const float " + name + r"\s*=\s*(-?[0-9.]+)f(?:\s*/\s*([0-9.]+)f)?", text)
    if not m:
        raise AssertionError(f"C# に const float {name} が無い")
    v = float(m.group(1))
    return v / float(m.group(2)) if m.group(2) else v


def cue(t, st, cid, **kw):
    return dict(ev="cue", t=str(t), st=st, id=cid, _cams=[], **kw)


def take(t, st, tid, **kw):
    return dict(ev="take", t=str(t), st=st, id=tid, _cams=[], **kw)


class BleedAnalyzerTests(unittest.TestCase):
    def levels(self, events):
        _, verdicts = xp.bleed_findings(events)
        return [level for level, _ in verdicts]

    def test_full_reveal_passes(self):
        self.assertEqual(self.levels([cue(10, "on", "cue_stain_A", rev="0.60+4.20"),
                                      cue(20, "off", "cue_stain_A", revMax="1.00")]), ["OK"])

    def test_reveal_that_never_grew_fails(self):
        self.assertEqual(self.levels([cue(10, "on", "cue_stain_A", rev="0.60+4.20"),
                                      cue(20, "off", "cue_stain_A", revMax="0.10")]), ["FAIL"])

    def test_reveal_cut_short_by_the_visitor_is_not_judged(self):
        # 著作の秒より前に引っ込めた cue は最大が小さくても正しい（報告・区間の移動）
        self.assertEqual(self.levels([cue(10, "on", "cue_hands_B", rev="0.80+3.40"),
                                      cue(12, "off", "cue_hands_B", revMax="0.40")]), ["OK"])

    def test_cue_without_reveal_is_silent(self):
        self.assertEqual(self.levels([cue(10, "on", "plate_A"), cue(20, "off", "plate_A", ovl="1.00")]), [])

    def test_press_that_reached_the_bulge_passes(self):
        self.assertEqual(self.levels([take(5, "begin", "stain_A"),
                                      take(9, "end", "stain_A", why="mark", pressMax="0.98")]), ["OK"])

    def test_dismissed_take_without_bulge_fails(self):
        self.assertEqual(self.levels([take(5, "begin", "stain_A"),
                                      take(9, "end", "stain_A", why="mark", pressMax="0.05")]), ["FAIL"])

    def test_take_that_ended_without_report_is_not_judged(self):
        self.assertEqual(self.levels([take(9, "end", "stain_A", why="yield", pressMax="0.00")]), ["OK"])

    def test_analyzer_reports_the_section(self):
        events = [cue(10, "on", "cue_stain_A", rev="0.60+4.20"), cue(20, "off", "cue_stain_A", revMax="0.10")]
        _, verdicts = xp.analyze(events, [], xp.expected_from_show({}))
        self.assertTrue(any(level == "FAIL" and "cue_stain_A" in text for level, text in verdicts))


class BleedWriterTests(unittest.TestCase):
    def test_writer_emits_every_key_the_reader_uses(self):
        host = read("Assets/Scripts/Diagnostics/ShowTelemetryHost.cs")
        for key in ("rev=", "revMax=", "pressMax=", "ovlRev=", "ovlPress="):
            self.assertIn(" " + key, host, f"ShowTelemetryHost が {key} を書いていない")
        analyzer = read("tools/analyze-xp-log.py")
        for key in ('"rev"', '"revMax"', '"pressMax"'):
            self.assertIn(key, analyzer, f"analyze-xp-log.py が {key} を読んでいない")


class BleedConstantTests(unittest.TestCase):
    """試し描き・C#・シェーダの数値が一致しているか。"""

    def test_reveal_constants_match_csharp(self):
        cs = read("Assets/Scripts/Streaming/OverlayRevealLogic.cs")
        self.assertAlmostEqual(cs_const(cs, "DefaultSoft"), bp.REVEAL_SOFT)
        self.assertAlmostEqual(cs_const(cs, "CurvePow"), bp.REVEAL_CURVE_POW)
        self.assertAlmostEqual(cs_const(cs, "BreathMin"), bp.BREATH_MIN)
        self.assertAlmostEqual(cs_const(cs, "BreathPeriodSec"), bp.BREATH_PERIOD_SEC)

    def test_press_constants_match_csharp(self):
        cs = read("Assets/Scripts/Streaming/OverlayPressLogic.cs")
        self.assertAlmostEqual(cs_const(cs, "NaturalHz"), bp.SPRING_HZ)
        self.assertAlmostEqual(cs_const(cs, "DampingRatio"), bp.SPRING_DAMPING)
        self.assertAlmostEqual(cs_const(cs, "Min"), bp.PRESS_MIN)
        self.assertAlmostEqual(cs_const(cs, "Max"), bp.PRESS_MAX)
        self.assertAlmostEqual(cs_const(cs, "SubstepSec"), bp.SPRING_SUBSTEP_SEC)
        self.assertAlmostEqual(cs_const(cs, "MaxStepSec"), bp.SPRING_MAX_DT)
        self.assertAlmostEqual(cs_const(cs, "CollapseSec"), bp.PRESS_COLLAPSE_SEC)

    def test_shader_constants_match_preview(self):
        hlsl = read("Assets/Art/Shaders/Streaming/CrtCompositeShared.hlsl")
        self.assertIn(f"ratio * (1.0 - {bp.EDGE_DARK_MUL:.2f} * revealEdge)", hlsl)
        self.assertIn(f"overlay *= (1.0 - {bp.EDGE_DARK_REPLACE:.2f} * revealEdge)", hlsl)
        self.assertIn(f"({bp.PRESS_PULL:.2f} * _OverlayPress * pressM)", hlsl)
        lo, hi = bp.PRESS_W_EDGE
        self.assertIn(f"smoothstep({lo:.2f}, {hi:.2f}, saturate(_OverlayPress))", hlsl)

    def test_spring_settles_and_dips_like_the_csharp_contract(self):
        s = bp.PressSpring()
        for _ in range(180):
            s.tick(1 / 60, True, 1.0)
        self.assertAlmostEqual(s.p, 1.0, delta=0.02)
        low = 1.0
        for _ in range(60):
            low = min(low, s.tick(1 / 60, False, 0.0))
        self.assertLess(low, 0.0)
        coarse = bp.PressSpring()
        for _ in range(20):
            v = coarse.tick(1.0, True, 1.0)
            self.assertTrue(bp.PRESS_MIN <= v <= bp.PRESS_MAX)


class ShowApplyBleedTests(unittest.TestCase):
    """tools/show-apply-bleed.py の書き換え。台本のキーは C# の CueDef が読む名前と一致していること。"""

    @classmethod
    def setUpClass(cls):
        s = importlib.util.spec_from_file_location("show_apply_bleed", TOOLS / "show-apply-bleed.py")
        cls.mod = importlib.util.module_from_spec(s)
        s.loader.exec_module(cls.mod)

    def plan(self):
        return {
            "cue_stain_A": {"blend": "multiply", "sourceUrl": "/static-inputs/r.png",
                            "revealUrl": "/static-inputs/v.png", "revealSec": 4.2,
                            "revealDelaySec": 0.6, "revealOutSec": 0.45},
            "stain_A_press_bulge": {"pressMaskUrl": "/static-inputs/p.png", "pressCenter": [0.35, 0.74]},
        }

    def show(self):
        return {"rev": 3, "cues": [{"id": "cue_stain_A", "sourceUrl": "/static-inputs/old.png"},
                                   {"id": "stain_A_press_bulge"}],
                "timeline": {"segments": [{"takes": [{"id": "t", "markStartCueId": "stain_A_press_bulge"}]}]}}

    def test_patch_is_idempotent(self):
        show = self.show()
        first = self.mod.patch(show, self.plan())
        self.assertTrue(first)
        self.assertEqual(show["rev"], 4)
        self.assertEqual(show["cues"][0]["blend"], "multiply")
        self.assertEqual(show["cues"][1]["pressCenter"], [0.35, 0.74])
        self.assertEqual(self.mod.patch(show, self.plan()), [], "2 回目は何も変えない")
        self.assertEqual(show["rev"], 4, "変えないときは rev も上げない")

    def test_missing_cue_is_reported_not_created(self):
        show = {"rev": 1, "cues": []}
        lines = self.mod.patch(show, self.plan())
        self.assertTrue(all(ln.startswith("  !") for ln in lines))
        self.assertEqual(show["cues"], [])
        self.assertEqual(show["rev"], 1)

    def test_press_needs_a_take_that_names_the_cue(self):
        self.assertEqual(self.mod.press_take_check(self.show()), [])
        self.assertTrue(self.mod.press_take_check({"timeline": {"segments": []}}))

    def test_keys_are_read_by_csharp(self):
        cs = read("Assets/Scripts/Streaming/ShowControlClient.cs")
        for fields in self.plan().values():
            for k in fields:
                self.assertRegex(cs, r"public [^;=\n]+ " + k + r"\s*[=;]", f"CueDef に {k} が無い")
        self.assertRegex(cs, r"public [^;=\n]+ maskUrl\s*[=;]")

    def test_plan_reads_the_recipes(self):
        recipe = (TOOLS / "gen-plate/mask-recipes" / self.mod.STAIN_RECIPE)
        self.assertTrue(recipe.exists())
        if not (self.mod.BLEED_OUT / "stain_A_maps_20261113.json").exists():
            self.skipTest("bleed_maps.py の出力が無い")
        plan = self.mod.load_plan()
        self.assertEqual(set(plan), {"cue_hands_B", "cue_stain_A", "stain_A_press_bulge", "stain_A_done_doll"})
        self.assertEqual(plan["cue_stain_A"]["blend"], "multiply")
        self.assertEqual(plan["cue_hands_B"]["blend"], "multiply")
        self.assertEqual(len(plan["stain_A_press_bulge"]["pressCenter"]), 2)


if __name__ == "__main__":
    unittest.main()
