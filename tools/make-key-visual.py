# -*- coding: utf-8 -*-
"""廻リ視のキービジュアルを、**作品の実物から**組む。

    py -3.11 tools/make-key-visual.py --emit-plate <地の生画.png>        # ① Codex へ渡す地を出す
    py -3.11 tools/make-key-visual.py --ground <照明を描き直した地.png>  # ② 題字と文字を組む
    py -3.11 tools/make-key-visual.py                                   # 旧経路（地を自前で作る）

| 何 | どこから |
|---|---|
| 地 | 4 周目 A の生成素材（生の 1448px を `gen-tone.py undim` で実機の明るさへ戻し、16:9 へ切る） |
|    | → **Codex に照明だけ描き直させる**（`--ground`。プロンプトは docs/key-visual/relight-prompt.txt） |
|    | `--ground` が無ければ旧経路: 白い壁を種との差分マスクで落とす（2026-08-24 の版） |
| 題字 | `Assets/Resources/Title/MawarimiTitle.png`（4ch の版） |
| 色 | `Assets/Art/Shaders/Title/TitleGlyph.shader` の `_InkColor` / `_AccentColor`。画の光に合わせて少し沈める |
| 文字 | 縦のキャッチ（Yu Mincho Light）と下段の情報（Yu Gothic Medium）。C:/Windows/Fonts から読む |

⚠ **版は絵ではなくマスク**。R=主の白墨（廻・リ）/ G=朱の墨（「視」1 字）/
  B=溶ける順（演出用・ここでは使わない）/ A=添えの白墨（払い・ルビ・罫）。
  詳しくは `.claude/memory/title_screen.md` §2。
⚠ **shader の色は linear**（プロジェクトは Linear color space）。表示用に sRGB へ直す。
⚠ **赤は 1 つだけ** — 朱は「視」だけに乗る。ここを崩すとアクセントが地に埋もれる（同 §5.1）。
  地の側では、着物の赤を影の中に沈めてもらう（relight-prompt.txt）。画の中でいちばん鮮やかな赤が「視」になる

構図（`canon/LEDGER.md` 0126 と 2026-09-10 の作り直し。判断はシュビー）:
  ・残すのは「幕とパイプで組んだ L 字」と「床」だけ。他は真っ暗。**暗さはマスクではなく光の不在で作る**
  ・題字は中心。主の墨（廻リ視）の外接矩形の中心を (0.5W, TITLE_Y) に置く。位置と幅は引数で動く
  ・題字の下に黒い暈しを敷く（文字の周りに呼吸できる暗部。光らせるのではなく沈める）
  ・「視」に廻リと同じ粗れを与える（版の G は縁が硬い活字で、白墨と質感が揃っていなかった）
  ・文字は 2 つだけ: 左上の縦のキャッチ、下段の情報行。時刻・走査線などの装置の UI は入れない
"""
from __future__ import annotations

import argparse
import importlib.util
import os
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
W, H = 1920, 1080

# `TitleGlyph.shader` の既定値（linear）
INK = (0.88, 0.82, 0.71)        # 生成り。「廻」「リ」と払い・ルビ・罫
ACCENT = (0.62, 0.030, 0.035)   # 朱。「視」1 字だけ — 画面で唯一の赤
GLOW = (0.82, 0.52, 0.24)       # 灯り（旧経路の暈しにだけ使う）

# キービジュアル用に沈めた表示色（sRGB 0-255）。白墨の最高輝度は人形の顔より少し下に置く
INK_KV = (222, 214, 199)
ACCENT_KV = (192, 44, 48)

BG_SRC = "logs/gen-plate/auto_20260823_201947_1/out.png"   # 4 周目 A の採用（生・1448px）
BG_CAM = "A"
ART = "Assets/Resources/Title/MawarimiTitle.png"

FONT_MINCHO = r"C:\Windows\Fonts\yuminl.ttf"     # Yu Mincho Light
FONT_GOTHIC = r"C:\Windows\Fonts\YuGothM.ttc"    # Yu Gothic Medium

