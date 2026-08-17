# -*- coding: utf-8 -*-
"""**どこまで沈めても読めるか**を探す（「暗がりへ沈めろ」の下限を数で出すため）。

    py -3.11 tools/gen-plate/limit.py --material <素材.png> --plate <プレート.jpg> --lap 4

なじませ規則 3 は「光の当たらない側は入力画像の暗がりと同じ暗さまで落とす」と言っているが、
**どこまで落としてよいか**は書いていない。落としすぎれば届いた画で消える。

素材を `プレート + (素材 − プレート) × k` で薄めながら実機の経路を通し、
**同じ画の実物と同じだけ読める所**まで k を下げる。出るのは
「周りとの明暗差を何割まで落としてよいか」で、部屋にも周にも依らない形になる。

⚠ これは「どこまで沈めるべきか」ではない（それは世界観の判断）。**下限**だけを出す。
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


def faded(material: str, plate: str, k: float, out_path: str) -> str:
    """素材をプレートへ k だけ寄せる（**リニア空間**で薄める）。"""
    a = screen.srgb_to_linear(np.asarray(Image.open(material).convert("RGB"), dtype=np.float64))
    b = screen.srgb_to_linear(np.asarray(Image.open(plate).convert("RGB"), dtype=np.float64))
    if a.shape != b.shape:
        b = screen.srgb_to_linear(np.asarray(
            Image.open(plate).convert("RGB").resize((a.shape[1], a.shape[0]), Image.LANCZOS),
            dtype=np.float64))
    m = b + (a - b) * k
    Image.fromarray(screen.linear_to_srgb(m).astype(np.uint8)).save(out_path)
    return out_path


def parts_of(material, plate, mask, lap, show):
    """**k=1 の素材で領域を決める**（薄めるたびに領域が変わると比較にならない）。

    ⚠ 最初これをやらずに掃引したら、100% の読みやすさが 70% より**低く**出た
    （2026-08-17）。薄いほど「変わった画素」の集合が縮んで、残った濃い所だけで
    平均を取ることになるため。**掃引は計器の欠陥をよく暴く。**
    """
    got, plain = legible._delivered(material, plate, mask, lap, show)
    h, w = got.shape
    sigma = legible._noise_sigma(plain)
    m = np.asarray(Image.open(mask).convert("L").resize((w, h), Image.BILINEAR),
                   dtype=np.float64) / 255.0
    added = metrics.clean(np.abs(got - plain) > max(3.0 * sigma, 4.0)) & (m > 0.5)
    parts = metrics.components(added, min_area=120)[:24] if added.any() else []
    return added, parts, m, sigma


def score(material, plate, mask, lap, show, added, parts, m, sigma) -> tuple[float, float, int]:
    got, plain = legible._delivered(material, plate, mask, lap, show)
    reads, sizes = [], []
    for c in parts:
        sub = np.zeros_like(added)
        sub[c["y0"]:c["y1"], c["x0"]:c["x1"]] = added[c["y0"]:c["y1"], c["x0"]:c["x1"]]
        if sub.sum() < 60:
            continue
        reads.append(legible.readability(got, plain, sub, sigma))
        sizes.append((c["x1"] - c["x0"], c["y1"] - c["y0"]))
    ref = legible.reference_band(plain, m <= 0.5, sigma, sizes or [(40, 60)])
    return (float(np.median(reads)) if reads else float("nan"),
            float(np.median(ref)) if ref else float("nan"), len(reads))


def main() -> int:
    ap = argparse.ArgumentParser(description="どこまで沈めても読めるか")
    ap.add_argument("--material", required=True)
    ap.add_argument("--plate", required=True)
    ap.add_argument("--mask", default=None)
    ap.add_argument("--lap", type=float, default=4.0)
    ap.add_argument("--steps", default="1.0,0.7,0.5,0.35,0.25,0.15,0.10")
    args = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")

    web = os.path.join(spec.REPO, "tools", "web-compositor")
    mask = args.mask or os.path.join(web, "masks", "split_left_half.png")
    with open(os.path.join(web, "show.json"), encoding="utf-8") as f:
        show = json.load(f)

    tmp = os.path.join(spec.REPO, "logs", "gen-plate", "_limit")
    os.makedirs(tmp, exist_ok=True)

    added, parts, m, sigma = parts_of(args.material, args.plate, mask, args.lap, show)
    print(f"{'残す明暗差':>10}{'読みやすさ':>12}{'実物':>8}{'比':>7}{'塊':>5}   判定")
    print("-" * 52)
    cross = None
    for k in [float(x) for x in args.steps.split(",")]:
        path = faded(args.material, args.plate, k, os.path.join(tmp, f"k{int(k * 100):03d}.png"))
        read, ref, n = score(path, args.plate, mask, args.lap, show, added, parts, m, sigma)
        ratio = read / ref if (read == read and ref == ref and ref > 0) else float("nan")
        # ⚠ 合否は **Rose の基準（信号 ÷ 粒 ≥ 5）**で見る。実物の帯は文脈として出すだけ
        #   （実物側は「そこに無ければ何が見えるか」を隣の平均で代用するので、
        #   明るい壁と暗い幕をまたぐ箱で値が跳ね、人形に厳しい側へ大きく偏る）
        ok = read == read and read >= 5.0
        if cross is None and not ok:
            cross = k
        f = lambda v: "—" if v != v else f"{v:.2f}"
        print(f"{k * 100:9.0f}%{f(read):>12}{f(ref):>8}{f(ratio):>7}{n:>5}   "
              f"{'見つけられる' if ok else '**粒に埋もれる**'}")

    print("")
    if cross is None:
        print("⭐ いちばん薄めた所でも見つけられる ＝ **まだ沈める余地がある**")
    else:
        print(f"⭐ 周りとの明暗差を **{cross * 100:.0f}%** まで落とすと粒に埋もれる ＝ そこが下限")
    print("⚠ これは下限であって狙いではない。どこまで沈めるかは世界観の判断")
    print("⚠ 判定は Rose の基準（信号 ÷ 粒 ≥ 5）。実物の列は文脈（代用が粗いので厳しめに出る）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
