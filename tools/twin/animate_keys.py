"""生成した数枚のキーフレームを、オプティカルフローで中割りして動画にする。

    py -3.11 tools/twin/animate_keys.py --keys k0.png k1.png k2.png --move 0.6 0.8 --hold 0.2 0 0.3 \
        --pingpong --keep 0,0,320,480 --out out.mp4 --sheet sheet.png

動画生成サービスを使わず、Codex の画像生成で作った数枚（同じ構図で少しずつ動かした絵）を連ねるための道具。
ツインの部屋で作り直した合成素材（tools/twin/assets.json）の動画はこれで作る。

- 中割りは前後の両方向のフローで引き寄せて混ぜる（DIS）。布の揺れ・人形の腕程度の動きを想定している。
  大きく位置が飛ぶもの（人形が別の場所に現れる）は中割りすると幽霊のように透けて重なる。そういうときは --move を 0 にして切る
- --keep の矩形の外は 1 枚目の画素に戻す（合成マスクの外まで生成が触っても、動かない所を揺らさない）
- 出力は H.264 / yuv420p / 640x480 / 24fps（本番の素材と同じ形）
"""
from __future__ import annotations

import argparse
import subprocess
import sys
import tempfile
from pathlib import Path

import cv2
import numpy as np


def load(path: str, size: tuple[int, int]) -> np.ndarray:
    im = cv2.imread(path, cv2.IMREAD_COLOR)
    if im is None:
        raise SystemExit(f"読めない: {path}")
    if (im.shape[1], im.shape[0]) != size:
        im = cv2.resize(im, size, interpolation=cv2.INTER_AREA)
    return im


def flow(a: np.ndarray, b: np.ndarray) -> np.ndarray:
    dis = cv2.DISOpticalFlow_create(cv2.DISOPTICAL_FLOW_PRESET_MEDIUM)
    return dis.calc(cv2.cvtColor(a, cv2.COLOR_BGR2GRAY), cv2.cvtColor(b, cv2.COLOR_BGR2GRAY), None)


def warp(img: np.ndarray, f: np.ndarray, t: float) -> np.ndarray:
    """img を、f（img→相手）の t 倍だけ進めた位置から引く（後方写像の近似）。"""
    h, w = img.shape[:2]
    gx, gy = np.meshgrid(np.arange(w, dtype=np.float32), np.arange(h, dtype=np.float32))
    return cv2.remap(img, gx - f[..., 0] * t, gy - f[..., 1] * t, cv2.INTER_LINEAR, borderMode=cv2.BORDER_REFLECT)


def tween(a: np.ndarray, b: np.ndarray, n: int) -> list[np.ndarray]:
    """a から b へ n コマ（a は含み b は含まない）。"""
    if n <= 0:
        return []
    fab, fba = flow(a, b), flow(b, a)
    out = []
    for i in range(n):
        t = i / n
        s = t * t * (3 - 2 * t)  # 動き始めと止まり際を緩める
        wa = warp(a, fab, s).astype(np.float32)
        wb = warp(b, fba, 1 - s).astype(np.float32)
        out.append(np.clip(wa * (1 - s) + wb * s, 0, 255).astype(np.uint8))
    return out


def keep_mask(size: tuple[int, int], box: list[int] | None, feather: int) -> np.ndarray | None:
    if not box:
        return None
    w, h = size
    m = np.zeros((h, w), np.float32)
    x0, y0, x1, y1 = box
    m[y0:y1, x0:x1] = 1.0
    if feather > 0:
        m = cv2.GaussianBlur(m, (0, 0), feather)
        m[y0:y1, x0:x1] = 1.0  # 箱の中は生成のまま。ぼかしは箱の外側にだけ掛かる
    return m[..., None]


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser()
    ap.add_argument("--keys", nargs="+", required=True)
    ap.add_argument("--move", nargs="+", type=float, required=True, help="キー i から i+1 へ動く秒（キーの数 - 1 個）。0 は切る")
    ap.add_argument("--hold", nargs="+", type=float, default=[], help="各キーで止まる秒（キーの数ぶん。足りなければ 0）")
    ap.add_argument("--pingpong", action="store_true", help="最後のキーから 1 枚目へ逆にたどって戻る")
    ap.add_argument("--keep", default="", help="x0,y0,x1,y1。この外は 1 枚目の画素に戻す")
    ap.add_argument("--feather", type=int, default=12)
    ap.add_argument("--fps", type=int, default=24)
    ap.add_argument("--size", default="640x480")
    ap.add_argument("--out", required=True)
    ap.add_argument("--sheet", default="", help="コマを並べた確認用 PNG")
    a = ap.parse_args()

    size = tuple(int(v) for v in a.size.split("x"))
    keys = [load(k, size) for k in a.keys]
    if len(a.move) != len(keys) - 1:
        raise SystemExit("--move はキーの数 - 1 個")
    hold = list(a.hold) + [0.0] * (len(keys) - len(a.hold))
    km = keep_mask(size, [int(v) for v in a.keep.split(",")] if a.keep else None, a.feather)
    if km is not None:
        base = keys[0].astype(np.float32)
        keys = [keys[0]] + [np.clip(k.astype(np.float32) * km + base * (1 - km), 0, 255).astype(np.uint8) for k in keys[1:]]

    frames: list[np.ndarray] = []
    for i, k in enumerate(keys):
        frames += [k] * round(hold[i] * a.fps)
        if i < len(keys) - 1:
            frames += tween(k, keys[i + 1], round(a.move[i] * a.fps)) if a.move[i] > 0 else []
    if a.pingpong:
        frames += frames[-2:0:-1] if len(frames) > 2 else []
    if not frames:
        raise SystemExit("コマが 0")

    out = Path(a.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory() as td:
        for i, f in enumerate(frames):
            cv2.imwrite(str(Path(td) / f"{i:04d}.png"), f)
        cmd = ["ffmpeg", "-y", "-v", "error", "-framerate", str(a.fps), "-i", str(Path(td) / "%04d.png"),
               "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "16", "-an", str(out)]
        rc = subprocess.run(cmd).returncode
        if rc != 0:
            raise SystemExit(f"ffmpeg exit={rc}")
    if a.sheet:
        pick = np.linspace(0, len(frames) - 1, 8).round().astype(int)
        tiles = []
        for i in pick:
            t = cv2.resize(frames[i], (320, 240))
            cv2.putText(t, f"{i}", (6, 22), cv2.FONT_HERSHEY_SIMPLEX, 0.7, (0, 255, 255), 2)
            tiles.append(t)
        cv2.imwrite(a.sheet, np.vstack([np.hstack(tiles[:4]), np.hstack(tiles[4:])]))
    print(f"{out} {len(frames)} コマ {len(frames) / a.fps:.2f} 秒")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
