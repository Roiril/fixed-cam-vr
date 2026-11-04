"""Selected screen telemetry: exercise the real analyzer with positive and negative observations."""
import importlib.util
from pathlib import Path
import unittest


spec = importlib.util.spec_from_file_location("xp_analyzer", Path(__file__).with_name("analyze-xp-log.py"))
xp = importlib.util.module_from_spec(spec)
spec.loader.exec_module(xp)


class ScreenProfileLogTests(unittest.TestCase):
    def sample(self, **changes):
        event = dict(ev="sum", t="1", _t=1.0, _cams=[], screenProfile="web20261101", coarseMode="Normal",
                     coarseShown=".5", coarsePos=".5", monoMat=".5", blocksMat="533.5",
                     noisePos=".075", noiseShown=".075", noiseMat=".075")
        event.update(changes)
        return event

    def profile_verdicts(self, event):
        _, verdicts = xp.analyze([event], [], xp.expected_from_show({}))
        return [(level, text) for level, text in verdicts if "web20261101" in text]

    def lens_verdicts(self, *pairs):
        events = [dict(self.sample(t=str(i), _t=float(i)), cam=str(cam), lensFish=fish) for i, (cam, fish) in enumerate(pairs)]
        _, verdicts = xp.analyze(events, [], xp.expected_from_show({}))
        return [level for level, text in verdicts if "魚眼" in text]

    def test_fisheye_only_on_camera_c_passes(self):
        self.assertEqual(self.lens_verdicts((0, "0"), (2, "1"), (2, "1")), ["OK"])

    def test_fisheye_missing_on_camera_c_fails(self):
        self.assertEqual(self.lens_verdicts((2, "0"), (2, "0")), ["FAIL"])

    def test_fisheye_on_other_camera_fails(self):
        self.assertEqual(self.lens_verdicts((0, "1"), (1, "1"), (2, "1")), ["FAIL"])

    def test_one_sample_at_the_switch_is_allowed(self):
        self.assertEqual(self.lens_verdicts((2, "1"), (1, "1"), (1, "0")), ["OK"])

    def test_normal_material_passes(self):
        self.assertEqual([level for level, _ in self.profile_verdicts(self.sample())], ["OK"])

    def test_wrong_noise_material_fails(self):
        self.assertEqual([level for level, _ in self.profile_verdicts(self.sample(noiseMat=".05"))], ["FAIL"])

    def test_wrong_mono_material_fails(self):
        self.assertEqual([level for level, _ in self.profile_verdicts(self.sample(monoMat="0"))], ["FAIL"])

    def test_wrong_blocks_material_fails(self):
        self.assertEqual([level for level, _ in self.profile_verdicts(self.sample(blocksMat="0"))], ["FAIL"])

    def test_rounded_shown_value_during_release_passes(self):
        event = self.sample(coarseMode="Release", coarseShown=".53", monoMat=".534", blocksMat="515.1")
        self.assertNotIn("FAIL", [level for level, _ in self.profile_verdicts(event)])

    def test_blocks_are_compared_with_the_material_mono(self):
        event = self.sample(coarseShown=".53", monoMat=".534", blocksMat="530")
        self.assertEqual([level for level, _ in self.profile_verdicts(event)], ["FAIL"])

    def test_nonfinite_material_fails(self):
        self.assertEqual([level for level, _ in self.profile_verdicts(self.sample(monoMat="NaN"))], ["FAIL"])

    def test_legacy_is_identified_and_excluded(self):
        event = self.sample()
        del event["screenProfile"]
        report, verdicts = xp.analyze([event], [], xp.expected_from_show({}))
        self.assertTrue(any("legacy" in line for line in report))
        self.assertFalse(any("web20261101" in text for _, text in verdicts))

    def test_adopted_release_accepts_half_mono_initial_state(self):
        event = self.sample(coarse=".8", coarseRel="1", coarseMode="Release",
                            noisePos=".2", noiseShown=".05", noiseMat=".05")
        _, verdicts = xp.analyze([event], [], xp.expected_from_show({}))
        self.assertFalse(any(level == "FAIL" and "戻り切っていない" in text for level, text in verdicts))
        self.assertEqual([level for level, text in verdicts if "web20261101" in text], ["OK"])

    def doll(self, **changes):
        return self.sample(screenProfile="dollweb20261101", dollProfileBlend="1", dollProfileMat="1",
                           coarseMode="DollHold", coarseShown=".56", monoMat=".56", blocksMat="501.52",
                           noisePos=".2", noiseShown=".34", noiseMat=".34", **changes)

    def test_doll_profile_and_legacy_hold_pass(self):
        self.assertEqual([level for level, _ in self.profile_verdicts(self.doll())], ["OK"])
        legacy = self.sample(coarseMode="DollHold", coarseShown="1", monoMat="1", blocksMat="267",
                             noisePos=".2", noiseShown=".2", noiseMat=".2")
        self.assertEqual([level for level, _ in self.profile_verdicts(legacy)], ["OK"])

    def test_doll_old_contract_or_lost_material_blend_fails(self):
        for changes in ({"coarseShown": "1", "monoMat": "1", "blocksMat": "267"},
                        {"noiseShown": ".2", "noiseMat": ".2"}, {"dollProfileMat": "0"}):
            event = self.doll()
            event.update(changes)
            self.assertEqual([level for level, _ in self.profile_verdicts(event)], ["FAIL"])

    def test_morph_pov_normal_and_release_pass(self):
        cases = [dict(coarseMode="DollMorph", coarseShown=".53", monoMat=".53", blocksMat="517.51",
                      noiseShown=".27", noiseMat=".27", dollProfileBlend=".5", dollProfileMat=".5"),
                 dict(coarseMode="DollPov", coarseShown="1", monoMat="1", blocksMat="267",
                      noiseShown=".2", noiseMat=".2", dollProfileBlend="0", dollProfileMat="0"),
                 dict(coarseMode="Normal", coarseShown=".5", monoMat=".5", blocksMat="533.5",
                      noiseShown=".2", noiseMat=".2", dollProfileBlend="0", dollProfileMat="0"),
                 dict(coarseMode="Release", coarseShown=".53", monoMat=".53", blocksMat="517.51",
                      noiseShown=".195", noiseMat=".195", dollProfileBlend=".5", dollProfileMat=".5")]
        for changes in cases:
            event = self.doll()
            event.update(changes)
            self.assertEqual([level for level, _ in self.profile_verdicts(event)], ["OK"])


if __name__ == "__main__":
    unittest.main()
