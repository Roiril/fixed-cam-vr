"""tools/git-tidy.py の判定（git の出力 → 問題の一覧）と、一時ディレクトリの本物の git リポジトリでの通し。

このリポジトリの枝・worktree・リモートには触らない。通しのテストは tempfile の中に origin（bare）と作業リポジトリを作り、
GIT_CONFIG_GLOBAL を空のファイルへ向けて、ユーザーの git 設定（署名・hook）を持ち込まない。
"""

import importlib.util
import json
import os
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path

TOOLS = Path(__file__).resolve().parent
SCRIPT = TOOLS / "git-tidy.py"
spec = importlib.util.spec_from_file_location("git_tidy", SCRIPT)
gt = importlib.util.module_from_spec(spec)
sys.modules["git_tidy"] = gt
spec.loader.exec_module(gt)

NOW = 1_800_000_000.0
DAY = 86400


def porcelain(*trees: tuple[str, str]) -> str:
    """(path, "branch x" か "detached") の並び → git worktree list --porcelain。"""
    out = []
    for i, (path, state) in enumerate(trees):
        out.append(f"worktree {path}\nHEAD {i:040d}\n" + (f"branch refs/heads/{state[7:]}" if state.startswith("branch ") else state) + "\n")
    return "\n".join(out) + "\n"


MAIN = "C:/r"


def facts(**over) -> dict:
    f = {"main": MAIN, "worktrees": porcelain((MAIN, "branch master")), "now": NOW, "fetch": "",
         "trunk": "a" * 40, "has_remote": True, "heads": f"master\t{NOW:.0f}\n", "heads_merged": "master\n",
         "remote_trunk": "a" * 40,
         "remotes": f"refs/remotes/origin/HEAD\t{NOW:.0f}\trefs/remotes/origin/master\nrefs/remotes/origin/master\t{NOW:.0f}\t\n",
         "remotes_merged": "refs/remotes/origin/HEAD\nrefs/remotes/origin/master\n", "trees": {}, "stash": ""}
    f.update(over)
    return f


def kinds(problems) -> list[tuple[str, str]]:
    return [(p.kind, p.name) for p in problems]


def tree(**over) -> dict:
    t = {"missing": False, "status": "", "ahead": "0", "cherry": "", "date": f"{NOW:.0f}", "touched": 0.0}
    t.update(over)
    return t


class MainTree(unittest.TestCase):
    def test_clean_master_has_no_problem(self):
        problems, info = gt.analyze(facts())
        self.assertEqual(problems, [])
        self.assertEqual(info, [])

    def test_branch_other_than_master_is_a_problem_with_no_action(self):
        f = facts(worktrees=porcelain((MAIN, "branch feature/x")), heads=f"master\t{NOW:.0f}\nfeature/x\t{NOW:.0f}\n",
                  heads_merged="master\nfeature/x\n")
        problems, _ = gt.analyze(f)
        self.assertEqual(kinds(problems), [("main-not-master", "feature/x")])   # チェックアウト中の枝は b に出さない
        self.assertEqual(problems[0].actions, [])
        self.assertIn("git -C C:/r switch master", problems[0].fix)

    def test_detached_is_a_problem(self):
        problems, _ = gt.analyze(facts(worktrees=porcelain((MAIN, "detached"))))
        self.assertEqual(kinds(problems), [("main-not-master", "HEAD（detached）")])

    def test_missing_trunk(self):
        problems, _ = gt.analyze(facts(trunk="", heads_merged="", heads=f"x\t{NOW - 10 * DAY:.0f}\n"))
        self.assertEqual([p.kind for p in problems], ["no-trunk"])   # master が無ければ古さの判定もしない


