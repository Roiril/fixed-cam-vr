# -*- coding: utf-8 -*-
"""**届いた画で人形として読めるか**を測る（無彩・低解像度・粒だらけの本番の画で）。

    py -3.11 tools/gen-plate/legible.py --material <素材.png> --plate <プレート.jpg> --lap 4

実機の画は 帰りの A で**完全な無彩・267 ブロック・粒 2.6 倍**になる（`screen.py`）。
そこまで来ると効くのは色でも彩度でもなく、**周りとの明暗差が粒に埋もれないこと**だけ。

**基準は同じ画に写っている実物**（棚・パイプ・椅子）。部屋の明るさもカメラも post も
共通なので、「実物と同じだけ読めるか」は場所に依らない問いになる。

  読みやすさ = |足したものの明るさ − すぐ周りの明るさ| ÷ その場の粒の大きさ

視覚の閾値（Rose の基準）では **5 を切ると見つけられない**。実物の帯と並べて出す。
"""
from __future__ import annotations

import argparse
import json
import os
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFont

import metrics
import screen
import spec


def _delivered(material, plate, mask, lap, show) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    """素材ありの画・素材なし（プレートだけ）の画・**映像が写っている所**。**同じ乱数**で描く。

    ⚠ 3 つ目は contain の黒帯を除く矩形。測る道具はこれで切ってから測る（`screen.frame_inside`）。
    """
    p = dict(exposure=-1.05, contrast=1.12, saturation=0.52, temperature=0.48,
             tint=0.0, lift=0.02, vignette=0.38)
    for k, v in (show.get("post") or {}).items():
        if k in p and isinstance(v, (int, float)):
            p[k] = float(v)
    total = max(2.0, float((show.get("run") or {}).get("totalLaps", 3)))
    prog = min(max((lap - 1.0) / (total - 1.0), 0.0), 1.0)
    blocks = screen.FINE_BLOCKS + (screen.END_BLOCKS - screen.FINE_BLOCKS) * prog

    out = []
    for ov in (material, None):
        screen.RNG = np.random.default_rng(20260817)      # 粒を揃える（差が粒で埋もれない）
        col = screen.render(plate, ov, mask if ov else None, p, blocks, prog)
        out.append(np.asarray(Image.fromarray(
            screen.linear_to_srgb(col).astype(np.uint8)).convert("L"), dtype=np.float64))
    with Image.open(plate) as im:
        inside = screen.frame_inside(im.size)
    return out[0], out[1], inside


def _noise_sigma(g: np.ndarray, inside: np.ndarray | None = None) -> float:
    """その場の粒の大きさ。平らな所の高周波のばらつきで測る。

    ⚠⚠ **黒帯を除いてから測る。** 除かないと、潰れて分散 0 の帯が「いちばん平らな所」として
    選ばれ、粒が 1/3 に化ける（2026-08-18 実測 1.39 → 0.47 → 読みやすさが 2 倍に嵩上げ）。
    """
    hp = metrics.highpass(g)
    if inside is not None:
        hp = hp[inside]
    flat = hp < np.percentile(hp, 60)
    return float(np.std(hp[flat]) * 1.4826 + 1e-6)


def _ring(mask: np.ndarray, box, pad: int) -> np.ndarray:
    y0, y1 = max(0, box[1] - pad), min(mask.shape[0], box[3] + pad)
    x0, x1 = max(0, box[0] - pad), min(mask.shape[1], box[2] + pad)
    r = np.zeros_like(mask)
    r[y0:y1, x0:x1] = True
    return r & ~metrics._shift_or(metrics._shift_or(mask))


def readability(got: np.ndarray, plain: np.ndarray, sub: np.ndarray, sigma: float) -> float:
    """読みやすさ ＝ **そこに足された信号の大きさ** ÷ 粒。

    ⚠ 平均の差だけでは測れない。人形は「白い顔 ＋ 黒い髪」なので**平均が背景と同じ**に
    なりうるが、画では明らかに見える。
    ⚠⚠ かといって「その領域の ばらつき」で測ってもいけない（2026-08-17 に踏んだ）。
    塊は融合して大きな箱になるので、**中に入っている背景の構造**（明るい壁と暗い幕の境目）を
    測ってしまう。実際、素材を 10% まで薄めても読みやすさが 32 → 24 にしか落ちず、
    **消えかけている素材を「読める」と言い続けた**。
    ⇒ 素材が無いときの画（`plain`）が分かっているのだから、**その差**を直接測る。
    """
    d = got[sub] - plain[sub]
    return float(np.sqrt(np.mean(d * d))) / sigma


def structure_of(g: np.ndarray, sub: np.ndarray, ring: np.ndarray, sigma: float) -> float:
    """実物側の比較用。**そこに無ければ見えるはずの背景**をリングの平均で代用して同じ式で測る。

    ⚠ 代用が粗いぶん実物の側が大きめに出る（勾配があるだけで値が付く）ので、
    **人形に厳しい側**へ倒れている。比較としてはそれでよい。
    """
    d = g[sub] - float(g[ring].mean())
    return float(np.sqrt(np.mean(d * d))) / sigma


