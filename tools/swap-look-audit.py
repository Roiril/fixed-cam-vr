#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""入れ替わりのほどけの**見た目**を、参考画像と並べて測る。

    py -3.11 tools/swap-look-audit.py <参考画像のフォルダ>

`.\\tools\\unity.ps1 menu swap` で焼いた連番 PNG から数コマを取り、参考画像と
**人型の高さを揃えて胴を等倍で切り出し**、1 枚のシートにして `logs/swap-look/` へ出す。

⚠ **縮小した一覧では判定できない**（`rules/visual-verification.md` §7）。線の太さも密度も
消えるので、同じ縮尺の切り出しでしか比べられない。

⚠⚠ **目視だけでは足りない**（2026-08-19 実測）。「線になっている」としか読めない絵でも、
測ると線の長さが参考の 2.5 倍・行間のコントラストが半分だった。次の 3 つを必ず並べて見る:

| 指標 | 何を見ている | 参考画像の実測 |
|---|---|---|
| `rowdiff` | 上下に隣り合う行の輝度差の平均 ＝ 走査線のコントラスト | 27〜29 |
| `bimodal` | 明暗の 2 値性（大津の判別比）。1 に近いほど濃い / 淡いに割れている | 0.94〜0.97 |
| `runlen` | 行方向に連続した明部の平均長 ＝ 線がどれだけ長く伸びているか | 28〜34 px |

⚠ `bimodal` は**元の映像の性質**にも引かれる（参考画像は明るい壁と暗い人の 2 極）。
実装の素材が中間調ばかりなら 0.86 前後で頭打ちになる。**そこを追って 2 値化しない** —
「線の中身は本人の画素」という原則の方が上位（`canon/LEDGER.md` 0090）。

参考画像のフォルダには `ref1_*.jpg` `ref2_*.jpg` … を置く。人型の高さと胴の中心は
画像ごとに違うので下の `REFS` に手で書く（自動検出は当てにならない）。
"""
from __future__ import annotations

import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

sys.stdout.reconfigure(encoding="utf-8")

ROOT = Path(__file__).resolve().parent.parent
FRAMES = ROOT / "Assets/Screenshots/swap"
OUTDIR = ROOT / "logs/swap-look"

CROP = 380          # 切り出しの一辺（人型の高さを揃えた後の画素）
TARGET_FIG = 560    # 人型の高さをこれに揃える

# (ファイル名の頭, ラベル, その画像での人型の高さ px, 胴の中心 (x, y))
REFS = [
    ("ref1", "REF 1", 880, (362, 500)),
    ("ref2", "REF 2", 906, (327, 550)),
]

# 実装から取るコマ。**ほどけ切りと途中の 2 つ**を見る（片方だけだと段の差が出ない）。
IMPLS = [
    ("f0030", "IMPL 1.0s", 605, (615, 400)),
    ("f0036", "IMPL covered", 605, (615, 400)),
]


def stats(gray: np.ndarray) -> dict:
    g = gray.astype(np.float64)
    rowdiff = float(np.abs(np.diff(g, axis=0)).mean())

    hist, _ = np.histogram(g, bins=256, range=(0, 256))
    p = hist / max(hist.sum(), 1)
    idx = np.arange(256)
    total_mu = float((p * idx).sum())
    total_var = float((p * (idx - total_mu) ** 2).sum())
    best = w0 = mu0 = 0.0
    for t in range(256):
        w0 += p[t]
        mu0 += p[t] * t
        w1 = 1.0 - w0
        if w0 <= 1e-9 or w1 <= 1e-9:
            continue
        between = w0 * w1 * (mu0 / w0 - (total_mu - mu0) / w1) ** 2
        best = max(best, between)
    bimodal = best / total_var if total_var > 1e-9 else 0.0

    thr = float(np.median(g))
    runs: list[int] = []
    for row in (g > thr):
        n = 0
        for v in row:
            if v:
                n += 1
            elif n:
                runs.append(n)
                n = 0
        if n:
            runs.append(n)
    return {"rowdiff": rowdiff, "bimodal": bimodal,
            "runlen": float(np.mean(runs)) if runs else 0.0}


def tile_of(path: Path, fig_h: int, center: tuple[int, int]) -> Image.Image:
    im = Image.open(path).convert("RGB")
    k = TARGET_FIG / fig_h
    im = im.resize((round(im.width * k), round(im.height * k)), Image.LANCZOS)
    cx, cy = round(center[0] * k), round(center[1] * k)
    box = (max(0, cx - CROP // 2), max(0, cy - CROP // 2),
           min(im.width, cx + CROP // 2), min(im.height, cy + CROP // 2))
    return im.crop(box).resize((CROP, CROP), Image.LANCZOS)


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    refdir = Path(sys.argv[1])
    if not refdir.is_dir():
        print(f"⚠ 参考画像のフォルダが無い: {refdir}")
        return 1

    panels = []
    for stem, label, fig_h, center in REFS:
        hits = sorted(refdir.glob(f"{stem}*"))
        if not hits:
            print(f"⚠ {stem}* が {refdir} に無い")
            continue
        panels.append((hits[0], label, fig_h, center))
    for stem, label, fig_h, center in IMPLS:
        hits = sorted(FRAMES.glob(f"{stem}*.png"))
        if not hits:
            print(f"⚠ {stem}*.png が無い（先に `unity.ps1 menu swap` で焼く）")
            continue
        panels.append((hits[0], label, fig_h, center))
    if not panels:
        return 1

    tiles = []
    for path, label, fig_h, center in panels:
        tile = tile_of(path, fig_h, center)
        s = stats(np.asarray(tile.convert("L")))
        print(f"{label:16s} rowdiff {s['rowdiff']:6.2f}   bimodal {s['bimodal']:.3f}   "
              f"runlen {s['runlen']:6.1f}px")
        tiles.append((tile, label, s))

    pad, head = 12, 46
    sheet = Image.new("RGB", (pad + len(tiles) * (CROP + pad), head + CROP + pad + 26),
                      (16, 16, 18))
    d = ImageDraw.Draw(sheet)
    d.text((pad, 10), "same figure height, 1:1 crop of the torso", fill=(220, 220, 220))
    for i, (tile, label, s) in enumerate(tiles):
        x = pad + i * (CROP + pad)
        sheet.paste(tile, (x, head))
        d.text((x, head - 16), label, fill=(255, 210, 120))
        d.text((x, head + CROP + 6),
               f"rowdiff {s['rowdiff']:.1f}  bimodal {s['bimodal']:.2f}  run {s['runlen']:.0f}px",
               fill=(170, 170, 175))

    OUTDIR.mkdir(parents=True, exist_ok=True)
    out = OUTDIR / "compare_torso.png"
    sheet.save(out)
    print(f"\n{out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
