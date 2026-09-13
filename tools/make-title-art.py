# -*- coding: utf-8 -*-
"""採用したタイトル「廻リ視」から各表示先の版を焼く。

出力: Assets/Resources/Title/MawarimiTitle.png（2048x1024 RGBA32・**マスクなので sRGB ではない**）

    R = 主の白墨（廻・リ）
    G = 朱の墨（視）
    B = 溶ける順（0 が先に消える）。左下から右上へ + ゆらぎ
    A = 添えの白墨（払い・小点・ルビ・罫）

3 つに分けてあるのは VR で**別の奥行きに置く**ため。添えを奥、主を中、朱を手前にすると、
平らな版のまま両眼視差だけで層が分かれる（押し出すと細い画が潰れる）。

正本は tools/title-art/mawarimi-title-master-v2.png。
Quest・Web UI は採用した題字正本から生成する。
キービジュアルは文字配置まで仕上げた keyvisual-layout-v3.png を使う。
正本が無い場合だけ、以前のフォント輪郭による版へ戻る。

⚠ **sRGB 変換を掛けさせない。** ここは絵ではなくマスクで、掛かると墨の量が変わる。
取り込み設定は Assets/Scripts/Streaming/Editor/TitleArtImporter.cs が機械で固定している。

使い方:
    py -3.11 tools/make-title-art.py
    py -3.11 tools/make-title-art.py --preview   # 判定用の見えるプレビューも出す
"""

import argparse
import math
import os
import sys
from PIL import Image, ImageDraw, ImageFont, ImageFilter, ImageChops

sys.stdout.reconfigure(encoding="utf-8")

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

MASTER_SOURCE = os.path.join(ROOT, "tools", "title-art", "mawarimi-title-master-v2.png")
KEYVISUAL_PLATE = os.path.join(ROOT, "tools", "title-art", "keyvisual-clean-v2.png")
KEYVISUAL_LAYOUT = os.path.join(ROOT, "tools", "title-art", "keyvisual-layout-v3.png")
WEB_DEST = os.path.join(ROOT, "Assets", "Resources", "Visitor", "title-logo-v2.png.bytes")
KEYVISUAL_DEST = os.path.join(ROOT, "Assets", "Art", "KeyVisual", "MawarimiKeyVisual-v2.png")

W, H = 2048, 1024
SS = 2                                   # supersample（字の輪郭のなめらかさ）

# 明朝。**横画が髪の毛のように細い**ことがこの体裁の要で、太いと「ただの見出し」になる。
FONTS = [
    "C:/Windows/Fonts/yumin.ttf",         # 游明朝 Regular
    "C:/Windows/Fonts/yuminl.ttf",        # 游明朝 Light
    "C:/Windows/Fonts/BIZ-UDMinchoM.ttc",
    "C:/Windows/Fonts/msmincho.ttc",
    "Assets/Art/Fonts/SourceHanSansJP-Normal.otf",   # 最後の砦（明朝ではない）
]

# ---- 組み（SS 前の座標。中心 x, 中心 y, 字面の高さ px）------------------------
# 字ごとに大きさも高さも変える。**揃えると「フォントで打った」に見える**。
GLYPHS = [
    ("廻", 520, 470, 520, "white"),
    ("リ", 1010, 375, 430, "white"),
    ("視", 1530, 480, 545, "accent"),
]

# ルビ「まわりみ」。縦組み + 上下に細い罫。参照の《よる、ともす》の位置に置く。
RUBY_TEXT = "まわりみ"
RUBY_X = 792
RUBY_TOP = 350
RUBY_BOTTOM = 600
RUBY_SIZE = 42

# 払い。廻の之繞（しんにょう）から続くつもりの一筆。
# ⚠ **横に長く引かない。** 全幅を横切ると下線に見えて、字の一部に見えない（2026-08-12 実測）。
#    落ちてから寝る形にして、視の手前で抜く。
SWEEP = [(302, 592), (432, 884), (772, 948), (1092, 852)]    # 3 次ベジエの制御点
SWEEP_W0, SWEEP_WMID, SWEEP_W1 = 1.8, 14.0, 1.0              # 端 / 腹 / 端の太さ px
SWEEP_TICK = [(1152, 834), (1268, 812)]                      # 離れた小さな一点
SWEEP_TICK_W = (5.0, 0.8)

# かすれで抜く墨の割合 (%)。**目視で決めない**（拡大すると足りて見え、本番の大きさで消える）。
WEAR_WHITE_PCT = 19.0
WEAR_ACCENT_PCT = 7.0


