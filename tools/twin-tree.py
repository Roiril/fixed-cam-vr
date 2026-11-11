"""ツイン走行を 2 本同時に回すための、常設の検証用 worktree。

    py -3.11 tools/twin-tree.py setup                 # 初回だけ。worktree を作り Library（6GB）を複製する
    py -3.11 tools/twin-tree.py sync                  # いまの作業ツリー（未コミットの変更ごと）へ揃える
    py -3.11 tools/twin-tree.py sync --ref HEAD       # コミット済みの状態へ揃える（直す前の側）
    py -3.11 tools/twin-tree.py run -- --no-video --nomark --tag tree-nomark   # 向こうの木で twin-verify.py
    py -3.11 tools/twin-tree.py status

同じ Unity プロジェクトは同時に 1 本しか開けない。木を分ければ、こちらの木と向こうの木で 1 本ずつ走る。
直す前後を並べるなら、向こうを `sync --ref HEAD`、こちらを直した作業ツリーにして同時に走らせ、twin-look.py pair で並べる。

sync が持ち込むもの:
- 追跡ファイル: 作業ツリーの今の中身（git stash create の木。こちらの作業ツリーと index は触らない）か、--ref のコミット
- 未追跡の新しいファイル（新しい .cs と .meta など。--ref のときは持ち込まない）
- git 管理外の入力: Assets/ の下の無視ファイル（show.json・音）・tools/web-compositor/show.json・Logs/twin-run/pack

⚠ 木の場所は、こちらのパスを含まない名前にしてある。unity.ps1 は「コマンドラインに自分のパスを含む Unity」を
   自分の Editor と数えるので、fixed-cam-vr-xxx のような名前だと、向こうの走行をこちらの衝突として止める。
⚠ 向こうの木の出力も同じ Drive の renders/twin-verify/ に入る。--tag で区別する。
⚠ 両方が同時に Play に居ると、後から入った方は来場者タブレットの口（:8090）を開けない。警告 1 行で走行は続く。
⚠ setup の Library の複製は、こちらの木の Library を数分読み続ける。こちらで走行が動いているあいだはしない
   （2026-11-11、複製と重なった走行が導入で止まった）。
⚠ 向こうの木を初めて開く走行は、取り込みで 6 分ほど余計にかかる（走行そのものの秒数は同じ）。
⚠ 向こうの木で編集・コミットしない。sync のたびに捨てる。
"""
from __future__ import annotations

import argparse
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TREE = ROOT.parent / "fcv-twin-tree"
# git 管理外で、走行に要る入力。Assets/ の下の無視ファイルはまとめて拾う。
EXTRA_INPUTS = ("tools/web-compositor/show.json", "Logs/twin-run/pack")
# 木に固有で、持ち込んではいけないもの。
SKIP_PARTS = {"__pycache__", "Temp", "Library", "obj", "Logs", "UserSettings"}


def git(*args: str, cwd: Path = ROOT, check: bool = True) -> str:
    r = subprocess.run(["git", *args], cwd=cwd, capture_output=True, text=True, encoding="utf-8", errors="replace")
    if check and r.returncode != 0:
        raise SystemExit(f"git {' '.join(args)} が失敗（{cwd}）: {r.stderr.strip()[:400]}")
    return r.stdout.strip()


def tree_must_exist() -> None:
    if not (TREE / ".git").exists():
        raise SystemExit(f"{TREE} が無い。先に: py -3.11 tools/twin-tree.py setup")
    # sync は向こうの木で reset --hard と clean をする。こちらの木に当たらないことを、打つ前に確かめる。
    top = Path(git("rev-parse", "--show-toplevel", cwd=TREE)).resolve()
    if top != TREE.resolve() or top == ROOT.resolve():
        raise SystemExit(f"{TREE} は検証用の worktree ではない（git の頂点が {top}）。何もしない")


def lock_holder_alive(lock: Path) -> bool:
    """持ち主が死んだ錠は、次の走行が引き継ぐので数えない。判定は twin-verify.py と同じもの。"""
    import importlib.util
    import json
    spec = importlib.util.spec_from_file_location("twin_verify", ROOT / "tools" / "twin-verify.py")
    tv = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(tv)
    try:
        return tv.pid_alive(json.loads(lock.read_text(encoding="utf-8")).get("pid", 0))
    except (OSError, ValueError):
        return False


def tree_must_be_idle() -> None:
    """向こうの木で Unity か走行が動いているあいだは、中身を入れ替えない。"""
    if (TREE / "Temp" / "UnityLockfile").exists():
        try:
            with open(TREE / "Temp" / "UnityLockfile", "r+b"):
                pass
        except OSError:
            raise SystemExit(f"{TREE} を Unity が開いている。走行が終わってから sync する")
    lock = TREE / "Logs" / "twin-run" / ".lock"
    if lock.exists() and lock_holder_alive(lock):
        raise SystemExit(f"{TREE} でツイン走行が動いている（Logs/twin-run/.lock）。終わってから sync する")


