# -*- coding: utf-8 -*-
"""暗がりの中の「光る目」が、装置の映像に乗っても残るかを測る（`canon/LEDGER.md` 0122）。

    py -3.11 tools/gen-plate/eyes.py --img <素材.png> [--live <プレート>] [--lap 2]

⚠⚠ **「暗い所の近くにある明点」を数えてはいけない。** 2026-08-23 にそれで測って
   471〜4721 px と出たが、**目は 1 対も残っていなかった**（拾っていたのは蛍光灯の縁）。
   暗がりの**内部**（縁から離れた所）だけを見る。

⚠ 判定は届いた画で行う（`--live` を渡すと `screen.py` を通した画でも測る）。
  素材の中では読める目が、伝送を通ると消える — それがこの計器を書いた理由。
"""
from __future__ import annotations

import argparse
import os
import subprocess
import sys
import tempfile

import numpy as np
from PIL import Image, ImageFilter

DARK = 60          # これ未満を「暗がり」とする（0-255）
BRIGHT = 100       # これ以上を「光っている点」とする
CLOSE = 8          # 暗がりの中の穴（＝目）を埋めて開口部を 1 つの塊にする半径
INSET = 4          # 開口部の縁から何画素内側だけを見るか（蛍光灯や壁との境を落とす）
MIN_PT = 2         # これ未満の点は数えない（画素）


def components(mask: np.ndarray, min_area: int) -> list[dict]:
    """4 近傍の連結成分（面積の大きい順）。"""
    h, w = mask.shape
    seen = np.zeros_like(mask)
    out = []
    ys, xs = np.nonzero(mask)
    for sy, sx in zip(ys, xs):
        if seen[sy, sx]:
            continue
        stack = [(sy, sx)]
        seen[sy, sx] = True
        px = []
        while stack:
            y, x = stack.pop()
            px.append((y, x))
            for ny, nx in ((y + 1, x), (y - 1, x), (y, x + 1), (y, x - 1)):
                if 0 <= ny < h and 0 <= nx < w and mask[ny, nx] and not seen[ny, nx]:
                    seen[ny, nx] = True
                    stack.append((ny, nx))
        if len(px) < min_area:
            continue
        a = np.array(px)
        out.append(dict(area=len(px),
                        cy=float(a[:, 0].mean()), cx=float(a[:, 1].mean()),
                        h=int(a[:, 0].max() - a[:, 0].min()) + 1,
                        w=int(a[:, 1].max() - a[:, 1].min()) + 1))
    out.sort(key=lambda c: -c["area"])
    return out


def pair_up(pts: list[dict]) -> list[tuple[dict, dict]]:
    """横に並んだ 2 点を「対」にする（目は 2 つで 1 つの意味を作る）。"""
    used = set()
    pairs = []
    for i, a in enumerate(pts):
        if i in used:
            continue
        best, bestd = -1, 1e9
        for j, b in enumerate(pts):
            if j <= i or j in used:
                continue
            dy, dx = abs(a["cy"] - b["cy"]), abs(a["cx"] - b["cx"])
            span = max(a["w"], b["w"])
            if dy > span * 1.2:            # 高さが揃っていない
                continue
            if not (span * 0.8 <= dx <= span * 6.0):   # 離れすぎ / 近すぎ
                continue
            if dx < bestd:
                best, bestd = j, dx
        if best >= 0:
            used.add(i)
            used.add(best)
            pairs.append((a, pts[best]))
    return pairs


def measure(path: str, label: str) -> dict:
    im = Image.open(path).convert("L")
    g = np.asarray(im, dtype=np.float64)

    dark = g < DARK
    # ⚠⚠ **収縮だけではいけない。** 「暗い画素」の集合を削った中には、定義上、明るい点は
    #   1 つも入らない（2026-08-23 に両方 0 を返して気づいた）。目は暗がりの**穴**なので、
    #   先に閉じて（膨張 → 収縮）開口部を 1 つの塊にしてから、その内部を見る。
    d8 = Image.fromarray((dark * 255).astype(np.uint8))
    closed = d8.filter(ImageFilter.MaxFilter(CLOSE * 2 + 1)).filter(ImageFilter.MinFilter(CLOSE * 2 + 1))
    inner = np.asarray(closed.filter(ImageFilter.MinFilter(INSET * 2 + 1)),
                       dtype=np.float64) > 128

    pts = components((g >= BRIGHT) & inner, MIN_PT)
    pairs = pair_up(pts)
    biggest = max((p["w"] for p in pts), default=0)

    print(f"== {label}  {im.size[0]}x{im.size[1]}")
    print(f"  暗がり {dark.mean() * 100:5.1f}%（うち内部 {inner.mean() * 100:4.1f}%）")
    print(f"  暗がりの内側の光る点 {len(pts):3d} 個 / **対になったもの {len(pairs):2d} 対**"
          f" / いちばん大きい点 {biggest} 画素幅")
    for a, b in pairs[:8]:
        print(f"    対: ({a['cx']:.0f},{a['cy']:.0f}) と ({b['cx']:.0f},{b['cy']:.0f})"
              f"  幅 {a['w']}/{b['w']}  間隔 {abs(a['cx'] - b['cx']):.0f}")
    return dict(points=len(pts), pairs=len(pairs), biggest=biggest)


def main() -> int:
    ap = argparse.ArgumentParser(description="暗がりの中の目を数える")
    ap.add_argument("--img", required=True, help="素材の画像")
    ap.add_argument("--live", help="渡すと screen.py を通した『届いた画』でも測る")
    ap.add_argument("--lap", type=float, default=2.0)
    ap.add_argument("--min-pairs", type=int, default=2, help="届いた画でこれ未満なら不合格")
    a = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")

    measure(a.img, f"素材  {os.path.basename(a.img)}")
    if not a.live:
        return 0

    here = os.path.dirname(os.path.abspath(__file__))
    with tempfile.TemporaryDirectory() as td:
        out = os.path.join(td, "screen.png")
        subprocess.run([sys.executable, os.path.join(here, "screen.py"),
                        "--overlay", a.img, "--live", a.live,
                        "--lap", str(a.lap), "--out", out],
                       check=True, stdout=subprocess.DEVNULL)
        print()
        r = measure(out, f"届いた画（周 {a.lap:g}）")

    ok = r["pairs"] >= a.min_pairs
    print()
    print(f"  → {'合格' if ok else '不合格'}（届いた画で {a.min_pairs} 対以上を要求）")
    print("  ⚠ 数が出ても絵は開く。対の判定は『横に並んだ 2 点』しか見ていない")
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
