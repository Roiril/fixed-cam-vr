#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""題字の出の連番（`menu titlereveal`）を音つきの mp4 にする（2026-10-14）。

    py -3.11 tools/title-reveal-video.py <after のフォルダ> [<before のフォルダ>]

before を渡すと上下に並べる（上 = BEFORE / 下 = AFTER。見出しはフォルダ名から焼く）。
出力は after のフォルダへ:
  title_reveal.mp4       等速・`sfx_title_in` を A の縁（timeline.tsv の段が In になったコマ）に重ねる
  title_reveal_slow.mp4  1/3 速・無音（筆の運びを見る）
⚠ コマが 1 枚でも欠けたら止まる（代わりの絵で埋めない）。
"""
from __future__ import annotations

import csv
import io
import os
import shutil
import subprocess
import sys
import tempfile

from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOUND = os.path.join(ROOT, "Assets", "Resources", "Sound", "sfx_title_in.wav")
FPS = 60


def font(size: int):
    for f in ("C:/Windows/Fonts/meiryo.ttc", "C:/Windows/Fonts/YuGothM.ttc"):
        if os.path.exists(f):
            return ImageFont.truetype(f, size)
    return ImageFont.load_default()


def load(run: str) -> tuple[list[str], float]:
    tl = os.path.join(run, "timeline.tsv")
    if not os.path.exists(tl):
        sys.exit(f"timeline.tsv が無い: {run}")
    with io.open(tl, encoding="utf-8") as f:
        rows = list(csv.DictReader(f, delimiter="\t"))
    frames = []
    summon = None
    for r in rows:
        p = os.path.join(run, f"f{int(r['frame']):04d}.png")
        if not os.path.exists(p):
            sys.exit(f"コマが欠けている: {p}")
        frames.append(p)
        if summon is None and r["stage"] != "Wait":
            summon = int(r["frame"]) / FPS
    if summon is None:
        sys.exit(f"題字を呼び出したコマが無い: {run}")
    return frames, summon


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    after = os.path.abspath(sys.argv[1])
    before = os.path.abspath(sys.argv[2]) if len(sys.argv) > 2 else None
    fa, summon = load(after)
    fb = load(before)[0] if before else None
    if fb and len(fb) != len(fa):
        sys.exit(f"コマ数が違う: before {len(fb)} / after {len(fa)}")
    if not os.path.exists(SOUND):
        sys.exit(f"音が無い: {SOUND}")

    # 題字の外接矩形（after の最後のコマ）に余白を付けて切り出す。
    last = Image.open(fa[-1]).convert("L").point(lambda v: 255 if v > 40 else 0)
    bb = last.getbbox()
    if bb is None:
        sys.exit("最後のコマに題字が写っていない")
    pad = 40
    W0, H0 = Image.open(fa[0]).size
    crop = (max(0, bb[0] - pad), max(0, bb[1] - pad), min(W0, bb[2] + pad), min(H0, bb[3] + pad))
    cw, ch = crop[2] - crop[0], crop[3] - crop[1]
    head = 34
    rows = [("BEFORE " + os.path.basename(before), fb)] if fb else []
    rows.append(("AFTER " + os.path.basename(after), fa))
    W = cw + (cw % 2)
    H = len(rows) * (ch + head)
    H += H % 2
    f_big, f_small = font(22), font(18)

    tmp = tempfile.mkdtemp(prefix="title_reveal_")
    try:
        for k in range(len(fa)):
            canvas = Image.new("RGB", (W, H), (14, 14, 14))
            d = ImageDraw.Draw(canvas)
            for r, (label, frames) in enumerate(rows):
                y = r * (ch + head)
                d.text((10, y + 5), label, font=f_big, fill=(240, 120, 120) if label.startswith("BEFORE") else (120, 220, 140))
                canvas.paste(Image.open(frames[k]).convert("RGB").crop(crop), (0, y + head))
            d.text((W - 150, 6), f"{k / FPS - summon:+.2f}s", font=f_small, fill=(230, 230, 230))
            canvas.save(os.path.join(tmp, f"{k:05d}.png"))
        import imageio_ffmpeg
        ff = imageio_ffmpeg.get_ffmpeg_exe()
        out = os.path.join(after, "title_reveal.mp4")
        delay_ms = int(round(summon * 1000))
        subprocess.run([ff, "-y", "-loglevel", "error", "-framerate", str(FPS), "-i", os.path.join(tmp, "%05d.png"),
                        "-i", SOUND, "-filter_complex", f"[1:a]adelay={delay_ms}|{delay_ms},afade=t=out:st={len(fa) / FPS - 0.4:.3f}:d=0.4[a]",
                        "-map", "0:v", "-map", "[a]", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "16",
                        "-c:a", "aac", "-b:a", "192k", "-shortest", out + ".tmp.mp4"], check=True)
        os.replace(out + ".tmp.mp4", out)
        slow = os.path.join(after, "title_reveal_slow.mp4")
        subprocess.run([ff, "-y", "-loglevel", "error", "-framerate", str(FPS // 3), "-i", os.path.join(tmp, "%05d.png"),
                        "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "16", "-r", "30", slow + ".tmp.mp4"], check=True)
        os.replace(slow + ".tmp.mp4", slow)
        print("wrote", out)
        print("wrote", slow)
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
