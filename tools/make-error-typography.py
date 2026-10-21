"""Typeset the takeover warning parts from a real OFL font, never from generated lettering.

v5 (2026-10-21・canon/LEDGER.md 0285「デジタル感が強すぎる」「のっぺりしている」):
14 セグメント表示（DSEG14）とドットゴシック（DotGothic16）をやめ、題字と同じ系統の
明朝（Source Han Serif JP・OFL・リポジトリ同梱）＋かすれで焼く。

焼くのは alpha だけ（白墨のマスク）。色・にじみ・粒・横ずれは Unity の
UnauthorizedAccessFx シェーダが付ける。かすれだけをここで焼くのは、画素ごとの
乾いた筆の抜けを実時間で作ると、字の大きさで抜け方が変わって読めなくなるため。

⚠ **かすれの閾値を定数で置かない。** 抜きたい割合から逆算する（tools/make-title-art.py と同じ作法）。
⚠ **余白は広めに取る。** シェーダのにじみは版の外へは出られないので、余白が狭いと
   quad の縁でにじみが四角く切れる。
⚠ **版を作り直したら、出力の bounds を UnauthorizedAccessEffect.OpaqueUvBounds へ写す。**
   本番の版は Read/Write 無効なので、実行時には不透明の範囲を測れない。

使い方:
    py -3.11 tools/make-error-typography.py
"""
import argparse
import json
import sys
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont

sys.stdout.reconfigure(encoding="utf-8")

SS = 3          # supersample（明朝の細い横画を潰さないため）
PADDING = 26    # 最終解像度での余白（シェーダのにじみ 18 texel が収まる）


def grain(size, cell_w, cell_h, sigma, seed_shift=0):
    w, h = size
    small = Image.effect_noise((max(2, w // max(1, cell_w)), max(2, h // max(1, cell_h))), sigma)
    if seed_shift:
        small = ImageChops.offset(small, seed_shift, seed_shift // 3)
    return small.resize((w, h), Image.BILINEAR)


def wear_mask(ink, target_pct, glyph_h):
    """乾いた筆のかすれ。粒の大きさは字の高さに比例させる（小さい字ほど細かく抜く）。"""
    w, h = ink.size
    k = glyph_h / 240.0
    blot = grain((w, h), int(30 * k) + 2, int(11 * k) + 2, 62)
    flow = grain((w, h), int(4 * k) + 1, int(40 * k) + 2, 50, 211)
    fine = Image.effect_noise((w, h), 26).filter(ImageFilter.GaussianBlur(0.8))
    m = ImageChops.blend(ImageChops.blend(blot, flow, 0.50), fine, 0.24)
    core = ink.filter(ImageFilter.GaussianBlur(max(2.0, 9 * k)))
    keep = core.point(lambda v: min(255, int((v / 255.0) ** 3.2 * 165)))
    m = ImageChops.add(m, keep)
    mp, ip = m.get_flattened_data(), ink.get_flattened_data()
    vals = sorted(mp[i] for i in range(0, len(ip), 3) if ip[i] > 20)
    if not vals or target_pct <= 0:
        return Image.new("L", (w, h), 255)
    t = vals[min(len(vals) - 1, int(len(vals) * target_pct / 100.0))]
    lo, hi = t - 4, t + 4
    hard = m.point(lambda v: 0 if v <= lo else (255 if v >= hi else int(255 * (v - lo) / 8.0)))
    return hard.filter(ImageFilter.GaussianBlur(0.7))


def eaten_pct(ink, wear):
    ip, wp = ink.get_flattened_data(), wear.get_flattened_data()
    n = eaten = 0
    for i in range(0, len(ip), 5):
        if ip[i] > 20:
            n += 1
            if wp[i] < 150:
                eaten += 1
    return 100.0 * eaten / max(1, n)


def typeset(font_path, text, size, tracking_em, weight_px, wear_pct, out_path):
    """size は最終解像度の em。weight_px は最終解像度での太らせ量（明朝の横画を残す程度）。"""
    font = ImageFont.truetype(str(font_path), size * SS)
    boxes = [font.getbbox(char) for char in text]
    top = min(b[1] for b in boxes)
    bottom = max(b[3] for b in boxes)
    advances = [font.getlength(char) for char in text]
    tracking = tracking_em * size * SS
    width = round(sum(advances) + tracking * (len(text) - 1))
    pad = PADDING * SS
    image = Image.new("L", (width + pad * 2, bottom - top + pad * 2), 0)
    draw = ImageDraw.Draw(image)
    x = pad
    for char, advance in zip(text, advances):
        draw.text((round(x), pad - top), char, font=font, fill=255)
        x += advance + tracking
    if weight_px > 0:
        radius = max(1, round(weight_px * SS))
        image = image.filter(ImageFilter.MaxFilter(radius * 2 + 1))
    out_w, out_h = round(image.width / SS), round(image.height / SS)
    ink = image.resize((out_w, out_h), Image.LANCZOS)
    wear = wear_mask(ink, wear_pct, bottom - top)
    alpha = ImageChops.multiply(ink, wear.resize(ink.size))
    rgba = Image.merge("RGBA", (Image.new("L", ink.size, 255),) * 3 + (alpha,))
    temporary = out_path.with_suffix(".tmp.png")
    rgba.save(temporary)
    temporary.replace(out_path)
    bbox = alpha.point(lambda v: 255 if v > 8 else 0).getbbox()
    return {
        "file": out_path.name, "text": text, "font": font_path.name,
        "pixels": list(rgba.size), "opaque": list(bbox) if bbox else None,
        "wear_pct": round(eaten_pct(ink, wear), 1),
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--font", type=Path, default=Path("Assets/Art/Fonts/SourceHanSerifJP-Regular.otf"))
    parser.add_argument("--out", type=Path, default=Path("Assets/Art/Textures/UnauthorizedAccess"))
    args = parser.parse_args()
    if not args.font.is_file():
        raise SystemExit(f"font not found: {args.font}")
    args.out.mkdir(parents=True, exist_ok=True)
    f = args.font
    results = [
        # 見出しは字間を開けた明朝の大文字。題字と同じく横画の細さを残す。
        typeset(f, "WARNING", 220, 0.14, 0.9, 11, args.out / "wordmark-v5.png"),
        typeset(f, "不正アクセス", 96, 0.06, 0.6, 6, args.out / "subtitle-v5.png"),
        typeset(f, "接続元不明", 96, 0.06, 0.6, 6, args.out / "context-v5.png"),
        typeset(f, "遮断を執行", 96, 0.06, 0.6, 5, args.out / "attempt-v5.png"),
        typeset(f, "失敗", 96, 0.10, 0.9, 10, args.out / "failed-v5.png"),
    ]
    print(json.dumps(results, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
