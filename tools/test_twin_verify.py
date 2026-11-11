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


class RunFailed(unittest.TestCase):
    def test_a_run_that_never_ran_is_a_fail_line(self):
        v = tv.run_failed_verdict({"result": "走行の結果が無い"}, 1)
        self.assertEqual(v["level"], "FAIL")
        self.assertIn("走行の結果が無い", v["text"])

    def test_a_finished_run_adds_nothing(self):
        self.assertIsNone(tv.run_failed_verdict({"result": "ok"}, 0))
        # Editor の終了処理で落ちても、走行が ok なら別の WARN が言う。ここでは FAIL にしない。
        self.assertIsNone(tv.run_failed_verdict({"result": "ok"}, 3221225620))


class VideoPlan(unittest.TestCase):
    def test_default_shoots_the_eye_inside_the_run(self):
        self.assertEqual(tv.video_plan(False, False, False, False), {"mode": "single-pass", "capture": "eye", "replay": []})

    def test_two_pass_shoots_the_eye_by_replay(self):
        self.assertEqual(tv.video_plan(False, True, False, False), {"mode": "two-pass", "capture": "", "replay": ["eye"]})

    def test_no_video_shoots_nothing_even_with_other_flags(self):
        for two_pass in (False, True):
            for follow in (False, True):
                self.assertEqual(tv.video_plan(True, two_pass, False, follow), {"mode": "none", "capture": "", "replay": []})

    def test_follow_is_always_a_replay(self):
        # 撮影カメラは 1 台。後ろ上は目線と同時に撮れないので、どちらの撮り方でも再生で撮る。
        self.assertEqual(tv.video_plan(False, False, False, True), {"mode": "single-pass", "capture": "eye", "replay": ["follow"]})
        self.assertEqual(tv.video_plan(False, True, False, True), {"mode": "two-pass", "capture": "", "replay": ["eye", "follow"]})

    def test_no_eye_does_not_attach_the_capture(self):
        self.assertEqual(tv.video_plan(False, False, True, False), {"mode": "none", "capture": "", "replay": []})
        self.assertEqual(tv.video_plan(False, False, True, True), {"mode": "two-pass", "capture": "", "replay": ["follow"]})


class CommandLine(unittest.TestCase):
    """コマンドラインから twin-run の -Set まで。Unity を立てずに分岐だけ見る。"""

    def plan(self, *argv: str) -> tuple[dict, dict]:
        a = tv.build_parser().parse_args(list(argv))
        plan = tv.plan_of(a)
        return plan, tv.run_set_args(a, plan, Path("work"))

    def test_default_is_single_pass_and_asks_the_run_to_capture_at_52_degrees(self):
        plan, args = self.plan()
        self.assertEqual(plan["mode"], "single-pass")
        self.assertEqual(args["capture"], "eye")
        self.assertEqual(float(args["fov"]), 52.0)

    def test_fast_asks_the_run_for_the_fixed_step_clock_and_real_is_the_default(self):
        self.assertNotIn("fast", self.plan()[1])
        self.assertEqual(self.plan("--fast")[1]["fast"], "1")
        self.assertEqual(self.plan("--fast", "--no-video")[1]["fast"], "1")

    def test_fov_reaches_the_run(self):
        self.assertEqual(float(self.plan("--fov", "72")[1]["fov"]), 72.0)

    def test_two_pass_runs_without_capture(self):
        plan, args = self.plan("--two-pass")
        self.assertEqual(plan, {"mode": "two-pass", "capture": "", "replay": ["eye"]})
        self.assertNotIn("capture", args)
        self.assertNotIn("fov", args)

    def test_no_video_runs_without_capture(self):
        for argv in (("--no-video",), ("--no-video", "--two-pass"), ("--no-video", "--follow")):
            plan, args = self.plan(*argv)
            self.assertEqual(plan["mode"], "none", argv)
            self.assertNotIn("capture", args, argv)

    def test_run_args_other_than_capture_are_unchanged(self):
        # 撮り方で twin-run の他の値が変わらない（判定は同じ走り方で比べる）。
        _, single = self.plan("--nomark", "--marks", "1A,2b", "--calib", "show")
        _, two = self.plan("--nomark", "--marks", "1A,2b", "--calib", "show", "--two-pass")
        for k in ("capture", "fov"):
            single.pop(k)
        self.assertEqual(single, two)
        self.assertEqual(two["marks"], "1A.2B")


class SinglePassVideo(unittest.TestCase):
    def test_the_written_video_is_taken(self):
        with tempfile.TemporaryDirectory() as d:
            work = Path(d)
            (work / "eye.mp4").write_bytes(b"x")
            got, why = tv.single_pass_video(work, {"result": "ok", "eyeVideo": str(work / "eye.mp4")})
            self.assertEqual(got, work / "eye.mp4")
            self.assertIsNone(why)

    def test_the_capture_error_is_named(self):
        with tempfile.TemporaryDirectory() as d:
            got, why = tv.single_pass_video(Path(d), {"result": "ok", "eyeVideo": "error: ffmpeg が失敗した"})
            self.assertIsNone(got)
            self.assertIn("ffmpeg", why)

    def test_an_error_beats_a_leftover_file(self):
        # 音の多重化に失敗した回などは eye.mp4 の名前で何かが残りうる。撮影が error と言ったら採らない。
        with tempfile.TemporaryDirectory() as d:
            work = Path(d)
            (work / "eye.mp4").write_bytes(b"x")
            got, why = tv.single_pass_video(work, {"result": "ok", "eyeVideo": "error: 音の多重化に失敗した"})
            self.assertIsNone(got)
            self.assertIn("多重化", why)

    def test_a_run_without_the_field_says_so(self):
        with tempfile.TemporaryDirectory() as d:
            self.assertIn("返していない", tv.single_pass_video(Path(d), {"result": "ok"})[1])
            self.assertIn("eye.mp4", tv.single_pass_video(Path(d), {"result": "ok", "eyeVideo": ""})[1])

    def test_single_pass_fingerprints_match_so_no_warning(self):
        # 1 回方式は走行と撮影が同じ Play。twin-verify は code.shoot に走行時の指紋を入れるので、食い違いの WARN は出ない。
        run = {"head": "aaa", "dirty": "", "show": "s"}
        self.assertIsNone(tv.code_check(run, run))


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
