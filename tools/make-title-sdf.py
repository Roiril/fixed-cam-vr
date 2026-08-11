# -*- coding: utf-8 -*-
"""タイトル「廻リ視」の SDF テクスチャを焼く。

出力: Assets/Resources/Title/MawarimiTitle.png（RGBA32・**距離場は alpha に入れる**）

なぜ alpha か: Unity は RGB にだけ sRGB 変換を掛ける。距離場を RGB に入れると
`sRGBTexture` の設定 1 つで値が歪み、**縁の太さが実機でだけ変わる**。alpha は
常に線形なので、この事故が原理的に起きない。

⚠ **帯（band）の座標は C# と対で持つ**。`TitleScreen.cs` の TitleUv / SubUv が
同じ数値を持っていて、片方だけ直すと文字が伸びる／切れる。

使い方:
    py -3.11 tools/make-title-sdf.py
    py -3.11 tools/make-title-sdf.py --preview   # 判定用の見えるプレビューも出す
"""

import argparse
import os
import sys
from PIL import Image, ImageDraw, ImageFont, ImageFilter

sys.stdout.reconfigure(encoding="utf-8")

W, H = 1024, 512

# ---- 帯（テクスチャ内での置き場所・px / 左上原点）------------------------------
# ⚠ この 4 つの矩形は Assets/Scripts/Streaming/TitleScreen.cs の TitleUv / SubUv と同じ。
TITLE_BAND = (32, 24, 992, 392)     # 廻リ視
SUB_BAND = (192, 424, 832, 472)     # MAWARIMI

TITLE_TEXT = "廻リ視"
SUB_TEXT = "MAWARIMI"
SUB_TRACKING = 0.42                 # 字送り（文字幅に対する比）

SPREAD_PX = 24.0                    # 距離場の射程（これを超える距離は 0 / 1 に飽和する）
SS = 4                              # スーパーサンプル倍率（縁の下地を滑らかにする）

# 明朝体。**ゴシックだと「機材の UI」に見える** — この作品は怪異調査なので明朝が正。
# 手元に無い機のために順に落ちる。最後は同梱フォント（SIL OFL）。
TITLE_FONTS = [
    ("C:/Windows/Fonts/yumindb.ttf", 0),      # 游明朝 Demibold
    ("C:/Windows/Fonts/yumin.ttf", 0),        # 游明朝 Regular
    ("C:/Windows/Fonts/BIZ-UDMinchoM.ttc", 0),
    ("C:/Windows/Fonts/msmincho.ttc", 0),
    ("C:/Windows/Fonts/HGRMB.TTC", 0),
    ("Assets/Art/Fonts/SourceHanSansJP-Normal.otf", 0),
]
SUB_FONTS = [
    ("Assets/Art/Fonts/SourceHanSansJP-Normal.otf", 0),
    ("C:/Windows/Fonts/yumin.ttf", 0),
]


def repo_root():
    return os.path.dirname(os.path.abspath(os.path.dirname(__file__) + "/../"))


