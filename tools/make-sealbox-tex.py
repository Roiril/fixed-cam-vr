#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""封印の箱の「使い込まれた地」を焼く。

出どころは .claude/canon/LEDGER.md 0011:
  「長年使いこまれた金属だけど金属じゃない得体のしれない使い込まれた物体」
  「ベースのテクスチャ感は金属っぽく、けどテカリはしないような感じ」
  「線だけ、赤だけの安っぽいデジタルな見た目はやめたい」

出力: Assets/Resources/Intro/SealBoxWear.png（512x512・RGBA・**タイル可能**）

  R = 地のむら（酸化・古び）。低周波が主。明るいほど地金が残っている
  G = 研磨目 / ざらつき。周方向へ引き伸ばした高周波 ＋ 細かい粒
  B = 磨耗（凹み・当たり傷・掻き傷）。明るいほど深く抉れている
  A = 熾が宿る場所。**まばら**（大半は 0 に近い。ここが「くっきり光る所は少なめ」の実体）

チャンネルの意味は SealedBox.shader と対。**片方だけ直すと沈黙して食い違う。**

## なぜ画像生成モデルを使わないか

ユーザーは「画像生成でも、コードで生成でもいい」と言っている。手続きにしたのは 3 つの理由:

  - **タイルにできる**。箱は側面 4 枚を 1 枚のシートとして巻いてあり（LEDGER 0006）、
    継ぎ目が噛み合うことが既に採られている。生成画像は端が繋がらないので、
    せっかく噛み合わせた縦 4 辺に別の継ぎ目を作ることになる
  - **チャンネルを分けられる**。地のむらと熾の宿り所は**別々に効かせる**必要がある
    （「暗い所が多く、わずかに光る」は 1 枚の絵では表せない）
  - **同じ種から同じ絵が出る**。焼き直しても見た目が変わらない

## 使い方

    py -3.11 tools/make-sealbox-tex.py            # 既定の種で焼く
    py -3.11 tools/make-sealbox-tex.py --seed 7   # 別の面を試す

