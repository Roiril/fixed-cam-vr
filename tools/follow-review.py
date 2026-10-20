#!/usr/bin/env python3
"""3 周目 A の位置合わせ版（recFollow）を、被った走行のあとで見直す。`canon/LEDGER.md` 0278。

    py -3.11 tools/follow-review.py capture --serial 2G0YC1ZF7S06BW   # 走行の前に起動しておく（logcat を貯める）
    py -3.11 tools/follow-review.py pull    --serial 2G0YC1ZF7S06BW   # 走行の後。2-A の録画を端末から取る
    py -3.11 tools/follow-review.py report  --log <logcat> --rec <dir>

report が出すもの（既定の出力先は G:/マイドライブ/shubie/screenshots/<日付>_follow-review/<時刻>/）:
  - shown.mp4      … ログの 0.1 秒ごとの行から「画面に出ていたはずの画」を組み直した動画（左: 混ぜた画 / 右: 混ぜない場合）
  - sheet.png      … 1 秒ごとの並べ図（進み具合・コマ番号・混ぜ具合を焼き込み）
  - report.md      … 体験者の進み具合の推移・立ち止まり・コマの跳び・展開の回数

⚠ 組み直した画は**ログから推定したもの**で、実機の画そのものではない（実機の画は quest-record.py）。
  確かめられるのは「どのコマをどれだけ混ぜて出していたか」と、その時間の流れ方まで。
⚠ 録画は**次のラン開始で端末から消える**。走行が終わったら、リセットする前に pull する。

標準出力は ASCII だけ（Windows 端末は cp932 で日本語が壊れる）。
"""
from __future__ import annotations

import argparse
import glob
import os
import re
import struct
import subprocess
import sys
import time
from datetime import datetime

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from android_devices import adb_command, resolve_adb  # noqa: E402

PKG = "com.roiril.mawarimi"
REC_ROOT = f"/sdcard/Android/data/{PKG}/cache/rec"
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CAPTURE_DIR = os.path.join(ROOT, "logs", "capture")
DRIVE_OUT = r"G:\マイドライブ\shubie\screenshots"

FOLLOW_RE = re.compile(
    r"FOLLOW t=(?P<t>[-\d.]+) p=(?P<p>[-\d.NaN]+) max=(?P<max>[-\d.NaN]+) a=(?P<a>-?\d+) b=(?P<b>-?\d+) "
    r"w=(?P<w>[-\d.]+) f=(?P<f>[-\d.]+) n=(?P<n>\d+).*?shown=(?P<shown>\d+) follow=(?P<follow>\d)")
END_RE = re.compile(r"FOLLOW 終了 .*")
CLOCK_RE = re.compile(r"^(\d\d-\d\d \d\d:\d\d:\d\d\.\d\d\d)")


def adb(serial, *args, timeout=60, binary=False):
    cmd = adb_command(resolve_adb(), serial, *args)
    r = subprocess.run(cmd, capture_output=True, timeout=timeout)
    return r.returncode, (r.stdout if binary else r.stdout.decode("utf-8", "replace")), r.stderr.decode("utf-8", "replace")


# ---------------------------------------------------------------- capture / pull

def cmd_capture(a):
    os.makedirs(CAPTURE_DIR, exist_ok=True)
    stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    path = os.path.join(CAPTURE_DIR, f"{stamp}_follow_logcat.log")
    adb(a.serial, "logcat", "-c")
    print(f"logcat -> {path}  (Ctrl+C or kill to stop)")
    cmd = adb_command(resolve_adb(), a.serial, "logcat", "-v", "time", "-s", "Unity:V")
    with open(path, "wb") as f:
        p = subprocess.Popen(cmd, stdout=f, stderr=subprocess.STDOUT)
        try:
            p.wait()
        except KeyboardInterrupt:
            p.terminate()
    return 0


def cmd_pull(a):
    code, out, err = adb(a.serial, "shell", f"ls -t {REC_ROOT}")
    epochs = [x.strip() for x in out.split() if x.strip()]
    if code != 0 or not epochs:
        print(f"no recordings under {REC_ROOT}: {err.strip()}")
        return 2
    epoch = epochs[0]
    stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    dest = a.out or os.path.join(CAPTURE_DIR, f"{stamp}_rec_{epoch}")
    os.makedirs(dest, exist_ok=True)
    for name in ("L2C0.mjr", "L2C0.mjr.prog"):
        # pull は保護領域で黙って 0 バイトを作る（実測）。exec-out cat なら取れる。
        code, data, err = adb(a.serial, "exec-out", f"cat {REC_ROOT}/{epoch}/{name}", timeout=120, binary=True)
        with open(os.path.join(dest, name), "wb") as f:
            f.write(data)
        print(f"{name}: {len(data)} bytes")
    print(f"rec -> {dest}")
    return 0


