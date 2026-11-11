"""実装のあとの検証を 1 コマンドで、漏れなく、並列に回す入口。

    py -3.11 tools/verify.py                  # 全部の門（重い 2 つは検証用の木とメインの木で同時に走る）
    py -3.11 tools/verify.py --quick          # 軽い門だけ（unity と twin を飛ばす。数秒）
    py -3.11 tools/verify.py --real           # ツインを実時間で回す（--fast を外す。音と性能も判定に入る）
    py -3.11 tools/verify.py --video          # ツインで目線の動画も撮る（既定は撮らない。判定に動画は要らない）
    py -3.11 tools/verify.py --only tools,bake
    py -3.11 tools/verify.py --skip twin
    py -3.11 tools/verify.py --tag fix-veil   # ツインの走行のタグ（既定は verify）

門（GATES の順に表へ出る）:
  tools    tools/ の Python テスト（test-tools.py）
  harness  ハーネスの機械の門（harness-check.py。ERROR があれば FAIL）
  canon    canon/NOW.md のずれ（canon-now.py --check）
  bake     焼き直し待ち（unity.ps1 bake-check。Unity を起動しない）
  git      幹 1 本の運用（git-tidy.py。メインの木が master か・取り込み済みの枝や worktree が残っていないか）
  unity    C# の EditMode テスト。検証用の木で走る（twin-tree.py test）
  twin     HMD なしの通し（twin-verify.py。既定は --fast）

同じ Unity プロジェクトは同時に 1 本しか開けないので、unity は検証用の木（tools/twin-tree.py の TREE）、
twin はメインの木で回す。木が無い（setup の前）か sync できなかったときは、unity もメインの木で回し、twin と直列にする。
unity を回すときだけ、最初に 1 回 twin-tree.py sync して、木をいまの作業ツリーに揃える。

出力は Logs/verify/<時刻>/（門ごとの <名前>.log と summary.json。どちらも UTF-8）。
終了コードは、どれか FAIL なら 1、全部 PASS か SKIP なら 0、門の名前を読めなければ 2。

⚠ PC の門を全部通しても、実機の門（quest-record.py --walk）の代わりにはならない。
   シェーダの剥がれ・パススルー・IL2CPP・音の聞こえは PC では出ない。
⚠ PowerShell の子（unity.ps1）は出力を cp932 で書く。ログへは 1 行ずつ UTF-8 に直して書く（cp932 に無い ✓ ⚠ は ? になる）。
"""
from __future__ import annotations

import argparse
import importlib.util
import json
import os
import re
import subprocess
import sys
import threading
import time
import unicodedata
from dataclasses import dataclass, field
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TOOLS = ROOT / "tools"
LOGS = ROOT / "Logs" / "verify"
PY = ["py", "-3.11"]
DEFAULT_TAG = "verify"
REAL_DEVICE_NOTE = "実機の門（quest-record.py --walk）: 未実施。PC では出ない不具合（シェーダの剥がれ・パススルー・IL2CPP・音）は実機でしか分からない"
FAST_NOTE = "ツインは --fast で回した。音と性能は判定していない。確定は --real"

_spec = importlib.util.spec_from_file_location("twin_tree", TOOLS / "twin-tree.py")
twin_tree = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(twin_tree)


def ps_file(root: Path) -> list[str]:
    return ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(root / "tools" / "unity.ps1")]


@dataclass(frozen=True)
class Options:
    real: bool = False
    no_video: bool = False
    tag: str = DEFAULT_TAG
    tree: bool = True        # unity を検証用の木で回せるか（sync が通ったか）


def twin_args(o: Options) -> list[str]:
    args = [] if o.real else ["--fast"]
    if o.no_video:
        args.append("--no-video")
    return args + ["--tag", o.tag]


def unity_cmd(o: Options) -> list[str]:
    if o.tree:
        return PY + [str(TOOLS / "twin-tree.py"), "test"]
    return ps_file(ROOT) + ["test"]