class LocalBranches(unittest.TestCase):
    def test_merged_is_deleted_with_d(self):
        problems, _ = gt.analyze(facts(heads=f"master\t{NOW:.0f}\ndone\t{NOW - 30 * DAY:.0f}\n", heads_merged="master\ndone\n"))
        self.assertEqual(kinds(problems), [("merged-branch", "done")])
        self.assertEqual(problems[0].actions, [["branch", "-d", "done"]])

    def test_unmerged_old_is_listed_but_not_deleted(self):
        problems, info = gt.analyze(facts(heads=f"master\t{NOW:.0f}\nold\t{NOW - 4 * DAY:.0f}\n"))
        self.assertEqual(kinds(problems), [("stale-branch", "old")])
        self.assertEqual(problems[0].actions, [])
        self.assertIn("git tag archive/old old", problems[0].fix)
        self.assertEqual(info, [])

    def test_unmerged_recent_is_in_progress_info(self):
        problems, info = gt.analyze(facts(heads=f"master\t{NOW:.0f}\nwip\t{NOW - 2 * DAY:.0f}\n"))
        self.assertEqual(problems, [])
        self.assertEqual([(i.kind, i.name) for i in info], [("in-progress", "wip")])

    def test_branch_checked_out_in_another_worktree_is_skipped(self):
        wt = porcelain((MAIN, "branch master"), ("C:/elsewhere", "branch side"))
        problems, info = gt.analyze(facts(worktrees=wt, heads=f"master\t{NOW:.0f}\nside\t{NOW - 30 * DAY:.0f}\n",
                                          heads_merged="master\nside\n"))
        self.assertEqual(problems, [])
        self.assertEqual([i.kind for i in info], ["worktree-outside"])


class RemoteBranches(unittest.TestCase):
    def remotes(self, *rows: str) -> str:
        return facts()["remotes"] + "".join(r + "\n" for r in rows)

    def test_head_and_master_are_never_problems(self):
        self.assertEqual(gt.analyze(facts())[0], [])

    def test_merged_remote_is_deleted_by_push(self):
        problems, _ = gt.analyze(facts(remotes=self.remotes(f"refs/remotes/origin/feat/a\t{NOW:.0f}\t"),
                                       remotes_merged=facts()["remotes_merged"] + "refs/remotes/origin/feat/a\n"))
        self.assertEqual(kinds(problems), [("merged-remote", "origin/feat/a")])
        self.assertEqual(problems[0].actions, [["push", "origin", "--delete", "feat/a"]])

    def test_unmerged_remote_old_and_recent(self):
        problems, info = gt.analyze(facts(remotes=self.remotes(f"refs/remotes/origin/old\t{NOW - 5 * DAY:.0f}\t",
                                                               f"refs/remotes/origin/new\t{NOW - DAY:.0f}\t")))
        self.assertEqual(kinds(problems), [("stale-remote", "origin/old")])
        self.assertIn("git push origin --delete old", problems[0].fix)
        self.assertEqual([(i.kind, i.name) for i in info], [("in-progress", "origin/new")])

    def test_fetch_failure_is_a_warning_not_a_problem(self):
        problems, info = gt.analyze(facts(fetch="fatal: unable to access"))
        self.assertEqual(problems, [])
        self.assertEqual(info[0].kind, "fetch")
        self.assertIn("unable to access", info[0].detail)