⚠ 依存は PIL だけ（numpy はこの機に入っていない — rules/windows-env.md §7）。
⚠ 焼き直したら `.\tools\unity.ps1 menu sealedbox` で必ず絵を見る。
"""

import argparse
import os
import random
import sys

from PIL import Image, ImageChops, ImageDraw, ImageFilter

sys.stdout.reconfigure(encoding="utf-8")

SIZE = 512
OUT = "Assets/Resources/Intro/SealBoxWear.png"


def tile_noise(cx, cy, rng, size=SIZE):
    """cx × cy の乱数を滑らかに引き伸ばした、**上下左右が繋がる**ノイズ。

    3×3 に並べてから拡大して中央を切り出す。こうしないと拡大の補間が端で
    途切れ、箱を巻いたときに縦の 4 辺へ継ぎ目が出る。
    """
    cx = max(2, cx)
    cy = max(2, cy)
    cell = Image.frombytes("L", (cx, cy),
                           bytes(rng.randrange(256) for _ in range(cx * cy)))
    tiled = Image.new("L", (cx * 3, cy * 3))
    for j in range(3):
        for i in range(3):
            tiled.paste(cell, (i * cx, j * cy))
    up = tiled.resize((size * 3, size * 3), Image.BICUBIC)
    return up.crop((size, size, size * 2, size * 2))


def blend_octaves(spec, rng):
    """[(cx, cy, 重み), ...] の重み付き平均。**float で足して最後に 1 度だけ量子化する**
    （8bit の中間画像で足すと、オクターブを重ねるほど段差が溜まってバンディングが出る）。"""
    size = SIZE
    total = sum(w for _, _, w in spec)
    acc = [0.0] * (size * size)
    for cx, cy, w in spec:
        data = tile_noise(cx, cy, rng).getdata()
        for i, v in enumerate(data):
            acc[i] += v * w
    out = Image.new("L", (size, size))
    out.putdata([int(min(255, max(0, v / total))) for v in acc])
    return out


def curve(img, lo, hi, gamma=1.0):
    """lo..hi を 0..255 へ伸ばして gamma を掛ける（まばらさ・コントラストを作る）。"""
    span = max(1e-6, hi - lo)
    return img.point(lambda v: int(255 * min(1.0, max(0.0, (v / 255.0 - lo) / span)) ** gamma))


def pct_curve(img, lo_pct, hi_pct, gamma=1.0):
    """**分位で**伸ばす。オクターブを重ねたノイズは中心極限で 0.5 付近へ寄るので、
    固定のしきい値（0.6 等）で切ると**何も残らない**（実測: 熾が平均 5/255 で全滅した）。
    「面積の何 % を残すか」で書けば、種を変えても出来上がりの粗密が変わらない。"""
    h = img.histogram()
    n = sum(h)
    acc, lo, hi = 0, 0, 255
    lo_n, hi_n = n * lo_pct, n * hi_pct
    for i, c in enumerate(h):
        acc += c
        if acc <= lo_n:
            lo = i
        if acc <= hi_n:
            hi = i
    return curve(img, lo / 255.0, max(hi, lo + 1) / 255.0, gamma)


def scratches(rng, count, length, width, size=SIZE):
    """掻き傷。**9 回描いて必ずタイルさせる**（端を跨ぐ傷が切れない）。"""
    img = Image.new("L", (size, size), 0)
    d = ImageDraw.Draw(img)
    for _ in range(count):
        x = rng.uniform(0, size)
        y = rng.uniform(0, size)
        ang = rng.uniform(-0.35, 0.35)          # ほぼ水平（巻いた方向＝製造の目）
        ln = rng.uniform(length * 0.4, length)
        dx = ln * (1 - abs(ang))
        dy = ln * ang
        w = rng.randint(1, max(1, width))
        v = rng.randint(45, 130)
        for oy in (-size, 0, size):
            for ox in (-size, 0, size):
                d.line((x + ox, y + oy, x + ox + dx, y + oy + dy), fill=v, width=w)
    return img.filter(ImageFilter.GaussianBlur(0.6))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--seed", type=int, default=20260812)
    ap.add_argument("--out", default=OUT)
    args = ap.parse_args()

    rng = random.Random(args.seed)

    # --- R: 地のむら（酸化・古び）。低周波が主で、面が「一枚の板」に見えないようにする ---
    base = blend_octaves([(3, 3, 1.00), (6, 6, 0.55), (13, 13, 0.30),
                          (29, 29, 0.16), (61, 61, 0.09)], rng)
    base = pct_curve(base, 0.03, 0.97, 0.95)

    # --- G: 研磨目 + ざらつき。周方向（横）へ引き伸ばす＝圧延・研磨の目 ---
    streak = blend_octaves([(220, 5, 1.00), (420, 11, 0.55), (96, 3, 0.35)], rng)
    grain = blend_octaves([(256, 256, 1.00), (170, 170, 0.5)], rng)
    g = Image.blend(pct_curve(streak, 0.02, 0.98), grain, 0.34)

    # --- B: 磨耗（凹み・当たり・掻き傷）。ここは光が乗りにくく、地が少し明るい ---
    # ⚠ **大きく柔らかい塊にしない。** 最初 (8,8) 主体で焼いたら、絵の上で
    #    「白いカビ / 霜」に見えた（2026-08-12 実測）。磨耗は面の高い所が擦れて出るものなので、
    #    **小さく・散って・輪郭が締まっている**方が正しい。
    dents = blend_octaves([(14, 14, 1.00), (31, 31, 0.7), (67, 67, 0.4)], rng)
    dents = pct_curve(dents, 0.80, 0.999, 1.6)     # 上位 2 割だけ・より締める
    # 掻き傷も白く光らせない（暗い面では線が浮いて「画面のゴミ」に見える）
    b = ImageChops.lighter(dents, scratches(rng, 18, 210, 2))

    # --- A: 熾が宿る場所。**大半は 0**。ここが「くっきり光る所は少なめ」の実体 ---
    hearth = blend_octaves([(4, 4, 1.00), (9, 9, 0.6), (19, 19, 0.28)], rng)
    # **上位 2 割弱だけを 0..1 へ伸ばして 2.0 乗**。面のほとんどが 0 に近く、
    # 熾がはっきり宿るのは 1% 前後になる（＝「くっきり光る所は少なめ、暗い所が多い」）。
    # ⚠ 2026-08-12 に 0.70/1.7 から絞った（ユーザー「減らしてみて」）。
    #    分位を上げると**宿る場所の数**が減り、乗数を上げると**宿った所の中の明るい面積**が減る。
    #    2 つは効き方が違うので、片方だけ動かして測る
    a = pct_curve(hearth, 0.82, 0.999, 2.0)

    out = Image.merge("RGBA", (base, g, b, a))
    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    out.save(args.out)

    def summary(name, img):
        h = img.histogram()
        n = sum(h)
        mean = sum(i * c for i, c in enumerate(h)) / n
        hot = sum(c for i, c in enumerate(h) if i > 160) / n * 100
        dark = sum(c for i, c in enumerate(h) if i < 40) / n * 100
        print(f"  {name}: 平均 {mean:5.1f} / 明るい(>160) {hot:5.1f}% / 暗い(<40) {dark:5.1f}%")

    print(f"焼いた: {args.out}  {SIZE}x{SIZE}  seed={args.seed}")
    summary("R 地のむら  ", base)
    summary("G 研磨目    ", g)
    summary("B 磨耗      ", b)
    summary("A 熾の宿り所", a)


if __name__ == "__main__":
    main()
