"""博士の部屋のテレビに映すツインの絵と動画を、テレビ用の明るさに揃えて layers/ へ置く。

py -3.11 tools/visitor-ui/room-proto/make-tv.py still <PNG> <名前> [--lift x0,y0,x1,y1,倍率] → layers/<名前>.jpg
py -3.11 tools/visitor-ui/room-proto/make-tv.py clip <mp4> <名前> [--trim 秒] → layers/<名前>.mp4

ツインの仕上げ（twin_post）は写真として明るく出る（中央値 90 前後）。テレビの中では人形の記録写真
（asset/doll-file-v1.jpg・中央値 21）と並び、テレビの粒と光でさらに持ち上がるので、そのままだと白く浮く。
中央値が MEDIAN になる冪で暗部を沈め、彩度を少し落とす。動画は最初のコマで冪を決めて全コマに同じ値を掛ける
（コマごとに決めると人の出入りで明滅する）。
"""
from __future__ import annotations

import argparse
import math
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

import numpy as np
from PIL import Image

HERE = Path(__file__).resolve().parent
LAYERS = HERE / "layers"
MEDIAN = 44.0      # 0–255。人形の記録写真より少し明るく（テレビの中で読めるように）
SATURATION = 0.85
GAMMA_RANGE = (1.0, 2.6)


def gamma_for(rgb: np.ndarray) -> float:
    lum = rgb.astype(np.float32) @ np.array([0.2126, 0.7152, 0.0722], dtype=np.float32)
    med = float(np.median(lum))
    if med <= 1:
        return 1.0
    g = math.log(MEDIAN / 255.0) / math.log(med / 255.0)
    return float(min(GAMMA_RANGE[1], max(GAMMA_RANGE[0], g)))


def grade(rgb: np.ndarray, g: float) -> np.ndarray:
    x = (rgb.astype(np.float32) / 255.0) ** g
    lum = (x @ np.array([0.2126, 0.7152, 0.0722], dtype=np.float32))[..., None]
    x = lum + (x - lum) * SATURATION
    return (np.clip(x, 0, 1) * 255.0 + 0.5).astype(np.uint8)


def median_of(rgb: np.ndarray) -> float:
    return float(np.median(rgb.astype(np.float32) @ np.array([0.2126, 0.7152, 0.0722], dtype=np.float32)))


def lift(rgb: np.ndarray, box: list[float]) -> np.ndarray:
    """箱 (x0, y0, x1, y1, 倍率。座標は 0〜1) の中を、縁をぼかして明るくする。光る画面が周りより暗く沈んだときに使う。"""
    x0, y0, x1, y1, gain = box
    h, w = rgb.shape[:2]
    yy = (np.arange(h) + 0.5) / h
    xx = (np.arange(w) + 0.5) / w
    soft = 0.012
    wx = np.clip((xx - x0) / soft, 0, 1) * np.clip((x1 - xx) / soft, 0, 1)
    wy = np.clip((yy - y0) / soft, 0, 1) * np.clip((y1 - yy) / soft, 0, 1)
    m = (wy[:, None] * wx[None, :])[..., None]
    lin = (rgb.astype(np.float32) / 255.0) ** 2.2
    lin = lin * (1 + (gain - 1) * m)
    return (np.clip(lin, 0, 1) ** (1 / 2.2) * 255.0 + 0.5).astype(np.uint8)


def still(src: Path, name: str, box: list[float] | None) -> None:
    rgb = np.asarray(Image.open(src).convert("RGB"))
    g = gamma_for(rgb)
    out = LAYERS / f"{name}.jpg"
    graded = grade(rgb, g)
    if box:
        graded = lift(graded, box)
    Image.fromarray(graded).save(out, quality=90, optimize=True)
    print(f"[make-tv] {out.name} {rgb.shape[1]}x{rgb.shape[0]} 冪 {g:.2f} 中央値 {median_of(rgb):.0f} → {median_of(np.asarray(Image.open(out))):.0f}")


def clip(src: Path, name: str, trim: float | None) -> None:
    tmp = Path(tempfile.mkdtemp(prefix="make_tv_"))
    try:
        cmd = ["ffmpeg", "-y", "-loglevel", "error", "-i", str(src)]
        if trim:
            cmd += ["-t", str(trim)]
        subprocess.run(cmd + [str(tmp / "f%05d.png")], check=True)
        files = sorted(tmp.glob("f*.png"))
        if not files:
            raise SystemExit(f"[make-tv] コマが取れない: {src}")
        g = gamma_for(np.asarray(Image.open(files[0]).convert("RGB")))
        for f in files:
            Image.fromarray(grade(np.asarray(Image.open(f).convert("RGB")), g)).save(f)
        fps = subprocess.run(["ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=r_frame_rate", "-of", "csv=p=0", str(src)],
                             capture_output=True, text=True, check=True).stdout.strip()
        out = LAYERS / f"{name}.mp4"
        # キーフレームを細かく（書き出しはコマごとに seek する）。crf 23: ツインの粒で 18 だと 1 周 23MB になった
        subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-framerate", fps, "-i", str(tmp / "f%05d.png"),
                        "-c:v", "libx264", "-crf", "23", "-preset", "slow", "-g", "10", "-pix_fmt", "yuv420p", "-movflags", "+faststart", str(out)], check=True)
        print(f"[make-tv] {out.name} {len(files)} コマ {fps}fps 冪 {g:.2f}")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser()
    ap.add_argument("kind", choices=["still", "clip"])
    ap.add_argument("src")
    ap.add_argument("name")
    ap.add_argument("--trim", type=float, default=None)
    ap.add_argument("--lift", default="", help="x0,y0,x1,y1,倍率（静止画だけ）")
    a = ap.parse_args()
    src = Path(a.src)
    if not src.exists():
        raise SystemExit(f"[make-tv] 無い: {src}")
    if a.kind == "still":
        still(src, a.name, [float(v) for v in a.lift.split(",")] if a.lift else None)
    else:
        clip(src, a.name, a.trim)
    return 0


if __name__ == "__main__":
    sys.exit(main())