def reference_band(g: np.ndarray, region: np.ndarray, sigma: float,
                   sizes: list[tuple[int, int]], n: int = 40) -> list[float]:
    """**同じ画に写っている実物**の読みやすさ（比較の相手）。

    ⚠ 「いちばん強い縁」を相手にしない（実物の中でも例外的に目立つ所を基準にすると、
    どんな素材も落ちる）。**足したものと同じ大きさの箱**を実写の側へばら撒いて、
    同じ式で測る ＝「この部屋にある普通の物は、この大きさでどれくらい目立つか」。
    """
    h, w = g.shape
    rng = np.random.default_rng(7)
    out = []
    tries = 0
    while len(out) < n and tries < n * 40:
        tries += 1
        bw, bh = sizes[rng.integers(len(sizes))]
        if bw >= w // 2 or bh >= h:
            continue
        x0 = int(rng.integers(0, w - bw)); y0 = int(rng.integers(0, h - bh))
        sub = np.zeros_like(region); sub[y0:y0 + bh, x0:x0 + bw] = True
        if not region[y0:y0 + bh, x0:x0 + bw].all():
            continue
        ring = _ring(sub, (x0, y0, x0 + bw, y0 + bh), pad=max(6, bh // 4)) & region
        if ring.sum() < 60:
            continue
        out.append(structure_of(g, sub, ring, sigma))
    out.sort()
    return out


def main() -> int:
    ap = argparse.ArgumentParser(description="届いた画で読めるか")
    ap.add_argument("--material", required=True, help="undim 済みの素材（captures に置く形）")
    ap.add_argument("--plate", required=True, help="ライブ側に出るプレート")
    ap.add_argument("--mask", default=None, help="枠空間のマスク（既定 = 左半分）")
    ap.add_argument("--lap", type=float, default=4.0)
    ap.add_argument("--out", default="logs/gen-plate/legible.png")
    args = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")

    web = os.path.join(spec.REPO, "tools", "web-compositor")
    mask = args.mask or os.path.join(web, "masks", "split_left_half.png")
    with open(os.path.join(web, "show.json"), encoding="utf-8") as f:
        show = json.load(f)

    got, plain, inside = _delivered(args.material, args.plate, mask, args.lap, show)
    h, w = got.shape
    sigma = _noise_sigma(plain, inside)

    m = np.asarray(Image.open(mask).convert("L").resize((w, h), Image.BILINEAR),
                   dtype=np.float64) / 255.0
    added = metrics.clean(np.abs(got - plain) > max(3.0 * sigma, 4.0)) & (m > 0.5) & inside
    parts = [c for c in metrics.components(added, min_area=120)][:24]

    rows = []
    for c in parts:
        box = (c["x0"], c["y0"], c["x1"], c["y1"])
        sub = np.zeros_like(added)
        sub[c["y0"]:c["y1"], c["x0"]:c["x1"]] = added[c["y0"]:c["y1"], c["x0"]:c["x1"]]
        ring = _ring(sub, box, pad=max(6, (c["y1"] - c["y0"]) // 4)) & (m > 0.5) & inside
        if ring.sum() < 60:
            continue
        rows.append(dict(h=c["y1"] - c["y0"], w=c["x1"] - c["x0"], area=c["area"],
                         cnr=readability(got, plain, sub, sigma), box=box))

    sizes = [(r["w"], r["h"]) for r in rows] or [(40, 60)]
    ref = reference_band(plain, (m <= 0.5) & inside, sigma, sizes)
    print(f"== 届いた画（周 {args.lap:.0f}）  枠 {w}x{h}  粒の大きさ {sigma:.2f}")
    if not rows:
        print("  足したものが 1 つも読めない（差が粒に埋もれている）")
    for i, r in enumerate(rows[:12], 1):
        mark = "OK " if r["cnr"] >= 5.0 else "NG "
        print(f"  {mark}{i:2d}: 背丈 {r['h']:3d}px  読みやすさ {r['cnr']:5.1f}")
    if rows:
        cn = sorted(x["cnr"] for x in rows)
        hs = sorted(x["h"] for x in rows)
        print(f"  中央値: 読みやすさ {cn[len(cn) // 2]:.1f} / 背丈 {hs[len(hs) // 2]}px  "
              f"（読めない塊 {sum(1 for x in rows if x['cnr'] < 5.0)}/{len(rows)}）")
    if ref:
        q = lambda x: ref[min(len(ref) - 1, int(len(ref) * x))]
        print(f"  ⭐ 同じ画の実物（同じ大きさの箱 {len(ref)} 個）: "
              f"下位 25% {q(0.25):.1f} / 中央値 {q(0.5):.1f} / 上位 25% {q(0.75):.1f}")
        print(f"     → 実物の下位 25%（{q(0.25):.1f}）を下回る塊は「この部屋の普通の物より目立たない」")

    # 絵にする（届いた画 ＋ 読めない塊を赤で囲む）
    im = Image.fromarray(got.astype(np.uint8)).convert("RGB")
    d = ImageDraw.Draw(im)
    try:
        f = ImageFont.truetype("C:/Windows/Fonts/meiryo.ttc", 14)
    except OSError:
        f = ImageFont.load_default()
    for r in rows:
        ok = r["cnr"] >= 5.0
        d.rectangle(r["box"], outline=(120, 220, 160) if ok else (240, 110, 100), width=2)
        d.text((r["box"][0] + 2, r["box"][1] + 2), f"{r['cnr']:.0f}",
               fill=(120, 220, 160) if ok else (240, 110, 100), font=f)
    os.makedirs(os.path.dirname(args.out) or ".", exist_ok=True)
    im.save(args.out)
    print(f"  絵にした: {args.out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
