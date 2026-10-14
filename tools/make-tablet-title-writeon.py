#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""タブレット題字「廻リ視」の書き出しの図を焼く（2026-10-14）。

    py -3.11 tools/make-tablet-title-writeon.py            # 焼く
    py -3.11 tools/make-tablet-title-writeon.py --debug D  # 確認用の色分け画像を D（ディレクトリ）へ

Quest 版の書き出しの図（Assets/Resources/Title/MawarimiTitleWrite.png。tools/make-title-writeon.py が焼く）を、
タブレット用の題字画像 tablet/app/src/main/assets/web/asset/title-logo-v2.png へ位置合わせして写す。
出力は title-logo-write-v1.png（8bit グレー。画素 = 筆が届く時刻 0..254。墨の無い所は 255）。

手順:
  1. 墨の領域（Quest: max(R,G,A) / タブレット: 画像の alpha）を作る。
  2. 一様な拡大率 s と平行移動 (ox, oy) を、粗い所から細かい所へ総当たりして IoU が最大になる所を探す。
     （位置は FFT の相互相関で全オフセットを一度に測る。拡大率だけ刻みで回す。）
     IoU が 0.85 に届かなければ exit 1（別の絵で代用しない）。
  3. 層（R / G / A）ごとに、その層の墨がある所の時刻を写し、層をまたいだら最小を取る。
  4. 時刻の無い題字の墨は最寄りの時刻で埋め、墨の外 8px まで時刻を広げる（縁のアンチエイリアスと滲みの帯のため）。