class Worktrees(unittest.TestCase):
    WT = f"{MAIN}/.claude/worktrees/agent-1"

    def run_tree(self, state: str = "branch agent-1", **t):
        wt = porcelain((MAIN, "branch master"), (self.WT, state))
        return gt.analyze(facts(worktrees=wt, trees={self.WT: tree(**t)}))

    def test_clean_and_ancestor_is_removed_with_its_branch(self):
        problems, _ = self.run_tree()
        self.assertEqual(kinds(problems), [("merged-worktree", ".claude/worktrees/agent-1")])
        self.assertEqual(problems[0].actions, [["worktree", "remove", self.WT], ["branch", "-D", "agent-1"]])

    def test_patch_equivalent_counts_as_merged(self):
        problems, _ = self.run_tree(ahead="2", cherry="- 1111\n- 2222\n")
        self.assertEqual([p.kind for p in problems], ["merged-worktree"])
        self.assertIn("git cherry", problems[0].detail)

    def test_detached_tree_has_no_branch_to_delete(self):
        problems, _ = self.run_tree(state="detached")
        self.assertEqual(problems[0].actions, [["worktree", "remove", self.WT]])

    def test_one_unpicked_commit_keeps_it(self):
        problems, info = self.run_tree(ahead="2", cherry="- 1111\n+ 2222\n")
        self.assertEqual(problems, [])
        self.assertEqual(info[0].kind, "worktree-wip")
        self.assertIn("未取り込みのコミット 1 件", info[0].detail)

    def test_dirty_tree_is_kept(self):
        problems, info = self.run_tree(status="?? new.txt\n M a.txt\n")
        self.assertEqual(problems, [])
        self.assertIn("未コミットの変更 2 件", info[0].detail)

    def test_left_over_three_days_is_a_problem_without_action(self):
        old = f"{NOW - 5 * DAY:.0f}"
        problems, _ = self.run_tree(status="?? x\n", date=old, touched=NOW - 4 * DAY)
        self.assertEqual([p.kind for p in problems], ["worktree-left"])
        self.assertEqual(problems[0].actions, [])

    def test_recent_index_keeps_an_old_head_in_progress(self):
        problems, info = self.run_tree(status="?? x\n", date=f"{NOW - 9 * DAY:.0f}", touched=NOW - 3600)
        self.assertEqual(problems, [])
        self.assertEqual(info[0].kind, "worktree-wip")

    def test_locked_tree_is_not_removed(self):
        wt = porcelain((MAIN, "branch master"), (self.WT, "branch agent-1")).replace("branch refs/heads/agent-1\n", "branch refs/heads/agent-1\nlocked\n")
        problems, info = gt.analyze(facts(worktrees=wt, trees={self.WT: tree()}))
        self.assertEqual(problems, [])
        self.assertIn("locked", info[0].detail)

    def test_missing_folder_is_pruned(self):
        problems, _ = self.run_tree(missing=True)
        self.assertEqual(problems[0].actions, [["worktree", "prune"]])

    def test_outside_worktree_is_only_info(self):
        wt = porcelain((MAIN, "branch master"), ("C:/fcv-twin-tree", "detached"))
        problems, info = gt.analyze(facts(worktrees=wt))
        self.assertEqual(problems, [])
        self.assertEqual([(i.kind, i.name) for i in info], [("worktree-outside", "C:/fcv-twin-tree")])

    def test_under_worktrees_is_case_and_slash_insensitive_on_windows(self):
        self.assertTrue(gt.under_worktrees("C:/r", "c:\\R\\.claude\\worktrees\\x"))
        self.assertFalse(gt.under_worktrees("C:/r", "C:/r/.claude/worktreesX/y"))
        self.assertFalse(gt.under_worktrees("C:/r", "C:/other/.claude/worktrees/y"))


class Stash(unittest.TestCase):
    def test_count_and_oldest(self):
        older = NOW - 7 * DAY
        problems, _ = gt.analyze(facts(stash=f"{NOW:.0f}\n{older:.0f}\n"))
        self.assertEqual([p.kind for p in problems], ["stash"])
        self.assertIn("2 件", problems[0].detail)
        self.assertIn(time.strftime("%Y-%m-%d", time.localtime(older)), problems[0].detail)
        self.assertEqual(problems[0].actions, [])


class ParseWorktrees(unittest.TestCase):
    def test_fields(self):
        text = ("worktree C:/r\nHEAD abc\nbranch refs/heads/master\n\n"
                "worktree C:/r/.claude/worktrees/a\nHEAD def\nbranch refs/heads/a\nlocked reason\n\n"
                "worktree C:/t\nHEAD 123\ndetached\nprunable gitdir file points to non-existent location\n\n")
        got = gt.parse_worktrees(text)
        self.assertEqual([t["path"] for t in got], ["C:/r", "C:/r/.claude/worktrees/a", "C:/t"])
        self.assertEqual(got[0]["branch"], "master")
        self.assertEqual(got[1]["locked"], "reason")
        self.assertTrue(got[2]["detached"])
        self.assertNotIn("branch", got[2])


# ---- 一時ディレクトリの本物の git リポジトリで通す ----

