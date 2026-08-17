# -*- coding: utf-8 -*-
"""種と生成物を突き合わせて測る。**合否は決めない**（それは judge.py）。

ここにあるのは「CG っぽい」を画素へ分解したもの。どれも**種との比**で測るので、
部屋が変わっても線を引き直さなくてよい ＝ 場所に依らない。

  背景を触ったか   bg_frac / tone_mean / tone_sd / keep_diff / seam
  足したか         added_pct / parts
  馴染んでいるか   grain（粒があるか）/ edge（縁が刃物でないか）/ dark（暗部が沈むか）
  乗っているか     ground（接地の影）/ persp（手前が大きい）/ see_through（下地が透ける）
"""
from __future__ import annotations

from collections import deque

import numpy as np
from PIL import Image

BG_THR = 10.0       # これ未満の差は「触っていない」とみなす
ADD_THR = 24.0      # これを超えた差を「足された画素」とみなす
MIN_PART = 60       # これ未満の塊は数えない（画素）


# --------------------------------------------------------------------- 下ごしらえ

def gray(im: Image.Image) -> np.ndarray:
    return np.asarray(im.convert("L"), dtype=np.float64)


def grad_mag(g: np.ndarray) -> np.ndarray:
    gx = np.zeros_like(g); gy = np.zeros_like(g)
    gx[:, 1:-1] = (g[:, 2:] - g[:, :-2]) * 0.5
    gy[1:-1, :] = (g[2:, :] - g[:-2, :]) * 0.5
    return np.hypot(gx, gy)


def _box3(g: np.ndarray) -> np.ndarray:
    """3x3 平均（端は複製）。numpy だけで畳む。"""
    p = np.pad(g, 1, mode="edge")
    return sum(p[y:y + g.shape[0], x:x + g.shape[1]]
               for y in range(3) for x in range(3)) / 9.0


def highpass(g: np.ndarray) -> np.ndarray:
    """粒（センサのノイズと圧縮の粗）の量。3x3 平均との差の絶対値。"""
    return np.abs(g - _box3(g))


def _range5(g: np.ndarray) -> np.ndarray:
    """5x5 の最大 - 最小（その場の明暗差）。"""
    p = np.pad(g, 2, mode="edge")
    st = np.stack([p[y:y + g.shape[0], x:x + g.shape[1]]
                   for y in range(5) for x in range(5)])
    return st.max(axis=0) - st.min(axis=0)


def sharpness(g: np.ndarray) -> np.ndarray:
    """縁の鋭さ（明暗差で割った勾配）。1 画素で変われば約 0.5、緩いほど小さい。"""
    return grad_mag(g) / np.maximum(6.0, _range5(g))


def box_mask(shape, box) -> np.ndarray:
    m = np.zeros(shape, dtype=bool)
    if box:
        x0, y0, x1, y1 = [int(v) for v in box]
        m[max(0, y0):max(0, y1), max(0, x0):max(0, x1)] = True
    return m


def _shift_or(m: np.ndarray) -> np.ndarray:
    o = m.copy()
    o[1:, :] |= m[:-1, :]; o[:-1, :] |= m[1:, :]
    o[:, 1:] |= m[:, :-1]; o[:, :-1] |= m[:, 1:]
    return o


def _shift_and(m: np.ndarray) -> np.ndarray:
    o = m.copy()
    o[1:, :] &= m[:-1, :]; o[:-1, :] &= m[1:, :]
    o[:, 1:] &= m[:, :-1]; o[:, :-1] &= m[:, 1:]
    return o


def clean(m: np.ndarray) -> np.ndarray:
    """開いて閉じる（点ノイズを落として穴を埋める）。"""
    return _shift_or(_shift_or(_shift_and(_shift_and(m))))


