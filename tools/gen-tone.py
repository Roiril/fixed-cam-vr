#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""生成の入出力でトーンを往復させる（廻リ視）。

**なぜ要るか**（`canon/LEDGER.md` 0050）:
種フレームは明るい部屋のまま生成へ渡していて、暗くするのは実機の post（合成のいちばん後ろ）だった。
つまり **生成モデルは暗い画を一度も描いていない**。暗所の人形は暗いだけでなく陰影の付き方が違う
（光源の向きが読める・輪郭が闇へ沈む・顔の一部だけが光を拾う）ので、後段の露出調整では作れない。

    種フレーム → dim → 生成 → undim → 素材として保存 → 実機で post
                （ここ）        （ここ）

**なぜ暗いまま保存しないか**: cue の素材は「そのカメラが撮ったならこう写る生映像」の位置に入る。
暗いまま置くと ①実機で post が二重に掛かる ②半分マスクで境目に段差が出る（画面の左右で撮像特性が
違う ＝ 装置が 2 台あることになる）③`LEDGER` 0020「人形を合成したあとに他の全ての画像処理を」と
`rules/streaming.md`「合成はポスト FX の前。浴びないと必ず浮く」に反する。

式と順序は `tools/web-compositor/shaders.js` の FS_POST ＝ Unity の `ScreenComposite.shader` に合わせる
（ヴィネット → 露出 → 色温度 → 色かぶり → コントラスト → 黒浮き → 彩度）。undim はその逆順・逆演算。

⚠ **clamp した所は戻らない。** 暗くしすぎると戻したとき縞や色ムラが出る。`check` が
「潰れた画素の割合」と「往復の誤差」を測るので、その 2 つを見て `--scale` を決める。

使い方:
    py -3.11 tools/gen-tone.py dim   種.jpg 暗い種.png [--scale 0.5] [--cam A]
    py -3.11 tools/gen-tone.py undim 生成物.png 素材.png [--scale 0.5] [--cam A]
    py -3.11 tools/gen-tone.py check 種.jpg [--scale 0.5]      # 往復の誤差と潰れを測る
    py -3.11 tools/gen-tone.py sweep 種.jpg 並べた.png         # scale を振って 1 枚に並べる