class Sandbox:
    def __init__(self, root: Path):
        self.root = root
        cfg = root / "gitconfig"
        cfg.write_text("", encoding="utf-8")
        self.env = {k: v for k, v in os.environ.items() if not k.startswith("GIT_")}
        self.env.update(GIT_CONFIG_GLOBAL=str(cfg), GIT_CONFIG_NOSYSTEM="1", GIT_TERMINAL_PROMPT="0")

    def git(self, cwd: Path, *args: str, date: float | None = None) -> str:
        env = dict(self.env)
        if date is not None:
            env["GIT_COMMITTER_DATE"] = env["GIT_AUTHOR_DATE"] = f"@{int(date)} +0000"
        r = subprocess.run(["git", "-C", str(cwd), *args], capture_output=True, text=True, encoding="utf-8",
                           errors="replace", env=env)
        if r.returncode != 0:
            raise AssertionError(f"git {' '.join(args)} が失敗: {r.stderr}")
        return r.stdout

    def repo(self, name: str) -> Path:
        path = self.root / name
        self.git(self.root, "init", "-q", "-b", "master", str(path))
        self.git(path, "config", "user.name", "tidy-test")
        self.git(path, "config", "user.email", "tidy-test@example.invalid")
        self.git(path, "config", "commit.gpgsign", "false")
        return path

    def commit(self, cwd: Path, name: str, date: float | None = None) -> None:
        (cwd / f"{name}.txt").write_text(name + "\n", encoding="utf-8")
        self.git(cwd, "add", f"{name}.txt")
        self.git(cwd, "commit", "-q", "-m", name, date=date)

    def branches(self, cwd: Path) -> set[str]:
        return set(self.git(cwd, "branch", "--format=%(refname:short)").split())

    def tidy(self, repo: Path, *args: str) -> tuple[int, dict]:
        r = subprocess.run([sys.executable, "-X", "utf8", str(SCRIPT), "--repo", str(repo), "--json", *args],
                           capture_output=True, text=True, encoding="utf-8", errors="replace", env=self.env)
        try:
            doc = json.loads(r.stdout)
        except ValueError:
            raise AssertionError(f"JSON を読めない (rc={r.returncode}): {r.stdout}\n{r.stderr}")
        return r.returncode, doc


def problem_set(doc: dict) -> set[tuple[str, str]]:
    return {(p["kind"], p["name"]) for p in doc["problems"]}