# 案 / 2026-09-10 / シュビー（canon/OPEN.md）。縦書き 2 行、右の行から読む
TAGLINE_LINES = ("カメラは動かない。", "動くのは、あなた。")

# 下段（映画の一枚絵のビリングブロックの形）。上から順に 種別 / 役職と名前。
# ⚠ 役職は名前より小さく淡く。同じ大きさで並べると名簿に見える
BILLING_GENRE = "VR固定視点ホラー"
BILLING_CREDIT = (("企画・監督・制作リーダー", "白石大晴"),)

SEED = 20260910


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


def plate_raw() -> Image.Image:
    """Codex へ渡す地。実機の明るさへ戻して 16:9 に切っただけ（粒も減光も無し）。"""
    gt = load_gen_tone()
    raw = Image.open(os.path.join(REPO, BG_SRC)).convert("RGB")
    post = gt.load_post(BG_CAM, os.path.join(REPO, "tools/web-compositor/show.json"))
    return _crop_to_frame(gt.undim(raw, post, 0.5, True))


# --------------------------------------------------------------------------- 旧経路
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
    m = m.filter(ImageFilter.MaxFilter(11)).filter(ImageFilter.MinFilter(7))
    # ⚠ **二値のまま拡大しない。** 1100 → 1920 で階段がそのまま 1.75 倍になる。先にぼかす
    m = m.filter(ImageFilter.GaussianBlur(5))
    return np.asarray(_crop_to_frame(m.convert("RGB")).convert("L"),
                      dtype=np.float64) / 255.0


def ground_legacy() -> Image.Image:
    """2026-08-24 の地。白い壁をマスクで落とし、周辺を減光する。"""
    a = np.asarray(plate_raw(), dtype=np.float64)
    yy, xx = np.mgrid[0:H, 0:W]
    u, v = xx / W, yy / H
    wall = (np.clip((0.285 - u) / 0.020, 0, 1)
            * np.clip((0.685 - 0.09 * u / 0.285 - v) / 0.016, 0, 1))
    a *= (1.0 - 0.995 * np.clip(wall - dolls_mask(), 0, 1))[:, :, None]
    a *= (1.0 - 0.55 * np.clip((0.055 - v) / 0.055, 0, 1))[:, :, None]
    r = np.sqrt(((xx - W / 2) / (W / 2)) ** 2 + ((yy - H / 2) / (H / 2)) ** 2)
    a *= (1.0 - 0.42 * np.clip(r - 0.20, 0, None) ** 1.5)[:, :, None]
    corner = np.clip((u - 0.46) / 0.30, 0, 1) * np.clip((v - 0.52) / 0.30, 0, 1)
    a *= (1.0 - 0.46 * corner)[:, :, None]
    return Image.fromarray(np.clip(a, 0, 255).astype(np.uint8))


