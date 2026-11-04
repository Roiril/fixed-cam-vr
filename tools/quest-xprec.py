"""体験の記録（紹介動画の素材）を Quest で ON/OFF し、取り出し、PC の再生と突き合わせる。

    py -3.11 tools/quest-xprec.py status  [--serial S]   # ON/OFF と端末内の記録
    py -3.11 tools/quest-xprec.py on      [--serial S]   # 次の体験から録る（引き渡しの瞬間に録り始める）
    py -3.11 tools/quest-xprec.py off     [--serial S]   # 録らない（既定）
    py -3.11 tools/quest-xprec.py pull    [--serial S] [--delete]   # → Logs/xprec/<日時>/
    py -3.11 tools/quest-xprec.py clear   [--serial S]   # 端末の記録を全部消す
    py -3.11 tools/quest-xprec.py compare <記録のフォルダ> <再生の replay_xp.log>

PC での撮り直しは `.\\tools\\unity.ps1 replay -Set rec=latest,view=eye`（`view=follow` で後ろ上から）。

1 回の記録 = 1 フォルダ（`20261023_183012/`）:

    meta.json      端末・同梱 show.json の id・カメラの並び・registration.json の本文
    track.bin      毎フレームの頭と両手の姿勢・コントローラの入力・言語と軽減
    cam_<名>.mjr   受けて展開できた JPEG（そのまま）
    xp.log         その間の [XP] テレメトリ

⚠ **`adb pull` は使えない**（scoped storage で 0 バイトになる）。`exec-out cat` をファイルへ直に流す。
⚠ 1 回で 300MB〜1GB ある（カメラ 3 台の JPEG）。取り出したら `--delete` で端末から消す。
⚠ 印 `xprec/ON` は録り始めのたびに読まれる（アプリの再起動は要らない）。
"""

import argparse
import difflib
import json
import os
import re
import subprocess
import sys

PKG = "com.roiril.mawarimi"
REC_DIR = f"/sdcard/Android/data/{PKG}/files/xprec"
DEFAULT_OUT = os.path.join("Logs", "xprec")
REC_RE = re.compile(r"^\d{8}_\d{6}$")

sys.stdout.reconfigure(encoding="utf-8")


def _run(serial, *args, timeout=60):
    cmd = ["adb"] + (["-s", serial] if serial else []) + [str(a) for a in args]
    try:
        p = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=timeout)
        return p.returncode, p.stdout.replace("\r", ""), p.stderr.replace("\r", "")
    except subprocess.TimeoutExpired:
        return 124, "", "timeout"
    except FileNotFoundError:
        print("adb が PATH に無い", file=sys.stderr)
        sys.exit(2)


def resolve_serial(serial):
    if serial:
        return serial
    _, out, _ = _run(None, "devices", "-l")
    ready = []
    for line in out.splitlines()[1:]:
        parts = line.split()
        if len(parts) >= 2 and parts[1] == "device":
            model = next((t[6:] for t in parts[2:] if t.startswith("model:")), "")
            if not model or model.lower().startswith("quest"):
                ready.append(parts[0])
    if len(ready) == 1:
        return ready[0]
    print("Quest が繋がっていない" if not ready else f"Quest が {len(ready)} 台ある。--serial で選ぶ: {', '.join(ready)}",
          file=sys.stderr)
    sys.exit(1)


def list_recs(serial):
    code, out, _ = _run(serial, "shell", f"ls -1 {REC_DIR} 2>/dev/null")
    return sorted(n.strip() for n in out.splitlines() if REC_RE.match(n.strip())) if code == 0 else []


def list_files(serial, name):
    _, out, _ = _run(serial, "shell", f"ls -l {REC_DIR}/{name} 2>/dev/null")
    files = []
    for line in out.splitlines():
        parts = line.split()
        if len(parts) >= 8 and not line.startswith("d") and not line.startswith("total"):
            try:
                files.append((parts[-1], int(parts[4])))
            except ValueError:
                pass
    return files


def pull_file(serial, remote, local, expected):
    """exec-out cat をファイルへ直に流す（メモリに載せない）。大きさが合わなければ失敗。"""
    tmp = local + ".part"
    cmd = ["adb", "-s", serial, "exec-out", f"cat {remote}"]
    with open(tmp, "wb") as fh:
        p = subprocess.run(cmd, stdout=fh, stderr=subprocess.PIPE, timeout=1800)
    got = os.path.getsize(tmp)
    if p.returncode != 0 or got != expected:
        os.remove(tmp)
        return False, got
    os.replace(tmp, local)
    return True, got


def marker(serial):
    _, out, _ = _run(serial, "shell", f"ls -1 {REC_DIR} 2>/dev/null")
    return "ON" in {n.strip() for n in out.splitlines()}


def cmd_status(a):
    s = resolve_serial(a.serial)
    recs = list_recs(s)
    print(f"{s}: 記録 {'ON（次の引き渡しから録る）' if marker(s) else 'OFF'}")
    for r in recs[-8:]:
        total = sum(sz for _, sz in list_files(s, r))
        print(f"  {r}  {total / 1e6:7.1f} MB")
    if not recs:
        print("  端末に記録は無い")
    return 0


def cmd_on(a):
    s = resolve_serial(a.serial)
    _run(s, "shell", f"mkdir -p {REC_DIR} && touch {REC_DIR}/ON")
    ok = marker(s)
    print(f"{s}: 記録を ON にした（確認: {ok}）。次にスタッフの準備画面が閉じた瞬間から録る")
    return 0 if ok else 1


def cmd_off(a):
    s = resolve_serial(a.serial)
    _run(s, "shell", f"rm -f {REC_DIR}/ON")
    ok = not marker(s)
    print(f"{s}: 記録を OFF にした（確認: {ok}）。録っている最中の分は最後まで録る")
    return 0 if ok else 1


