"""tools/twin-verify.py の判定（周の印・結末）。Unity を起動しない。"""

import importlib.util
import json
import sys
import tempfile
import unittest
from pathlib import Path

TOOLS = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("twin_verify", TOOLS / "twin-verify.py")
tv = importlib.util.module_from_spec(spec)
sys.modules["twin_verify"] = tv
spec.loader.exec_module(tv)


def lap(shown: int, src: str, seg: str) -> dict:
    return {"t": 0.0, "ev": "twinlap", "text": f"shown={shown} src={src} plap=3 seg={seg}"}


GOOD = [lap(2, "rec", "3/0"), lap(3, "live", "3/0"), lap(1, "rec", "3/1"), lap(1, "rec", "3/2")]


class TwinlapCheck(unittest.TestCase):
    def test_expected_laps_pass(self):
        self.assertEqual(tv.twinlap_check(GOOD)[0], "OK")

    def test_tail_of_the_next_lap_is_allowed(self):
        # 録画は区間を出たあとも少し回るので、カットの後ろに次の周の印が映る（実走行で確認）。
        self.assertEqual(tv.twinlap_check(GOOD + [lap(2, "rec", "3/2")])[0], "OK")

    def test_wrong_lap_at_the_head_fails(self):
        level, text = tv.twinlap_check([lap(1, "rec", "3/0"), lap(1, "rec", "3/1"), lap(1, "rec", "3/2")])
        self.assertEqual(level, "FAIL")
        self.assertIn("3-A", text)

    def test_head_correct_but_jumping_two_laps_fails(self):
        self.assertEqual(tv.twinlap_check(GOOD + [lap(3, "rec", "3/2")])[0], "FAIL")

    def test_live_badge_does_not_count_as_recording(self):
        self.assertEqual(tv.twinlap_check([lap(3, "live", "3/0")])[0], "WARN")

    def test_no_badge_is_not_a_pass(self):
        self.assertEqual(tv.twinlap_check([])[0], "WARN")


class EndingChecks(unittest.TestCase):
    def check(self, text: str, calib: str = "twin", nomark: bool = False):
        with tempfile.TemporaryDirectory() as d:
            log = Path(d) / "run_xp.log"
            log.write_text(text, encoding="utf-8")
            return tv.ending_checks(log, calib, nomark)

    def test_released_with_the_requested_calib_passes(self):
        got = self.check("[XP] t=1 ev=config a=1 calib=twin\n"
                         "[XP] t=1.5 ev=farewell phase=turn cg=1 shot=1 level=1 held=4.2\n"
                         "[XP] t=2 ev=outro stage=Report x=1 ending=Released\n")
        self.assertEqual([x[0] for x in got], ["OK", "OK"])

    def test_released_without_the_photo_is_named(self):
        got = self.check("[XP] t=1 ev=config calib=twin\n"
                         "[XP] t=1.5 ev=farewell phase=turn cg=1 shot=0 level=2 held=15.0\n"
                         "[XP] t=2 ev=outro stage=Report ending=Released\n")
        self.assertEqual(got[-1][0], "FAIL")
        self.assertIn("写真", got[-1][1])

    def test_other_calib_is_reported(self):
        got = self.check("[XP] t=1 ev=config calib=show\n[XP] t=2 ev=outro stage=Report ending=Released\n")
        self.assertEqual(got[0][0], "FAIL")
        self.assertIn("calib=twin", got[0][1])

    def test_interrupted_fails_when_a_report_was_made(self):
        got = self.check("[XP] t=1 ev=config calib=twin\n[XP] t=2 ev=outro stage=Report ending=Interrupted\n")
        self.assertEqual(got[0][0], "FAIL")

    def test_nomark_expects_trapped(self):
        got = self.check("[XP] t=1 ev=config calib=twin\n[XP] t=2 ev=outro stage=Report ending=Trapped\n", nomark=True)
        self.assertEqual(got[0][0], "OK")

    def test_unsolved_farewell_is_named(self):
        got = self.check("[XP] t=1 ev=config calib=twin\n"
                         "[XP] t=2 ev=outro stage=Report ending=Interrupted\n"
                         "[EndingFarewell] placement unavailable visitors=325 dolls=6\n")
        self.assertTrue(any("カメラ A" in x[1] for x in got))


class ReadEvents(unittest.TestCase):
    def test_time_starts_at_recording_head_and_samples_are_dropped(self):
        with tempfile.TemporaryDirectory() as d:
            log = Path(d) / "run_xp.log"
            log.write_text("[XP] t=10.00 ev=xprec state=start\n"
                           "[XP] t=11.00 ev=sum fps=90\n"
                           "[XP] t=12.50 ev=mark n=1\n", encoding="utf-8")
            self.assertEqual(tv.read_events(log), [{"t": 2.5, "ev": "mark", "text": "n=1"}])


