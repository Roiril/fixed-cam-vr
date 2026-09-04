# -*- coding: utf-8 -*-
"""cue の動画を**実機の経路に通した動画**にする（体験者が見る絵をそのまま見るため）。

    py -3.11 tools/gen-plate/seenvideo.py --video tools/web-compositor/captures/gen_stainA_smile_20260904.mp4 \
        --mask tools/web-compositor/masks/cue_stain_A_20260904_vid.png \
        --live tools/web-compositor/captures/plate_A_20260823_194234.jpg \
        --lap 2 --out logs/gen-plate/seen_stainA_lap2.mp4

`screen.py` は 1 枚しか通せない。動きのある異変は**1 枚では判断できない**ので、全コマを通す。
1 コマ 2.7 秒ほど掛かる（97 コマで 4〜5 分）。

⚠ これは **PC の中で経路を再現した画**で、実機で撮った映像ではない。
   実機の絵が要るなら `py -3.11 tools/quest-record.py --sec 300 --walk`（走行 1 回ぶん）。
"""
from __future__ import annotations

import argparse
import os
import shutil
import subprocess
import sys
import tempfile
import time

import numpy as np
from PIL import Image

import screen

try:
    import imageio_ffmpeg
except ImportError:
    imageio_ffmpeg = None


def ffmpeg() -> str:
    if imageio_ffmpeg is not None:
        return imageio_ffmpeg.get_ffmpeg_exe()
    exe = shutil.which("ffmpeg")
    if not exe:
        raise SystemExit("ffmpeg が無い")
    return exe


def main() -> int:
    ap = argparse.ArgumentParser(description="cue の動画を実機の経路に通した動画にする")
    ap.add_argument("--video", required=True, help="cue の素材（動画）")
    ap.add_argument("--mask", default="", help="cue のマスク（無ければ全画面差し替え）")
    ap.add_argument("--live", required=True, help="ライブ側に置くプレート")
    ap.add_argument("--lap", type=float, default=2.0)
    ap.add_argument("--out", required=True)
    ap.add_argument("--fps", type=float, default=0.0, help="0 = 元のまま")
    ap.add_argument("--every", type=int, default=1, help="N コマに 1 枚だけ通す（下見用）")
    a = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")

    show = screen.load_show()
    p = dict(exposure=-1.05, contrast=1.12, saturation=0.52, temperature=0.48,
             tint=0.0, lift=0.02, vignette=0.38)
    for k, v in (show.get("post") or {}).items():
        if k in p and isinstance(v, (int, float)):
            p[k] = float(v)
    total = max(2.0, float((show.get("run") or {}).get("totalLaps", 3)))
    prog = min(max((a.lap - 1.0) / (total - 1.0), 0.0), 1.0)
    blocks = screen.FINE_BLOCKS + (screen.END_BLOCKS - screen.FINE_BLOCKS) * prog

    ff = ffmpeg()
    tmp = tempfile.mkdtemp(prefix="seenvideo_")
    src = os.path.join(tmp, "src")
    dst = os.path.join(tmp, "dst")
    os.makedirs(src); os.makedirs(dst)

    r = subprocess.run([ff, "-y", "-i", a.video, os.path.join(src, "%05d.png")],
                       capture_output=True, text=True)
    frames = sorted(os.listdir(src))
    if not frames:
        print(r.stderr[-800:], file=sys.stderr)
        raise SystemExit("コマを取り出せなかった")
    fps = a.fps
    if fps <= 0:
        for line in r.stderr.splitlines():
            if " fps," in line and "Video:" in line:
                try:
                    fps = float(line.split(" fps,")[0].split(",")[-1].strip())
                except ValueError:
                    pass
        fps = fps or 24.0
    use = frames[::max(1, a.every)]
    print(f"コマ {len(frames)} 枚（通すのは {len(use)} 枚） / {fps:g} fps  "
          f"周 {a.lap:.0f} → 劣化 {prog:.2f} / {blocks:.0f} ブロック")

    t0 = time.time()
    for i, name in enumerate(use):
        col = screen.render(a.live, os.path.join(src, name), a.mask or None, p, blocks, prog)
        Image.fromarray(screen.linear_to_srgb(col).astype(np.uint8)).save(
            os.path.join(dst, f"{i + 1:05d}.png"))
        if (i + 1) % 10 == 0 or i + 1 == len(use):
            el = time.time() - t0
            print(f"  {i + 1}/{len(use)}  {el:.0f}s 経過 / 残り {el / (i + 1) * (len(use) - i - 1):.0f}s")

    os.makedirs(os.path.dirname(os.path.abspath(a.out)) or ".", exist_ok=True)
    out_fps = fps / max(1, a.every)
    r2 = subprocess.run([ff, "-y", "-framerate", f"{out_fps:g}",
                         "-i", os.path.join(dst, "%05d.png"),
                         "-an", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "18", a.out],
                        capture_output=True, text=True)
    if r2.returncode != 0 or not os.path.exists(a.out):
        print(r2.stderr[-1200:], file=sys.stderr)
        raise SystemExit("書き出しに失敗した")
    print(f"{a.out}  {len(use)} コマ / {len(use) / out_fps:.2f} 秒 / "
          f"{os.path.getsize(a.out) // 1024}KB")
    print("⚠ これは PC の中で経路を再現した画。実機で撮った映像ではない")
    shutil.rmtree(tmp, ignore_errors=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
