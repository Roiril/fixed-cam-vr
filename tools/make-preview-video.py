#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Unity が焼いた連番 PNG を mp4 にする。

    py -3.11 tools/make-preview-video.py Assets/Screenshots/comms-preview comms

⚠ **絵は Unity が描いたものをそのまま並べるだけ**（CPU の模写ではない）。
`tools/preview-visitor-mark.py` は実装を写して自分で描くが、こちらは実物のコマを繋ぐ。

出力は `logs/preview/<日時>_<名前>.mp4`。連番は `f0000.png` から。
"""
from __future__ import annotations

import sys
from datetime import datetime
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8")

ROOT = Path(__file__).resolve().parent.parent
OUTDIR = ROOT / "logs/preview"
FPS = 30


def main() -> int:
    if len(sys.argv) < 3:
        print("使い方: make-preview-video.py <連番 PNG のフォルダ> <名前>")
        return 2

    src = (ROOT / sys.argv[1]).resolve()
    name = sys.argv[2]
    frames = sorted(src.glob("f*.png"))
    if not frames:
        print(f"⚠ 連番 PNG が無い: {src}")
        return 1

    try:
        import imageio_ffmpeg
        from PIL import Image
    except ImportError as e:
        print(f"⚠ 依存が無い（py -3.11 -m pip install imageio-ffmpeg pillow）: {e}")
        return 1

    w, h = Image.open(frames[0]).size
    OUTDIR.mkdir(parents=True, exist_ok=True)
    mp4 = OUTDIR / f"{datetime.now():%Y%m%d_%H%M%S}_{name}.mp4"

    writer = imageio_ffmpeg.write_frames(
        str(mp4), (w, h), fps=FPS, quality=8, macro_block_size=1)
    writer.send(None)
    for p in frames:
        writer.send(Image.open(p).convert("RGB").tobytes())
    writer.close()

    print(f"{mp4.relative_to(ROOT)}  ({len(frames)} コマ / {len(frames)/FPS:.1f} 秒 / {w}x{h})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
