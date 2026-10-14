#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""題字「廻リ視」の書き出しの図を焼く（2026-10-14・Claude の案。判定はまだ無い — canon/OPEN.md）。

    py -3.11 tools/make-title-writeon.py            # 焼く
    py -3.11 tools/make-title-writeon.py --check    # 部品表と版の照合だけ（書かない）

入力は版 `Assets/Resources/Title/MawarimiTitle.png`（R=主の白墨 / G=朱 / B=焼ける順 / A=添え）。
出力は版と**同じチャンネルの意味**で 2 枚（シェーダは版と同じ `LayerInk` で層を選ぶ）:

  MawarimiTitleWrite.png  R/G/A = その層の墨に筆が届く時刻（0..1。出現の進み `_Reveal` の単位）。B は未使用
  MawarimiTitleBleed.png  R/G/A = その層の墨をぼかしたもの（濡れた墨の滲み）。B は未使用

時刻の決め方:
  - 版を連結成分に分け、下の `PLAN` で筆順どおりの区間 [始, 終] を割り当てる（廻は「回」を先に、「廴」を最後に）。
  - 区間の中は**墨の中を通る距離**（起筆からの測地距離）で進める。筆が画の中を流れていくように見える。
    入筆を少し溜める（u^0.8）。
  - どの成分にも入らない薄い墨と、墨の外の滲みの帯は、いちばん近い成分の時刻を受け継ぐ（層ごと）。

