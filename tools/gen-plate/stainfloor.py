# -*- coding: utf-8 -*-
"""**シミはどれだけ濃ければ届くか**（合成した染みを実機の経路へ通して下限を探す）。

    py -3.11 tools/gen-plate/stainfloor.py --site B_20260816 --surface wall --lap 1

染みは「言われないと気づかない薄さでは、装置の映像に乗った時点で消える」（`anomaly/face-stain.md`）。
**その「消える濃さ」を数で出す。** 人形と違って染みは**面が広くて縁が無い**ので、
人形で測った下限（明暗差 35% / 50%）がそのまま使えるとは限らない。

生成を待たずに測れる — プレートの面へ**合成の染み**（中心が濃く外へ溶ける・縁なし）を置き、
濃さを掃引して Rose の基準（信号 ÷ 粒 ≥ 5）を切る所を探す。

⚠ ここで出るのは**下限であって狙いではない**。どこまで薄くするかは世界観の判断。
"""
from __future__ import annotations

import argparse
import json
import os
import sys

import numpy as np
from PIL import Image

import legible
import metrics
import screen
import spec


def stain(plate_path: str, box, depth: float, seed: int = 3) -> Image.Image:
    """面の箱の中へ、**縁の無い**染みを 1 つ置く（中心が最も濃く、外へ行くほど布の色へ溶ける）。

    ⚠ **掛け算で暗くする**（引き算ではない）。汚れは光の反射率が落ちることなので、
    明るい所ほど大きく下がる ＝ 下地の凹凸が透ける（`judge.py` の「下地が透ける」と同じ理屈）。
    """
    im = Image.open(plate_path).convert("RGB")
    a = np.asarray(im, dtype=np.float64)
    h, w = a.shape[:2]
    x0, y0, x1, y1 = [int(v) for v in box]
    cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
    rx, ry = (x1 - x0) * 0.30, (y1 - y0) * 0.22

    y, x = np.ogrid[:h, :w]
    d = np.sqrt(((x - cx) / max(rx, 1)) ** 2 + ((y - cy) / max(ry, 1)) ** 2)
    blob = np.clip(1.0 - d, 0.0, 1.0) ** 1.6          # 縁を作らない（外へ滑らかに 0 へ）

    # 顔らしさ（濃い所 2 つ ＋ 横長の薄い所 1 つ）。輪郭線は引かない
    for ox, oy, sx, sy, k in ((-0.34, -0.20, 0.20, 0.16, 0.55),
                              (+0.34, -0.20, 0.20, 0.16, 0.55),
                              (0.0, +0.34, 0.46, 0.11, 0.35)):
        dd = np.sqrt(((x - (cx + ox * rx * 2)) / max(sx * rx * 2, 1)) ** 2
                     + ((y - (cy + oy * ry * 2)) / max(sy * ry * 2, 1)) ** 2)
        blob = blob + np.clip(1.0 - dd, 0.0, 1.0) ** 1.6 * k
    blob = np.clip(blob, 0.0, 1.0)

    rng = np.random.default_rng(seed)                  # むらを乗せる（一様な楕円にしない）
    n = rng.normal(0.0, 1.0, (h // 8 + 2, w // 8 + 2))
    n = np.asarray(Image.fromarray(((n - n.min()) / max(np.ptp(n), 1e-6) * 255).astype(np.uint8))
                   .resize((w, h), Image.BICUBIC), dtype=np.float64) / 255.0
    blob = blob * (0.65 + 0.7 * n)

    out = a * (1.0 - depth * blob[..., None])
    return Image.fromarray(np.clip(out, 0, 255).astype(np.uint8))


def main() -> int:
    ap = argparse.ArgumentParser(description="シミはどれだけ濃ければ届くか")
    ap.add_argument("--site", required=True)
    ap.add_argument("--surface", default="wall")
    ap.add_argument("--lap", type=float, default=1.0)
    ap.add_argument("--depths", default="0.60,0.45,0.35,0.25,0.18,0.12,0.08,0.05")
    args = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")

    with open(os.path.join(spec.REPO, "tools", "gen-plate", "sites",
                           f"{args.site}.json"), encoding="utf-8") as f:
        site = json.load(f)
    surf = (site.get("surfaces") or {}).get(args.surface)
    if not surf:
        print(f"この場所に「{args.surface}」の面が無い（あるのは "
              f"{', '.join((site.get('surfaces') or {}).keys())}）")
        return 2
    plate = os.path.join(spec.REPO, site["plate"])
    web = os.path.join(spec.REPO, "tools", "web-compositor")
    mask = os.path.join(web, "masks", "split_left_half.png")
    show = json.load(open(os.path.join(web, "show.json"), encoding="utf-8"))

    tmp = os.path.join(spec.REPO, "logs", "gen-plate", "_stain")
    os.makedirs(tmp, exist_ok=True)

    print(f"== {args.site} の「{surf['say']}」へ染みを置く  周 {args.lap:.0f}")
    print(f"{'濃さ':>6}{'面の明暗差':>12}{'読みやすさ':>12}{'塊':>5}   判定")
    print("-" * 50)
    floor = None
    for depth in [float(v) for v in args.depths.split(",")]:
        p = os.path.join(tmp, f"d{int(depth * 100):03d}.png")
        stain(plate, surf["box"], depth).save(p)

        got, plain, inside, sigma = legible._delivered(p, plate, mask, args.lap, show)
        h, w = got.shape
        mk = np.asarray(Image.open(mask).convert("L").resize((w, h), Image.BILINEAR),
                        dtype=np.float64) / 255.0
        added = metrics.clean(np.abs(got - plain) > max(3.0 * sigma, 4.0)) & (mk > 0.5) & inside
        parts = metrics.components(added, min_area=120)[:8] if added.any() else []
        reads = []
        for c in parts:
            sub = np.zeros_like(added)
            sub[c["y0"]:c["y1"], c["x0"]:c["x1"]] = added[c["y0"]:c["y1"], c["x0"]:c["x1"]]
            if sub.sum() >= 60:
                reads.append(legible.readability(got, plain, sub, sigma))
        read = float(np.max(reads)) if reads else 0.0   # 染みは 1 つなので最大の塊で見る

        # 素材の中で、その面がどれだけ暗くなったか（プロンプトへ書ける形＝明暗差）
        src = np.asarray(Image.open(p).convert("L"), dtype=np.float64)
        ref = np.asarray(Image.open(plate).convert("L"), dtype=np.float64)
        x0, y0, x1, y1 = [int(v) for v in surf["box"]]
        d = (ref - src)[y0:y1, x0:x1]
        contrast = float(np.percentile(d, 99.5) / max(np.median(ref[y0:y1, x0:x1]), 1e-6) * 100)

        ok = read >= 5.0
        if floor is None and not ok:
            floor = depth
        print(f"{depth * 100:5.0f}%{contrast:12.0f}%{read:12.1f}{len(parts):5d}   "
              f"{'見つけられる' if ok else '**粒に埋もれる**'}")

    print("")
    if floor is None:
        print("⭐ いちばん薄くしても見つけられる ＝ まだ薄くする余地がある")
    else:
        print(f"⭐ 掛ける濃さを **{floor * 100:.0f}%** まで落とすと粒に埋もれる ＝ そこが下限")
    print("⚠ これは下限であって狙いではない。どこまで薄くするかは世界観の判断")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