⚠ RGBA のまま縮めると PIL は alpha で前乗算する。チャンネルごとに縮める。
"""
from __future__ import annotations

import argparse
import math
import os
import sys

import numpy as np
from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LOGO = os.path.join(ROOT, "tablet", "app", "src", "main", "assets", "web", "asset", "title-logo-v2.png")
QART = os.path.join(ROOT, "Assets", "Resources", "Title", "MawarimiTitle.png")
QWRITE = os.path.join(ROOT, "Assets", "Resources", "Title", "MawarimiTitleWrite.png")
OUT = os.path.join(ROOT, "tablet", "app", "src", "main", "assets", "web", "asset", "title-logo-write-v1.png")

IOU_MIN = 0.85
MASK_T = 40 / 255.0     # 墨の領域とみなす下限
INK_MIN = 0.04          # 層の墨があるとみなす下限（make-title-writeon.py の INK_MIN と同じ）
DILATE_PX = 8           # 墨の外へ時刻を広げる距離
LAYERS = (("R", 0), ("G", 1), ("A", 3))

N8 = [(-1, -1), (-1, 0), (-1, 1), (0, -1), (0, 1), (1, -1), (1, 0), (1, 1)]


def load_logo() -> tuple[np.ndarray, np.ndarray]:
    im = Image.open(LOGO)
    if im.mode != "RGBA":
        im = im.convert("RGBA")
    a = np.asarray(im.split()[3]).astype(np.float32) / 255.0
    if a.min() == a.max():
        lum = np.asarray(im.convert("L")).astype(np.float32) / 255.0
        a = lum
        print("alpha が一様なので輝度を墨にする")
    return a, np.asarray(im.split()[3])


def load_quest() -> tuple[np.ndarray, dict[str, np.ndarray]]:
    im = Image.open(QART)
    if im.mode != "RGBA":
        sys.exit(f"版が RGBA ではない: {im.mode}")
    ch = [np.asarray(c).astype(np.float32) / 255.0 for c in im.split()]
    mask = np.maximum(np.maximum(ch[0], ch[1]), ch[3])
    # 層ごとの墨は 1024（書き出しの図と同じ解像度）へチャンネルごとに縮める
    half = {k: np.asarray(c.resize((1024, 512), Image.LANCZOS)).astype(np.float32) / 255.0
            for (k, i), c in zip(LAYERS, [im.split()[0], im.split()[1], im.split()[3]])}
    return mask, half


def resize_f(a: np.ndarray, w: int, h: int) -> np.ndarray:
    return np.asarray(Image.fromarray(a.astype(np.float32), "F").resize((w, h), Image.BILINEAR))


def xcorr_best(q: np.ndarray, l: np.ndarray) -> tuple[float, int, int]:
    """q の上に l を重ねる全オフセットの IoU（連続値版）のうち最大を返す。(iou, oy, ox)"""
    hq, wq = q.shape
    h, w = l.shape
    if h > hq or w > wq:
        return -1.0, 0, 0
    pad = np.zeros_like(q)
    pad[:h, :w] = l
    inter = np.fft.irfft2(np.fft.rfft2(q) * np.conj(np.fft.rfft2(pad)), s=q.shape)
    inter = inter[: hq - h + 1, : wq - w + 1]
    union = q.sum() + l.sum() - inter
    iou = inter / np.maximum(union, 1e-6)
    i = int(np.argmax(iou))
    oy, ox = divmod(i, iou.shape[1])
    return float(iou[oy, ox]), oy, ox


def downscale(a: np.ndarray, k: float) -> np.ndarray:
    return resize_f(a, round(a.shape[1] * k), round(a.shape[0] * k))


def search(qm: np.ndarray, lm: np.ndarray) -> tuple[float, int, int, float, int, int]:
    """戻り値: (s, ox, oy, iou, w', h')。座標は Quest 2048 の画素。"""
    qb = qm > MASK_T
    lb = lm > MASK_T
    qy, qx = np.nonzero(qb)
    ly, lx = np.nonzero(lb)
    s0 = (qx.max() - qx.min() + 1) / (lx.max() - lx.min() + 1)
    print(f"墨の外接矩形から見た拡大率の目安 {s0:.4f}")
    H, W = lm.shape

    best = None
    # 粗 → 細。k は Quest 2048 に対する作業解像度。
    stages = [(0.25, s0 * 0.80, s0 * 1.20, 0.005),
              (0.5, None, None, 0.002),
              (1.0, None, None, 0.0005)]
    s_lo, s_hi = s0 * 0.80, s0 * 1.20
    for k, _, _, step in stages:
        qk = downscale(qm, k)
        n = max(1, int(round((s_hi - s_lo) / step)) + 1)
        best = None
        for s in np.linspace(s_lo, s_hi, n):
            w, h = round(W * s * k), round(H * s * k)
            lk = resize_f(lm, w, h)
            iou, oy, ox = xcorr_best(qk, lk)
            if best is None or iou > best[0]:
                best = (iou, s, ox / k, oy / k)
        print(f"  k={k}: s={best[1]:.4f} ox={best[2]:.1f} oy={best[3]:.1f} 連続 IoU={best[0]:.4f}")
        s = best[1]
        span = step * 6
        s_lo, s_hi = s - span, s + span
    # 最終: 2048 の 2 値で IoU を測り直し、近傍を ±1px で詰める
    iou, s, ox, oy = best
    w, h = round(W * s), round(H * s)
    lk = resize_f(lm, w, h) > MASK_T
    best_b = (-1.0, ox, oy)
    for dy in range(-2, 3):
        for dx in range(-2, 3):
            x0, y0 = int(round(ox)) + dx, int(round(oy)) + dy
            if x0 < 0 or y0 < 0 or x0 + w > qb.shape[1] or y0 + h > qb.shape[0]:
                continue
            region = qb[y0:y0 + h, x0:x0 + w]
            inter = np.logical_and(region, lk).sum()
            union = qb.sum() + lk.sum() - inter
            v = inter / union
            if v > best_b[0]:
                best_b = (v, x0, y0)
    return s, best_b[1], best_b[2], best_b[0], w, h


def sample_bilinear(arr: np.ndarray, X: np.ndarray, Y: np.ndarray) -> np.ndarray:
    h, w = arr.shape
    X = np.clip(X, 0, w - 1)
    Y = np.clip(Y, 0, h - 1)
    x0 = np.floor(X).astype(np.int32)
    y0 = np.floor(Y).astype(np.int32)
    x1 = np.minimum(x0 + 1, w - 1)
    y1 = np.minimum(y0 + 1, h - 1)
    fx = (X - x0).astype(np.float32)
    fy = (Y - y0).astype(np.float32)
    return (arr[y0, x0] * (1 - fx) * (1 - fy) + arr[y0, x1] * fx * (1 - fy)
            + arr[y1, x0] * (1 - fx) * fy + arr[y1, x1] * fx * fy)


def neighbor_min(t: np.ndarray, known: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    """各画素について、既知の 8 近傍の最小値と、既知の近傍があるか。"""
    h, w = t.shape
    big = np.float32(1e9)
    tp = np.full((h + 2, w + 2), big, np.float32)
    tp[1:-1, 1:-1] = np.where(known, t, big)
    out = np.full((h, w), big, np.float32)
    for dy, dx in N8:
        out = np.minimum(out, tp[1 + dy:1 + dy + h, 1 + dx:1 + dx + w])
    return out, out < big


def spread(t: np.ndarray, known: np.ndarray, allowed: np.ndarray, iters: int) -> np.ndarray:
    known = known.copy()
    for _ in range(iters):
        nm, has = neighbor_min(t, known)
        new = has & ~known & allowed
        if not new.any():
            break
        t[new] = nm[new]
        known |= new
    return known


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser()
    ap.add_argument("--debug", help="確認用の画像を書くディレクトリ")
    args = ap.parse_args()

    for p in (LOGO, QART, QWRITE):
        if not os.path.exists(p):
            sys.exit(f"無い: {p}")

    logo_a, logo_a8 = load_logo()
    qmask, qlayers = load_quest()
    H, W = logo_a.shape

    s, ox, oy, iou, w, h = search(qmask, logo_a)
    sx, sy = w / W, h / H
    print(f"最良の変換: 拡大率 s={s:.4f}（x {sx:.4f} / y {sy:.4f}） 平行移動 ox={ox} oy={oy}（Quest 2048 の画素）  IoU={iou:.4f}")
    if iou < IOU_MIN:
        print(f"IoU が {IOU_MIN} に届かない。位置合わせに失敗した（代用はしない）")
        return 1

    # 書き出しの図（1024×512）
    wm = np.asarray(Image.open(QWRITE))
    if wm.shape[:2] != (512, 1024):
        sys.exit(f"書き出しの図の大きさが想定と違う: {wm.shape}")

    # ロゴ画素の中心 → Quest 2048 → 書き出しの図 1024
    xs = (np.arange(W, dtype=np.float32) + 0.5) * sx - 0.5 + ox
    ys = (np.arange(H, dtype=np.float32) + 0.5) * sy - 0.5 + oy
    X = ((xs + 0.5) / 2 - 0.5)[None, :].repeat(H, 0)
    Y = ((ys + 0.5) / 2 - 0.5)[:, None].repeat(W, 1)

    INF = np.float32(1e9)
    T = np.full((H, W), INF, np.float32)
    for name, ci in LAYERS:
        plane = wm[..., ci].astype(np.float32)
        valid = (plane < 255).astype(np.float32)
        num = sample_bilinear(plane / 255.0 * valid, X, Y)
        den = sample_bilinear(valid, X, Y)
        tk = np.where(den > 1e-3, num / np.maximum(den, 1e-3), INF)
        ink = sample_bilinear(qlayers[name], X, Y) > INK_MIN
        cand = np.where(ink & (den > 1e-3), tk, INF)
        T = np.minimum(T, cand)
        print(f"層 {name}: 時刻を写せた画素 {int((cand < INF).sum())}")

    ink_logo = logo_a > (4 / 255.0)
    known = T < INF
    # 墨の中で時刻の無い画素 → 最寄りの時刻（8 近傍の層ごとに外へ 1 画素ずつ）
    miss0 = int((ink_logo & ~known).sum())
    known = spread(T, known, ink_logo, 4000)
    miss1 = int((ink_logo & ~known).sum())
    print(f"墨の中で時刻が無かった画素 {miss0} → 埋めた後 {miss1}")
    if miss1:
        # 孤立した墨（本体から離れた点）は、墨の外も通って最寄りの時刻を借りる
        Tfar = T.copy()
        kfar = spread(Tfar, known, np.ones((H, W), bool), 400)
        iso = ink_logo & ~known & kfar
        T[iso] = Tfar[iso]
        known |= iso
        print(f"孤立した墨 {int(iso.sum())} 画素は墨の外を通って最寄りから借りた（残り {int((ink_logo & ~known).sum())}）")
        assert not (ink_logo & ~known).any(), "時刻の無い墨が残った"
    # 墨の外へ広げる
    known = spread(T, known, np.ones((H, W), bool), DILATE_PX)

    out = np.full((H, W), 255, np.uint8)
    out[known] = np.clip(np.round(T[known] * 255.0), 0, 254).astype(np.uint8)
    tt = out[ink_logo & (out < 255)] / 255.0
    print(f"時刻の範囲（墨の中）: {tt.min():.3f} .. {tt.max():.3f}  墨の画素 {int(ink_logo.sum())}")

    tmp = OUT + ".tmp.png"
    Image.fromarray(out, "L").save(tmp)
    os.replace(tmp, OUT)
    print("wrote", os.path.relpath(OUT, ROOT))

    if args.debug:
        os.makedirs(args.debug, exist_ok=True)
        import colorsys
        t01 = out.astype(np.float32) / 255.0
        hue = np.clip(t01, 0, 1) * 0.83   # 赤 → 紫
        # hsv → rgb をベクトルで
        hh = hue * 6.0
        i = np.floor(hh).astype(np.int32) % 6
        f = hh - np.floor(hh)
        v = np.ones_like(hh)
        p_ = np.zeros_like(hh)
        q_ = 1 - f
        t_ = f
        r = np.choose(i, [v, q_, p_, p_, t_, v])
        g = np.choose(i, [t_, v, v, q_, p_, p_])
        b = np.choose(i, [p_, p_, t_, v, v, q_])
        al = np.clip(logo_a, 0, 1)
        dbg = np.stack([r, g, b], -1) * al[..., None] + 0.12 * (1 - al[..., None])
        Image.fromarray((np.clip(dbg, 0, 1) * 255).astype(np.uint8)).save(os.path.join(args.debug, "time_hue.png"))
        # 時刻のしきい値ごとの出現（進行の確認）
        base = np.asarray(Image.open(LOGO).convert("RGBA")).astype(np.float32) / 255.0
        panels = []
        for p in (0.1, 0.25, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 1.0):
            age = p * 1.035 - out.astype(np.float32) / 255.0
            a = np.clip(age / 0.035, 0, 1) * base[..., 3]
            comp = base[..., :3] * a[..., None] + 0.12 * (1 - a[..., None])
            panels.append(Image.fromarray((np.clip(comp, 0, 1) * 255).astype(np.uint8)).resize((W // 3, H // 3), Image.LANCZOS))
        sheet = Image.new("RGB", (3 * (W // 3), 3 * (H // 3)))
        for n, im in enumerate(panels):
            sheet.paste(im, ((n % 3) * (W // 3), (n // 3) * (H // 3)))
        sheet.save(os.path.join(args.debug, "time_steps.png"))
        print("debug ->", args.debug)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