# 門の表。名前 → 重いか・何を見るか・起動コマンド。cwd はどれもメインの木（unity の木への切り替えは twin-tree.py がする）。
GATES: dict[str, dict] = {
    "tools":   {"heavy": False, "desc": "tools/ の Python テスト",
                "cmd": lambda o: PY + [str(TOOLS / "test-tools.py")]},
    "harness": {"heavy": False, "desc": "ハーネスの機械の門",
                "cmd": lambda o: PY + [str(TOOLS / "harness-check.py")]},
    "canon":   {"heavy": False, "desc": "canon/NOW.md のずれ",
                "cmd": lambda o: PY + [str(TOOLS / "canon-now.py"), "--check"]},
    "bake":    {"heavy": False, "desc": "焼き直し待ち（Unity を起動しない）",
                "cmd": lambda o: ps_file(ROOT) + ["bake-check"]},
    "git":     {"heavy": False, "desc": "幹 1 本の運用（枝・worktree・stash）",
                "cmd": lambda o: PY + [str(TOOLS / "git-tidy.py")]},
    "unity":  {"heavy": True, "desc": "C# の EditMode テスト（検証用の木）",
                "cmd": unity_cmd},
    "twin":    {"heavy": True, "desc": "HMD なしの通し（twin-verify.py）",
                "cmd": lambda o: PY + [str(TOOLS / "twin-verify.py"), *twin_args(o)]},
}


# ---- 引数 → 回す門 ----

def build_parser() -> argparse.ArgumentParser:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--quick", action="store_true", help="軽い門だけ（unity と twin を飛ばす）")
    ap.add_argument("--real", action="store_true", help="ツインを実時間で回す（twin-verify.py の --fast を外す）")
    ap.add_argument("--video", action="store_true", help="ツインで目線の動画も撮る（見るとき。約 80 秒延びる）")
    ap.add_argument("--no-video", action="store_true", help="既定なので何もしない（前の書き方を通すため）")
    ap.add_argument("--only", default="", help="回す門（カンマ区切り）")
    ap.add_argument("--skip", default="", help="飛ばす門（カンマ区切り）")
    ap.add_argument("--tag", default=DEFAULT_TAG, help=f"ツインの走行のタグ（既定は {DEFAULT_TAG}）")
    return ap


def names_of(ap: argparse.ArgumentParser, text: str, flag: str) -> list[str]:
    names = [x.strip() for x in text.split(",") if x.strip()]
    unknown = [x for x in names if x not in GATES]
    if unknown:
        ap.error(f"{flag} に知らない門: {', '.join(unknown)}（ある門: {', '.join(GATES)}）")   # exit 2
    return names


def select(argv: list[str]) -> tuple[argparse.Namespace, dict[str, str]]:
    """回す門と、回さない門の理由。返す dict は GATES の順で、値が空なら回す。"""
    ap = build_parser()
    a = ap.parse_args(argv)
    only = names_of(ap, a.only, "--only")
    skip = names_of(ap, a.skip, "--skip")
    why: dict[str, str] = {}
    for name, gate in GATES.items():
        if only and name not in only:
            why[name] = "--only に無い"
        elif name in skip:
            why[name] = "--skip"
        elif a.quick and gate["heavy"]:
            why[name] = "--quick"
        else:
            why[name] = ""
    if all(why.values()):
        ap.error("回す門が 1 つも無い（--only / --skip / --quick の組み合わせを見直す）")
    return a, why


def plan_lanes(selected: list[str], tree: bool) -> list[list[str]]:
    """同時に走らせる列。列の中は順に回す。木が使えないときは unity と twin が同じプロジェクトを開くので直列にする。"""
    lanes: list[list[str]] = []
    serial = [n for n in ("unity", "twin") if n in selected] if not tree else []
    for name in selected:
        if len(serial) > 1 and name in serial:
            if name == serial[0]:
                lanes.append(serial)
            continue
        lanes.append([name])
    return lanes


# ---- 木の sync（スナップショット） ----

SYNC_LINE = re.compile(r"揃えた: ([0-9a-f]{7,40})")


def parse_sync(text: str) -> dict:
    line = next((l.strip() for l in reversed(text.splitlines()) if l.strip()), "")
    m = SYNC_LINE.search(text)
    return {"commit": m.group(1) if m else None, "line": line}


def git_out(*args: str) -> str:
    r = subprocess.run(["git", *args], cwd=ROOT, capture_output=True, text=True, encoding="utf-8", errors="replace")
    return r.stdout.strip() if r.returncode == 0 else ""