def cmd_clear(a):
    s = resolve_serial(a.serial)
    for r in list_recs(s):
        _run(s, "shell", f"rm -rf {REC_DIR}/{r}")
    print(f"{s}: 端末の記録を消した（残り {len(list_recs(s))} 件）")
    return 0


def cmd_pull(a):
    s = resolve_serial(a.serial)
    recs = list_recs(s)
    if not recs:
        print(f"{s}: 端末に記録が無い（{REC_DIR}）")
        return 0
    bad = 0
    for r in recs:
        files = list_files(s, r)
        names = {n for n, _ in files}
        if "meta.json" not in names or "track.bin" not in names:
            print(f"  {r}: meta.json / track.bin が無い（録っている最中か、壊れている）— 飛ばす")
            continue
        local = os.path.join(a.out, r)
        os.makedirs(local, exist_ok=True)
        failed = []
        total = 0
        for n, size in files:
            if n.endswith(".tmp"):
                continue
            ok, got = pull_file(s, f"{REC_DIR}/{r}/{n}", os.path.join(local, n), size)
            total += got
            if not ok:
                failed.append(f"{n}({got}/{size})")
        meta = {}
        try:
            with open(os.path.join(local, "meta.json"), encoding="utf-8") as fh:
                meta = json.load(fh)
        except Exception:
            pass
        info = (f"{meta.get('durationSec', 0):.0f}s {meta.get('frames', 0)} 行 "
                f"jpeg={meta.get('cameraFrames', [])} stop={meta.get('stopReason', '?')}"
                f"{' ⚠映像を上限で落とした' if meta.get('capped') else ''}")
        print(f"  {r}: {total / 1e6:.0f} MB  {info}  {'OK' if not failed else '失敗: ' + ', '.join(failed)}")
        if failed:
            bad += 1
        elif a.delete:
            _run(s, "shell", f"rm -rf {REC_DIR}/{r}")
    print(f"→ {os.path.abspath(a.out)}")
    return 1 if bad else 0


# ---------------------------------------------------------------- 突き合わせ

# 体験の進み方を表す出来事。数値の揺れる行（sum / status）は比べない。
KEY_EVS = ("phase", "seg", "take", "step", "zone", "mark", "title", "intro", "closingLine", "outro", "comms", "commsPossess")
LINE_RE = re.compile(r"^(?:tau=(?P<tau>-?[\d.]+) )?\[XP\] t=(?P<t>[\d.]+) ev=(?P<ev>\w+)(?P<rest>.*)$")


def signature(ev, rest):
    toks = [t for t in rest.split() if "=" in t][:2]
    return ev + " " + " ".join(toks)


def read_events(path, rebase=False, t0=None):
    out = []
    with open(path, encoding="utf-8", errors="replace") as fh:
        for line in fh:
            m = LINE_RE.match(line.strip())
            if not m:
                continue
            ev = m.group("ev")
            if ev == "xprec" and "state=start" in line and t0 is None:
                t0 = float(m.group("t"))
            if ev not in KEY_EVS:
                continue
            t = float(m.group("tau")) if m.group("tau") is not None else float(m.group("t"))
            out.append((t, signature(ev, m.group("rest"))))
    if rebase and t0 is not None:
        out = [(t - t0, s) for t, s in out]
    return out


def cmd_compare(a):
    dev = read_events(os.path.join(a.rec, "xp.log") if os.path.isdir(a.rec) else a.rec, rebase=True, t0=a.t0)
    if not dev:
        print("端末の出来事が 0 件（xp.log が空か、頭の ev=xprec に t= が無い。--t0 で頭の時刻を渡す）")
        return 1
    rep = read_events(a.replay)
    sm = difflib.SequenceMatcher(a=[s for _, s in dev], b=[s for _, s in rep], autojunk=False)
    matched, deltas = 0, []
    for blk in sm.get_matching_blocks():
        for k in range(blk.size):
            matched += 1
            deltas.append(rep[blk.b + k][0] - dev[blk.a + k][0])
    print(f"端末 {len(dev)} 件 / 再生 {len(rep)} 件 / 一致 {matched} 件（比 {sm.ratio():.2f}）")
    if deltas:
        ds = sorted(abs(d) for d in deltas)
        print(f"  時刻の差: 中央 {ds[len(ds) // 2]:.2f}s / 90% {ds[int(len(ds) * 0.9)]:.2f}s / 最大 {ds[-1]:.2f}s")
    shown = 0
    for tag, i1, i2, j1, j2 in sm.get_opcodes():
        if tag == "equal":
            continue
        for t, s in dev[i1:i2][:3]:
            print(f"  端末だけ  {t:7.2f}  {s}")
        for t, s in rep[j1:j2][:3]:
            print(f"  再生だけ  {t:7.2f}  {s}")
        shown += 1
        if shown >= 12:
            print("  …")
            break
    return 0


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    for name in ("status", "on", "off", "clear", "pull"):
        p = sub.add_parser(name)
        p.add_argument("--serial")
        if name == "pull":
            p.add_argument("--out", default=DEFAULT_OUT)
            p.add_argument("--delete", action="store_true")
    p = sub.add_parser("compare")
    p.add_argument("rec")
    p.add_argument("replay")
    p.add_argument("--t0", type=float, help="記録の頭の端末時刻（t=）。xprec の行に t= が無い古い記録用")
    a = ap.parse_args()
    return {"status": cmd_status, "on": cmd_on, "off": cmd_off, "clear": cmd_clear,
            "pull": cmd_pull, "compare": cmd_compare}[a.cmd](a)


if __name__ == "__main__":
    sys.exit(main())
