"""tools/verify.py の、回す門の選び方・起動コマンド・要点の抜き出し・集計。Unity も git も動かさない。"""

import argparse
import contextlib
import importlib.util
import io
import json
import sys
import tempfile
import unittest
from pathlib import Path

TOOLS = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("verify", TOOLS / "verify.py")
vf = importlib.util.module_from_spec(spec)
sys.modules["verify"] = vf
spec.loader.exec_module(vf)


def selected(argv: list[str]) -> list[str]:
    _, why = vf.select(argv)
    return [n for n, w in why.items() if not w]


def twin_cmd(argv: list[str]) -> list[str]:
    a, _ = vf.select(argv)
    return vf.GATES["twin"]["cmd"](vf.Options(real=a.real, no_video=a.no_video, tag=a.tag))


def exit_code(argv: list[str]) -> int | None:
    """select が SystemExit で抜けたときの終了コード。抜けなければ None。"""
    try:
        with contextlib.redirect_stderr(io.StringIO()):
            vf.select(argv)
    except SystemExit as e:
        return e.code
    return None


class Selection(unittest.TestCase):
    def test_default_runs_every_gate_in_table_order(self):
        self.assertEqual(selected([]), ["tools", "harness", "canon", "bake", "unity", "twin"])

    def test_quick_drops_the_heavy_two(self):
        self.assertEqual(selected(["--quick"]), ["tools", "harness", "canon", "bake"])
        _, why = vf.select(["--quick"])
        self.assertEqual(why["unity"], "--quick")
        self.assertEqual(why["twin"], "--quick")

    def test_only_and_skip(self):
        self.assertEqual(selected(["--only", "tools,bake"]), ["tools", "bake"])
        self.assertEqual(selected(["--only", " twin , canon "]), ["canon", "twin"])
        self.assertEqual(selected(["--skip", "twin,unity"]), ["tools", "harness", "canon", "bake"])
        self.assertEqual(selected(["--only", "tools,twin", "--skip", "twin"]), ["tools"])

    def test_unknown_gate_name_exits_2(self):
        self.assertEqual(exit_code(["--only", "tools,nope"]), 2)
        self.assertEqual(exit_code(["--skip", "unit"]), 2)

    def test_nothing_left_to_run_exits_2(self):
        self.assertEqual(exit_code(["--only", "twin", "--quick"]), 2)


class Commands(unittest.TestCase):
    def test_twin_default_is_fast_with_the_verify_tag(self):
        cmd = twin_cmd([])
        self.assertEqual(cmd[2], str(vf.ROOT / "tools" / "twin-verify.py"))
        self.assertIn("--fast", cmd)
        self.assertNotIn("--no-video", cmd)
        self.assertEqual(cmd[cmd.index("--tag") + 1], "verify")

    def test_real_drops_fast(self):
        self.assertNotIn("--fast", twin_cmd(["--real"]))

    def test_no_video_and_tag_are_passed(self):
        cmd = twin_cmd(["--no-video", "--tag", "fix-veil"])
        self.assertIn("--no-video", cmd)
        self.assertEqual(cmd[cmd.index("--tag") + 1], "fix-veil")

    def test_unity_runs_in_the_tree_when_synced(self):
        cmd = vf.GATES["unity"]["cmd"](vf.Options(tree=True))
        self.assertTrue(cmd[-2].endswith("twin-tree.py"))
        self.assertEqual(cmd[-1], "test")

    def test_unity_falls_back_to_the_main_tree(self):
        cmd = vf.GATES["unity"]["cmd"](vf.Options(tree=False))
        self.assertEqual(cmd[-2], str(vf.ROOT / "tools" / "unity.ps1"))
        self.assertEqual(cmd[-1], "test")

    def test_tree_test_command_points_at_the_tree(self):
        cmd = vf.twin_tree.unity_test_cmd(["PlayMode"])
        self.assertEqual(cmd[-3], str(vf.twin_tree.TREE / "tools" / "unity.ps1"))
        self.assertEqual(cmd[-2:], ["test", "PlayMode"])

    def test_light_gates(self):
        o = vf.Options()
        self.assertTrue(vf.GATES["tools"]["cmd"](o)[-1].endswith("test-tools.py"))
        self.assertTrue(vf.GATES["harness"]["cmd"](o)[-1].endswith("harness-check.py"))
        self.assertEqual(vf.GATES["canon"]["cmd"](o)[-1], "--check")
        self.assertEqual(vf.GATES["bake"]["cmd"](o)[-1], "bake-check")
        self.assertEqual([n for n, g in vf.GATES.items() if g["heavy"]], ["unity", "twin"])


