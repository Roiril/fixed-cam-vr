# -*- coding: utf-8 -*-
"""Flow が返した 4-A の「人形が集まる」動画を、無人写真の座標へ戻して cue の素材にする。

    py -3.11 tools/gen-plate/video/fit_dolls_in.py --video <Flow の mp4> \
        --plate tools/web-compositor/captures/plate_A_<日時>.jpg \
        --still tools/web-compositor/captures/gen_dollsA_left_<日時>.png \
        --out tools/web-compositor/captures/gen_dollsA_in_<日付>.mp4 [--sheet <コマ一覧.jpg>]

首尾フレームは 2560x1440 の黒地の中央に 4:3 を置いて渡す（`dolls-gather.md`）。返ってくる 640x360 の中の画は
少し拡大されている（2026-11-15 実測 1.02 倍）。1 コマ目と無人写真が最も重なる倍率と位置を探して 640x480 へ戻し、
はみ出した所は無人写真で埋める。頭の数コマを無人写真から、尾の数コマを静止画へ溶かして、
切り替わりで跳ねないようにする。
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

import imageio_ffmpeg

W, H = 640, 480


def load(p: Path) -> np.ndarray:
    return np.asarray(Image.open(p).convert("RGB").resize((W, H), Image.LANCZOS), np.float32)


def place(frame: Image.Image, scale: float, cx: float, cy: float, plate: np.ndarray) -> np.ndarray:
    """フレーム中の中心 (cx,cy)・幅 480*scale*(fh/360) の 4:3 枠を 640x480 へ写す。外は plate。"""
    fw, fh = frame.size
    k = fh / 360.0
    bw = 480.0 * scale * k
    bh = 360.0 * scale * k
    x0, y0 = cx * k - bw / 2, cy * k - bh / 2
    a = np.asarray(frame.convert("RGB"), np.float32)
    # 出力画素 (u,v) → 入力 (x0 + u*bw/W, y0 + v*bh/H)
    u = (np.arange(W) + 0.5) * bw / W + x0 - 0.5
    v = (np.arange(H) + 0.5) * bh / H + y0 - 0.5
    uu, vv = np.meshgrid(u, v)
    inside = (uu >= 0) & (uu <= fw - 1) & (vv >= 0) & (vv <= fh - 1)
    x = np.clip(uu, 0, fw - 1)
    y = np.clip(vv, 0, fh - 1)
    xi, yi = np.floor(x).astype(int), np.floor(y).astype(int)
    xj, yj = np.minimum(xi + 1, fw - 1), np.minimum(yi + 1, fh - 1)
    fx, fy = (x - xi)[..., None], (y - yi)[..., None]
    out = (a[yi, xi] * (1 - fx) * (1 - fy) + a[yi, xj] * fx * (1 - fy)
           + a[yj, xi] * (1 - fx) * fy + a[yj, xj] * fx * fy)
    return np.where(inside[..., None], out, plate)


def diff(a: np.ndarray, b: np.ndarray) -> float:
    return float(np.abs(a.mean(2) - b.mean(2)).mean())


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--video", required=True)
    ap.add_argument("--plate", required=True)
    ap.add_argument("--still", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--head", type=int, default=4, help="無人写真から溶かすコマ数")
    ap.add_argument("--tail", type=int, default=8, help="静止画へ溶かすコマ数")
    ap.add_argument("--sheet")
    a = ap.parse_args()

    ff = imageio_ffmpeg.get_ffmpeg_exe()
    plate = load(Path(a.plate))
    still = load(Path(a.still))
    tmp = Path(tempfile.mkdtemp(prefix="dollsin_"))
    try:
        subprocess.run([ff, "-v", "error", "-i", a.video, "-an", str(tmp / "f_%04d.png")], check=True)
        frames = sorted(tmp.glob("f_*.png"))
        if not frames:
            print("コマが取れない", file=sys.stderr)
            return 1
        f0 = Image.open(frames[0])
        fl = Image.open(frames[-1])
        # 倍率と中心を 1 コマ目 × 無人写真で探す（粗→細）
        best = None
        for s in np.arange(0.99, 1.05, 0.005):
            for dx in range(-6, 7, 2):
                for dy in range(-6, 7, 2):
                    d = diff(place(f0, s, 320 + dx, 180 + dy, plate), plate)
                    if best is None or d < best[0]:
                        best = (d, s, 320 + dx, 180 + dy)
        _, s0, cx0, cy0 = best
        for s in np.arange(s0 - 0.004, s0 + 0.0041, 0.001):
            for dx in np.arange(-1.5, 1.6, 0.5):
                for dy in np.arange(-1.5, 1.6, 0.5):
                    d = diff(place(f0, s, cx0 + dx, cy0 + dy, plate), plate)
                    if d < best[0]:
                        best = (d, s, cx0 + dx, cy0 + dy)
        d0, s, cx, cy = best
        dl = diff(place(fl, s, cx, cy, plate), still)
        print(f"倍率 {s:.3f}  中心 ({cx:.1f},{cy:.1f})  1 コマ目と無人写真の差 {d0:.2f}  最終コマと静止画の差 {dl:.2f}  コマ数 {len(frames)}")
        outdir = tmp / "out"
        outdir.mkdir()
        n = len(frames)
        placed = []
        for i, fp in enumerate(frames):
            g = place(Image.open(fp), s, cx, cy, plate)
            if i < a.head:
                t = (i + 1) / (a.head + 1)
                g = plate * (1 - t) + g * t
            if i >= n - a.tail:
                t = (i - (n - a.tail) + 1) / a.tail
                g = g * (1 - t) + still * t
            img = Image.fromarray(np.clip(g + 0.5, 0, 255).astype(np.uint8))
            img.save(outdir / f"o_{i:04d}.png")
            placed.append(img)
        fps = subprocess.run([ff, "-i", a.video], capture_output=True, text=True, errors="replace").stderr
        rate = "24"
        for tok in fps.split(","):
            tok = tok.strip()
            if tok.endswith(" fps"):
                rate = tok[:-4]
                break
        out = Path(a.out)
        out.parent.mkdir(parents=True, exist_ok=True)
        subprocess.run([ff, "-v", "error", "-y", "-framerate", rate, "-i", str(outdir / "o_%04d.png"),
                        "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "16", "-movflags", "+faststart",
                        str(out)], check=True)
        print(f"書いた {out}  {n} コマ / {rate} fps / {n / float(rate):.2f} 秒")
        if a.sheet:
            idx = np.linspace(0, n - 1, 12).astype(int)
            sw, sh = 320, 240
            sheet = Image.new("RGB", (sw * 4, sh * 3))
            for k, i in enumerate(idx):
                sheet.paste(placed[i].resize((sw, sh)), ((k % 4) * sw, (k // 4) * sh))
            sheet.save(a.sheet, quality=88)
            print(f"コマ一覧 {a.sheet}")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    return 0


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    raise SystemExit(main())