class AssetCheck(unittest.TestCase):
    SWAPPED = {"plate_A.jpg": "tools/twin/plates/twin_A_plate.jpg", "gen_hands.png": "tools/twin/assets/twin_handsB.png"}

    def line(self, name: str) -> str:
        return f"[ShowAssetResolver] override {name} -> file://C:/x/{name}\n"

    def test_all_read_passes(self):
        log = self.line("plate_A.jpg") + self.line("gen_hands.png") + self.line("plate_A.jpg")
        self.assertEqual(tv.asset_check(log, self.SWAPPED), ("OK", "ツインの合成素材を 2/2 本読んだ"))

    def test_none_read_fails(self):
        # 2026-11-11: 行が run_xp.log に写っていなかったとき 0 本と出た。0 本は本番の素材で走ったのと区別できない。
        level, text = tv.asset_check("[XP] t=1.00 ev=sum fps=90\n", self.SWAPPED)
        self.assertEqual(level, "FAIL")
        self.assertIn("0/2", text)

    def test_partial_names_the_missing_one(self):
        level, text = tv.asset_check(self.line("plate_A.jpg") + self.line("other.png"), self.SWAPPED)
        self.assertEqual(level, "WARN")
        self.assertIn("1/2", text)
        self.assertIn("gen_hands.png", text)


class Lock(unittest.TestCase):
    def test_free_lock_is_taken_and_released(self):
        with tempfile.TemporaryDirectory() as d:
            lock = Path(d) / "x" / ".lock"
            self.assertIsNone(tv.acquire_lock(lock, "s1"))
            self.assertTrue(lock.exists())
            tv.release_lock(lock)
            self.assertFalse(lock.exists())

    def test_live_holder_blocks_and_is_named(self):
        with tempfile.TemporaryDirectory() as d:
            lock = Path(d) / ".lock"
            lock.write_text('{"pid": 4242, "stamp": "other", "started": "10:00:00"}', encoding="utf-8")
            msg = tv.acquire_lock(lock, "s1", alive=lambda pid: True)
            self.assertIn("other", msg)
            tv.release_lock(lock)                       # 他人の錠は外さない
            self.assertTrue(lock.exists())

    def test_dead_holder_is_taken_over(self):
        with tempfile.TemporaryDirectory() as d:
            lock = Path(d) / ".lock"
            lock.write_text('{"pid": 4242, "stamp": "dead"}', encoding="utf-8")
            self.assertIsNone(tv.acquire_lock(lock, "s1", alive=lambda pid: False))

    def test_broken_lock_file_is_taken_over(self):
        with tempfile.TemporaryDirectory() as d:
            lock = Path(d) / ".lock"
            lock.write_text("", encoding="utf-8")
            self.assertIsNone(tv.acquire_lock(lock, "s1"))


class CodeCheck(unittest.TestCase):
    A = {"head": "aaa", "dirty": "111", "show": "s"}

    def test_same_code_says_nothing(self):
        self.assertIsNone(tv.code_check(self.A, dict(self.A)))

    def test_no_shoot_says_nothing(self):
        self.assertIsNone(tv.code_check(self.A, None))

    def test_edit_between_run_and_shoot_warns_and_names_what(self):
        level, text = tv.code_check(self.A, dict(self.A, dirty="222"))
        self.assertEqual(level, "WARN")
        self.assertIn("dirty", text)
        self.assertNotIn("head", text)


class Known(unittest.TestCase):
    K = [{"id": "name", "level": "WARN", "match": "^自己紹介は省略"},
         {"id": "count", "level": "WARN", "match": "^警告が 4 行"}]

    def test_known_is_tagged_and_new_is_not(self):
        v = [{"level": "WARN", "text": "自己紹介は省略される"}, {"level": "WARN", "text": "警告が 5 行"},
             {"level": "OK", "text": "自己紹介は省略される"}]
        tv.tag_known(v, self.K)
        self.assertEqual([x.get("known") for x in v], ["name", None, None])

    def test_level_must_match(self):
        v = [{"level": "FAIL", "text": "自己紹介は省略される"}]
        tv.tag_known(v, self.K)
        self.assertNotIn("known", v[0])

    def test_fail_entry_is_refused(self):
        with tempfile.TemporaryDirectory() as d:
            f = Path(d) / "known.json"
            f.write_text('{"entries": [{"id": "x", "level": "FAIL", "match": "a"}]}', encoding="utf-8")
            with self.assertRaises(SystemExit):
                tv.load_known(f)

    def test_shipped_table_loads(self):
        self.assertTrue(all({"id", "level", "match", "dep", "why", "ref", "since", "confirmed"} <= set(e) for e in tv.load_known()))

    def test_stale_needs_three_silent_runs(self):
        hit = [{"level": "WARN", "text": "自己紹介は省略される"}]
        self.assertEqual(tv.stale_known(self.K, [], [{"verdicts": []}]), [])                       # 履歴が足りない
        self.assertEqual(tv.stale_known(self.K, [], [{"verdicts": hit}, {"verdicts": []}]), ["count"])
        self.assertEqual(tv.stale_known(self.K, hit, [{"verdicts": []}, {"verdicts": []}]), ["count"])


class History(unittest.TestCase):
    def test_only_earlier_finished_runs_with_the_same_args(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            def put(name, args, result="ok", raw=None):
                (root / name).mkdir()
                (root / name / "verdict.json").write_text(
                    raw if raw is not None else json.dumps({"args": args, "run": {"result": result}, "stamp": name}), encoding="utf-8")
            put("20260101_a", {"speed": 1})
            put("20260102_b", {"speed": 2})
            put("20260103_c", {"speed": 1}, result="timeout")
            put("20260104_d", {"speed": 1}, raw="")
            put("20260105_e", {"speed": 1})
            put("20260107_g", {"speed": 1})
            got = tv.same_args_history(root, {"speed": 1}, "20260106_f", 5)
            self.assertEqual([v["stamp"] for v in got], ["20260105_e", "20260101_a"])


if __name__ == "__main__":
    unittest.main()