"""
from __future__ import annotations

import argparse
import json
import os
import sys

from PIL import Image, ImageDraw, ImageFont, ImageMath

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SHOW_JSON = os.path.join(REPO, "tools", "web-compositor", "show.json")

# show.json が無い現場でも動く既定（2026-08-15 時点の global post）
FALLBACK_POST = dict(exposure=-1.05, contrast=1.12, saturation=0.52,
                     temperature=0.48, tint=0.0, lift=0.02, vignette=0.38)

# 順投影で 0 / 255 に張り付いた画素は情報が失われている。この割合が大きいと undim で戻らない。
CLIP_LO, CLIP_HI = 1, 254


# --------------------------------------------------------------------------- post を読む

def load_post(cam_id: str | None = None, path: str = SHOW_JSON) -> dict:
    """show.json の post を読む。カメラ別（`cameras[].post`）があればそれを優先する。"""
    post = dict(FALLBACK_POST)
    try:
        with open(path, encoding="utf-8") as f:
            show = json.load(f)
    except (OSError, ValueError) as e:
        print(f"⚠ show.json を読めないので既定値を使う: {e}", file=sys.stderr)
        return post

    g = show.get("post") or {}
    for k in post:
        if isinstance(g.get(k), (int, float)):
            post[k] = float(g[k])

    if cam_id:
        for c in show.get("cameras", []):
            if str(c.get("id", "")).upper() != cam_id.upper():
                continue
            cp = c.get("post") or {}
            for k in post:
                if isinstance(cp.get(k), (int, float)):
                    post[k] = float(cp[k])
            break
    return post


def scaled(post: dict, scale: float) -> dict:
    """post の効き目を按分する。1.0 = 実機と同じ、0.0 = 素通し。"""
    s = max(0.0, min(1.0, scale))
    return dict(
        exposure=post["exposure"] * s,
        temperature=post["temperature"] * s,
        tint=post["tint"] * s,
        contrast=1.0 + (post["contrast"] - 1.0) * s,
        lift=post["lift"] * s,
        saturation=1.0 + (post["saturation"] - 1.0) * s,
        vignette=post["vignette"] * s,
    )


# --------------------------------------------------------------------------- 各段

def _vignette_map(w: int, h: int, amount: float) -> Image.Image:
    """FS_POST と同じ r^4 の減光係数（1.0 が減光なし）を "F" 画像で返す。

    dir = uv - 0.5 / r2 = clamp(dot(dir,dir)*4, 0, 1) / col *= 1 - v*0.58*r2^2
    行ごとに作るので画素ループは h 回で済む。
    """
    m = Image.new("F", (w, h))
    px = m.load()
    k = amount * 0.58
    for y in range(h):
        dy = (y + 0.5) / h - 0.5
        dy2 = dy * dy
        for x in range(w):
            dx = (x + 0.5) / w - 0.5
            r2 = (dx * dx + dy2) * 4.0
            if r2 > 1.0:
                r2 = 1.0
            px[x, y] = 1.0 - k * r2 * r2
    return m


def _apply_map(img: Image.Image, vmap: Image.Image, invert: bool) -> Image.Image:
    """減光係数を掛ける / 割る。ImageMath なので画素ループは無い。

    ⚠ Pillow 10.3 で `ImageMath.eval` が非推奨、11 で削除された（この機は 12.3）。
    `lambda_eval` は「dict を受け取る関数」を渡す形。
    """
    out = []
    for ch in img.split():
        f = ImageMath.lambda_eval(
            (lambda a: a["v"] / a["m"]) if invert else (lambda a: a["v"] * a["m"]),
            v=ch.convert("F"), m=vmap)
        out.append(f)
    return _merge_f(out)


def _merge_f(channels) -> Image.Image:
    """"F" の 3 チャンネルを 0..255 にクランプして "RGB" へ畳む。"""
    # ⚠ min / max は第 1 引数が画像でないと通らない（`imagemath_min(self, other)`）。
    conv = [
        ImageMath.lambda_eval(
            lambda a: a["convert"](a["max"](a["min"](a["v"] + 0.5, 255.0), 0.0), "L"), v=c)
        for c in channels
    ]
    return Image.merge("RGB", conv)


def _tone_lut(p: dict, invert: bool) -> list[int]:
    """位置に依らない段（露出→色温度→色かぶり→コントラスト→黒浮き）を 3 本の LUT へ畳む。

    順: y = ((x*k - 0.5)*c + 0.5)*(1-l) + l
    逆: x = (((y - l)/(1-l) - 0.5)/c + 0.5) / k
    """
    gain = 2.0 ** p["exposure"]
    t, tint = p["temperature"], p["tint"]
    ks = (
        gain * (1.0 + 0.25 * t) * (1.0 - 0.12 * tint),
        gain * (1.0 + 0.25 * tint),
        gain * (1.0 - 0.25 * t) * (1.0 - 0.12 * tint),
    )
    c, l = p["contrast"], p["lift"]
    lut: list[int] = []
    for k in ks:
        k = k if abs(k) > 1e-6 else 1e-6
        c_ = c if abs(c) > 1e-6 else 1e-6
        for i in range(256):
            v = i / 255.0
            if invert:
                v = ((v - l) / max(1e-6, 1.0 - l) - 0.5) / c_ + 0.5
                v /= k
            else:
                v = ((v * k - 0.5) * c_ + 0.5) * (1.0 - l) + l
            lut.append(max(0, min(255, int(v * 255.0 + 0.5))))
    return lut


def _saturate(img: Image.Image, s: float) -> Image.Image:
    """luma への線形補間（FS_POST と同じ Rec.709 重み）。s<1 で退色、1/s で戻る。"""
    if abs(s - 1.0) < 1e-6:
        return img
    r, g, b = (ch.convert("F") for ch in img.split())
    luma = ImageMath.lambda_eval(
        lambda a: 0.2126 * a["r"] + 0.7152 * a["g"] + 0.0722 * a["b"], r=r, g=g, b=b)
    out = [
        ImageMath.lambda_eval(lambda a: a["l"] + (a["c"] - a["l"]) * a["s"], l=luma, c=ch, s=s)
        for ch in (r, g, b)
    ]
    return _merge_f(out)


# --------------------------------------------------------------------------- 往復

def dim(img: Image.Image, post: dict, scale: float) -> Image.Image:
    """種フレームへ post を掛ける（生成モデルに暗所を見せるため）。"""
    p = scaled(post, scale)
    out = img.convert("RGB")
    if p["vignette"] > 1e-4:
        out = _apply_map(out, _vignette_map(*out.size, p["vignette"]), invert=False)
    out = out.point(_tone_lut(p, invert=False))
    return _saturate(out, p["saturation"])


def undim(img: Image.Image, post: dict, scale: float) -> Image.Image:
    """生成物から post を抜く（cue の素材 ＝ 生映像の位置へ戻す）。dim の逆順・逆演算。"""
    p = scaled(post, scale)
    out = img.convert("RGB")
    out = _saturate(out, 1.0 / max(1e-6, p["saturation"]))
    out = out.point(_tone_lut(p, invert=True))
    if p["vignette"] > 1e-4:
        out = _apply_map(out, _vignette_map(*out.size, p["vignette"]), invert=True)
    return out


# --------------------------------------------------------------------------- 測る

def _stats(img: Image.Image) -> dict:
    """平均輝度と、0 / 255 に張り付いた画素の割合。"""
    rgb = img.convert("RGB")
    h = rgb.convert("L").histogram()
    n = sum(h)
    mean = sum(i * c for i, c in enumerate(h)) / max(1, n)
    lo = sum(h[:CLIP_LO + 1]) / max(1, n)
    hi = sum(h[CLIP_HI:]) / max(1, n)
    return dict(mean=mean, clip_lo=lo * 100.0, clip_hi=hi * 100.0)


def _diff(a: Image.Image, b: Image.Image) -> dict:
    """往復の誤差（RGB の絶対差）。"""
    from PIL import ImageChops
    d = ImageChops.difference(a.convert("RGB"), b.convert("RGB"))
    h = d.convert("L").histogram()
    n = sum(h)
    mean = sum(i * c for i, c in enumerate(h)) / max(1, n)
    worst = max(i for i, c in enumerate(h) if c)
    over8 = sum(h[9:]) / max(1, n) * 100.0
    return dict(mean=mean, worst=worst, over8=over8)


def check(path: str, post: dict, scale: float) -> None:
    src = Image.open(path).convert("RGB")
    d = dim(src, post, scale)
    back = undim(d, post, scale)
    s0, s1 = _stats(src), _stats(d)
    e = _diff(src, back)
    print(f"scale={scale:.2f}")
    print(f"  種フレーム   平均輝度 {s0['mean']:6.1f}")
    print(f"  暗くした画   平均輝度 {s1['mean']:6.1f}  "
          f"潰れ 暗部 {s1['clip_lo']:.2f}% / 明部 {s1['clip_hi']:.2f}%")
    print(f"  往復の誤差   平均 {e['mean']:.2f} / 最大 {e['worst']} / "
          f"8 を超える画素 {e['over8']:.2f}%")


def sweep(path: str, out_path: str, post: dict, scales) -> None:
    """scale を振って 1 枚に並べる（目で決めるための索引）。"""
    src = Image.open(path).convert("RGB")
    panels = [("種フレーム", src, None)]
    for s in scales:
        d = dim(src, post, s)
        panels.append((f"scale {s:.2f}", d, s))

    w, h = src.size
    pad, label = 8, 40
    sheet = Image.new("RGB", (w * len(panels) + pad * (len(panels) + 1), h + label + pad * 2),
                      (18, 18, 18))
    draw = ImageDraw.Draw(sheet)
    try:
        font = ImageFont.truetype("C:/Windows/Fonts/meiryo.ttc", 13)
    except OSError:
        font = ImageFont.load_default()

    for i, (name, img, s) in enumerate(panels):
        x = pad + i * (w + pad)
        sheet.paste(img, (x, pad + label))
        st = _stats(img)
        line2 = ""
        if s is not None:
            back = undim(img, post, s)
            e = _diff(src, back)
            line2 = (f"潰れ {st['clip_lo']:.1f}%  往復の誤差 平均 {e['mean']:.1f} / "
                     f"最大 {e['worst']}")
        draw.text((x, pad), f"{name}   平均輝度 {st['mean']:.1f}", fill=(230, 220, 200), font=font)
        if line2:
            draw.text((x, pad + 18), line2, fill=(170, 165, 155), font=font)
        print(f"{name}: 平均輝度 {st['mean']:.1f}  {line2}")

    sheet.save(out_path)
    print("saved", out_path)


# --------------------------------------------------------------------------- CLI

def main() -> int:
    ap = argparse.ArgumentParser(description="生成の入出力でトーンを往復させる")
    ap.add_argument("mode", choices=["dim", "undim", "check", "sweep"])
    ap.add_argument("src")
    ap.add_argument("dst", nargs="?")
    ap.add_argument("--scale", type=float, default=0.5,
                    help="post の効き目（1.0 = 実機と同じ・既定 0.5）")
    ap.add_argument("--cam", default=None, help="カメラ id（cameras[].post を使う）")
    ap.add_argument("--show", default=SHOW_JSON)
    args = ap.parse_args()

    sys.stdout.reconfigure(encoding="utf-8")
    post = load_post(args.cam, args.show)

    if args.mode == "check":
        check(args.src, post, args.scale)
        return 0
    if args.mode == "sweep":
        dst = args.dst or os.path.splitext(args.src)[0] + "_sweep.png"
        sweep(args.src, dst, post, (0.35, 0.5, 0.7, 1.0))
        return 0

    if not args.dst:
        print("dst が要る", file=sys.stderr)
        return 2
    img = Image.open(args.src)
    out = (dim if args.mode == "dim" else undim)(img, post, args.scale)
    out.save(args.dst)
    st = _stats(out)
    print(f"{args.mode}: {args.src} → {args.dst}  平均輝度 {st['mean']:.1f}  "
          f"潰れ 暗部 {st['clip_lo']:.2f}% / 明部 {st['clip_hi']:.2f}%")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
