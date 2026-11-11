"""tools/twin-look.py の場面の切り方と、コマを切る時刻。動画も Unity も使わない。"""

import importlib.util
import sys
import unittest
from pathlib import Path

TOOLS = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("twin_look", TOOLS / "twin-look.py")
tl = importlib.util.module_from_spec(spec)
sys.modules["twin_look"] = tl
spec.loader.exec_module(tl)


def ev(t: float, name: str, text: str = "") -> dict:
    return {"t": t, "ev": name, "text": text}


RUN = [
    ev(-9.0, "intro", "stage=Black veil=1"),
    ev(5.0, "intro", "stage=Real veil=1"),
    ev(10.0, "seg", "lap=1 cam=0 plap=1"),
    ev(10.1, "outro", "stage=Off pw=1.00"),
    ev(20.0, "seg", "lap=1 cam=1 plap=1"),
    ev(20.1, "cue", "id=L1C1#0#0 st=on ovl=0.00"),
    ev(24.0, "sfx", "id=knock"),
    ev(25.0, "cue", "id=L1C1#0#0 st=off ovl=0.00"),
    ev(30.0, "seg", "lap=4 cam=0 plap=4"),
    ev(33.0, "mark", "n=1"),
    ev(40.0, "outro", "stage=Noise pw=1.00"),
    ev(41.0, "outro", "stage=Dark pw=0.00"),
]


class BuildScenes(unittest.TestCase):
    def setUp(self):
        self.scenes = tl.build_scenes(RUN, 60.0)

    def test_order_and_names(self):
        self.assertEqual([s.name for s in self.scenes], ["00_導入", "01_1-A", "02_1-B", "03_4-A", "04_終幕"])

    def test_bounds_are_contiguous_and_end_at_the_video(self):
        self.assertEqual([(s.t0, s.t1) for s in self.scenes],
                         [(0.0, 10.0), (10.0, 20.0), (20.0, 30.0), (30.0, 40.0), (40.0, 60.0)])

    def test_outro_off_does_not_start_the_ending(self):
        self.assertEqual(sum(1 for s in self.scenes if "終幕" in s.name), 1)

    def test_cue_off_and_sound_are_not_cut_but_sound_is_counted(self):
        b = self.scenes[2]
        self.assertEqual([sig for _, sig, _ in b.marks], ["cue:L1C1#0#0"])
        self.assertEqual(b.counts["sfx"], 1)

    def test_events_before_the_recording_are_not_cut(self):
        self.assertEqual([sig for _, sig, _ in self.scenes[0].marks], ["intro:Real"])

    def test_report_is_counted_in_its_scene(self):
        self.assertEqual(self.scenes[3].counts["mark"], 1)

    def test_revisited_segment_gets_a_suffix(self):
        scenes = tl.build_scenes([ev(1, "seg", "cam=0 plap=1"), ev(2, "seg", "cam=1 plap=1"), ev(3, "seg", "cam=0 plap=1")], 5.0)
        self.assertEqual([s.name for s in scenes], ["00_導入", "01_1-A", "02_1-B", "03_1-A#2"])


class Picks(unittest.TestCase):
    def test_head_marks_fill_end_stay_inside_the_scene(self):
        sc = tl.Scene("02_1-B", 20.0, 30.0, marks=[(20.1, "cue:X", "cue X")])
        rows = tl.picks(sc, 4.0)
        self.assertTrue(all(20.0 <= t < 30.0 for t, _, _ in rows))
        self.assertEqual(rows[0][1], "head")
        self.assertEqual(rows[-1][1], "end")
        self.assertEqual(sorted(t for t, _, _ in rows), [t for t, _, _ in rows])

    def test_a_mark_at_the_head_merges_into_one_frame(self):
        sc = tl.Scene("x", 20.0, 30.0, marks=[(20.1, "cue:X", "cue X")])
        heads = [r for r in tl.picks(sc, 0) if r[0] < 21.0]
        self.assertEqual(len(heads), 1)
        self.assertIn("cue X", heads[0][2])

    def test_gaps_longer_than_the_limit_are_filled(self):
        sc = tl.Scene("x", 0.0, 20.0)
        times = [t for t, _, _ in tl.picks(sc, 4.0)]
        self.assertLessEqual(max(b - a for a, b in zip(times, times[1:])), 4.0 + 1e-6)

    def test_pair_keys_do_not_depend_on_scene_length(self):
        short = {k for _, k, _ in tl.picks(tl.Scene("x", 0.0, 8.0), 0)}
        long = {k for _, k, _ in tl.picks(tl.Scene("x", 0.0, 40.0), 0)}
        self.assertEqual(short, long)
        self.assertEqual(short, {"head", "q1", "q2", "q3", "end"})

    def test_repeated_events_get_an_occurrence_number(self):
        sc = tl.Scene("x", 0.0, 20.0, marks=[(5.0, "step:T", "a"), (10.0, "step:T", "b")])
        keys = [k for _, k, _ in tl.picks(sc, 0)]
        self.assertIn("step:T#1", keys)
        self.assertIn("step:T#2", keys)

    def test_empty_scene_has_no_frames(self):
        self.assertEqual(tl.picks(tl.Scene("x", 5.0, 5.0), 4.0), [])


class ReplayLog(unittest.TestCase):
    def test_tau_is_the_clock_and_samples_are_dropped(self):
        text = "\n".join([
            "tau=12.50 [XP] t=28.10 ev=seg lap=1 cam=0 plap=1",
            "tau=13.00 [XP] t=28.60 ev=sum fps=72",
            "[XP] t=30.00 ev=seg lap=1 cam=1 plap=1",
        ])
        self.assertEqual(tl.parse_log(text), [{"t": 12.5, "ev": "seg", "text": "lap=1 cam=0 plap=1"}])


class Signature(unittest.TestCase):
    def test_continuous_values_do_not_split_the_name(self):
        self.assertEqual(tl.signature("commsInvasion", "v=0.37 lap=2"), tl.signature("commsInvasion", "v=1.00 lap=3"))

    def test_stage_names_the_intro_and_outro(self):
        self.assertEqual(tl.signature("outro", "stage=Dark pw=0.00"), "outro:Dark")


if __name__ == "__main__":
    unittest.main()
