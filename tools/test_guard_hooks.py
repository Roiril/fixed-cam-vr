""".claude/hooks/guard-owned-files.js（PreToolUse）の、メインの木の枝の判定と既存の所有ファイルの判定。

hook は止めるとき exit 0 で {"hookSpecificOutput": {"permissionDecision": "deny", ...}} を出し、通すときは何も出さず exit 0。
このリポジトリには触らない。一時ディレクトリに作った git リポジトリを CLAUDE_PROJECT_DIR に渡して、止める側と通す側の両方を見る。
入力の JSON は json.dumps で組む（シェルで組むと `\\` が 1 段消える）。
"""

import json
import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
HOOK = ROOT / ".claude" / "hooks" / "guard-owned-files.js"
NODE = shutil.which("node")


def msys(p: Path) -> str:
    """C:\\Users\\x → /c/Users/x（Git Bash の書き方）。"""
    s = str(p.resolve()).replace("\\", "/")
    return f"/{s[0].lower()}{s[2:]}"


@unittest.skipIf(NODE is None, "node が無い")
class GuardHook(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        root = Path(cls.tmp.name)
        cfg = root / "gitconfig"
        cfg.write_text("", encoding="utf-8")
        cls.env = {k: v for k, v in os.environ.items() if not k.startswith(("GIT_", "CLAUDE_PROJECT_DIR"))}
        cls.env.update(GIT_CONFIG_GLOBAL=str(cfg), GIT_CONFIG_NOSYSTEM="1", GIT_TERMINAL_PROMPT="0")
        cls.main = cls.make_repo(root / "main")
        cls.other = cls.make_repo(root / "other")
        cls.git(cls.other, "switch", "-q", "-c", "elsewhere")       # 別のリポジトリは master でなくても通す
        cls.worktree = root / "linked"
        cls.git(cls.main, "worktree", "add", "-q", "-b", "linked", str(cls.worktree))
        cls.git(cls.main, "branch", "feat")

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    @classmethod
    def git(cls, cwd: Path, *args: str) -> str:
        r = subprocess.run(["git", "-C", str(cwd), *args], capture_output=True, text=True, encoding="utf-8",
                           errors="replace", env=cls.env)
        if r.returncode != 0:
            raise AssertionError(f"git {' '.join(args)} が失敗: {r.stderr}")
        return r.stdout

    @classmethod
    def make_repo(cls, path: Path) -> Path:
        cls.git(path.parent, "init", "-q", "-b", "master", str(path))
        cls.git(path, "config", "user.name", "hook-test")
        cls.git(path, "config", "user.email", "hook-test@example.invalid")
        (path / "a.txt").write_text("a\n", encoding="utf-8")
        cls.git(path, "add", "a.txt")
        cls.git(path, "commit", "-q", "-m", "c1")
        return path

    def setUp(self):
        self.git(self.main, "switch", "-q", "master")

    def decision(self, command: str, tool: str = "Bash") -> str | None:
        """hook の判定。止めたら 'deny'、通したら None。"""
        payload = json.dumps({"tool_name": tool, "tool_input": {"command": command}})
        env = dict(self.env, CLAUDE_PROJECT_DIR=str(self.main))
        r = subprocess.run([NODE, str(HOOK)], input=payload, capture_output=True, text=True, encoding="utf-8",
                           errors="replace", env=env, cwd=self.main)
        self.assertEqual(r.returncode, 0, r.stderr)
        if not r.stdout.strip():
            return None
        out = json.loads(r.stdout)["hookSpecificOutput"]
        self.last_reason = out["permissionDecisionReason"]
        return out["permissionDecision"]

    # ---- コミット ----

    def test_commit_on_master_passes(self):
        self.assertIsNone(self.decision('git commit -m "x" -- a.txt'))

    def test_commit_on_a_branch_is_denied(self):
        self.git(self.main, "switch", "-q", "feat")
        self.assertEqual(self.decision('git commit -m "x" -- a.txt'), "deny")
        self.assertIn("feat", self.last_reason)
        self.assertIn("git switch master", self.last_reason)
        self.assertIn("git worktree add", self.last_reason)

    def test_commit_on_detached_head_is_denied(self):
        self.git(self.main, "switch", "-q", "--detach")
        self.assertEqual(self.decision("git commit -m x"), "deny")
        self.assertIn("detached", self.last_reason)

    def test_allow_branch_passes(self):
        self.git(self.main, "switch", "-q", "feat")
        self.assertIsNone(self.decision('git commit -m "x" -- a.txt # allow-branch'))

    def test_commit_in_another_repository_passes(self):
        self.git(self.main, "switch", "-q", "feat")
        self.assertIsNone(self.decision(f'cd "{self.other}" && git commit -m x'))
        self.assertIsNone(self.decision(f'git -C "{self.other}" commit -m x'))
        self.assertIsNone(self.decision(f"cd {msys(self.other)} && git commit -m x"))

    def test_commit_in_a_linked_worktree_passes(self):
        self.assertIsNone(self.decision(f'cd "{self.worktree}" && git commit -m x'))
        self.assertIsNone(self.decision(f'git -C "{self.worktree}" commit -m x'))

    def test_target_is_resolved_through_cd_and_dash_c(self):
        """途中で別の木へ cd しても、git を打つ時点の行き先がメインの木なら止める。MSYS の /c/... も PowerShell の書き方も解く。"""
        self.git(self.main, "switch", "-q", "feat")
        self.assertEqual(self.decision(f"cd {msys(self.worktree)} && cd {msys(self.main)} && git commit -m x"), "deny")
        self.assertEqual(self.decision(f'git -C "{self.main}" commit -m x', tool="PowerShell"), "deny")
        self.assertEqual(self.decision(f"Set-Location '{self.main}'; git commit -m x", tool="PowerShell"), "deny")

    def test_unresolvable_target_passes(self):
        self.git(self.main, "switch", "-q", "feat")
        self.assertIsNone(self.decision("git -C $repo commit -m x", tool="PowerShell"))
        self.assertIsNone(self.decision(f'git --git-dir="{self.other}/.git" commit -m x'))

    # ---- 枝を切る・移る ----

    def test_creating_a_branch_is_denied(self):
        for cmd in ("git switch -c x", "git switch --create x", "git checkout -b x", "git checkout -B x master",
                    f'git -C "{self.main}" switch -c x'):
            with self.subTest(cmd=cmd):
                self.assertEqual(self.decision(cmd), "deny")

    def test_moving_to_another_branch_is_denied(self):
        self.assertEqual(self.decision("git switch feat"), "deny")
        self.assertEqual(self.decision("git checkout feat"), "deny")       # 既存のローカル枝

    def test_back_to_master_and_detach_pass(self):
        for cmd in ("git switch master", "git switch -", "git switch --detach", "git checkout master"):
            with self.subTest(cmd=cmd):
                self.assertIsNone(self.decision(cmd))

    def test_restoring_files_passes(self):
        for cmd in ("git checkout -- a.txt", "git checkout HEAD -- a.txt", "git checkout feat -- a.txt",
                    "git checkout a.txt"):
            with self.subTest(cmd=cmd):
                self.assertIsNone(self.decision(cmd))

    def test_worktree_add_with_a_branch_passes(self):
        self.assertIsNone(self.decision("git worktree add -b agent-9 .claude/worktrees/agent-9"))

    def test_branching_elsewhere_passes(self):
        self.assertIsNone(self.decision(f'cd "{self.other}" && git switch -c y'))
        self.assertIsNone(self.decision(f'cd "{self.worktree}" && git switch -c y'))

    def test_allow_branch_passes_a_switch(self):
        self.assertIsNone(self.decision("git switch -c x  # allow-branch"))

    # ---- 既存の所有ファイルの判定 ----

    def test_commit_all_is_still_denied(self):
        self.assertEqual(self.decision("git commit -a -m x"), "deny")
        self.assertIn("git commit -a", self.last_reason)

    def test_owned_file_in_pathspec_is_still_denied(self):
        (self.main / "Packages").mkdir(exist_ok=True)
        (self.main / "Packages" / "manifest.json").write_text("{}\n", encoding="utf-8")
        try:
            self.assertEqual(self.decision("git commit -m x -- Packages/manifest.json"), "deny")
            self.assertIn("Packages/manifest.json", self.last_reason)
            self.assertIsNone(self.decision("git commit -m x -- Packages/manifest.json # allow-owned"))
        finally:
            shutil.rmtree(self.main / "Packages")

    def test_commands_without_git_pass(self):
        self.assertIsNone(self.decision("ls -la"))


if __name__ == "__main__":
    unittest.main()