def wanted(rel: str) -> bool:
    return not (set(Path(rel).parts) & SKIP_PARTS)


def copy_files(rels: list[str]) -> int:
    n = 0
    for rel in rels:
        src, dst = ROOT / rel, TREE / rel
        if not src.is_file():
            continue
        if dst.exists() and dst.stat().st_size == src.stat().st_size and dst.stat().st_mtime >= src.stat().st_mtime:
            continue
        dst.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(src, dst)
        n += 1
    return n


def ignored_inputs() -> list[str]:
    out = [r for r in git("ls-files", "-o", "-i", "--exclude-standard", "--", "Assets").splitlines() if wanted(r)]
    for extra in EXTRA_INPUTS:
        p = ROOT / extra
        if p.is_file():
            out.append(extra)
        elif p.is_dir():
            out += [str(f.relative_to(ROOT)).replace("\\", "/") for f in p.rglob("*") if f.is_file()]
    return out


def untracked_new() -> list[str]:
    return [r for r in git("ls-files", "-o", "--exclude-standard").splitlines() if wanted(r)]


def cmd_setup(a) -> int:
    if (TREE / ".git").exists():
        print(f"{TREE} は既にある")
    else:
        git("worktree", "add", "--detach", str(TREE), "HEAD")
        print(f"worktree を作った: {TREE}")
    lib = TREE / "Library"
    if lib.exists() and any(lib.iterdir()) and not a.relibrary:
        print("Library は既にある（作り直すなら --relibrary）")
    else:
        print("Library を複製する（6GB 前後・数分）", flush=True)
        # robocopy の終了コードは 8 未満が成功。こちらの Editor が掴んでいるファイルは飛ばす（向こうで作り直される）。
        rc = subprocess.run(["robocopy", str(ROOT / "Library"), str(lib), "/MIR", "/MT:16", "/R:0", "/W:0",
                             "/NFL", "/NDL", "/NP", "/NJH", "/XF", "*.lock", "EditorInstance.json"]).returncode
        if rc >= 8:
            raise SystemExit(f"Library の複製に失敗（robocopy exit={rc}）")
    return cmd_sync(a)


def cmd_sync(a) -> int:
    tree_must_exist()
    tree_must_be_idle()
    ref = getattr(a, "ref", None)
    if ref:
        target, label = git("rev-parse", ref), f"{ref}（コミット済みの状態）"
    else:
        # 作業ツリーの追跡ファイルを 1 つのコミットにする。作業ツリーにも index にも stash の一覧にも触らない。
        target = git("stash", "create") or git("rev-parse", "HEAD")
        label = "いまの作業ツリー（未コミットの変更ごと）"
    git("reset", "--hard", "-q", cwd=TREE)
    git("clean", "-fdq", cwd=TREE)                    # 前の sync が持ち込んだ未追跡ファイルを捨てる（無視ファイルは残る）
    git("checkout", "--detach", "-q", target, cwd=TREE)
    new = 0 if ref else copy_files(untracked_new())
    inputs = copy_files(ignored_inputs())
    print(f"{TREE} を {label} へ揃えた: {target[:8]} / 未追跡 {new} 本 / 管理外の入力 {inputs} 本を更新")
    return 0


def cmd_run(a) -> int:
    tree_must_exist()
    rest = [x for x in a.rest if x != "--"]
    print(f"== {TREE} で twin-verify.py {' '.join(rest)}", flush=True)
    return subprocess.run(["py", "-3.11", str(TREE / "tools" / "twin-verify.py"), *rest], cwd=TREE).returncode


def cmd_status(a) -> int:
    if not (TREE / ".git").exists():
        print(f"{TREE} は無い（setup がまだ）")
        return 1
    print(f"木: {TREE}")
    print(f"向こう: {git('log', '--oneline', '-1', cwd=TREE)}")
    print(f"こちら: {git('log', '--oneline', '-1')}")
    lib = TREE / "Library"
    print(f"Library: {'ある' if lib.exists() else '無い'}")
    lock = TREE / "Logs" / "twin-run" / ".lock"
    print(f"走行中: {'はい' if lock.exists() and lock_holder_alive(lock) else 'いいえ'}")
    return 0


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("setup")
    p.add_argument("--relibrary", action="store_true", help="Library を複製し直す")
    p = sub.add_parser("sync")
    p.add_argument("--ref", help="このコミットへ揃える（省くと、いまの作業ツリー）")
    p = sub.add_parser("run")
    p.add_argument("rest", nargs=argparse.REMAINDER, help="-- の後ろを twin-verify.py へ渡す")
    sub.add_parser("status")
    a = ap.parse_args()
    return {"setup": cmd_setup, "sync": cmd_sync, "run": cmd_run, "status": cmd_status}[a.cmd](a)


if __name__ == "__main__":
    sys.exit(main())
