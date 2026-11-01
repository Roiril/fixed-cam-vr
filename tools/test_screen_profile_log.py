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

    def test_normal_material_passes(self):
        self.assertEqual([level for level, _ in self.profile_verdicts(self.sample())], ["OK"])

    def test_wrong_noise_material_fails(self):
        self.assertEqual([level for level, _ in self.profile_verdicts(self.sample(noiseMat=".05"))], ["FAIL"])

    def test_wrong_mono_material_fails(self):
        self.assertEqual([level for level, _ in self.profile_verdicts(self.sample(monoMat="0"))], ["FAIL"])

    def test_wrong_blocks_material_fails(self):
        self.assertEqual([level for level, _ in self.profile_verdicts(self.sample(blocksMat="0"))], ["FAIL"])

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


if __name__ == "__main__":
    unittest.main()