# ---------------------------------------------------------------- report

def read_mjr(path):
    b = open(path, "rb").read()
    if b[:4] != b"MJR1":
        raise SystemExit(f"not an MJR1 file: {path}")
    o, frames = 12, []
    while o + 8 <= len(b):
        n, pts = struct.unpack_from("<II", b, o)
        if o + 8 + n > len(b):
            break
        frames.append((pts, b[o + 8:o + 8 + n]))
        o += 8 + n
    return frames


def read_prog(path, count):
    b = open(path, "rb").read()
    if b[:4] != b"PRG1":
        return None
    n = struct.unpack_from("<I", b, 4)[0]
    if n != count:
        return None
    return list(struct.unpack_from("<%df" % n, b, 8))


def parse_log(path):
    rows, ends = [], []
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            m = FOLLOW_RE.search(line)
            if m:
                d = {k: float(v) if k in ("t", "p", "max", "w", "f") else int(v) for k, v in m.groupdict().items()}
                c = CLOCK_RE.match(line)
                d["clock"] = c.group(1) if c else ""
                rows.append(d)
                continue
            if "FOLLOW 終了" in line:
                ends.append(line.strip())
    # 走行が複数あれば区切る（t が戻ったら新しい走行）。
    runs, cur = [], []
    for r in rows:
        if cur and r["t"] < cur[-1]["t"]:
            runs.append(cur)
            cur = []
        cur.append(r)
    if cur:
        runs.append(cur)
    return runs, ends


def analyze(run, prog):
    lines = []
    dur = run[-1]["t"] - run[0]["t"]
    lines.append(f"- 長さ {dur:.1f} 秒 / 行 {len(run)}（0.1 秒ごと）")
    lines.append(f"- 進み具合（最大）{run[0]['max']:.3f} → {run[-1]['max']:.3f}")
    # 立ち止まり: max が 0.5 秒以上動かなかった区間
    stalls, start = [], None
    for i in range(1, len(run)):
        moving = run[i]["max"] - run[i - 1]["max"] > 1e-4
        if not moving and start is None:
            start = run[i - 1]["t"]
        if moving and start is not None:
            if run[i - 1]["t"] - start >= 0.5:
                stalls.append((start, run[i - 1]["t"], run[i - 1]["max"]))
            start = None
    if start is not None and run[-1]["t"] - start >= 0.5:
        stalls.append((start, run[-1]["t"], run[-1]["max"]))
    lines.append(f"- 立ち止まり（進み具合が 0.5 秒以上止まった）{len(stalls)} 回")
    for s, e, m in stalls:
        lines.append(f"  - {s:.1f}〜{e:.1f} 秒（{e - s:.1f} 秒）進み具合 {m:.3f}")
    # 画の動き: 0.1 秒あたりの f の変化
    dfs = [abs(run[i]["f"] - run[i - 1]["f"]) for i in range(1, len(run))]
    walking = [d for i, d in enumerate(dfs) if run[i + 1]["max"] - run[i]["max"] > 1e-4]
    if walking:
        walking.sort()
        lines.append(f"- 歩いている間の画の動き（0.1 秒あたりのコマ数）中央値 {walking[len(walking)//2]:.2f} / 最大 {walking[-1]:.2f}")
    blended = sum(1 for r in run if r["b"] >= 0 and r["w"] > 0.01)
    lines.append(f"- 2 コマを混ぜていた行 {blended}/{len(run)}")
    lines.append(f"- 展開した枚数 {run[-1]['shown'] - run[0]['shown']}")
    if prog:
        valid = [(i, p) for i, p in enumerate(prog) if p == p]
        gaps = sorted((valid[k + 1][1] - valid[k][1], valid[k][1]) for k in range(len(valid) - 1))
        if gaps:
            lines.append(f"- 録画のコマ間の進み具合 中央値 {gaps[len(gaps)//2][0]:.4f} / 最大 {gaps[-1][0]:.4f}（{gaps[-1][1]:.3f} 付近）")
    return lines