# --------------------------------------------------------------------------- 新経路
def ground_from(path: str, black: float, tame_red: float = 1.0) -> Image.Image:
    """Codex が照明を描き直した地。寸法を W×H へ揃え、黒を締め（black は 0-255 の黒点）、
    着物の赤を沈める（tame_red は赤い画素の彩度の倍率）。"""
    im = Image.open(path).convert("RGB")
    if im.size != (W, H):
        # 16:9 でなければ中央で切る（上下の余りを落とす方が L 字と床に効かない）
        bw, bh = im.size
        if abs(bw / bh - 16 / 9) > 0.01:
            if bw / bh > 16 / 9:
                cw = int(round(bh * 16 / 9)); im = im.crop(((bw - cw) // 2, 0, (bw - cw) // 2 + cw, bh))
            else:
                ch = int(round(bw * 9 / 16)); im = im.crop((0, (bh - ch) // 2, bw, (bh - ch) // 2 + ch))
        im = im.resize((W, H), Image.LANCZOS)
    a = np.asarray(im, dtype=np.float64)
    if black > 0:
        a = np.clip((a - black) * (255.0 / (255.0 - black)), 0, 255)
    if tame_red < 1.0:
        # 着物の赤を沈める。画の中でいちばん鮮やかな赤を「視」にするため（赤は 1 つだけ）。
        # 赤い画素だけ、彩度を tame_red 倍にする（境目は彩度と赤みで滑らかに）
        r, g, b = a[:, :, 0], a[:, :, 1], a[:, :, 2]
        mx = a.max(axis=2); mn = a.min(axis=2)
        sat = (mx - mn) / np.maximum(mx, 1e-6)
        redness = np.clip((r - np.maximum(g, b)) / np.maximum(mx, 1e-6) / 0.25, 0, 1)
        w = redness * np.clip((sat - 0.25) / 0.25, 0, 1)
        lum = (0.299 * r + 0.587 * g + 0.114 * b)[:, :, None]
        tamed = lum + (a - lum) * tame_red
        a = a * (1 - w)[:, :, None] + tamed * w[:, :, None]
    return Image.fromarray(np.clip(a, 0, 255).astype(np.uint8))


def _noise(shape, sigma_px: float, rng) -> np.ndarray:
    """0..1 の低周波ノイズ（正規化した gaussian blur）。"""
    n = rng.random(shape)
    im = Image.fromarray((n * 255).astype(np.uint8), "L").filter(ImageFilter.GaussianBlur(sigma_px))
    a = np.asarray(im, dtype=np.float64)
    lo, hi = np.percentile(a, 1), np.percentile(a, 99)
    return np.clip((a - lo) / max(hi - lo, 1e-6), 0, 1)


def glyph(width_frac: float, rng) -> tuple[Image.Image, Image.Image, tuple[int, int, int, int]]:
    """題字の版を表示色で起こす。返すのは (墨, 黒い暈し, 主の墨の外接矩形[墨画像内])。

    - 「視」に白墨と同じ粗れを与える（G は縁が硬い活字のまま焼かれている）
    - 添えの白墨（A: ルビ・罫）は 0.55 倍、払いの弧は 0.3 倍。弧線が装飾として浮き、人形の顔を横切っていた
    """
    art = np.asarray(Image.open(os.path.join(REPO, ART)), dtype=np.float64) / 255.0
    ph, pw = art.shape[:2]
    main_m = art[:, :, 0]
    # 添え（A）: ルビと罫は 0.55、主の墨より下へ伸びる払いの弧は 0.3。弧が人形の顔を横切って
    # 傷のように見えたので、そこだけさらに落とす（版の y=760 より下が弧）
    yy = np.arange(ph, dtype=np.float64)[:, None]
    add_gain = 0.55 - 0.25 * np.clip((yy - 730.0) / 60.0, 0, 1)
    add_m = art[:, :, 3] * add_gain
    acc_m = art[:, :, 1]

    # 「視」の粗れ: 低周波のむらと、小さな抜け
    mottle = 0.84 + 0.16 * _noise((ph, pw), 6, rng)
    pits = _noise((ph, pw), 1.2, rng) > 0.965
    acc_m = acc_m * mottle
    acc_m = np.where(pits, acc_m * 0.3, acc_m)

    ink_m = np.maximum(main_m, add_m)
    alpha = np.clip(ink_m + acc_m, 0, 1)
    mix = np.divide(acc_m, np.maximum(alpha, 1e-6))
    rgb = (np.asarray(INK_KV, dtype=np.float64)[None, None, :] * (1 - mix)[:, :, None]
           + np.asarray(ACCENT_KV, dtype=np.float64)[None, None, :] * mix[:, :, None])
    t = Image.fromarray(np.dstack([rgb, alpha * 255.0]).astype(np.uint8), "RGBA")

    tw = int(W * width_frac)                        # 版は 2:1
    th = tw // 2
    t = t.resize((tw, th), Image.LANCZOS)

    # 主の墨（廻リ視）の外接矩形。中心合わせに使う
    ys, xs = np.where(np.maximum(main_m, art[:, :, 1]) > 0.2)
    sx, sy = tw / pw, th / ph
    bbox = (int(xs.min() * sx), int(ys.min() * sy), int(xs.max() * sx), int(ys.max() * sy))

    # 黒い暈し: 墨の足元を沈めて、文字の周りに呼吸できる暗部を作る
    a_im = t.split()[3]
    near = np.asarray(a_im.filter(ImageFilter.GaussianBlur(26)), dtype=np.float64) / 255.0
    far = np.asarray(a_im.filter(ImageFilter.GaussianBlur(70)), dtype=np.float64) / 255.0
    halo_a = np.clip(near * 0.55 + far * 0.45, 0, 1) * 0.62
    halo = Image.new("RGBA", t.size, (0, 0, 0, 0))
    halo.putalpha(Image.fromarray((halo_a * 255).astype(np.uint8), "L"))
    return t, halo, bbox


def _font(path: str, size: int, index: int = 0) -> ImageFont.FreeTypeFont:
    return ImageFont.truetype(path, size, index=index)


def draw_vertical(canvas: Image.Image, text: str, font: ImageFont.FreeTypeFont,
                  x: int, y: int, fill: tuple[int, int, int, int], gap: float = 1.32) -> int:
    """縦書き。1 字ずつ落とす。句読点は升の右上へ寄せる。返すのは末尾の y。"""
    layer = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    size = font.size
    step = int(size * gap)
    cy = y
    for ch in text:
        if ch in "。、":
            d.text((x + size * 0.58, cy - size * 0.50), ch, font=font, fill=fill)
            cy += int(size * 0.72)
            continue
        d.text((x, cy), ch, font=font, fill=fill)
        cy += step
    canvas.alpha_composite(layer)
    return cy


def draw_vertical_lines(canvas: Image.Image, lines, font: ImageFont.FreeTypeFont,
                        x_right: int, y: int, fill: tuple[int, int, int, int],
                        gap: float = 1.30, col: float = 1.95) -> None:
    """縦書きを複数行。右の行から左へ並べる（日本語の縦組みの読み順）。"""
    x = x_right
    for line in lines:
        draw_vertical(canvas, line, font, x, y, fill, gap)
        x -= int(font.size * col)


def draw_spaced(canvas: Image.Image, text: str, font: ImageFont.FreeTypeFont,
                cx: int, y: int, fill: tuple[int, int, int, int], tracking: float) -> None:
    """字間を空けた横書きを中央揃えで置く。tracking は em 比。"""
    layer = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    tr = font.size * tracking
    widths = [d.textlength(ch, font=font) for ch in text]
    total = sum(widths) + tr * (len(text) - 1)
    x = cx - total / 2
    for ch, w in zip(text, widths):
        d.text((x, y), ch, font=font, fill=fill)
        x += w + tr
    canvas.alpha_composite(layer)


def draw_billing(canvas: Image.Image, parts, cx: int, baseline: int) -> None:
    """役職と名前を 1 行に。大きさの違う断片をベースラインで揃えて中央へ置く。

    parts は (文字列, フォント, 塗り, 字間 em 比, 前の空き px) の並び。
    """
    layer = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    runs = []
    total = 0.0
    for text, font, fill, tracking, lead in parts:
        tr = font.size * tracking
        widths = [d.textlength(ch, font=font) for ch in text]
        w = sum(widths) + tr * (len(text) - 1)
        runs.append((text, font, fill, tr, widths, w, lead))
        total += w + lead
    x = cx - total / 2
    for text, font, fill, tr, widths, w, lead in runs:
        x += lead
        for ch, cw in zip(text, widths):
            d.text((x, baseline), ch, font=font, fill=fill, anchor="ls")
            x += cw + tr
    canvas.alpha_composite(layer)


def grain(im: Image.Image, sigma: float, rng) -> Image.Image:
    a = np.asarray(im.convert("RGB"), dtype=np.float64)
    a += rng.normal(0.0, sigma, (H, W, 1))            # 輝度のみ
    return Image.fromarray(np.clip(a, 0, 255).astype(np.uint8))


def main() -> int:
    ap = argparse.ArgumentParser(description="廻リ視のキービジュアル")
    ap.add_argument("--out", default="docs/key-visual/mawarimi_key_visual.png")
    ap.add_argument("--ground", help="Codex が照明を描き直した地（16:9）。無ければ旧経路で作る")
    ap.add_argument("--emit-plate", help="Codex へ渡す地の生画をここへ書いて終わる")
    ap.add_argument("--black", type=float, default=0.0, help="地の黒点（0-255）。締めたいときだけ")
    ap.add_argument("--tame-red", type=float, default=0.72, help="地の赤い画素の彩度の倍率（着物を沈めて「視」を唯一の赤にする）")
    ap.add_argument("--title-width", type=float, default=0.52, help="版の幅（画面比）。主の墨はその 0.76 倍")
    ap.add_argument("--title-x", type=float, default=0.50, help="主の墨の中心 x（画面比）")
    ap.add_argument("--title-y", type=float, default=0.385, help="主の墨の中心 y（画面比）")
    ap.add_argument("--billing-x", type=float, default=0.50, help="下段の中心 x（画面比）")
    ap.add_argument("--billing-y", type=float, default=0.902, help="下段の 1 行目の上端 y（画面比）。役職と名前は +0.064")
    ap.add_argument("--no-text", action="store_true", help="キャッチと情報行を入れない")
    ap.add_argument("--no-halo", action="store_true", help="題字の下の黒い暈しを入れない")
    ap.add_argument("--grain", type=float, default=2.2)
    a = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")
    rng = np.random.default_rng(SEED)

    if a.emit_plate:
        p = a.emit_plate if os.path.isabs(a.emit_plate) else os.path.join(REPO, a.emit_plate)
        os.makedirs(os.path.dirname(p), exist_ok=True)
        plate_raw().save(p)
        print(f"wrote {p}  {W}x{H}")
        return 0

    if a.ground:
        g = a.ground if os.path.isabs(a.ground) else os.path.join(REPO, a.ground)
        canvas = ground_from(g, a.black, a.tame_red).convert("RGBA")
    else:
        canvas = ground_legacy().convert("RGBA")

    t, halo, bb = glyph(a.title_width, rng)
    bcx, bcy = (bb[0] + bb[2]) / 2, (bb[1] + bb[3]) / 2
    pos = (int(round(W * a.title_x - bcx)), int(round(H * a.title_y - bcy)))
    if not a.no_halo:
        canvas.alpha_composite(halo, pos)
    canvas.alpha_composite(t, pos)

    if not a.no_text:
        ink = INK_KV
        # 左上の暗部に 2 行。人形の頭（y ≈ 0.33H〜）に掛からない高さで終える
        draw_vertical_lines(canvas, TAGLINE_LINES, _font(FONT_MINCHO, 29), int(W * 0.088), int(H * 0.075),
                            ink + (205,))
        # 下段は 2 段。種別 → 役職と名前。中心（題字と同じ軸）に置く
        bx = int(W * a.billing_x)
        draw_spaced(canvas, BILLING_GENRE, _font(FONT_MINCHO, 30), bx, int(H * a.billing_y), ink + (175,), 0.32)
        f_role, f_name = _font(FONT_GOTHIC, 17), _font(FONT_MINCHO, 28)
        for i, (role, name) in enumerate(BILLING_CREDIT):
            draw_billing(canvas, [(role, f_role, ink + (120,), 0.06, 0),
                                  (name, f_name, ink + (190,), 0.16, 30)],
                         bx, int(H * (a.billing_y + 0.064 + 0.042 * i)))

    out_im = grain(canvas, a.grain, rng)
    out = a.out if os.path.isabs(a.out) else os.path.join(REPO, a.out)
    os.makedirs(os.path.dirname(out), exist_ok=True)
    out_im.save(out)
    print(f"wrote {out}  {W}x{H}  ground={'codex' if a.ground else 'legacy'}")
    print(f"  題字: 版 {t.size}  主の墨 {bb} → 中心 ({W * a.title_x:.0f}, {H * a.title_y:.0f})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