def sync_tree(out_dir: Path) -> dict:
    """木を作業ツリーに揃える。返すのはスナップショットの記録（synced が False なら unity はメインの木で回す）。"""
    if not (twin_tree.TREE / ".git").exists():
        return {"synced": False, "line": f"{twin_tree.TREE} が無い（twin-tree.py setup の前）。unity はメインの木で twin と直列に回す"}
    r = subprocess.run(PY + [str(TOOLS / "twin-tree.py"), "sync"], cwd=ROOT, capture_output=True,
                       env=child_env())
    text = decode(r.stdout) + decode(r.stderr)
    (out_dir / "sync.log").write_text(text, encoding="utf-8", newline="\n")
    snap = parse_sync(text)
    if r.returncode != 0:
        return {"synced": False, "line": snap["line"] + "（sync できなかったので unity はメインの木で twin と直列に回す）"}
    full = git_out("rev-parse", "--verify", "-q", f"{snap['commit']}^{{commit}}") if snap["commit"] else ""
    head = git_out("rev-parse", "HEAD")
    commit = full or snap["commit"]
    kind = "HEAD（未コミットの変更なし）" if commit and commit == head else "git stash create（未コミットの変更ごと）"
    return {"synced": True, "commit": commit, "kind": kind, "line": snap["line"]}


def main_snapshot() -> dict:
    """sync しないとき（unity を回さない）。メインの木の HEAD と、未コミットの変更の本数だけ残す。"""
    dirty = git_out("status", "--short")
    return {"synced": False, "head": git_out("rev-parse", "HEAD"),
            "dirty": len(dirty.splitlines()) if dirty else 0, "line": "sync していない（unity を回さないので木は使わない）"}


# ---- 実行 ----

def decode(raw: bytes) -> str:
    """Python の子は UTF-8、PowerShell の子は cp932 で書く。1 行ずつ両方を試す。"""
    out = []
    for line in raw.splitlines(keepends=True):
        try:
            out.append(line.decode("utf-8"))
        except UnicodeDecodeError:
            out.append(line.decode("cp932", errors="replace"))
    return "".join(out).replace("\r\n", "\n")


def child_env() -> dict:
    return dict(os.environ, PYTHONIOENCODING="utf-8")


@dataclass
class Result:
    name: str
    result: str = "SKIP"       # PASS / FAIL / SKIP
    rc: int | None = None
    sec: float | None = None
    point: str = ""
    cmd: list[str] = field(default_factory=list)
    log: str = ""


def run_gate(name: str, cmd: list[str], log: Path, t0: float) -> tuple[int, float, str]:
    start = time.monotonic()
    print(f"  [{start - t0:5.0f}s] {name} 開始", flush=True)
    chunks: list[str] = []
    with open(log, "w", encoding="utf-8", newline="\n") as f:
        f.write("$ " + subprocess.list2cmdline(cmd) + "\n")
        try:
            p = subprocess.Popen(cmd, cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, env=child_env())
        except OSError as e:
            msg = f"起動できなかった: {e}\n"
            f.write(msg)
            return 127, time.monotonic() - start, msg
        for raw in p.stdout:
            line = decode(raw)
            chunks.append(line)
            f.write(line)
            f.flush()
        rc = p.wait()
    sec = time.monotonic() - start
    print(f"  [{time.monotonic() - t0:5.0f}s] {name} 終了 exit={rc}（{sec:.0f} 秒）", flush=True)
    return rc, sec, "".join(chunks)


# ---- 要点の抜き出し（門ごと）。返すのは (要点, 出力から見て FAIL か) ----

def point_tools(text: str, rc: int) -> tuple[str, bool]:
    m = list(re.finditer(r"(\d+) / (\d+) 通過", text))
    point = f"{m[-1].group(1)} / {m[-1].group(2)} 通過" if m else "通過数の行が無い"
    failed = re.findall(r"^FAIL (\S+)", text, re.M)
    if failed:
        point += "・落ちた: " + ", ".join(failed)
    return point, False