def pick_font(cands, size, need_chars):
    for path, idx in cands:
        p = path if os.path.isabs(path) else os.path.join(ROOT, path)
        if not os.path.exists(p):
            continue
        try:
            f = ImageFont.truetype(p, size, index=idx)
        except Exception:
            continue
        # 実際にその字が入っているか（豆腐で焼かない）。
        ok = True
        for ch in need_chars:
            m = Image.new("L", (size * 2, size * 2), 0)
            ImageDraw.Draw(m).text((size // 4, size // 4), ch, font=f, fill=255)
            if m.getbbox() is None:
                ok = False
                break
        if ok:
            return f, p
    raise SystemExit(f"フォントが見つからない: {need_chars}")


def draw_fitted(mask, band, text, cands, tracking=0.0):
    """帯の中に text を最大サイズで収める（縦横比は保つ）。戻り値は使ったフォントのパス。"""
    x0, y0, x1, y1 = band
    bw, bh = x1 - x0, y1 - y0
    # スーパーサンプル空間で作ってから縮小する。
    probe = 200
    font, path = pick_font(cands, probe, text)

    def render(size):
        f = ImageFont.truetype(path, size)
        pad = size
        img = Image.new("L", (int(len(text) * size * 1.6) + pad * 2, size * 3), 0)
        d = ImageDraw.Draw(img)
        if tracking <= 0.0:
            d.text((pad, pad), text, font=f, fill=255)
        else:
            cx = pad
            for ch in text:
                d.text((cx, pad), ch, font=f, fill=255)
                cx += d.textlength(ch, font=f) + size * tracking
        bb = img.getbbox()
        return img.crop(bb) if bb else None

    # 二分探索でなく、1 回測って比で決め打ち → 1 回だけ微修正（十分に精度が出る）。
    ink = render(probe)
    if ink is None:
        raise SystemExit(f"字が焼けない: {text}")
    k = min(bw * SS / ink.width, bh * SS / ink.height)
    size = max(8, int(probe * k))
    ink = render(size)
    if ink.width > bw * SS or ink.height > bh * SS:
        size = int(size * min(bw * SS / ink.width, bh * SS / ink.height))
        ink = render(size)

    ox = x0 * SS + (bw * SS - ink.width) // 2
    oy = y0 * SS + (bh * SS - ink.height) // 2
    mask.paste(ink, (int(ox), int(oy)), ink)
    return path


def chamfer_sdf(cov):
    """被覆率マップ（0..1・W×H の list）から符号つき距離場を作る。

    5-7-11 チャンファー（5 で正規化）で内外それぞれの距離を出し、
    **縁だけは被覆率で副画素補正する**（チャンファーは近距離でも 0.5px ずれる）。
    """
    INF = 1e9
    n = W * H
    inside = [c >= 0.5 for c in cov]

    def dt(is_seed):
        # is_seed[i] == True の画素を 0 として距離を伝播させる。
        d = [0.0 if is_seed[i] else INF for i in range(n)]
        # 前方走査
        for y in range(H):
            row = y * W
            for x in range(W):
                i = row + x
                v = d[i]
                if v == 0.0:
                    continue
                if y > 0:
                    if x > 1:
                        t = d[i - W - 2] + 11.0
                        if t < v: v = t
                    if x > 0:
                        t = d[i - W - 1] + 7.0
                        if t < v: v = t
                    t = d[i - W] + 5.0
                    if t < v: v = t
                    if x < W - 1:
                        t = d[i - W + 1] + 7.0
                        if t < v: v = t
                    if x < W - 2:
                        t = d[i - W + 2] + 11.0
                        if t < v: v = t
                if y > 1:
                    if x > 0:
                        t = d[i - 2 * W - 1] + 11.0
                        if t < v: v = t
                    if x < W - 1:
                        t = d[i - 2 * W + 1] + 11.0
                        if t < v: v = t
                if x > 0:
                    t = d[i - 1] + 5.0
                    if t < v: v = t
                d[i] = v
        # 後方走査
        for y in range(H - 1, -1, -1):
            row = y * W
            for x in range(W - 1, -1, -1):
                i = row + x
                v = d[i]
                if v == 0.0:
                    continue
                if y < H - 1:
                    if x < W - 2:
                        t = d[i + W + 2] + 11.0
                        if t < v: v = t
                    if x < W - 1:
                        t = d[i + W + 1] + 7.0
                        if t < v: v = t
                    t = d[i + W] + 5.0
                    if t < v: v = t
                    if x > 0:
                        t = d[i + W - 1] + 7.0
                        if t < v: v = t
                    if x > 1:
                        t = d[i + W - 2] + 11.0
                        if t < v: v = t
                if y < H - 2:
                    if x < W - 1:
                        t = d[i + 2 * W + 1] + 11.0
                        if t < v: v = t
                    if x > 0:
                        t = d[i + 2 * W - 1] + 11.0
                        if t < v: v = t
                if x < W - 1:
                    t = d[i + 1] + 5.0
                    if t < v: v = t
                d[i] = v
        return d

    d_out = dt(inside)                       # 外側の画素 → 一番近い ink までの距離
    d_in = dt([not b for b in inside])       # 内側の画素 → 一番近い背景までの距離

    out = bytearray(n)
    for i in range(n):
        if inside[i]:
            s = d_in[i] / 5.0
            if s <= 1.5:                     # 縁の 1〜2 px は被覆率が真値に近い
                s = cov[i] - 0.5
        else:
            s = -d_out[i] / 5.0
            if s >= -1.5:
                s = cov[i] - 0.5
        v = 0.5 + s / (2.0 * SPREAD_PX)
        out[i] = 0 if v <= 0 else (255 if v >= 1 else int(v * 255.0 + 0.5))
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--preview", action="store_true")
    a = ap.parse_args()

    big = Image.new("L", (W * SS, H * SS), 0)
    f1 = draw_fitted(big, TITLE_BAND, TITLE_TEXT, TITLE_FONTS)
    f2 = draw_fitted(big, SUB_BAND, SUB_TEXT, SUB_FONTS, tracking=SUB_TRACKING)
    print(f"title font : {f1}")
    print(f"sub   font : {f2}")

    small = big.resize((W, H), Image.LANCZOS)
    cov = [p / 255.0 for p in small.getdata()]
    sdf = chamfer_sdf(cov)

    # ⚠ **上下を反さない。** PNG の 1 行目は Unity では v=1（上）に入るので、
    # 「左上原点の px」→「v = 1 - y/H」で読めば一致する（TitleScreen.AddQuad がそう読む）。
    # ここで反すと読む側の変換と二重になり、**字が上下逆さまに出る**（2026-08-12 に実測）。
    a8 = Image.frombytes("L", (W, H), bytes(sdf))
    white = Image.new("L", (W, H), 255)
    rgba = Image.merge("RGBA", (white, white, white, a8))

    out = os.path.join(ROOT, "Assets", "Resources", "Title", "MawarimiTitle.png")
    os.makedirs(os.path.dirname(out), exist_ok=True)
    rgba.save(out)
    print(f"wrote      : {out}  ({W}x{H} RGBA32 / 距離場は alpha / spread {SPREAD_PX}px)")

    if a.preview:
        pv = os.path.join(ROOT, "Assets", "Screenshots", "title", "sdf-preview.png")
        os.makedirs(os.path.dirname(pv), exist_ok=True)
        # 塗り + 縁を、シェーダと同じ読み方で可視化する（焼けたか目で見るためだけ）。
        px = a8.load()
        vis = Image.new("RGB", (W, H))
        vp = vis.load()
        for y in range(H):
            for x in range(W):
                s = px[x, y] / 255.0
                fill = 1.0 if s > 0.5 else 0.0
                edge = max(0.0, 1.0 - abs(s - 0.5) * 2.0 * SPREAD_PX / 2.0)
                vp[x, y] = (int(30 * fill + 220 * edge), int(34 * fill + 235 * edge),
                            int(30 * fill + 255 * edge))
        vis.save(pv)
        print(f"preview    : {pv}")


ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

if __name__ == "__main__":
    main()
