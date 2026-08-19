#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Unity が焼いた連番 PNG を mp4 にする。

    py -3.11 tools/make-preview-video.py Assets/Screenshots/comms-preview comms
    py -3.11 tools/make-preview-video.py <folder> <name> --audio logs/preview/comms_type.wav

⚠ **絵は Unity が描いたものをそのまま並べるだけ**（CPU の模写ではない）。
`tools/preview-visitor-mark.py` は実装を写して自分で描くが、こちらは実物のコマを繋ぐ。

`--audio` を渡すと WAV を重ねる。**音は録画にも実機にも残らない**ので、
「絵と合っているか」を人が判定できる形はこれしか無い（`tools/comms-preview-audio.py` が作る）。

出力は `logs/preview/<日時>_<名前>.mp4`。連番は `f0000.png` から。
"""
from __future__ import annotations

import subprocess
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
    audio = None
    if "--audio" in sys.argv:
        audio = (ROOT / sys.argv[sys.argv.index("--audio") + 1]).resolve()
        if not audio.exists():
            print(f"⚠ 音が無い: {audio}")
            return 1
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
    # ⚠ H.264（yuv420p）は**縦横とも偶数**でないとエンコーダが開かない。
    #   奇数のまま渡すと ffmpeg が `height not divisible by 2` で落ち、
    #   **0 バイトの mp4 が残る**（実測 2026-08-19。1280x893）。1 画素だけ切って偶数へ。
    even_w, even_h = w - (w % 2), h - (h % 2)
    if (even_w, even_h) != (w, h):
        print(f"⚠ {w}x{h} は奇数を含むので {even_w}x{even_h} へ 1 画素切る（H.264 の制約）")
    OUTDIR.mkdir(parents=True, exist_ok=True)
    mp4 = OUTDIR / f"{datetime.now():%Y%m%d_%H%M%S}_{name}.mp4"

    writer = imageio_ffmpeg.write_frames(
        str(mp4), (even_w, even_h), fps=FPS, quality=8, macro_block_size=1)
    writer.send(None)
    for p in frames:
        im = Image.open(p).convert("RGB")
        if im.size != (even_w, even_h):
            im = im.crop((0, 0, even_w, even_h))
        writer.send(im.tobytes())
    writer.close()

    if audio is not None:
        # ⚠ **絵は焼き直さない**（`-c:v copy`）。再エンコードすると、コマを繋いだ意味が薄れる。
        #    音より絵が短ければ音を切る（`-shortest`）。
        muxed = mp4.with_name(mp4.stem + "_snd.mp4")
        r = subprocess.run([imageio_ffmpeg.get_ffmpeg_exe(), "-y", "-v", "error",
                            "-i", str(mp4), "-i", str(audio),
                            "-c:v", "copy", "-c:a", "aac", "-b:a", "192k",
                            "-shortest", str(muxed)],
                           capture_output=True, text=True, encoding="utf-8", errors="replace")
        if r.returncode != 0:
            print(f"⚠ 音を重ねられなかった: {r.stderr.strip()[:200]}")
        else:
            mp4.unlink()
            mp4 = muxed

    print(f"{mp4.relative_to(ROOT)}  ({len(frames)} コマ / {len(frames)/FPS:.1f} 秒 / {even_w}x{even_h}"
          f"{' ＋ 音' if audio is not None else ''})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
