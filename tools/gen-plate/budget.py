# -*- coding: utf-8 -*-
"""**その周で出す素材は、どこまで届くのか**を周ごとに測る（作り込む所を決めるため）。

    py -3.11 tools/gen-plate/budget.py --material <素材.png> --plate <プレート.jpg>

周を重ねるごとに伝送が痩せる（`ScreenDecayLogic`: 800 → 267 ブロック）ので、
**同じ素材でも届く情報量が周によって桁で違う**。1 周目で出すなら縁の質まで届くが、
3 周目以降は mip が全部なまして無彩にもなる ＝ **そこで作り込んでも画に出ない**。

出すのは 3 つ:

  読みやすさ  足したものが粒に埋もれずに見えるか（同じ画の実物と比べる）
  縁の鋭さ    素材の縁の鋭さが、届いた画でも実写の縁と違って見えるか
  細かさ      素材の高周波が、届いた画でも実写より多いか（＝「人形だけ解像度が高い」）

⚠ どれも**届いた画の中**で実写と比べる。素材の中で測った値ではない。
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
import spec


def measure_at(material, plate, mask, lap, show) -> dict:
    got, plain, inside = legible._delivered(material, plate, mask, lap, show)
    h, w = got.shape
    sigma = legible._noise_sigma(plain, inside)
    m = np.asarray(Image.open(mask).convert("L").resize((w, h), Image.BILINEAR),
                   dtype=np.float64) / 255.0
    added = metrics.clean(np.abs(got - plain) > max(3.0 * sigma, 4.0)) & (m > 0.5) & inside
    live = (m <= 0.5) & inside

    r = dict(lap=lap, sigma=sigma, added_pct=float(added.sum() / max(1, (m > 0.5).sum()) * 100))
    if not added.any():
        return r | dict(read=float("nan"), edge=float("nan"), fine=float("nan"),
                        ref_read=float("nan"))

    # 読みやすさ（塊ごと → 中央値）と、同じ大きさの箱で測った実物の帯
    parts = metrics.components(added, min_area=120)[:24]
    reads, sizes = [], []
    for c in parts:
        sub = np.zeros_like(added)
        sub[c["y0"]:c["y1"], c["x0"]:c["x1"]] = added[c["y0"]:c["y1"], c["x0"]:c["x1"]]
        ring = legible._ring(sub, (c["x0"], c["y0"], c["x1"], c["y1"]),
                             pad=max(6, (c["y1"] - c["y0"]) // 4)) & (m > 0.5) & inside
        if ring.sum() < 60:
            continue
        reads.append(legible.readability(got, plain, sub, sigma))
        sizes.append((c["x1"] - c["x0"], c["y1"] - c["y0"]))
    ref = legible.reference_band(plain, live, sigma, sizes or [(40, 60)])

    # 縁の鋭さ: 足した所の輪郭 ÷ 実写側の強い縁（どちらも**届いた画**で）
    border = added & ~metrics._shift_and(added)
    sh = metrics.sharpness(got)
    gm = metrics.grad_mag(got)
    live_edges = live & (gm >= np.percentile(gm[live], 90))
    edge = float(np.median(sh[border]) / max(1e-6, np.median(sh[live_edges])))

    # 細かさ: 足した所の高周波 ÷ 実写側の同じ明るさの所
    # ⚠ **中央値で割らない。** 実写側が真っ暗な幕だと中央値が 0 になり、比が 2 億に化ける
    #   （2026-08-17 に実際に出た）。上位 25% どうしで比べる。
    hp = metrics.highpass(got)
    lum = float(np.median(got[added]))
    ref_px = live & (np.abs(got - lum) < 18)
    if ref_px.sum() < 400:
        ref_px = live
    denom = float(np.percentile(hp[ref_px], 75))
    fine = float(np.percentile(hp[added], 75) / denom) if denom > 0.05 else float("nan")

    # ⚠ **接地の影は届いた画では測れない。** 塊が融合して「直下の帯」が取れず、
    #   実際に影のある素材（生成直後 2.45）で 0.02、影の無い素材（-0.12）で 0.20 と
    #   **順序が逆に出た**（2026-08-17 実測）。嘘をつく計器を載せない。接地は生成直後に測る。

    return r | dict(read=float(np.median(reads)) if reads else float("nan"),
                    ref_read=float(np.median(ref)) if ref else float("nan"),
                    edge=edge, fine=fine)


def main() -> int:
    ap = argparse.ArgumentParser(description="周ごとに、どこまで届くか")
    ap.add_argument("--material", required=True)
    ap.add_argument("--plate", required=True)
    ap.add_argument("--mask", default=None)
    ap.add_argument("--laps", default="1,2,3,4")
    args = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")

    web = os.path.join(spec.REPO, "tools", "web-compositor")
    mask = args.mask or os.path.join(web, "masks", "split_left_half.png")
    with open(os.path.join(web, "show.json"), encoding="utf-8") as f:
        show = json.load(f)

    total = max(2.0, float((show.get("run") or {}).get("totalLaps", 3)))
    print(f"{'周':<4}{'ブロック':>8}{'無彩':>6}{'読みやすさ':>12}{'実物':>8}"
          f"{'縁の鋭さ':>10}{'細かさ':>8}")
    print("-" * 58)
    for lap in [float(x) for x in args.laps.split(",")]:
        r = measure_at(args.material, args.plate, mask, lap, show)
        prog = min(max((lap - 1.0) / (total - 1.0), 0.0), 1.0)
        blocks = legible.screen.FINE_BLOCKS + \
            (legible.screen.END_BLOCKS - legible.screen.FINE_BLOCKS) * prog
        f = lambda v: "—" if v != v else f"{v:.2f}"
        print(f"{int(lap):<4}{blocks:8.0f}{prog:6.2f}{f(r['read']):>12}{f(r['ref_read']):>8}"
              f"{f(r['edge']):>10}{f(r['fine']):>8}")
    print("\n読みやすさ ＝ 足したものが粒に埋もれずに見えるか（実物の列と比べる）")
    print("縁の鋭さ ＝ 1 を大きく超えると「切り抜きを貼った」に見える")
    print("細かさ ＝ 1 を大きく超えると「人形だけ解像度が高い」（`canon/LEDGER.md` 0020）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