class Lanes(unittest.TestCase):
    def test_with_the_tree_everything_runs_at_once(self):
        names = list(vf.GATES)
        self.assertEqual(vf.plan_lanes(names, tree=True), [[n] for n in names])

    def test_without_the_tree_unity_then_twin_share_a_lane(self):
        lanes = vf.plan_lanes(list(vf.GATES), tree=False)
        self.assertIn(["unity", "twin"], lanes)
        self.assertEqual(sum(len(l) for l in lanes), len(vf.GATES))

    def test_without_the_tree_a_lone_heavy_gate_is_alone(self):
        self.assertEqual(vf.plan_lanes(["tools", "twin"], tree=False), [["tools"], ["twin"]])


class Overall(unittest.TestCase):
    def results(self, *codes: str) -> list:
        return [vf.Result(f"g{i}", result=c) for i, c in enumerate(codes)]

    def test_one_fail_fails_the_whole(self):
        self.assertEqual(vf.overall(self.results("PASS", "FAIL", "SKIP")), 1)

    def test_skip_is_not_a_fail(self):
        self.assertEqual(vf.overall(self.results("PASS", "SKIP", "SKIP")), 0)

    def test_nonzero_exit_is_fail_even_if_the_point_looks_fine(self):
        self.assertEqual(vf.judge("bake", 1, "焼き直し待ち 1 件\n  - .\\tools\\unity.ps1 menu scene（説明）\n")[0], "FAIL")
        self.assertEqual(vf.judge("harness", 0, "harness-check: ERROR 0 / WARN 3\n")[0], "PASS")


class Points(unittest.TestCase):
    UNITY = ("== C:\\x\\fcv-twin-tree で unity.ps1 test \n"
             "EditMode : 全 2301 / 通過 2299 / 失敗 2 / 除外 4\n")

    def test_unity_counts(self):
        result, point = vf.judge("unity", 0, self.UNITY)
        self.assertEqual(point, "全 2301 / 通過 2299 / 失敗 2")
        self.assertEqual(result, "FAIL")      # 終了コードが 0 でも失敗があれば落とす

    def test_unity_compile_error_is_named(self):
        result, point = vf.judge("unity", 5, "EditMode : 全 10 / 通過 10 / 失敗 0 / 除外 0\n"
                                             "? コンパイルエラー — **上の結果は古いアセンブリで走ったもの**\n")
        self.assertEqual(result, "FAIL")
        self.assertIn("コンパイルエラー", point)

    def test_unity_without_a_result_line_fails(self):
        self.assertEqual(vf.judge("unity", 0, "結果 XML が無い: x\n")[0], "FAIL")

    def test_tools_counts_and_failed_names(self):
        text = "OK   test_a.py  Ran 3 tests  (0.1s)\nFAIL test_b.py  Ran 2 tests  (0.2s)\n\n19 / 20 通過\n"
        result, point = vf.judge("tools", 1, text)
        self.assertEqual(result, "FAIL")
        self.assertEqual(point, "19 / 20 通過・落ちた: test_b.py")

    def test_counts_line(self):
        result, point = vf.judge("canon", 1, "ERROR take `L1C1#1` が NOW.md に無い\ncanon-now: ERROR 1 / WARN 0\n")
        self.assertEqual(result, "FAIL")
        self.assertTrue(point.startswith("ERROR 1 / WARN 0・take"))

    def test_bake_lists_the_fixes(self):
        text = ("焼き直し待ち 2 件。このまま焼いても APK には入らない\n"
                "  - .\\tools\\unity.ps1 menu scene（Main シーンへ演出（? ビルド前に必須））\n"
                "  - py -3.11 tools/make-osd-font.py（時刻表示の版）\n")
        self.assertEqual(vf.judge("bake", 1, text)[1],
                         "焼き直し待ち 2 件: .\\tools\\unity.ps1 menu scene / py -3.11 tools/make-osd-font.py")
        self.assertEqual(vf.judge("bake", 0, "焼き直し待ちは無い\n"), ("PASS", "焼き直し待ちは無い"))

    def write_verdict(self, d: Path, **over) -> Path:
        verdict = {"stamp": "20261111_120000_verify", "pass": True, "new": {"FAIL": 0, "WARN": 2},
                   "run": {"result": "ok"},
                   "verdicts": [{"level": "OK", "text": "結末は想定どおり Released"},
                                {"level": "WARN", "text": "何か", "known": "k1"}]}
        verdict.update(over)
        dest = d / "20261111_120000_verify"
        dest.mkdir()
        (dest / "verdict.json").write_text(json.dumps(verdict, ensure_ascii=False), encoding="utf-8")
        return dest

    def test_twin_reads_the_verdict(self):
        with tempfile.TemporaryDirectory() as d:
            dest = self.write_verdict(Path(d))
            text = f"== 走行\nPASS FAIL=0 WARN=3 OK=40\n新規: FAIL 0 / WARN 2\n→ {dest}\n"
            result, point = vf.judge("twin", 0, text)
        self.assertEqual(result, "PASS")
        self.assertEqual(point, f"新規 FAIL 0 / WARN 2・結末 Released・{dest}")

    def test_twin_wrong_ending_and_failed_run(self):
        with tempfile.TemporaryDirectory() as d:
            dest = self.write_verdict(Path(d), **{"pass": False, "new": {"FAIL": 1, "WARN": 0}, "run": {"result": "timeout"},
                                                  "verdicts": [{"level": "FAIL", "text": "結末が Trapped（想定は Released）"}]})
            result, point = vf.judge("twin", 1, f"→ {dest}\n")
        self.assertEqual(result, "FAIL")
        self.assertIn("新規 FAIL 1 / WARN 0・結末 Trapped・走行 timeout", point)

    def test_twin_without_a_verdict_fails(self):
        self.assertEqual(vf.judge("twin", 0, "== 走行\n")[0], "FAIL")
        result, point = vf.judge("twin", 3, "ツイン走行が動いている（pid 1）\n")
        self.assertEqual(result, "FAIL")
        self.assertIn("錠", point)


