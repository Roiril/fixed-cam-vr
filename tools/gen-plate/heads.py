# -*- coding: utf-8 -*-
"""足したものの**頭だけ**を切り出して並べる（視線と顔の作りを見るため）。

    py -3.11 tools/gen-plate/heads.py --run logs/gen-plate/<走行> [--top 24]

塊の上側を頭とみなして拡大し、番号を付けて敷き詰める。人形が 20 体居るなら 20 個並ぶので、
**「全部こちらを見ている」を目で 1 個ずつ確かめられる**（`~/.claude/rules/work-style.md` §2）。

添える数字は **左右対称度**（顔を左右反転して重ねたときの一致度）。
正面を向いた顔は高く、横を向くほど低い。⚠ **これは向きの代わりであって向きそのものではない**
（横からの光でも下がる）。**同じプレートで撮った走行どうしを比べるためだけに使う。**
"""
from __future__ import annotations

import argparse
import json
import os
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFont

import metrics

CELL = 104          # 1 個ぶんの高さ（画素）
HEAD_FRAC = 0.42    # 塊の上から何割を頭とみなすか


def _font(size: int):
    for p in ("C:/Windows/Fonts/meiryo.ttc", "C:/Windows/Fonts/msgothic.ttc"):
        try:
            return ImageFont.truetype(p, size)
        except OSError:
            continue
    return ImageFont.load_default()


def symmetry(crop: Image.Image) -> float:
    """左右反転との一致度（0..1）。正面ほど高い。"""
    g = np.asarray(crop.convert("L").resize((32, 32), Image.LANCZOS), dtype=np.float64)
    a, b = g, g[:, ::-1]
    a = a - a.mean(); b = b - b.mean()
    d = float(np.sqrt((a * a).sum() * (b * b).sum()))
    return float((a * b).sum() / d) if d > 1e-9 else 0.0


def faces(out: Image.Image, added: np.ndarray) -> list[tuple]:
    """足した所の中の**明るい小さな塊**＝顔を拾う。

    人形の顔はこの部屋でいちばん明るい面になる。塊（＝人形の群れ）は互いにくっついて
    1 つになるので、塊の上側を頭とみなす方法だと着物を切ってしまう（実測で 24 個中 15 個）。
    """
    g = np.asarray(out.convert("L"), dtype=np.float64)
    if not added.any():
        return []
    thr = float(np.percentile(g[added], 78))
    bright = metrics.clean(added & (g >= thr))
    out_boxes = []
    for c in metrics.components(bright, min_area=18):
        w, h = c["x1"] - c["x0"], c["y1"] - c["y0"]
        if not (5 <= w <= 90 and 5 <= h <= 90) or c["area"] > 2600:
            continue
        if w > h * 2.2 or h > w * 2.6:      # 帯や袖のような細長い明部を落とす
            continue
        pad = max(2, min(w, h) // 3)
        out_boxes.append((max(0, c["x0"] - pad), max(0, c["y0"] - pad),
                          min(out.width, c["x1"] + pad), min(out.height, c["y1"] + pad)))

    # 明るい塊には顔と**着物の襟**が混ざる。顔の方が色が付いていないので、
    # 彩度の低い側の半分だけ残す（絶対値を決め打ちにしないので部屋に依らない）
    if len(out_boxes) >= 6:
        rgb = np.asarray(out, dtype=np.float64)
        sat = rgb.max(axis=2) - rgb.min(axis=2)
        scored = [(float(np.median(sat[b[1]:b[3], b[0]:b[2]])), b) for b in out_boxes]
        cut = float(np.median([s for s, _ in scored]))
        out_boxes = [b for s, b in scored if s <= cut]
    return out_boxes


def build(run_dir: str, top: int) -> tuple[str, list[float]]:
    with open(os.path.join(run_dir, "manifest.json"), encoding="utf-8") as f:
        man = json.load(f)
    out_path = man["out"]
    seed = Image.open(man["seed"]).convert("RGB")
    raw = Image.open(out_path)
    out = raw.convert("RGB")
    if list(raw.size) != man["size"]:
        out = out.resize(tuple(man["size"]), Image.LANCZOS)

    m = metrics.measure(seed, out, man)
    boxes = faces(out, m["_added_mask"])
    if len(boxes) < 4:      # 顔が拾えない異変・暗すぎる回は、塊の上側で代用する
        boxes = [(max(0, (c["x0"] + c["x1"]) // 2 - max(8, int((c["y1"] - c["y0"]) * HEAD_FRAC) // 2)),
                  c["y0"],
                  min(out.width, (c["x0"] + c["x1"]) // 2 + max(8, int((c["y1"] - c["y0"]) * HEAD_FRAC) // 2)),
                  min(out.height, c["y0"] + max(12, int((c["y1"] - c["y0"]) * HEAD_FRAC))))
                 for c in m["components"] if (c["y1"] - c["y0"]) >= 14]
    boxes = boxes[:top]

    cells, syms = [], []
    for box in boxes:
        if box[2] - box[0] < 6 or box[3] - box[1] < 6:
            continue
        crop = out.crop(box)
        syms.append(symmetry(crop))
        w = max(8, int(crop.width * CELL / crop.height))
        cells.append(crop.resize((w, CELL), Image.LANCZOS))

    if not cells:
        raise SystemExit("頭に見える塊が無い")

    cols = min(8, len(cells))
    rows = (len(cells) + cols - 1) // cols
    cw = max(c.width for c in cells) + 8
    sheet = Image.new("RGB", (cols * cw + 8, rows * (CELL + 22) + 30), (17, 17, 19))
    d = ImageDraw.Draw(sheet)
    f, fs = _font(13), _font(11)
    d.text((8, 6), f"{man['anomaly']} @ {man['site']}  頭 {len(cells)} 個  "
                   f"左右対称度 中央値 {float(np.median(syms)):.2f}", fill=(232, 226, 214), font=f)
    for i, (c, s) in enumerate(zip(cells, syms)):
        x = 8 + (i % cols) * cw
        y = 26 + (i // cols) * (CELL + 22)
        sheet.paste(c, (x, y))
        d.text((x, y + CELL + 3), f"{i + 1}: {s:.2f}", fill=(160, 155, 146), font=fs)

    path = os.path.join(run_dir, "heads.png")
    sheet.save(path)
    return path, syms


def main() -> int:
    ap = argparse.ArgumentParser(description="頭だけ切り出して並べる")
    ap.add_argument("--run", required=True)
    ap.add_argument("--top", type=int, default=24)
    args = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")
    path, syms = build(args.run, args.top)
    print(f"{path}  頭 {len(syms)} 個  左右対称度 中央値 {float(np.median(syms)):.3f} "
          f"（{min(syms):.2f}〜{max(syms):.2f}）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