class EndToEnd(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.sb = Sandbox(Path(self.tmp.name))

    def tearDown(self):
        self.tmp.cleanup()

    def test_look_only_keeps_everything_and_apply_removes_only_merged(self):
        sb = self.sb
        origin = sb.root / "origin.git"
        sb.git(sb.root, "init", "-q", "--bare", "-b", "master", str(origin))
        repo = sb.repo("work")
        sb.commit(repo, "c1")
        sb.commit(repo, "c2")
        sb.git(repo, "remote", "add", "origin", str(origin))
        sb.git(repo, "switch", "-q", "-c", "done")
        sb.commit(repo, "done")
        sb.git(repo, "switch", "-q", "master")
        sb.git(repo, "merge", "-q", "--no-ff", "-m", "merge done", "done")
        sb.git(repo, "switch", "-q", "-c", "old")
        sb.commit(repo, "old", date=time.time() - 10 * DAY)
        sb.git(repo, "switch", "-q", "master")
        sb.git(repo, "switch", "-q", "-c", "wip")
        sb.commit(repo, "wip")
        sb.git(repo, "switch", "-q", "master")
        sb.git(repo, "push", "-q", "origin", "master", "done", "old", "wip")

        rc, doc = sb.tidy(repo)
        self.assertEqual(rc, 1)
        self.assertEqual(problem_set(doc), {("merged-branch", "done"), ("merged-remote", "origin/done"),
                                            ("stale-branch", "old"), ("stale-remote", "origin/old")})
        self.assertEqual({i["name"] for i in doc["info"] if i["kind"] == "in-progress"}, {"wip", "origin/wip"})
        self.assertEqual(sb.branches(repo), {"master", "done", "old", "wip"})   # 見るだけでは何も消えない
        self.assertEqual(sb.branches(origin), {"master", "done", "old", "wip"})

        rc, doc = sb.tidy(repo, "--apply")
        self.assertEqual(rc, 1)                                                  # 古い未取り込みの枝は残る
        self.assertTrue(all(a["ok"] for a in doc["applied"]), doc["applied"])
        self.assertEqual(sb.branches(repo), {"master", "old", "wip"})
        self.assertEqual(sb.branches(origin), {"master", "old", "wip"})
        self.assertEqual(problem_set(doc), {("stale-branch", "old"), ("stale-remote", "origin/old")})

        sb.git(repo, "tag", "archive/old", "old")
        sb.git(repo, "branch", "-q", "-D", "old")
        sb.git(repo, "push", "-q", "origin", "--delete", "old")
        rc, doc = sb.tidy(repo)
        self.assertEqual(rc, 0, doc)
        self.assertIn("archive/old", sb.git(repo, "tag").split())

    def test_main_tree_off_master_exits_1(self):
        sb = self.sb
        repo = sb.repo("work")
        sb.commit(repo, "c1")
        self.assertEqual(sb.tidy(repo)[0], 0)
        sb.git(repo, "switch", "-q", "-c", "side")
        rc, doc = sb.tidy(repo)
        self.assertEqual(rc, 1)
        self.assertEqual(problem_set(doc), {("main-not-master", "side")})
        rc, doc = sb.tidy(repo, "--apply")
        self.assertEqual(rc, 1)                                                  # --apply でも枝は替えない
        self.assertEqual(sb.git(repo, "branch", "--show-current").strip(), "side")
        sb.git(repo, "switch", "-q", "master")
        rc, doc = sb.tidy(repo)
        self.assertEqual(problem_set(doc), {("merged-branch", "side")})
        sb.git(repo, "branch", "-q", "-d", "side")
        self.assertEqual(sb.tidy(repo)[0], 0)

    def test_worktree_picked_into_master_is_removed_and_dirty_one_stays(self):
        sb = self.sb
        repo = sb.repo("work")
        sb.commit(repo, "c1")
        picked = repo / ".claude" / "worktrees" / "agent-1"
        dirty = repo / ".claude" / "worktrees" / "agent-2"
        outside = sb.root / "twin-tree"
        sb.git(repo, "worktree", "add", "-q", "-b", "agent-1", str(picked))
        sb.commit(picked, "from-agent")
        sb.git(repo, "cherry-pick", "agent-1")                                   # 同じ差分の別コミットで取り込む
        sb.git(repo, "worktree", "add", "-q", "-b", "agent-2", str(dirty))
        (dirty / "scratch.txt").write_text("x\n", encoding="utf-8")
        sb.git(repo, "worktree", "add", "-q", "--detach", str(outside))

        rc, doc = sb.tidy(repo)
        self.assertEqual(rc, 1)
        self.assertEqual(problem_set(doc), {("merged-worktree", ".claude/worktrees/agent-1")})
        self.assertTrue(picked.is_dir())

        rc, doc = sb.tidy(repo, "--apply")
        self.assertEqual(rc, 0, doc)
        self.assertTrue(all(a["ok"] for a in doc["applied"]), doc["applied"])
        self.assertFalse(picked.exists())
        self.assertEqual(sb.branches(repo), {"master", "agent-2"})
        self.assertTrue((dirty / "scratch.txt").is_file())
        self.assertEqual({(i["kind"], i["name"]) for i in doc["info"]},
                         {("worktree-wip", ".claude/worktrees/agent-2"), ("worktree-outside", outside.as_posix())})

    def test_not_a_repository_exits_2(self):
        r = subprocess.run([sys.executable, "-X", "utf8", str(SCRIPT), "--repo", str(self.sb.root)],
                           capture_output=True, text=True, encoding="utf-8", errors="replace", env=self.sb.env)
        self.assertEqual(r.returncode, 2)

    def test_repo_can_come_from_the_environment(self):
        repo = self.sb.repo("work")
        self.sb.commit(repo, "c1")
        env = dict(self.sb.env, GIT_TIDY_REPO=str(repo))
        r = subprocess.run([sys.executable, "-X", "utf8", str(SCRIPT)], capture_output=True, text=True,
                           encoding="utf-8", errors="replace", env=env)
        self.assertEqual(r.returncode, 0, r.stdout)
        self.assertIn(repo.as_posix(), r.stdout)


if __name__ == "__main__":
    unittest.main()