def pick_font(size, need):
    for path in FONTS:
        p = path if os.path.isabs(path) else os.path.join(ROOT, path)
        if not os.path.exists(p):
            continue
        try:
            f = ImageFont.truetype(p, size)
        except Exception:
            continue
        ok = True
        for ch in need:
            probe = Image.new("L", (size * 2, size * 2), 0)
            ImageDraw.Draw(probe).text((size // 4, size // 4), ch, font=f, fill=255)
            if probe.getbbox() is None:
                ok = False
                break
        if ok:
            return f, p
    raise SystemExit(f"フォントが見つからない: {need}")


def draw_glyph(canvas, ch, cx, cy, target_h):
    """字を 1 つ、字面の実寸で置く。フォントのメトリクスではなく**インクの外接矩形**で合わせる。"""
    probe = 400
    font, path = pick_font(probe, ch)
    tmp = Image.new("L", (probe * 3, probe * 3), 0)
    ImageDraw.Draw(tmp).text((probe, probe), ch, font=font, fill=255)
    bb = tmp.getbbox()
    size = max(8, int(probe * target_h * SS / (bb[3] - bb[1])))

    font = ImageFont.truetype(path, size)
    tmp = Image.new("L", (size * 3, size * 3), 0)
    ImageDraw.Draw(tmp).text((size, size), ch, font=font, fill=255)
    ink = tmp.crop(tmp.getbbox())
    x = int(cx * SS - ink.width / 2)
    y = int(cy * SS - ink.height / 2)
    canvas.paste(ImageChops.lighter(canvas.crop((x, y, x + ink.width, y + ink.height)), ink),
                 (x, y))
    return path


def bezier(pts, t):
    """3 次ベジエ（制御点 4 個）。"""
    (x0, y0), (x1, y1), (x2, y2), (x3, y3) = pts
    u = 1.0 - t
    b0, b1, b2, b3 = u * u * u, 3 * u * u * t, 3 * u * t * t, t * t * t
    return (x0 * b0 + x1 * b1 + x2 * b2 + x3 * b3,
            y0 * b0 + y1 * b1 + y2 * b2 + y3 * b3)


def draw_sweep(canvas, pts, w0, wmid, w1, steps=1400):
    """太さの変わる一筆。円を敷き詰めて描く（PIL に可変幅の線が無い）。"""
    d = ImageDraw.Draw(canvas)
    for i in range(steps + 1):
        t = i / steps
        x, y = bezier(pts, t)
        # 端は細く、腹で太く。両端の落ち方を変えて「入り」と「抜き」を作る。
        if t < 0.5:
            k = t / 0.5
            w = w0 + (wmid - w0) * (k ** 0.55)
        else:
            k = (t - 0.5) / 0.5
            w = wmid + (w1 - wmid) * (k ** 1.7)
        r = max(0.4, w * SS * 0.5)
        d.ellipse([x * SS - r, y * SS - r, x * SS + r, y * SS + r], fill=255)


def draw_tick(canvas, p0, p1, w):
    d = ImageDraw.Draw(canvas)
    steps = 240
    for i in range(steps + 1):
        t = i / steps
        x = p0[0] + (p1[0] - p0[0]) * t
        y = p0[1] + (p1[1] - p0[1]) * t
        r = max(0.4, (w[0] + (w[1] - w[0]) * (t ** 1.4)) * SS * 0.5)
        d.ellipse([x * SS - r, y * SS - r, x * SS + r, y * SS + r], fill=255)


def draw_ruby(canvas):
    font, _ = pick_font(int(RUBY_SIZE * SS), RUBY_TEXT)
    d = ImageDraw.Draw(canvas)
    n = len(RUBY_TEXT)
    span = (RUBY_BOTTOM - RUBY_TOP) * SS
    pitch = span / n
    for i, ch in enumerate(RUBY_TEXT):
        bb = d.textbbox((0, 0), ch, font=font)
        cx = RUBY_X * SS - (bb[2] - bb[0]) / 2 - bb[0]
        cy = RUBY_TOP * SS + pitch * (i + 0.5) - (bb[3] - bb[1]) / 2 - bb[1]
        d.text((cx, cy), ch, font=font, fill=255)
    # 上下の細い罫。**これがあると「読み」ではなく「銘」に見える**。
    rw = max(1, int(1.6 * SS))
    half = int(19 * SS)
    for y in (RUBY_TOP * SS - int(26 * SS), RUBY_BOTTOM * SS + int(26 * SS)):
        d.rectangle([RUBY_X * SS - half, y, RUBY_X * SS + half, y + rw], fill=255)


def grain(size, cell_w, cell_h, sigma, shift=0):
    """任意の粗さの粒。cell が大きいほど大きな塊になる。"""
    w, h = size
    small = Image.effect_noise((max(2, w // cell_w), max(2, h // cell_h)), sigma)
    if shift:
        small = ImageChops.offset(small, shift, shift // 3)
    return small.resize((w, h), Image.BILINEAR)


def wear_mask(size, ink, target_pct):
    """
    かすれ。**筆の縦方向へ、疎らに、太い所は残す。**

    ⚠ 細かい縞を全面へ均一に掛けると<b>木目</b>に見える（2026-08-12 実測）。
    かすれは (1) 粒が大きいこと (2) 疎らであること (3) 画の芯には出ないこと の 3 つで決まる。

    ⚠ <b>閾値を定数で置かない。</b> 粒の作り方を少し触るだけで抜ける量が桁で変わり、
    同じ日に 35% と 1.4% の両方を出した。<b>抜きたい割合から閾値を逆算する</b>ので、
    粒を触っても見た目の濃さは保たれる。
    """
    w, h = size
    # 大きな斑（どこがかすれる区画か）+ 筆の縦の流れ + 細かい粒。
    blot = grain((w, h), 30, 11, 62)
    flow = grain((w, h), 4, 40, 50, 211)
    fine = Image.effect_noise((w, h), 26).filter(ImageFilter.GaussianBlur(0.8))
    m = ImageChops.blend(ImageChops.blend(blot, flow, 0.50), fine, 0.24)

    # 画の芯（太い所）は抜かない。**乾いた筆は縁と細い画から先に切れる**。
    core = ink.filter(ImageFilter.GaussianBlur(9))
    keep = core.point(lambda v: min(255, int((v / 255.0) ** 3.2 * 165)))
    m = ImageChops.add(m, keep)

    # 墨のある所だけを見て閾値を決める（背景の分布に引っ張られない）。
    mp, ip = m.getdata(), ink.getdata()
    vals = sorted(mp[i] for i in range(0, len(ip), 5) if ip[i] > 20)
    if not vals:
        return m.point(lambda v: 255)
    t = vals[min(len(vals) - 1, int(len(vals) * target_pct / 100.0))]
    # ⚠ **縁を硬く切る。** なだらかに落とすと墨が半分残った灰色の帯になり、
    #    字が「破れている」ではなく「ぼけている」に見える（2026-08-12 実測）。
    #    参照の破れは紙が裂けた縁で、途中の階調を持たない。
    lo, hi = t - 4, t + 4
    hard = m.point(lambda v: 0 if v <= lo else (255 if v >= hi else int(255 * (v - lo) / 8.0)))
    # 1 画素だけぼかしてジャギを取る（VR は拡大されるので、生の階段は必ず見える）。
    return hard.filter(ImageFilter.GaussianBlur(0.7))


def report_wear(ink, wear):
    """
    かすれの量を数字で出す。**目視だけで決めない** — 拡大して見ると足りて見え、
    本番の大きさ（見かけ 41°）では消える。狙いは「墨の 12〜25% が抜けている」。
    """
    ip, wp = ink.getdata(), wear.getdata()
    n = eaten = 0
    for i in range(0, len(ip), 7):          # 7 画素おきで足りる（比率だけ見る）
        if ip[i] > 20:
            n += 1
            if wp[i] < 150:
                eaten += 1
    pct = 100.0 * eaten / max(1, n)
    print(f"wear : 墨の {pct:.1f}% がかすれ（狙い 12〜25%）")


def dissolve_order(size):
    """
    溶ける順。左下から右上へ + 大きなゆらぎ。0 が先に消える。

    ⚠ **ゆらぎを細かくしない。** 細かいと墨が「砂に食われる」ように点々と抜け、
    波が横切っているように見えない（2026-08-12 実測）。削れ際が光る演出も、
    際が 1 本の線にならないと出ない。
    """
    w, h = size
    grad = Image.new("L", (w, h))
    px = grad.load()
    for y in range(h):
        fy = 1.0 - y / h
        for x in range(0, w, 4):
            v = int(255 * max(0.0, min(1.0, 0.66 * (x / w) + 0.34 * fy)))
            for k in range(4):
                if x + k < w:
                    px[x + k, y] = v
    n = (Image.effect_noise((max(2, w // 96), max(2, h // 96)), 70)
         .resize((w, h), Image.BICUBIC).filter(ImageFilter.GaussianBlur(18)))
    return ImageChops.blend(grad, n, 0.26)


def _clamp_byte(v):
    return max(0, min(255, int(round(v))))


def _master_layers():
    """正本の暗い地を落とし、主・朱・添えを別のマスクにする。"""
    src = Image.open(MASTER_SOURCE).convert("RGB")
    w, h = src.size
    corners = [src.getpixel((x, y)) for x, y in
               ((0, 0), (w - 1, 0), (0, h - 1), (w - 1, h - 1))]
    bg = tuple(sum(p[c] for p in corners) / len(corners) for c in range(3))
    bg_luma = sum(bg) / 3.0

    main = Image.new("L", src.size, 0)
    accent = Image.new("L", src.size, 0)
    deco = Image.new("L", src.size, 0)
    transparent = Image.new("RGBA", src.size, (0, 0, 0, 0))
    sp = src.load(); mp = main.load(); ap = accent.load(); dp = deco.load(); tp = transparent.load()

    for y in range(h):
        for x in range(w):
            r, g, b = sp[x, y]
            is_red = r > 58 and r > g * 1.42 and r > b * 1.30
            if is_red:
                alpha = _clamp_byte(255 * (r - bg[0]) / max(1.0, 205 - bg[0]))
                if alpha < 8: alpha = 0
                ap[x, y] = alpha
                tp[x, y] = (176, 43, 49, alpha)
                continue

            luma = (r + g + b) / 3.0
            alpha = _clamp_byte(255 * (luma - bg_luma) / max(1.0, 235 - bg_luma))
            if alpha < 8: alpha = 0
            if not alpha:
                continue

            # 小さな縦ルビと、下側の灰色の一筆だけを奥の添えへ置く。
            ruby = 705 <= x <= 790 and 315 <= y <= 575
            lower_trace = 300 <= x <= 1030 and y >= 545 and luma < 195
            if ruby or lower_trace: dp[x, y] = alpha
            else: mp[x, y] = alpha
            tp[x, y] = (241, 235, 222, alpha)

    combined = ImageChops.lighter(ImageChops.lighter(main, accent), deco)
    bbox = combined.point(lambda v: 255 if v >= 8 else 0).getbbox()
    if bbox is None:
        raise RuntimeError("題字正本から墨を抽出できない")
    pad = 28
    bbox = (max(0, bbox[0] - pad), max(0, bbox[1] - pad),
            min(w, bbox[2] + pad), min(h, bbox[3] + pad))
    return tuple(im.crop(bbox) for im in (main, accent, deco, transparent))


def _fit(im, max_w, max_h):
    scale = min(max_w / im.width, max_h / im.height)
    return im.resize((max(1, int(round(im.width * scale))),
                      max(1, int(round(im.height * scale)))), Image.LANCZOS)


def build_from_master(preview=False):
    """同じ題字を Quest の多層版、Web の透過版、キービジュアルへ配る。"""
    main, accent, deco, transparent = _master_layers()

    fitted = [_fit(im, 1780, 760) for im in (main, accent, deco)]
    ox = (W - fitted[0].width) // 2
    oy = (H - fitted[0].height) // 2
    layers = []
    for im in fitted:
        canvas = Image.new("L", (W, H), 0)
        canvas.paste(im, (ox, oy))
        layers.append(canvas)
    quest = Image.merge("RGBA", (layers[0], layers[1], dissolve_order((W, H)), layers[2]))
    dst = os.path.join(ROOT, "Assets", "Resources", "Title", "MawarimiTitle.png")
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    quest.save(dst)

    if preview:
        preview_path = os.path.join(ROOT, "Assets", "Screenshots", "title", "art-preview.png")
        os.makedirs(os.path.dirname(preview_path), exist_ok=True)
        ground = Image.new("RGB", (W, H), (36, 24, 22))
        ink = Image.new("RGB", (W, H), (238, 234, 226))
        red = Image.new("RGB", (W, H), (196, 26, 34))
        visible = Image.composite(ink, ground, ImageChops.lighter(layers[0], layers[2]))
        visible = Image.composite(red, visible, layers[1])
        visible.save(preview_path)
        print(f"preview: {preview_path}")

    os.makedirs(os.path.dirname(WEB_DEST), exist_ok=True)
    transparent.save(WEB_DEST, format="PNG", optimize=True)

    if os.path.exists(KEYVISUAL_LAYOUT):
        build_keyvisual_layout()
    elif os.path.exists(KEYVISUAL_PLATE):
        plate = Image.open(KEYVISUAL_PLATE).convert("RGBA")
        # 人形群の右上にある暗幕へ置く。顔と左のコピーを避ける。
        logo = _fit(transparent, int(plate.width * 0.43), int(plate.height * 0.35))
        x = int(plate.width * 0.515)
        y = int(plate.height * 0.225)
        plate.alpha_composite(logo, (x, y))
        plate = plate.convert("RGB").resize((1920, 1080), Image.LANCZOS)
        os.makedirs(os.path.dirname(KEYVISUAL_DEST), exist_ok=True)
        plate.save(KEYVISUAL_DEST, quality=95)

    print(f"master: {MASTER_SOURCE}")
    print(f"wrote: {dst}  ({W}x{H} RGBA32 / R=白墨 G=朱墨 B=溶ける順 A=添え)")
    print(f"web  : {WEB_DEST}  ({transparent.width}x{transparent.height} RGBA32)")
    if os.path.exists(KEYVISUAL_PLATE): print(f"key  : {KEYVISUAL_DEST}  (1920x1080)")


def build_keyvisual_layout():
    """文字配置を含む完成原稿から配布サイズを書き出す。"""
    with Image.open(KEYVISUAL_LAYOUT) as source:
        plate = source.convert("RGB").resize((1920, 1080), Image.Resampling.LANCZOS)
    os.makedirs(os.path.dirname(KEYVISUAL_DEST), exist_ok=True)
    plate.save(KEYVISUAL_DEST)
    print(f"keyvisual: {KEYVISUAL_DEST} (1920x1080)")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--preview", action="store_true")
    ap.add_argument("--keyvisual-only", action="store_true", help="完成原稿からキービジュアルだけを書き出す")
    a = ap.parse_args()

    if a.keyvisual_only:
        build_keyvisual_layout()
        return

    if os.path.exists(MASTER_SOURCE):
        build_from_master(a.preview)
        return

    big = (W * SS, H * SS)
    main = Image.new("L", big, 0)          # 廻・リ
    accent = Image.new("L", big, 0)        # 視
    deco = Image.new("L", big, 0)          # 払い・小点
    ruby = Image.new("L", big, 0)          # ルビ・罫

    used = set()
    for ch, cx, cy, hgt, layer in GLYPHS:
        used.add(draw_glyph(main if layer == "white" else accent, ch, cx, cy, hgt))
    draw_sweep(deco, SWEEP, SWEEP_W0, SWEEP_WMID, SWEEP_W1)
    draw_tick(deco, SWEEP_TICK[0], SWEEP_TICK[1], SWEEP_TICK_W)
    draw_ruby(ruby)
    print("font :", ", ".join(sorted(used)))

    main = main.resize((W, H), Image.LANCZOS)
    accent = accent.resize((W, H), Image.LANCZOS)
    deco = deco.resize((W, H), Image.LANCZOS)
    ruby = ruby.resize((W, H), Image.LANCZOS)

    # かすれは白い墨に乗せ、朱はほぼ残す（1 色だけ無傷だと視線がそこへ行く）。
    main_wear = wear_mask((W, H), main, WEAR_WHITE_PCT)
    report_wear(main, main_wear)
    main = ImageChops.multiply(main, main_wear)
    accent = ImageChops.multiply(
        accent, wear_mask((W, H), accent, WEAR_ACCENT_PCT).point(lambda v: 150 + v * 105 // 255))
    # ⚠ **ルビと罫はかすれさせない。** 画が細すぎて、同じ割合で抜くと読めなくなる
    #    （2026-08-12 実測）。払いだけ抜いてから足す。
    deco = ImageChops.lighter(
        ImageChops.multiply(deco, wear_mask((W, H), deco, WEAR_WHITE_PCT)), ruby)

    out = Image.merge("RGBA", (main, accent, dissolve_order((W, H)), deco))

    dst = os.path.join(ROOT, "Assets", "Resources", "Title", "MawarimiTitle.png")
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    out.save(dst)
    print(f"wrote: {dst}  ({W}x{H} RGBA32 / R=白墨 G=朱墨 B=溶ける順 A=形)")

    if a.preview:
        pv = os.path.join(ROOT, "Assets", "Screenshots", "title", "art-preview.png")
        os.makedirs(os.path.dirname(pv), exist_ok=True)
        # シェーダと同じ読み方で見える形にする（焼けたか目で見るためだけ）。
        ground = Image.new("RGB", (W, H), (36, 24, 22))
        ink = Image.new("RGB", (W, H), (238, 234, 226))
        red = Image.new("RGB", (W, H), (196, 26, 34))
        vis = Image.composite(ink, ground, ImageChops.lighter(main, deco))
        vis = Image.composite(red, vis, accent)
        vis.save(pv)
        print(f"preview: {pv}")


if __name__ == "__main__":
    main()
