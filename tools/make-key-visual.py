# -*- coding: utf-8 -*-
"""廻リ視のキービジュアルを、**作品の実物から**組む。

    py -3.11 tools/make-key-visual.py [--out docs/key-visual/mawarimi_key_visual.png]

| 何 | どこから |
|---|---|
| 地 | 4 周目 A の生成素材（生の 1448px を `gen-tone.py undim` で実機の明るさへ戻す） |
| 題字 | `Assets/Resources/Title/MawarimiTitle.png`（4ch の版） |
| 色 | `Assets/Art/Shaders/Title/TitleGlyph.shader` の `_InkColor` / `_AccentColor` |

⚠ **版は絵ではなくマスク**。R=主の白墨（廻・リ）/ G=朱の墨（「視」1 字）/
  B=溶ける順（演出用・ここでは使わない）/ A=添えの白墨（払い・ルビ・罫）。
  詳しくは `.claude/memory/title_screen.md` §2。
⚠ **shader の色は linear**（プロジェクトは Linear color space）。表示用に sRGB へ直す。
⚠ **赤は 1 つだけ** — 朱は「視」だけに乗る。ここを崩すとアクセントが地に埋もれる（同 §5.1）。

構図の判断（すべてシュビーの判断。ユーザーの判定が出たらここを直す）:
  ・人形は左、題字は右上の幕へ分ける。中央に置くと「廻」が人形の頭に乗る
  ・右端の事務室の棚は明るく雑然としているので切る
  ・右下は無地の床が広く空くので沈めて、画の重心を人形へ寄せる
"""
from __future__ import annotations

import argparse
import importlib.util
import os
import sys

import numpy as np
from PIL import Image, ImageFilter

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
W, H = 1920, 1080

# `TitleGlyph.shader` の既定値（linear）
INK = (0.88, 0.82, 0.71)        # 生成り。「廻」「リ」と払い・ルビ・罫
ACCENT = (0.62, 0.030, 0.035)   # 朱。「視」1 字だけ — 画面で唯一の赤
GLOW = (0.82, 0.52, 0.24)       # 灯り。墨の周りにごく淡く敷く

BG_SRC = "logs/gen-plate/auto_20260823_201947_1/out.png"   # 4 周目 A の採用（生・1448px）
BG_CAM = "A"
ART = "Assets/Resources/Title/MawarimiTitle.png"


def to_srgb(c) -> np.ndarray:
    a = np.asarray(c, dtype=np.float64)
    return np.where(a <= 0.0031308, a * 12.92,
                    1.055 * np.power(np.clip(a, 0, None), 1 / 2.4) - 0.055)


def load_gen_tone():
    """`tools/gen-tone.py` はハイフン入りなので import 文で読めない。"""
    p = os.path.join(REPO, "tools", "gen-tone.py")
    s = importlib.util.spec_from_file_location("gen_tone", p)
    m = importlib.util.module_from_spec(s)
    s.loader.exec_module(m)
    return m


def _crop_to_frame(im: Image.Image) -> Image.Image:
    """右端の事務室の棚を落として 16:9 へ。縦は下端まで（人形の足元を切らない）。"""
    bw, bh = im.size
    cw = int(bw * 0.76)
    ch = int(round(cw * 9 / 16))
    return im.crop((0, bh - ch, cw, bh)).resize((W, H), Image.LANCZOS)


def dolls_mask() -> np.ndarray:
    """人形だけを抜く。種（人形の居ないプレート）との差分で作る。

    ⚠ 白い壁を黒く落とすとき、**人形は残す**。壁の前に立っている個体が居るので、
      矩形で落とすと人形の左半分が消える。
    """
    run = os.path.dirname(os.path.join(REPO, BG_SRC))
    seed = Image.open(os.path.join(run, "seed.png")).convert("RGB")
    gen = Image.open(os.path.join(REPO, BG_SRC)).convert("RGB")
    if gen.size != seed.size:
        gen = gen.resize(seed.size, Image.LANCZOS)
    d = np.abs(np.asarray(gen, dtype=np.float64)
               - np.asarray(seed, dtype=np.float64)).max(axis=2)
    m = Image.fromarray(((d > 20) * 255).astype(np.uint8), "L")
    # 穴（着物と幕の色が近い所）を埋めてから、斑を落とす
    m = m.filter(ImageFilter.MaxFilter(11)).filter(ImageFilter.MinFilter(7))
    # ⚠ **二値のまま拡大しない。** 1100 → 1920 で階段がそのまま 1.75 倍になり、
    #   切り抜いた輪郭が画で見える（2026-08-24 に 1 度出した）。先にぼかして連続値にする。
    m = m.filter(ImageFilter.GaussianBlur(5))
    return np.asarray(_crop_to_frame(m.convert("RGB")).convert("L"),
                      dtype=np.float64) / 255.0


