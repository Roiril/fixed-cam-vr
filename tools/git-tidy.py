"""幹 1 本の運用（メインの木は常に master）を機械で守る。使い終わった枝・リモートの枝・worktree を見つけて片づける。

    py -3.11 tools/git-tidy.py                 # 見るだけ（既定）
    py -3.11 tools/git-tidy.py --apply         # 取り込み済みの枝・リモートの枝・worktree を消す
    py -3.11 tools/git-tidy.py --json          # {"problems": [...], "info": [...]}
    py -3.11 tools/git-tidy.py --repo <パス>   # 対象のリポジトリを差し替える（環境変数 GIT_TIDY_REPO でも可）

調べること（どれも 1 件 1 行の「問題」として出す）:
  a  メインの木の枝が master でない（detached も）。--apply でも直さない（未コミットの変更が付いているので）
  b  master へ取り込み済みのローカルの枝。--apply で git branch -d
  c  origin/master へ取り込み済みのリモートの枝。--apply で git push origin --delete（最初に fetch --prune）
  d  .claude/worktrees/ の下の worktree で、作業ツリーが綺麗かつ取り込み済み（HEAD が master の祖先か、
     git cherry master が全部 -）のもの。--apply で git worktree remove と、その枝の git branch -D。
     汚れている木・未取り込みの木は消さない（最後の動きから 3 日を超えたら問題、3 日以内は「進行中」）
  e  取り込まれていない枝（ローカル・リモート）で、最後のコミットから 3 日を超えたもの。消さない
  f  stash が 1 件以上。消さない

.claude/worktrees/ の外の worktree（検証用の木 fcv-twin-tree など）は対象外。どれかの worktree が
チェックアウト中の枝は b と e から外す（メインの木の枝は a、.claude/worktrees/ の木の枝は d が見る）。

終了コードは、問題が 1 つでも残っていれば 1（--apply のあとは数え直す）、無ければ 0。git を読めなければ 2。
git を呼ぶのは git() だけ。判定は collect() が集めた git の出力（文字列）を analyze() が読む純関数。
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import time
from dataclasses import dataclass, field
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
REPO_ENV = "GIT_TIDY_REPO"
TRUNK = "master"
REMOTE = "origin"
WORKTREES_DIR = ".claude/worktrees"
STALE_DAYS = 3
DAY = 86400
GIT_TIMEOUT = 60.0


@dataclass
class Item:
    kind: str
    name: str
    detail: str
    fix: str = ""
    actions: list[list[str]] = field(default_factory=list)   # --apply で順に打つ git の引数（cwd はメインの木）

    def to_json(self, problem: bool) -> dict:
        d = {"kind": self.kind, "name": self.name, "detail": self.detail}
        if problem:
            d["fix"] = self.fix
        return d


# ---- git を呼ぶ唯一の場所 ----

def git(cwd: str | Path, *args: str, timeout: float = GIT_TIMEOUT) -> tuple[int, str, str]:
    """返すのは (終了コード, stdout, stderr)。起動できない・時間切れは終了コード -1。"""
    env = dict(os.environ, GIT_TERMINAL_PROMPT="0")   # fetch / push が認証の入力待ちで止まらないように
    try:
        r = subprocess.run(["git", "-C", str(cwd), *args], capture_output=True, text=True,
                           encoding="utf-8", errors="replace", timeout=timeout, env=env)
    except FileNotFoundError:
        return -1, "", "git が見つからない"
    except subprocess.TimeoutExpired:
        return -1, "", f"{timeout:.0f} 秒で終わらなかった"
    return r.returncode, r.stdout, r.stderr


def out(cwd: str | Path, *args: str) -> str:
    rc, stdout, _ = git(cwd, *args)
    return stdout if rc == 0 else ""


def last_line(text: str) -> str:
    return next((l.strip() for l in reversed(text.splitlines()) if l.strip()), "")


# ---- 集める（git の出力をそのまま文字列で持つ） ----

def collect(repo: str | Path, fetch: bool = True) -> dict:
    rc, wt, err = git(repo, "worktree", "list", "--porcelain")
    if rc != 0:
        return {"error": last_line(err) or f"{repo} は git リポジトリではない"}
    trees = parse_worktrees(wt)
    if not trees:
        return {"error": f"{repo} の worktree を読めない"}
    main = trees[0]["path"]
    facts: dict = {"main": main, "worktrees": wt, "now": time.time(), "fetch": ""}
    facts["trunk"] = out(main, "rev-parse", "--verify", "-q", f"refs/heads/{TRUNK}").strip()
    facts["has_remote"] = REMOTE in out(main, "remote").split()
    if fetch and facts["has_remote"]:
        rc, _, err = git(main, "fetch", "--prune", REMOTE)
        if rc != 0:
            facts["fetch"] = last_line(err) or f"終了コード {rc}"
    facts["heads"] = out(main, "for-each-ref", "--format=%(refname:short)%09%(committerdate:unix)", "refs/heads")
    facts["heads_merged"] = (out(main, "for-each-ref", f"--merged={TRUNK}", "--format=%(refname:short)", "refs/heads")
                             if facts["trunk"] else "")
    upstream = f"refs/remotes/{REMOTE}/{TRUNK}"
    facts["remote_trunk"] = out(main, "rev-parse", "--verify", "-q", upstream).strip() if facts["has_remote"] else ""
    facts["remotes"] = (out(main, "for-each-ref", "--format=%(refname)%09%(committerdate:unix)%09%(symref)",
                            f"refs/remotes/{REMOTE}") if facts["has_remote"] else "")
    facts["remotes_merged"] = (out(main, "for-each-ref", f"--merged={upstream}", "--format=%(refname)",
                                   f"refs/remotes/{REMOTE}") if facts["remote_trunk"] else "")
    facts["trees"] = {t["path"]: tree_facts(main, t, bool(facts["trunk"]))
                      for t in trees[1:] if under_worktrees(main, t["path"])}
    facts["stash"] = out(main, "stash", "list", "--format=%ct")
    return facts


def tree_facts(main: str, t: dict, has_trunk: bool) -> dict:
    """.claude/worktrees/ の下の 1 本。status より先に index の時刻を読む（status が index を書き直すことがある）。"""
    path, head = t["path"], t.get("head", "")
    f = {"missing": not Path(path).is_dir(), "status": None, "ahead": "", "cherry": "", "date": "", "touched": 0.0}
    if f["missing"]:
        return f
    gitdir = out(path, "rev-parse", "--absolute-git-dir").strip()
    index = Path(gitdir) / "index" if gitdir else None
    if index and index.is_file():
        f["touched"] = index.stat().st_mtime
    f["date"] = out(main, "log", "-1", "--format=%ct", head).strip() if head else ""
    rc, status, _ = git(path, "status", "--porcelain")
    f["status"] = status if rc == 0 else None
    if has_trunk and head:
        f["ahead"] = out(main, "rev-list", "--count", f"{TRUNK}..{head}").strip()
        f["cherry"] = out(main, "cherry", TRUNK, head)
    return f


# ---- 読む（純関数） ----

def parse_worktrees(text: str) -> list[dict]:
    """git worktree list --porcelain。先頭がメインの木。"""
    trees: list[dict] = []
    cur: dict | None = None
    for line in text.splitlines():
        if not line.strip():
            cur = None
            continue
        key, _, value = line.partition(" ")
        if key == "worktree":
            cur = {"path": value}
            trees.append(cur)
        elif cur is None:
            continue
        elif key == "HEAD":
            cur["head"] = value
        elif key == "branch":
            cur["branch"] = value.removeprefix("refs/heads/")
        elif key in ("detached", "bare", "locked", "prunable"):
            cur[key] = value or True
    return trees


def norm(path: str) -> str:
    p = path.replace("\\", "/").rstrip("/")
    return p.casefold() if os.name == "nt" else p


def under_worktrees(main: str, path: str) -> bool:
    return norm(path).startswith(norm(main) + "/" + WORKTREES_DIR + "/")


def lines(text: str) -> list[str]:
    return [l.strip() for l in text.splitlines() if l.strip()]


def to_float(text: str) -> float:
    try:
        return float(text)
    except (TypeError, ValueError):
        return 0.0


def days(now: float, then: float) -> int:
    return int((now - then) // DAY)


def ymd(t: float) -> str:
    return time.strftime("%Y-%m-%d", time.localtime(t)) if t else "日付不明"


def stale(now: float, then: float) -> bool:
    return now - then > STALE_DAYS * DAY


def relative(main: str, path: str) -> str:
    p = path.replace("\\", "/")
    m = main.replace("\\", "/").rstrip("/") + "/"
    return p[len(m):] if norm(p).startswith(norm(m)) else p


def analyze(facts: dict) -> tuple[list[Item], list[Item]]:
    """collect() の出力 → (問題, 情報)。git を呼ばない。"""
    problems: list[Item] = []
    info: list[Item] = []
    now = facts["now"]
    trees = parse_worktrees(facts["worktrees"])
    main = trees[0]
    checked = {t["branch"] for t in trees if t.get("branch")}

    # a. メインの木の枝
    here = main.get("branch") or "HEAD（detached）"
    if main.get("branch") != TRUNK:
        problems.append(Item("main-not-master", here, f"メインの木（{main['path']}）が {TRUNK} に居ない",
                             f"`git -C {main['path']} switch {TRUNK}`（未コミットの変更は付いてくる。--apply では直さない）"))
    if facts.get("fetch"):
        info.append(Item("fetch", REMOTE, f"⚠ git fetch --prune {REMOTE} に失敗した（リモートの枝は前回の fetch の時点で見る）: {facts['fetch']}"))
    if not facts.get("trunk"):
        problems.append(Item("no-trunk", TRUNK, f"ローカルの {TRUNK} が無い。取り込み済みかどうかを判定できない",
                             f"`git fetch {REMOTE}` して `git branch {TRUNK} {REMOTE}/{TRUNK}`"))

    # b / e. ローカルの枝
    merged = set(lines(facts.get("heads_merged", "")))
    for row in lines(facts.get("heads", "")):
        name, _, date = row.partition("\t")
        if name == TRUNK or name in checked:
            continue
        if name in merged:
            problems.append(Item("merged-branch", name, f"{TRUNK} へ取り込み済み",
                                 f"`git branch -d {name}`（--apply で打つ）", [["branch", "-d", name]]))
        elif facts.get("trunk"):
            stale_or_wip(problems, info, now, to_float(date), "stale-branch", name,
                         f"`git tag archive/{name} {name}` を付けて `git branch -D {name}`")

    # c / e. リモートの枝
    prefix = f"refs/remotes/{REMOTE}/"
    rmerged = set(lines(facts.get("remotes_merged", "")))
    for row in lines(facts.get("remotes", "")):
        ref, _, rest = row.partition("\t")
        date, _, symref = rest.partition("\t")
        if symref.strip() or not ref.startswith(prefix) or ref in (prefix + TRUNK, prefix + "HEAD"):
            continue
        short = ref[len(prefix):]
        name = f"{REMOTE}/{short}"
        if ref in rmerged:
            problems.append(Item("merged-remote", name, f"{REMOTE}/{TRUNK} へ取り込み済み",
                                 f"`git push {REMOTE} --delete {short}`（--apply で打つ）",
                                 [["push", REMOTE, "--delete", short]]))
        elif facts.get("remote_trunk"):
            stale_or_wip(problems, info, now, to_float(date), "stale-remote", name,
                         f"`git tag archive/{short} {name}` を付けて `git push {REMOTE} --delete {short}`")

    # d. .claude/worktrees/ の木
    by_path = {t["path"]: t for t in trees}
    for path, tf in facts.get("trees", {}).items():
        t = by_path.get(path, {"path": path})
        label = relative(main["path"], path)
        branch = t.get("branch", "")
        what = f"枝 {branch}" if branch else "detached"
        if tf["missing"]:
            problems.append(Item("worktree-missing", label, f"木のフォルダが無い（{what}）。登録だけ残っている",
                                 "`git worktree prune`（--apply で打つ）", [["worktree", "prune"]]))
            continue
        dirty = tf["status"] is None or bool(tf["status"].strip())
        cherry = lines(tf["cherry"])
        ancestor = tf["ahead"] == "0"
        equivalent = bool(cherry) and all(l.startswith("-") for l in cherry)
        if not dirty and (ancestor or equivalent) and not t.get("locked"):
            how = f"HEAD が {TRUNK} の祖先" if ancestor else f"全コミットが {TRUNK} に同じ差分で入っている（git cherry が全部 -）"
            actions = [["worktree", "remove", path]] + ([["branch", "-D", branch]] if branch else [])
            fix = f"`git worktree remove {path}`" + (f" と `git branch -D {branch}`" if branch else "")
            problems.append(Item("merged-worktree", label, f"綺麗で取り込み済み（{what}・{how}）", fix + "（--apply で打つ）", actions))
            continue
        why = []
        if tf["status"] is None:
            why.append("status を読めない")
        elif dirty:
            why.append(f"未コミットの変更 {len(lines(tf['status']))} 件")
        if not (ancestor or equivalent):
            plus = sum(1 for l in cherry if l.startswith("+"))
            why.append(f"未取り込みのコミット {plus} 件" if plus else "取り込み済みか判定できない")
        if t.get("locked"):
            why.append("locked")
        last = max(to_float(tf["date"]), tf["touched"])
        detail = f"残っている（{what}・{'・'.join(why)}）。最後の動きから {days(now, last)} 日（{ymd(last)}）"
        if stale(now, last):
            problems.append(Item("worktree-left", label, detail,
                                 f"中身を見て取り込むか、要らなければ `git worktree remove --force {path}`"))
        else:
            info.append(Item("worktree-wip", label, detail + "・進行中"))

    # 対象外の worktree
    for t in trees[1:]:
        if not under_worktrees(main["path"], t["path"]):
            info.append(Item("worktree-outside", t["path"], f"対象外（{WORKTREES_DIR}/ の外。{('枝 ' + t['branch']) if t.get('branch') else 'detached'}）"))

    # f. stash
    stamps = sorted(to_float(l) for l in lines(facts.get("stash", "")))
    if stamps:
        problems.append(Item("stash", f"{len(stamps)} 件", f"stash が {len(stamps)} 件残っている（最古 {ymd(stamps[0])}）",
                             "`git stash list` で中身を見て、要るものは取り込み、要らないものは `git stash drop`"))
    return problems, info


def stale_or_wip(problems: list[Item], info: list[Item], now: float, date: float, kind: str, name: str, discard: str) -> None:
    detail = f"{TRUNK} へ取り込まれていない。最後のコミットから {days(now, date)} 日（{ymd(date)}）"
    if stale(now, date):
        problems.append(Item(kind, name, detail, f"取り込むか、{discard}"))
    else:
        info.append(Item("in-progress", name, detail + "・進行中"))


# ---- 片づける ----

APPLY_ORDER = ["worktree-missing", "merged-worktree", "merged-branch", "merged-remote"]


def apply(main: str, problems: list[Item]) -> list[dict]:
    """worktree を先に外す（枝がチェックアウト中だと消せない）。1 件の中で 1 つ失敗したら残りは打たない。"""
    done: list[dict] = []
    for kind in APPLY_ORDER:
        for p in problems:
            if p.kind != kind:
                continue
            for args in p.actions:
                rc, _, err = git(main, *args)
                done.append({"cmd": "git " + " ".join(args), "ok": rc == 0, "error": "" if rc == 0 else last_line(err)})
                if rc != 0:
                    break
    return done


# ---- 出力 ----

def report(problems: list[Item], info: list[Item]) -> list[str]:
    out_lines = [f"問題 {len(problems)} 件"]
    for p in problems:
        out_lines.append(f"  問題 [{p.kind}] {p.name}: {p.detail}")
        out_lines.append(f"       直し方: {p.fix}")
    if info:
        out_lines.append(f"情報 {len(info)} 件")
        for i in info:
            out_lines.append(f"  情報 [{i.kind}] {i.name}: {i.detail}")
    return out_lines


def resolve_repo(arg: str | None) -> Path:
    return Path(arg or os.environ.get(REPO_ENV) or ROOT)


def main(argv: list[str] | None = None) -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--apply", action="store_true", help="取り込み済みの枝・リモートの枝・worktree を消す")
    ap.add_argument("--json", action="store_true", help='{"problems": [...], "info": [...]} を出す')
    ap.add_argument("--repo", help=f"対象のリポジトリ（既定は環境変数 {REPO_ENV}、無ければこのリポジトリ）")
    a = ap.parse_args(argv)
    repo = resolve_repo(a.repo)

    facts = collect(repo, fetch=True)
    if "error" in facts:
        if a.json:
            print(json.dumps({"error": facts["error"]}, ensure_ascii=False))
        else:
            print(f"git-tidy: {facts['error']}")
        return 2
    problems, info = analyze(facts)
    applied: list[dict] | None = None
    before: list[str] = []
    if a.apply:
        before = report(problems, info)
        applied = apply(facts["main"], problems)
        facts = collect(repo, fetch=False)
        if "error" in facts:
            print(f"git-tidy: 数え直せなかった: {facts['error']}")
            return 2
        problems, info = analyze(facts)

    if a.json:
        doc = {"problems": [p.to_json(True) for p in problems], "info": [i.to_json(False) for i in info]}
        if applied is not None:
            doc["applied"] = applied
        print(json.dumps(doc, ensure_ascii=False, indent=1))
    else:
        print(f"git-tidy: {facts['main']}")
        if applied is not None:
            print("--apply の前:")
            for line in before:
                print("  " + line)
            print("打ったもの:" if applied else "打ったもの: 無い（自動で消せる問題が無かった）")
            for d in applied:
                print(f"  {'ok  ' if d['ok'] else '失敗'} {d['cmd']}" + (f": {d['error']}" if d["error"] else ""))
            print("数え直し:")
        for line in report(problems, info):
            print(line)
        print(f"git-tidy: 問題 {len(problems)} 件")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