⚠ 版を焼き直したら（`tools/make-title-art.py`）、これも焼き直す。部品表が合わなければ止まる。
⚠ RGBA のまま縮めると PIL は alpha で前乗算する（A=0 の所の RGB が消える）。チャンネルごとに縮める。
"""
from __future__ import annotations

import argparse
import heapq
import io
import math
import os
import sys
from collections import deque

import numpy as np
from PIL import Image, ImageFilter

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ART = os.path.join(ROOT, "Assets", "Resources", "Title", "MawarimiTitle.png")
OUT_WRITE = os.path.join(ROOT, "Assets", "Resources", "Title", "MawarimiTitleWrite.png")
OUT_BLEED = os.path.join(ROOT, "Assets", "Resources", "Title", "MawarimiTitleBleed.png")

# 取り込みは 1024 に落ちる（TitleArtImporter.maxTextureSize）。同じ解像度で解く。
W, H = 1024, 512
LAYER_CH = {"R": 0, "G": 1, "A": 3}
COMPONENT_MIN = 0.25   # 成分を分ける閾値
INK_MIN = 0.04         # 時刻を持たせる墨の下限
HALO_PX = 18           # 滲みの帯（墨の外へ時刻を広げる距離）
BLEED_SIGMA = 5.0      # 滲みのぼかし（px・1024 基準）
SEED_EASE = 0.8        # 区間の中の進み u → u^この値（入筆を溜める）

# (名前, 層, 成分の中の 1 点, 始, 終, 起筆)。起筆: t=上 / tl=左上 / tr=右上 / l=左
# 点は「その点を含むいちばん小さい外接矩形の成分」を選ぶ（成分の外接矩形は入れ子になる）。
PLAN = [
    # 廻 — 回（左の縦 → 横折 → 内の口 → 底の横）→ 廴（上の点 → 本体から長い払い）
    ("回 左の縦",   "R", (266, 220), 0.00, 0.09, "t"),
    ("回 横折",     "R", (380, 120), 0.05, 0.16, "tl"),
    ("回 内の口",   "R", (320, 218), 0.12, 0.22, "tl"),
    ("回 底の横a",  "R", (266, 308), 0.20, 0.22, "l"),
    ("回 底の横b",  "R", (306, 309), 0.20, 0.24, "l"),
    ("廴 上",       "R", (141, 124), 0.23, 0.26, "l"),
    ("廴 本体",     "R", (150, 300), 0.25, 0.52, "tl"),
    # リ
    ("リ 左",       "R", (514, 170), 0.44, 0.53, "t"),
    ("リ 右",       "R", (590, 150), 0.49, 0.66, "t"),
    # 視（朱）— 礻 → 見の目と乚 → 見のノ
    ("視 礻",       "G", (690, 200), 0.60, 0.75, "t"),
    ("視 目と乚",   "G", (850, 200), 0.70, 0.88, "tl"),
    ("視 ノ",       "G", (760, 340), 0.82, 0.92, "tr"),
    # 添え — 二本の払いは左から右へ一息で、ルビは上から
    ("払い",        "A", (300, 400), 0.80, 0.97, "l"),
    ("小点",        "A", (557, 389), 0.95, 0.98, "l"),
    ("罫 上",       "A", (446, 163), 0.84, 0.87, "l"),
    ("ルビ ま",     "A", (446, 192), 0.86, 0.90, "t"),
    ("ルビ わ",     "A", (446, 226), 0.88, 0.92, "t"),
    ("ルビ り",     "A", (446, 259), 0.90, 0.94, "t"),
    ("ルビ み",     "A", (446, 292), 0.92, 0.96, "t"),
    ("罫 下",       "A", (446, 321), 0.95, 0.98, "l"),
]

N8 = [(-1, -1, math.sqrt(2)), (-1, 0, 1.0), (-1, 1, math.sqrt(2)), (0, -1, 1.0),
      (0, 1, 1.0), (1, -1, math.sqrt(2)), (1, 0, 1.0), (1, 1, math.sqrt(2))]


def load_layers() -> dict[str, np.ndarray]:
    if not os.path.exists(ART):
        sys.exit(f"版が無い: {ART}")
    im = Image.open(ART)
    if im.mode != "RGBA":
        sys.exit(f"版が RGBA ではない: {im.mode}")
    chans = [np.asarray(c.resize((W, H), Image.LANCZOS)).astype(np.float32) / 255.0 for c in im.split()]
    return {k: chans[i] for k, i in LAYER_CH.items()}


def components(mask: np.ndarray, min_area: int = 25) -> list[tuple[np.ndarray, tuple[int, int, int, int]]]:
    seen = np.zeros_like(mask, bool)
    out = []
    ys, xs = np.nonzero(mask)
    for y0, x0 in zip(ys, xs):
        if seen[y0, x0]:
            continue
        q = deque([(y0, x0)])
        seen[y0, x0] = True
        pts = []
        while q:
            y, x = q.popleft()
            pts.append((y, x))
            for dy, dx, _ in N8:
                ny, nx = y + dy, x + dx
                if 0 <= ny < H and 0 <= nx < W and mask[ny, nx] and not seen[ny, nx]:
                    seen[ny, nx] = True
                    q.append((ny, nx))
        if len(pts) < min_area:
            continue
        p = np.array(pts)
        out.append((p, (p[:, 1].min(), p[:, 0].min(), p[:, 1].max(), p[:, 0].max())))
    return out


def seed_of(pts: np.ndarray, rule: str) -> tuple[int, int]:
    y, x = pts[:, 0], pts[:, 1]
    key = {"t": y * 4 + x * 0.01, "tl": x + y, "tr": -x + y, "l": x * 4 + y * 0.01}[rule]
    i = int(np.argmin(key))
    return int(y[i]), int(x[i])


def geodesic(pts: np.ndarray, seed: tuple[int, int]) -> dict[tuple[int, int], float]:
    inside = {(int(a), int(b)) for a, b in pts}
    dist = {seed: 0.0}
    heap = [(0.0, seed)]
    while heap:
        d, (y, x) = heapq.heappop(heap)
        if d > dist.get((y, x), 1e18):
            continue
        for dy, dx, w in N8:
            n = (y + dy, x + dx)
            if n in inside:
                nd = d + w
                if nd < dist.get(n, 1e18):
                    dist[n] = nd
                    heapq.heappush(heap, (nd, n))
    return dist


def spread(t: np.ndarray, known: np.ndarray, allowed: np.ndarray, max_px: float) -> None:
    """known の時刻を、allowed の中へ最寄りの値で広げる（多始点の距離順）。"""
    dist = np.full(t.shape, np.inf, np.float32)
    heap = []
    for y, x in zip(*np.nonzero(known)):
        dist[y, x] = 0.0
        heap.append((0.0, int(y), int(x)))
    heapq.heapify(heap)
    while heap:
        d, y, x = heapq.heappop(heap)
        if d > dist[y, x]:
            continue
        for dy, dx, w in N8:
            ny, nx = y + dy, x + dx
            if 0 <= ny < H and 0 <= nx < W and allowed[ny, nx]:
                nd = d + w
                if nd <= max_px and nd < dist[ny, nx]:
                    dist[ny, nx] = nd
                    t[ny, nx] = t[y, x]
                    heapq.heappush(heap, (nd, ny, nx))


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true")
    args = ap.parse_args()

    layers = load_layers()
    comps = {k: components(v > COMPONENT_MIN) for k, v in layers.items()}
    times = {k: np.ones((H, W), np.float32) for k in layers}
    known = {k: np.zeros((H, W), bool) for k in layers}
    used: set[tuple[str, int]] = set()
    sweep_extent = (0, 0)

    for name, layer, (ax, ay), t0, t1, rule in PLAN:
        cands = [(i, c) for i, c in enumerate(comps[layer])
                 if c[1][0] <= ax <= c[1][2] and c[1][1] <= ay <= c[1][3]]
        if not cands:
            sys.exit(f"部品表が版と合わない: 「{name}」の点 ({ax},{ay}) を含む成分が {layer} に無い")
        i, (pts, bb) = min(cands, key=lambda ic: (ic[1][1][2] - ic[1][1][0]) * (ic[1][1][3] - ic[1][1][1]))
        if (layer, i) in used:
            sys.exit(f"部品表が版と合わない: 「{name}」が別の行と同じ成分を指している")
        used.add((layer, i))
        seed = seed_of(pts, rule)
        dist = geodesic(pts, seed)
        dmax = max(dist.values()) or 1.0
        unreached = len(pts) - len(dist)
        if name == "払い":
            sweep_extent = (bb[0], bb[2])
        for (y, x), dv in dist.items():
            times[layer][y, x] = t0 + (t1 - t0) * (dv / dmax) ** SEED_EASE
            known[layer][y, x] = True
        print(f"{name:10s} {layer} 面積 {len(pts):6d} 起筆 ({seed[1]},{seed[0]}) 道のり {dmax:6.0f}px "
              f"[{t0:.2f}, {t1:.2f}]" + (f"  ⚠ 届かない {unreached}" if unreached else ""))

    left = [(k, i, len(c[0])) for k in comps for i, c in enumerate(comps[k]) if (k, i) not in used and len(c[0]) >= 300]
    if left:
        sys.exit("部品表に無い大きな成分がある（版が変わった？）: " + ", ".join(f"{k}#{i} 面積{a}" for k, i, a in left))

    # 払いの先へ薄く続く二本目の筆（閾値の下で成分にならない）は、払いの速さのまま右へ延ばす。
    sweep = next(p for p in PLAN if p[0] == "払い")
    sx0, sx1 = sweep_extent
    slope = (sweep[4] - sweep[3]) / max(sx1 - sx0, 1)
    A = layers["A"]
    tail = (A > INK_MIN) & ~known["A"] & (np.arange(W)[None, :] > sx1) & (np.arange(H)[:, None] > 330)
    ys, xs = np.nonzero(tail)
    times["A"][ys, xs] = np.minimum(1.0, sweep[4] + (xs - sx1) * slope)
    known["A"][ys, xs] = True
    print(f"払いの先の薄い筆 {len(xs)} px を x で延長（傾き {slope:.5f}/px）")

    for k, L in layers.items():
        # 墨のある所と滲みの帯へ、最寄りの筆の時刻を広げる（層ごと。別の層の時刻を借りない）。
        halo = np.asarray(Image.fromarray((L > INK_MIN).astype(np.uint8) * 255)
                          .filter(ImageFilter.MaxFilter(2 * (HALO_PX // 2) + 1))) > 0
        spread(times[k], known[k], halo | (L > INK_MIN), HALO_PX * 1.5)
        # 帯より離れた薄い墨も、最寄りの筆から受け継ぐ（取り残すと最後の 1 刻みでまとめて出る）。
        rest = (L > INK_MIN) & (times[k] >= 1.0) & ~known[k]
        if rest.any():
            spread(times[k], (times[k] < 1.0), (L > INK_MIN) | halo, 1e9)
        miss = int(((L > INK_MIN) & (times[k] >= 1.0) & ~known[k]).sum())
        print(f"層 {k}: 時刻の無い墨 {miss} px")

    if args.check:
        print("check のみ（書かない）")
        return 0

    def save(path: str, chans: dict[str, np.ndarray]) -> None:
        planes = []
        for c in ("R", "G", "B", "A"):
            v = chans.get(c)
            planes.append(Image.fromarray(np.clip((v if v is not None else np.ones((H, W))) * 255 + 0.5, 0, 255)
                                          .astype(np.uint8)))
        tmp = path + ".tmp.png"
        Image.merge("RGBA", planes).save(tmp)
        os.replace(tmp, path)
        print("wrote", os.path.relpath(path, ROOT))

    save(OUT_WRITE, {k: times[k] for k in layers})
    bleed = {k: np.asarray(Image.fromarray((L * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(BLEED_SIGMA)))
             .astype(np.float32) / 255.0 for k, L in layers.items()}
    save(OUT_BLEED, bleed)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
