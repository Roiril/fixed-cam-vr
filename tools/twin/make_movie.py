"""1 経路ぶんの固定カメラの動画を焼く。py -3.11 tools/twin/make_movie.py forward [--cams A,B,C] [--w 960] [--spp 48]

C: の空きが少ないので、カメラごとに焼いて仕上げ、中間ファイル（1 コマ 4MB）をその都度消す。
"""
from __future__ import annotations

import argparse
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
BLENDER = r"C:\Program Files\Blender Foundation\Blender 5.1\blender.exe"


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("route")
    ap.add_argument("--cams", default="A,B,C")
    ap.add_argument("--w", default="960")
    ap.add_argument("--spp", default="48")
    a = ap.parse_args()
    for cam in a.cams.split(","):
        subprocess.run([BLENDER, "--background", "--factory-startup", "--python-exit-code", "1", "--python", str(HERE / "twin_render.py"), "--", "seq",
                        "--route", a.route, "--cams", cam, "--w", a.w, "--spp", a.spp, "--plate-spp", "512"], check=True, stdout=subprocess.DEVNULL)
        subprocess.run([sys.executable, str(HERE / "seq_post.py"), a.route, "--cams", cam], check=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