def components(m: np.ndarray, min_area: int = MIN_PART) -> list[dict]:
    """4 近傍の連結成分。面積の大きい順。"""
    h, w = m.shape
    seen = np.zeros_like(m)
    out = []
    ys, xs = np.nonzero(m)
    for sy, sx in zip(ys, xs):
        if seen[sy, sx]:
            continue
        q = deque([(sy, sx)])
        seen[sy, sx] = True
        px = []
        while q:
            y, x = q.popleft()
            px.append((y, x))
            for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                ny, nx = y + dy, x + dx
                if 0 <= ny < h and 0 <= nx < w and m[ny, nx] and not seen[ny, nx]:
                    seen[ny, nx] = True
                    q.append((ny, nx))
        if len(px) < min_area:
            continue
        a = np.array(px)
        out.append(dict(area=len(px), y0=int(a[:, 0].min()), y1=int(a[:, 0].max()) + 1,
                        x0=int(a[:, 1].min()), x1=int(a[:, 1].max()) + 1))
    out.sort(key=lambda c: -c["area"])
    return out


def _p(a: np.ndarray, q: float) -> float:
    return float(np.percentile(a, q)) if a.size else float("nan")


# --------------------------------------------------------------------- 測る

def measure(seed: Image.Image, out: Image.Image, man: dict) -> dict:
    gs, go = gray(seed), gray(out)
    d = np.abs(go - gs)
    shape = gs.shape
    r = {}

    place = box_mask(shape, man["target_region"])
    keep = box_mask(shape, man["keep_out"]) if man.get("keep_out") else ~place

    added = clean(d > ADD_THR) & place
    bg = d < BG_THR
    r["added_pct"] = float(added.sum() / max(1, place.sum()) * 100.0)

    # --- 背景を触ったか -------------------------------------------------
    for name, m in (("place", place), ("keep", keep)):
        sel = bg & m
        r[f"bg_frac_{name}"] = float(sel.sum() / max(1, m.sum()))
        if sel.sum() > 500:
            r[f"tone_mean_{name}"] = float(go[sel].mean() / max(1e-6, gs[sel].mean()))
            r[f"tone_sd_{name}"] = float(go[sel].std() / max(1e-6, gs[sel].std()))
        else:
            r[f"tone_mean_{name}"] = float("nan")
            r[f"tone_sd_{name}"] = float("nan")
    r["keep_diff"] = float(d[keep].mean()) if keep.any() else 0.0
    r["keep_added_pct"] = float((clean(d > ADD_THR) & keep).sum() / max(1, keep.sum()) * 100.0)

    # --- 継ぎ目（置く側と置かない側の境目に段差が出ていないか）-----------
    r["seam"] = float("nan")
    ko = man.get("keep_out")
    if ko and man["target_region"]:
        x = ko[0] if ko[0] > 0 else ko[2]
        if 1 < x < shape[1] - 1:
            j_out = np.abs(go[:, x] - go[:, x - 1]).mean()
            j_seed = np.abs(gs[:, x] - gs[:, x - 1]).mean()
            r["seam"] = float(j_out - j_seed)

    # --- 足したもの -----------------------------------------------------
    parts = components(added)
    r["parts"] = len(parts)
    r["part_areas"] = [c["area"] for c in parts[:12]]
    r["components"] = parts

    r["_added_mask"] = added
    r["_diff"] = d
    if not added.any():
        for k in ("grain", "edge", "dark_added", "dark_bg", "ground", "persp", "see_through",
                  "bright", "sat"):
            r[k] = float("nan")
        return r

    # 粒: 足した所と、同じ明るさの「触っていない所」で高周波を比べる
    hp_o, hp_s = highpass(go), highpass(gs)
    lum = float(np.median(go[added]))
    ref = bg & (np.abs(gs - lum) < 18)
    if ref.sum() < 400:
        ref = bg
    r["grain"] = float(np.median(hp_o[added]) / max(1e-6, np.median(hp_s[ref])))

    # 縁: 足した所の輪郭が「何画素で変わるか」を、種の縁と比べる
    #   ⚠ 勾配の生の大きさで測ると**縁の鋭さと縁の明暗差が混ざる**（自己検査で判明。
    #     50 階調の段差を 1 画素で切っても、種の強い縁より小さい値になり捕まえられなかった）。
    #     勾配を「その場の明暗差」で割ると、1 画素で変われば約 0.5、3 画素かけると 0.2 になり、
    #     明暗差に依らず鋭さだけが残る。さらに種の縁で割るので場所にも依らない。
    #   ⚠⚠ **段差が無い所を縁として数えない**（2026-08-18・自己検査が場所で割れて判明）。
    #     足した所の輪郭には、**背景と同じ明るさで重なった所**が混ざる（その画素は「足された」に
    #     入らないので、輪郭が物の外周ではなく穴の縁になる）。そこは勾配 0 なので、
    #     中央値を取ると**鋭さではなく「穴がどれだけ空いているか」を測る**ことになる。
    #     実際、ある場所では貼り付けの偽物が **0.00** と出て、門を素通りした（他の場所では 1.12）。
    #     ⇒ その場に段差がある輪郭だけで測る。無い所に「縁の鋭さ」は定義できない。
    border = added & ~_shift_and(added) & (_range5(go) >= 10.0)
    sharp_o, sharp_s = sharpness(go), sharpness(gs)
    gm_s = grad_mag(gs)
    seed_edges = gm_s >= _p(gm_s[gm_s > 0], 90)
    r["edge"] = (float(np.median(sharp_o[border]) / max(1e-6, np.median(sharp_s[seed_edges])))
                 if border.sum() >= 40 else float("nan"))

    # 暗部: 足したものの暗い方が、その場所の暗がりまで沈んでいるか
    r["dark_added"] = _p(go[added], 5)
    r["dark_bg"] = _p(gs[bg & place], 5) if (bg & place).any() else float("nan")

    # 明るさと鮮やかさ: 足したものだけ浮いていないか（どちらもその場所との比）
    ref_bg = bg & place
    r["bright"] = float(np.median(go[added]) / max(1e-6, np.median(gs[ref_bg]))) \
        if ref_bg.any() else float("nan")
    ao = np.asarray(out, dtype=np.float64)
    as_ = np.asarray(seed, dtype=np.float64)
    sat_o = ao.max(axis=2) - ao.min(axis=2)
    sat_s = as_.max(axis=2) - as_.min(axis=2)
    r["sat"] = float((np.median(sat_o[added]) + 1.0) /
                     (np.median(sat_s[ref_bg]) + 1.0)) if ref_bg.any() else float("nan")

    # 接地: 塊の直下が種より暗くなっているか（影）
    #   ⚠ 除くのは「大きく変わった画素」＝ 物そのもの。影は 5〜25 の変化なので残す。
    #     `added`（掃除して置く側へ絞った後）で除くと、閾値をわずかに下回った物の画素が
    #     帯に混ざって符号が反転する（2026-08-17 の自己検査で踏んだ）
    body = d > ADD_THR
    if man.get("surface") == "floor" and man.get("opaque", True):
        deltas = []
        for c in parts[:24]:
            y0, y1 = c["y1"], min(shape[0], c["y1"] + 7)
            if y1 <= y0:
                continue
            band = np.zeros(shape, dtype=bool)
            band[y0:y1, c["x0"]:c["x1"]] = True
            band &= ~body
            if band.sum() > 40:
                deltas.append(float((gs[band] - go[band]).mean()))
        r["ground"] = float(np.median(deltas)) if deltas else float("nan")
    else:
        r["ground"] = float("nan")

    # 遠近: 足元が下にある塊ほど大きいか（相関）
    if man.get("persp") and len(parts) >= 4:
        by = np.array([c["y1"] for c in parts], dtype=float)
        hh = np.array([c["y1"] - c["y0"] for c in parts], dtype=float)
        r["persp"] = float(np.corrcoef(by, hh)[0, 1]) if by.std() > 1e-6 else float("nan")
    else:
        r["persp"] = float("nan")

    # 透け: 下地（布の襞）が残っているか（不透明な物では低くて当たり前）
    a, b = gs[added], go[added]
    r["see_through"] = float(np.corrcoef(a, b)[0, 1]) if a.std() > 1e-6 and b.std() > 1e-6 else float("nan")
    return r