def point_counts(tool: str):
    def f(text: str, rc: int) -> tuple[str, bool]:
        m = list(re.finditer(rf"{tool}: ERROR (\d+) / WARN (\d+)", text))
        if not m:
            return f"{tool} の集計行が無い", False
        point = f"ERROR {m[-1].group(1)} / WARN {m[-1].group(2)}"
        first = next((l[5:].strip() for l in text.splitlines() if l.startswith("ERROR")), "")
        if first:
            point += f"・{first[:80]}"
        return point, int(m[-1].group(1)) > 0
    return f


def point_bake(text: str, rc: int) -> tuple[str, bool]:
    if rc == 0:
        return "焼き直し待ちは無い", False
    fixes = re.findall(r"^\s+- (.+?)（", text, re.M)
    if not fixes:
        return (text.strip().splitlines() or ["出力が無い"])[-1][:100], False
    return f"焼き直し待ち {len(fixes)} 件: " + " / ".join(fixes), False


def point_git(text: str, rc: int) -> tuple[str, bool]:
    m = list(re.finditer(r"^git-tidy: 問題 (\d+) 件", text, re.M))
    if not m:
        return (text.strip().splitlines() or ["出力が無い"])[-1][:100], True
    count = int(m[-1].group(1))
    if count == 0:
        return "問題なし", False
    first = next((l.strip()[3:] for l in text.splitlines() if l.strip().startswith("問題 [")), "")
    return f"問題 {count} 件・{first[:100]}", True


def point_unity(text: str, rc: int) -> tuple[str, bool]:
    m = list(re.finditer(r"全 (\d+) / 通過 (\d+) / 失敗 (\d+)", text))
    if m:
        total, passed, failed = (int(x) for x in m[-1].groups())
        point, bad = f"全 {total} / 通過 {passed} / 失敗 {failed}", failed > 0
    else:
        point, bad = "結果の行が無い", True
    if "コンパイルエラー" in text:
        point += "・コンパイルエラー（結果は古いアセンブリで走ったもの）"
        bad = True
    elif "Editor がこのプロジェクトを開いている" in text or "を Unity が開いている" in text:
        point += "・Unity が開いていて走れなかった"
    elif "ツイン走行が動いている" in text:
        point += "・木でツイン走行が動いていて走れなかった"
    return point, bad


ENDING = re.compile(r"結末(?:は想定どおり |が )(\w+)")


def twin_dest(text: str) -> Path | None:
    m = re.findall(r"^→ (.+)$", text, re.M)
    return Path(m[-1].strip()) if m else None


def point_twin(text: str, rc: int) -> tuple[str, bool]:
    dest = twin_dest(text)
    verdict = None
    if dest and (dest / "verdict.json").is_file():
        try:
            verdict = json.loads((dest / "verdict.json").read_text(encoding="utf-8"))
        except (OSError, ValueError):
            verdict = None
    if verdict is None:
        last = next((l.strip() for l in reversed(text.splitlines()) if l.strip()), "出力が無い")
        if rc == 3:
            return f"別の走行が錠を持っていて走れなかった（{last[:80]}）", True
        return f"verdict.json を読めない（{last[:80]}）", True
    new = verdict.get("new", {})
    ending = next((m.group(1) for v in verdict.get("verdicts", []) for m in [ENDING.search(v.get("text", ""))] if m), "-")
    run = verdict.get("run", {}).get("result")
    point = f"新規 FAIL {new.get('FAIL', '?')} / WARN {new.get('WARN', '?')}・結末 {ending}"
    if run and run != "ok":
        point += f"・走行 {run}"
    return point + f"・{dest}", not verdict.get("pass", False)


POINTS = {"tools": point_tools, "harness": point_counts("harness-check"), "canon": point_counts("canon-now"),
          "bake": point_bake, "git": point_git, "unity": point_unity, "twin": point_twin}


def judge(name: str, rc: int, text: str) -> tuple[str, str]:
    point, bad = POINTS[name](text, rc)
    return ("FAIL" if rc != 0 or bad else "PASS"), point


def overall(results: list[Result]) -> int:
    return 1 if any(r.result == "FAIL" for r in results) else 0


# ---- 出力 ----

def cols(text: str) -> int:
    """端末での幅。全角は 2 桁。"""
    return sum(2 if unicodedata.east_asian_width(c) in "WF" else 1 for c in text)


def pad(text: str, width: int, right: bool = False) -> str:
    """全角を 2 桁と数えて揃える（見出しの「門」「結果」がずれないように）。"""
    space = " " * max(0, width - cols(text))
    return space + text if right else text + space


