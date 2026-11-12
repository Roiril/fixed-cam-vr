"""人形視点のコマ（twin_render.py pov）を仕上げて、合成素材の動画にする。

    py -3.11 tools/twin/make_pov.py            # tools/twin/assets/twin_pov_<番号>.mp4（640x480・24fps）
    py -3.11 tools/twin/make_pov.py --sheet x.jpg   # 動画は書かず、あるコマだけを並べて見る

入力は Logs/twin-cycles/pov/<番号>/fNNNNN.npy。仕上げは固定カメラと同じスマホの見た目（twin_post の phone）。
露出は 5 本を通して 1 度だけ決める（カットごとに測ると、迫るにつれて黒い背中で明るさが跳ねる）。
スマホが配る明るさへの階調（feed_tone）はここでは掛けない。make_assets.py が cue のカメラの表で掛ける。
"""
from __future__ import annotations

import argparse
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

import numpy as np
from PIL import Image

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import twin_post  # noqa: E402

ROOT = HERE.parents[1]
POV = ROOT / "Logs" / "twin-cycles" / "pov"
OUT = HERE / "assets"
FPS = 24


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser()
    ap.add_argument("--sheet", default="")
    a = ap.parse_args()
    clips = sorted(p for p in POV.iterdir() if p.is_dir() and p.name.isdigit())
    if not clips:
        raise SystemExit(f"コマが無い: {POV}（twin_render.py pov）")
    first = sorted(clips[0].glob("f*.npy"))
    if not first:
        raise SystemExit(f"コマが無い: {clips[0]}")
    exposure = twin_post.auto_exposure(np.load(first[0]).astype(np.float32), twin_post.LOOKS["phone"]["key"])

    if a.sheet:
        tiles = []
        for c in clips:
            for p in sorted(c.glob("f*.npy")):
                bgr = twin_post.process(np.load(p).astype(np.float32), "phone", seed=int(p.stem[1:]) + 1, exposure=exposure)
                tiles.append((f"pov{c.name} {p.stem}", Image.fromarray(bgr[..., ::-1])))
        w, h = tiles[0][1].size
        cols = 5
        rows = (len(tiles) + cols - 1) // cols
        sheet = Image.new("RGB", (w * cols, h * rows))
        for i, (_, im) in enumerate(tiles):
            sheet.paste(im, ((i % cols) * w, (i // cols) * h))
        sheet.save(a.sheet, quality=88)
        print(" / ".join(n for n, _ in tiles))
        return 0

    for c in clips:
        files = sorted(c.glob("f*.npy"))
        ids = [int(p.stem[1:]) for p in files]
        if ids != list(range(len(ids))):
            raise SystemExit(f"コマが飛んでいる: {c}")
        tmp = Path(tempfile.mkdtemp(prefix="twin_pov_"))
        try:
            for i, p in enumerate(files):
                bgr = twin_post.process(np.load(p).astype(np.float32), "phone", seed=2000 + int(c.name) * 100 + i, exposure=exposure)
                twin_post.write_image(str(tmp / f"f{i:05d}.jpg"), bgr)
            mp4 = tmp / "out.mp4"
            subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-framerate", str(FPS), "-i", str(tmp / "f%05d.jpg"),
                            "-c:v", "libx264", "-crf", "17", "-preset", "slow", "-g", "6", "-pix_fmt", "yuv420p",
                            "-movflags", "+faststart", str(mp4)], check=True)
            shutil.copy2(mp4, OUT / f"twin_pov_{c.name}.mp4")
            print(f"pov{c.name}: {len(files)} コマ → {OUT / ('twin_pov_' + c.name + '.mp4')}")
        finally:
            shutil.rmtree(tmp, ignore_errors=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