def compose(frames_jpeg, row, cache):
    from PIL import Image
    import io

    def img(i):
        if i not in cache:
            cache[i] = Image.open(io.BytesIO(frames_jpeg[i][1])).convert("RGB")
        return cache[i]
    a = img(row["a"])
    blended = Image.blend(a, img(row["b"]), row["w"]) if row["b"] >= 0 and row["w"] > 0 else a
    return blended, a


def label(im, text):
    from PIL import ImageDraw
    im = im.copy()
    d = ImageDraw.Draw(im)
    d.rectangle([0, 0, im.width, 16], fill=(0, 0, 0))
    d.text((4, 2), text, fill=(255, 220, 120))
    return im


def cmd_report(a):
    import cv2
    import numpy as np
    from PIL import Image

    runs, ends = parse_log(a.log)
    if not runs:
        print("no FOLLOW lines in log (old APK? or 3-A not reached)")
        return 2
    mjr = os.path.join(a.rec, "L2C0.mjr")
    frames = read_mjr(mjr)
    prog = read_prog(mjr + ".prog", len(frames))
    stamp = datetime.now().strftime("%H%M%S")
    out = a.out or os.path.join(DRIVE_OUT, datetime.now().strftime("%Y-%m-%d") + "_follow-review", stamp)
    os.makedirs(out, exist_ok=True)
    md = [f"# 3-A 位置合わせ版の見直し", "", f"- ログ: `{a.log}`", f"- 録画: `{mjr}`（{len(frames)} コマ）", ""]
    for k, run in enumerate(runs):
        n = run[0]["n"]
        if n != len(frames):
            md.append(f"⚠ 走行 {k+1}: ログのコマ数 {n} と録画 {len(frames)} が違う（別の走行の録画）")
        md.append(f"## 走行 {k+1}（{run[0]['clock']}〜）")
        md += analyze(run, prog)
        md.append("")
        cache = {}
        w0, h0 = Image.open(__import__("io").BytesIO(frames[0][1])).size
        vw = cv2.VideoWriter(os.path.join(out, f"shown_{k+1}.mp4"), cv2.VideoWriter_fourcc(*"mp4v"), 10, (w0 * 2, h0))
        sheet = []
        for r in run:
            if r["a"] >= len(frames) or r["b"] >= len(frames):
                continue
            mix, nomix = compose(frames, r, cache)
            txt = f"t={r['t']:.1f} max={r['max']:.3f} f={r['f']:.2f} w={r['w']:.2f}"
            pair = Image.new("RGB", (w0 * 2, h0))
            pair.paste(label(mix, "blend  " + txt), (0, 0))
            pair.paste(label(nomix, "no blend  a=%d" % r["a"]), (w0, 0))
            vw.write(cv2.cvtColor(np.asarray(pair), cv2.COLOR_RGB2BGR))
            if abs(r["t"] - round(r["t"])) < 0.051:
                sheet.append(label(mix, txt).resize((w0 // 2, h0 // 2)))
        vw.release()
        if sheet:
            cols = 6
            rows_n = (len(sheet) + cols - 1) // cols
            sw, sh = sheet[0].size
            canvas = Image.new("RGB", (sw * cols, sh * rows_n))
            for i, im in enumerate(sheet):
                canvas.paste(im, ((i % cols) * sw, (i // cols) * sh))
            canvas.save(os.path.join(out, f"sheet_{k+1}.png"))
    if ends:
        md.append("## 実機の集計行")
        md += [f"- `{e.split('[TakeRunner] ')[-1]}`" for e in ends]
    with open(os.path.join(out, "report.md"), "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(md) + "\n")
    print(f"report -> {out}")
    return 0


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest="cmd", required=True)
    c = sub.add_parser("capture"); c.add_argument("--serial", required=True)
    p = sub.add_parser("pull"); p.add_argument("--serial", required=True); p.add_argument("--out")
    r = sub.add_parser("report"); r.add_argument("--log", required=True); r.add_argument("--rec", required=True); r.add_argument("--out")
    a = ap.parse_args()
    return {"capture": cmd_capture, "pull": cmd_pull, "report": cmd_report}[a.cmd](a)


if __name__ == "__main__":
    sys.exit(main())