def table(results: list[Result]) -> list[str]:
    lines = [f"{pad('門', 8)} {pad('結果', 5)} {pad('秒', 6, right=True)}  要点"]
    for r in results:
        sec = f"{r.sec:.0f}" if r.sec is not None else "-"
        lines.append(f"{pad(r.name, 8)} {pad(r.result, 5)} {pad(sec, 6, right=True)}  {r.point}")
    return lines


def notes(results: list[Result], a: argparse.Namespace) -> list[str]:
    out = [REAL_DEVICE_NOTE]
    if any(r.name == "twin" and r.result != "SKIP" for r in results) and not a.real:
        out.append(FAST_NOTE)
    return out


def new_out_dir() -> tuple[str, Path]:
    stamp = time.strftime("%Y%m%d_%H%M%S")
    d, n = LOGS / stamp, 1
    while d.exists():
        n += 1
        d = LOGS / f"{stamp}_{n}"
    d.mkdir(parents=True)
    return d.name, d


def main(argv: list[str] | None = None) -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")      # argparse の誤り（知らない門の名前）は stderr へ出る
    a, why = select(sys.argv[1:] if argv is None else argv)
    selected = [n for n, w in why.items() if not w]
    stamp, out_dir = new_out_dir()
    t0 = time.monotonic()
    print(f"verify {stamp}: {', '.join(selected)}（ログ: {out_dir}）", flush=True)

    if "unity" in selected:
        print("  検証用の木を作業ツリーに揃える（twin-tree.py sync）", flush=True)
        snapshot = sync_tree(out_dir)
        print(f"  {snapshot['line']}", flush=True)
    else:
        snapshot = main_snapshot()
    opts = Options(real=a.real, no_video=not a.video, tag=a.tag, tree=snapshot["synced"])

    results = {n: Result(n, point=f"回していない（{w}）") for n, w in why.items()}
    for n in selected:
        results[n].cmd = GATES[n]["cmd"](opts)
        results[n].log = str(out_dir / f"{n}.log")

    def lane_worker(lane: list[str]) -> None:
        for n in lane:
            r = results[n]
            try:
                rc, sec, text = run_gate(n, r.cmd, Path(r.log), t0)
                r.result, r.point = judge(n, rc, text)
            except Exception as e:   # 門 1 つの事故で全体の表を失わない
                rc, sec = -1, 0.0
                r.result, r.point = "FAIL", f"verify.py 側で落ちた: {e!r}"[:160]
            r.rc, r.sec = rc, sec
            if n == "unity":
                r.point += "（検証用の木）" if opts.tree else "（メインの木・twin と直列）"

    threads = [threading.Thread(target=lane_worker, args=(lane,), daemon=True)
               for lane in plan_lanes(selected, opts.tree)]
    for t in threads:
        t.start()
    try:
        for t in threads:
            while t.is_alive():
                t.join(1.0)
    except KeyboardInterrupt:
        print("中断した。起動した門（Unity を含む）はまだ走っているかもしれない。ログ: " + str(out_dir), flush=True)
        return 130

    ordered = [results[n] for n in GATES]
    wall = time.monotonic() - t0
    print()
    for line in table(ordered):
        print(line)
    print(f"\n全体: {wall:.0f} 秒（実時間）")
    for line in notes(ordered, a):
        print(line)
    code = overall(ordered)
    summary = {
        "stamp": stamp, "pass": code == 0, "wallSec": round(wall, 1),
        "args": {"quick": a.quick, "real": a.real, "noVideo": not a.video, "only": a.only, "skip": a.skip, "tag": a.tag},
        "snapshot": snapshot,
        "gates": [{"name": r.name, "result": r.result, "rc": r.rc, "sec": None if r.sec is None else round(r.sec, 1),
                   "point": r.point, "cmd": r.cmd, "log": r.log} for r in ordered],
        "notes": notes(ordered, a),
    }
    (out_dir / "summary.json").write_text(json.dumps(summary, ensure_ascii=False, indent=1), encoding="utf-8", newline="\n")
    print(f"→ {out_dir / 'summary.json'}")
    return code


if __name__ == "__main__":
    sys.exit(main())