def ground() -> Image.Image:
    gt = load_gen_tone()
    raw = Image.open(os.path.join(REPO, BG_SRC)).convert("RGB")
    post = gt.load_post(BG_CAM, os.path.join(REPO, "tools/web-compositor/show.json"))
    bg = _crop_to_frame(gt.undim(raw, post, 0.5, True))   # dim と同じ指定で戻す（露出だけ）

    a = np.asarray(bg, dtype=np.float64)
    yy, xx = np.mgrid[0:H, 0:W]
    u, v = xx / W, yy / H

    # --- 残すのは「幕とパイプで組んだ L 字」と「床」だけ ---------------------
    # 左上の白い壁は幕でも床でもないので黒へ落とす。境目は幕を吊る縦のパイプ（u≈0.285）と、
    # 壁と床の境（v≈0.63）。**人形は落とさない**。
    # ⚠ 壁と床の境（幅木）は左端ほど下にある。水平線で切ると左端に白い帯が残る
    wall = (np.clip((0.285 - u) / 0.020, 0, 1)
            * np.clip((0.685 - 0.09 * u / 0.285 - v) / 0.016, 0, 1))
    a *= (1.0 - 0.995 * np.clip(wall - dolls_mask(), 0, 1))[:, :, None]

    # 幕の上端（天井へ抜ける所）も暗がりへ溶かす
    a *= (1.0 - 0.55 * np.clip((0.055 - v) / 0.055, 0, 1))[:, :, None]

    r = np.sqrt(((xx - W / 2) / (W / 2)) ** 2 + ((yy - H / 2) / (H / 2)) ** 2)
    a *= (1.0 - 0.42 * np.clip(r - 0.20, 0, None) ** 1.5)[:, :, None]

    corner = np.clip((u - 0.46) / 0.30, 0, 1) * np.clip((v - 0.52) / 0.30, 0, 1)
    a *= (1.0 - 0.46 * corner)[:, :, None]

    rng = np.random.default_rng(20260824)        # 撮像の粒（輝度のみ・固定種で再現する）
    a += rng.normal(0.0, 2.4, (H, W, 1))
    return Image.fromarray(np.clip(a, 0, 255).astype(np.uint8))


def glyph(width_frac: float) -> tuple[Image.Image, Image.Image]:
    art = np.asarray(Image.open(os.path.join(REPO, ART)), dtype=np.float64) / 255.0
    ink_m = np.maximum(art[:, :, 0], art[:, :, 3])     # 主 + 添え
    acc_m = art[:, :, 1]                               # 朱（「視」）
    alpha = np.clip(ink_m + acc_m, 0, 1)
    # shader の lerp(_InkColor, _AccentColor, color.g) と同じ考え方で混ぜる
    mix = np.divide(acc_m, np.maximum(alpha, 1e-6))
    rgb = (to_srgb(INK) * 255.0)[None, None, :] * (1 - mix)[:, :, None] \
        + (to_srgb(ACCENT) * 255.0)[None, None, :] * mix[:, :, None]

    t = Image.fromarray(np.dstack([rgb, alpha * 255.0]).astype(np.uint8), "RGBA")
    tw = int(W * width_frac)                           # 版は 2:1
    t = t.resize((tw, tw // 2), Image.LANCZOS)

    gl = Image.new("RGBA", t.size, tuple((to_srgb(GLOW) * 255).astype(int)) + (0,))
    gl.putalpha(t.split()[3].filter(ImageFilter.GaussianBlur(18)).point(lambda v: int(v * 0.34)))
    return t, gl


def main() -> int:
    ap = argparse.ArgumentParser(description="廻リ視のキービジュアル")
    ap.add_argument("--out", default="docs/key-visual/mawarimi_key_visual.png")
    ap.add_argument("--title-width", type=float, default=0.44, help="題字の幅（画面比）")
    ap.add_argument("--title-x", type=float, default=0.525)
    ap.add_argument("--title-y", type=float, default=0.10)
    a = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")

    canvas = ground().convert("RGBA")
    t, gl = glyph(a.title_width)
    pos = (int(W * a.title_x), int(H * a.title_y))
    canvas.alpha_composite(gl, pos)
    canvas.alpha_composite(t, pos)

    out = a.out if os.path.isabs(a.out) else os.path.join(REPO, a.out)
    os.makedirs(os.path.dirname(out), exist_ok=True)
    canvas.convert("RGB").save(out)
    print(f"wrote {out}  {W}x{H}")
    print(f"  生成り sRGB {(to_srgb(INK) * 255).round(0)}  /  朱 sRGB {(to_srgb(ACCENT) * 255).round(0)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