class SyncLine(unittest.TestCase):
    def test_commit_is_read_from_the_sync_line(self):
        text = "C:\\x\\fcv-twin-tree を いまの作業ツリー（未コミットの変更ごと） へ揃えた: 277c6a54 / 未追跡 3 本 / 管理外の入力 0 本を更新\n"
        got = vf.parse_sync(text)
        self.assertEqual(got["commit"], "277c6a54")
        self.assertIn("揃えた", got["line"])

    def test_failed_sync_keeps_the_line(self):
        got = vf.parse_sync("C:\\x\\fcv-twin-tree を Unity が開いている。走行が終わってから sync する\n")
        self.assertIsNone(got["commit"])
        self.assertIn("Unity が開いている", got["line"])


class Decode(unittest.TestCase):
    def test_mixed_utf8_and_cp932_lines(self):
        raw = "通過\n".encode("utf-8") + "焼き直し待ち\n".encode("cp932")
        self.assertEqual(vf.decode(raw), "通過\n焼き直し待ち\n")


class Table(unittest.TestCase):
    def test_header_and_rows_line_up(self):
        lines = vf.table([vf.Result("tools", "PASS", 0, 3.2, "16 / 16 通過"), vf.Result("twin", point="回していない（--quick）")])
        self.assertEqual(vf.cols(lines[0][:lines[0].index("要点")]), vf.cols(lines[1][:lines[1].index("16")]))
        self.assertTrue(lines[2].startswith("twin     SKIP       -  "))


class Notes(unittest.TestCase):
    def ns(self, real: bool) -> argparse.Namespace:
        return argparse.Namespace(real=real)

    def test_real_device_gate_is_always_named(self):
        self.assertIn(vf.REAL_DEVICE_NOTE, vf.notes([vf.Result("tools", "PASS")], self.ns(False)))

    def test_fast_note_only_when_twin_ran_fast(self):
        ran = [vf.Result("twin", "PASS")]
        self.assertIn(vf.FAST_NOTE, vf.notes(ran, self.ns(False)))
        self.assertNotIn(vf.FAST_NOTE, vf.notes(ran, self.ns(True)))
        self.assertNotIn(vf.FAST_NOTE, vf.notes([vf.Result("twin", "SKIP")], self.ns(False)))


if __name__ == "__main__":
    unittest.main()
