# -*- coding: utf-8 -*-
"""異変が「滲んで現れる」・布が「膨らむ」ための画を、生成素材と無人の写真から作る（0349）。

    py -3.11 tools/gen-plate/bleed_maps.py --recipe tools/gen-plate/mask-recipes/bleed-stain-a-20261113.json
    py -3.11 tools/gen-plate/bleed_maps.py --recipe tools/gen-plate/mask-recipes/bleed-hands-b-20261113.json

出力は既定で `Logs/gen-plate/bleed/out/`（git 管理外）。作り方はレシピ JSON が全部持つ。
同じレシピからは同じバイト列が出る（乱数は seed 固定）。

作る画は 5 種類。シェーダ（`CrtCompositeShared.hlsl` の `SampleBase`）がどう読むかとあわせて書く。

| 画 | 空間 | 中身 | シェーダの読み |
|---|---|---|---|
| 比の画 `*_ratio_*.png` | 素材（contain-fit） | 線形の比 `シミの写真 / シミの無い写真` を sRGB で符号化 | `live * lerp(1, ratio, a)`（cue の `blend:"multiply"`） |
| 順の画 `*_reveal_*.png` | 枠 640×360 | その画素が現れる順 v（0 が最初・1 が最後・外は 1）を sRGB で符号化 | `a = saturate((reveal*(1+soft) - v) / soft)` |
| press マスク `*_pressmask_*.png` | 枠 640×360 | 顔の周りだけ白く、縁を広くぼかした形 m を sRGB で符号化 | 膨らむ uv の寄せと混ぜの重み |
| 人形の周りのマスク `*_dollmask_*.png` | 枠 640×360 | 従来のマスクと同じ（白 = 差し替える） | `mask.r` |
| `*_maps_*.json` | — | press の中心（枠 uv。v は下から）と、作ったときの数値 | cue の `pressCenter` |

⚠ **データの画は sRGB で符号化して書く。** 実機は Linear 色空間で、URL から読んだテクスチャは
   sRGB として読まれる（`ScreenOverlayController.WithMipmaps`）。そのまま 0..1 を書くと、シェーダが
   読む値は `srgb_to_linear(v)` に化けて、順も比も曲がる。従来のマスク（白黒）は端の値しか無いので
   この影響を受けない — だから人形の周りのマスクだけは従来どおり生の値で書く。
⚠ 比は**線形**で計算する（ライブも線形で読まれる）。0.965 以上は 1 に倒す — 布の目のわずかな差まで
   掛けると、体験者の体の上に布の皺が薄く乗る。
⚠ 依存は PIL と numpy だけ（scipy は無い）。測地的な距離と距離変換は 8 近傍の緩和で解く。
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
from pathlib import Path

import numpy as np
from PIL import Image

FRAME_W, FRAME_H = 640, 360          # 枠空間（common.js の MW/MH・make-diff-mask.py と対）
LUMA = np.array([0.2126, 0.7152, 0.0722], dtype=np.float32)
SQ2 = float(np.sqrt(2.0))

ROOT = Path(__file__).resolve().parents[2]


# ---------------------------------------------------------------- 入出力

def search_roots() -> list[Path]:
    """素材を探す根。worktree には現場の素材が無いので、メインの木も見る（読むだけ）。"""
    roots = [ROOT]
    try:
        common = subprocess.run(["git", "-C", str(ROOT), "rev-parse", "--git-common-dir"],
                                capture_output=True, text=True, check=True).stdout.strip()
        main = (ROOT / common).resolve().parent if not os.path.isabs(common) else Path(common).resolve().parent
        if main not in roots:
            roots.append(main)
    except (OSError, subprocess.CalledProcessError):
        pass
    return roots


def find_input(rel: str, roots: list[Path]) -> Path:
    for r in roots:
        p = (r / rel)
        if p.exists():
            return p
    raise FileNotFoundError(f"入力が見つからない: {rel}（探した根: {', '.join(map(str, roots))}）")


def srgb_to_lin(a: np.ndarray) -> np.ndarray:
    return np.where(a <= 0.04045, a / 12.92, ((a + 0.055) / 1.055) ** 2.4).astype(np.float32)


def lin_to_srgb(a: np.ndarray) -> np.ndarray:
    a = np.clip(a, 0.0, 1.0)
    return np.where(a <= 0.0031308, a * 12.92, 1.055 * np.power(a, 1 / 2.4) - 0.055).astype(np.float32)


def load_rgb_lin(p: Path) -> np.ndarray:
    return srgb_to_lin(np.asarray(Image.open(p).convert("RGB"), dtype=np.float32) / 255.0)


def load_gray(p: Path) -> np.ndarray:
    return np.asarray(Image.open(p).convert("L"), dtype=np.float32) / 255.0


def save_gray(a: np.ndarray, p: Path, encode_srgb: bool) -> None:
    v = lin_to_srgb(a) if encode_srgb else np.clip(a, 0.0, 1.0)
    Image.fromarray(np.round(v * 255.0).astype(np.uint8), "L").convert("RGB").save(p, optimize=True)


def save_rgb_lin(a: np.ndarray, p: Path) -> None:
    Image.fromarray(np.round(lin_to_srgb(a) * 255.0).astype(np.uint8), "RGB").save(p, optimize=True)


# ---------------------------------------------------------------- 画の基本操作

def box(a: np.ndarray, r: int) -> np.ndarray:
    """(2r+1) 四方の平均。端は端の値で延ばす。"""
    if r <= 0:
        return a.astype(np.float32)
    if a.ndim == 2:
        return box(a[..., None], r)[..., 0]
    k = 2 * r + 1
    p = np.pad(a.astype(np.float64), ((r, r), (r, r), (0, 0)), mode="edge")
    c = p.cumsum(0)
    c = np.concatenate([c[k - 1:k], c[k:] - c[:-k]], 0)
    c = c.cumsum(1)
    c = np.concatenate([c[:, k - 1:k], c[:, k:] - c[:, :-k]], 1)
    return (c / (k * k)).astype(np.float32)


def max_filter(a: np.ndarray, r: int) -> np.ndarray:
    """(2r+1) 四方の最大（分離して 2 回）。端は回り込まない（np.roll は反対の端へ漏れる）。"""
    out = a.astype(np.float32)
    lo = np.float32(-np.inf)
    for dy, dx in ((1, 0), (0, 1)):
        src = out
        out = src.copy()
        for d in range(1, r + 1):
            out = np.maximum(out, _shift(src, d * dy, d * dx, lo))
            out = np.maximum(out, _shift(src, -d * dy, -d * dx, lo))
    return out


def dilate(m: np.ndarray, r: int) -> np.ndarray:
    return max_filter(m.astype(np.float32), r) > 0.5


def smoothstep(e0: float, e1: float, x: np.ndarray) -> np.ndarray:
    t = np.clip((x - e0) / max(e1 - e0, 1e-6), 0.0, 1.0)
    return t * t * (3.0 - 2.0 * t)


def _shift(a: np.ndarray, dy: int, dx: int, fill: float) -> np.ndarray:
    out = np.full_like(a, fill)
    h, w = a.shape
    ys, yd = (slice(0, h - dy), slice(dy, h)) if dy >= 0 else (slice(-dy, h), slice(0, h + dy))
    xs, xd = (slice(0, w - dx), slice(dx, w)) if dx >= 0 else (slice(-dx, w), slice(0, w + dx))
    out[yd, xd] = a[ys, xs]
    return out


NEIGHBORS = [(-1, 0, 1.0), (1, 0, 1.0), (0, -1, 1.0), (0, 1, 1.0),
             (-1, -1, SQ2), (-1, 1, SQ2), (1, -1, SQ2), (1, 1, SQ2)]


def geodesic(seed: np.ndarray, domain: np.ndarray, max_iter: int = 2000) -> np.ndarray:
    """domain の中だけを通る seed からの距離（8 近傍・画素単位）。届かない所は inf。"""
    inf = np.float32(np.inf)
    d = np.where(seed & domain, 0.0, inf).astype(np.float32)
    for _ in range(max_iter):
        prev = d
        cand = d
        for dy, dx, c in NEIGHBORS:
            cand = np.minimum(cand, _shift(d, dy, dx, inf) + np.float32(c))
        d = np.where(domain, cand, inf)
        if np.array_equal(d, prev):
            break
    return d


def distance_inside(m: np.ndarray) -> np.ndarray:
    """m の中で、m の外までの距離（距離変換）。"""
    return geodesic(~m, np.ones_like(m, dtype=bool))


def components(m: np.ndarray) -> np.ndarray:
    """8 連結のラベル（0 = 背景、1.. = 成分）。画素ラベルの伝播で解く。"""
    h, w = m.shape
    lab = np.where(m, np.arange(1, h * w + 1, dtype=np.int64).reshape(h, w), 0)
    big = np.int64(h * w + 10)
    while True:
        prev = lab
        cur = np.where(m, lab, big)
        best = cur
        for dy, dx, _ in NEIGHBORS:
            best = np.minimum(best, _shift(cur, dy, dx, big))
        lab = np.where(m, best, 0)
        if np.array_equal(lab, prev):
            break
    _, inv = np.unique(lab, return_inverse=True)
    return inv.reshape(h, w)


def value_noise(h: int, w: int, cell: float, rng: np.random.Generator) -> np.ndarray:
    """低周波の値ノイズ（-1..1）。粗い乱数の格子を双三次で引き伸ばす。"""
    gh, gw = int(np.ceil(h / cell)) + 3, int(np.ceil(w / cell)) + 3
    g = rng.uniform(-1.0, 1.0, (gh, gw)).astype(np.float32)
    im = Image.fromarray(g, "F").resize((int(gw * cell), int(gh * cell)), Image.BICUBIC)
    a = np.asarray(im, dtype=np.float32)
    oy, ox = int(cell), int(cell)
    return np.clip(a[oy:oy + h, ox:ox + w], -1.0, 1.0)


def fbm(h: int, w: int, rng: np.random.Generator) -> np.ndarray:
    n = 0.62 * value_noise(h, w, 46.0, rng) + 0.38 * value_noise(h, w, 17.0, rng)
    m = np.abs(n).max()
    return n / m if m > 0 else n


# ---------------------------------------------------------------- 素材空間 ↔ 枠空間

def frame_rect(src_w: int, src_h: int) -> tuple[int, int, int, int]:
    """素材を枠へ contain-fit したときの矩形（x0, y0, w, h）。シェーダの ContainUv と同じ。"""
    s = min(FRAME_W / src_w, FRAME_H / src_h)
    w, h = int(round(src_w * s)), int(round(src_h * s))
    return (FRAME_W - w) // 2, (FRAME_H - h) // 2, w, h


def to_frame(a: np.ndarray, fill: float, resample=Image.BILINEAR) -> np.ndarray:
    """素材空間の 1 チャンネルを枠空間へ写す（外は fill）。"""
    x0, y0, w, h = frame_rect(a.shape[1], a.shape[0])
    im = Image.fromarray(a.astype(np.float32), "F").resize((w, h), resample)
    out = np.full((FRAME_H, FRAME_W), fill, dtype=np.float32)
    out[y0:y0 + h, x0:x0 + w] = np.asarray(im, dtype=np.float32)
    return out


def frame_uv(x: float, y: float) -> list[float]:
    """枠の画素座標 → シェーダの uv（v は下から）。"""
    return [round(x / FRAME_W, 4), round(1.0 - y / FRAME_H, 4)]


# ---------------------------------------------------------------- 比の画

def ratio_map(src: np.ndarray, cleans: list[np.ndarray], domain: np.ndarray, rec: dict) -> np.ndarray:
    """線形の比 `src / clean`（素材空間・RGB）。合わない所と domain の外は 1。

    clean が複数あるときは**比の大きい方**を採る。生成で描き直された布の皺は参照ごとに
    違う所に出るが、本物の染みはどちらの参照に対しても暗い。だから max を取ると皺だけが落ちる。
    """
    r_blur = int(rec.get("noiseRadius", 2))
    s = box(src, r_blur)
    ref_box = rec.get("normalizeBox")              # 布の地の明るさを揃える箱（素材座標）
    best = None
    for c in cleans:
        r = s / np.maximum(box(c, r_blur), 1e-3)
        if ref_box:
            x0, y0, x1, y1 = ref_box
            med = np.median(r[y0:y1, x0:x1].reshape(-1, 3), axis=0)
        else:
            med = np.median(r[domain].reshape(-1, 3), axis=0)
        r = r / np.maximum(med, 1e-3)
        env_r = int(rec.get("envelopeRadius", 0))
        if env_r > 0:
            # 地の明るさが場所で揺れる素材（生成で壁を描き直した手形）は、近所の明るい側を 1 とする。
            env = box(max_filter((r * LUMA).sum(-1), env_r), env_r)
            r = r / np.maximum(env[..., None], 1e-3)
        best = r if best is None else np.maximum(best, r)
    y = (best * LUMA).sum(-1)
    chroma = float(rec.get("chroma", 0.0))         # 0 = 灰色の比 / 1 = チャンネルごとの比
    rgb = y[..., None] + chroma * (best - y[..., None])
    lo, hi = rec.get("knee", [0.93, 0.965])
    # 0.965 以上は 1。下の 0.93 から滑らかに 1 へ寄せる（段差を作らない）。
    k = smoothstep(lo, hi, y)[..., None]
    rgb = rgb * (1.0 - k) + k
    rgb = np.clip(rgb, float(rec.get("floor", 0.0)), 1.0)
    rgb[~domain] = 1.0
    feather = int(rec.get("domainFeather", 0))
    if feather > 0:
        w = box(domain.astype(np.float32), feather)[..., None]
        rgb = 1.0 + (rgb - 1.0) * np.clip(w * 2.0 - 1.0, 0.0, 1.0)
    return rgb.astype(np.float32)


# ---------------------------------------------------------------- 順の画（シミ）

def stain_reveal(dark_f: np.ndarray, rec: dict, rng: np.random.Generator) -> tuple[np.ndarray, dict]:
    """シミの順の画（枠空間）。芯（いちばん濃い所）から染みの中だけを通って広がり、垂れは後から伸びる。"""
    on = float(rec.get("inThreshold", 0.035))
    S = dark_f > on
    # 小さな離れ小島（生成で描き直された布の皺の切れ端）は染みに数えない。順は 1 のまま＝現れない。
    island = int(rec.get("minIslandPx", 0))
    if island > 0:
        lab0 = components(S)
        sizes0 = np.bincount(lab0.ravel())
        sizes0[0] = 0
        S = S & (sizes0[lab0] >= island)
    dropped = int((dark_f > on).sum() - S.sum())
    vals = dark_f[S]
    core_pct = float(rec.get("coreTopPct", 12.0))
    core = S & (dark_f >= np.percentile(vals, 100.0 - core_pct))
    g = geodesic(core, S)
    reach = S & np.isfinite(g)
    # 芯から届かない染み（離れた飛び沫）は、自分の中のいちばん濃い所から始める。
    if (S & ~reach).any():
        lab = components(S & ~reach)
        for i in range(1, lab.max() + 1):
            comp = lab == i
            seed = comp & (dark_f >= dark_f[comp].max() * 0.9)
            gi = geodesic(seed, comp)
            g = np.where(comp, gi + np.nanmax(np.where(reach, g, 0)) * 0.35, g)

    # 垂れ: 芯より下の、横幅の細い枝。
    yy = np.arange(FRAME_H, dtype=np.float32)[:, None] * np.ones((1, FRAME_W), np.float32)
    core_low = np.percentile(yy[core], float(rec.get("dripBelowCorePct", 85.0)))
    hw = int(rec.get("dripWidthPx", 5))
    k = 2 * hw + 1
    p = np.pad(S.astype(np.float32), ((0, 0), (hw, hw)))
    c = p.cumsum(1)
    width = (np.concatenate([c[:, k - 1:k], c[:, k:] - c[:, :-k]], 1)) / k
    drip = S & (width < float(rec.get("dripWidthFrac", 0.6))) & (yy > core_low)
    # 細い所が途切れ途切れに拾われるので、縦に繋いでから小さい切れ端を捨てる。
    drip = dilate(drip, 1) & S & (yy > core_low)
    lab = components(drip)
    body = S & ~drip
    v = np.ones((FRAME_H, FRAME_W), np.float32)
    b0, b1 = rec.get("bodyRange", [0.0, 0.8])
    gb = g[body & np.isfinite(g)]
    gmax = float(gb.max()) if gb.size else 1.0
    dn = np.clip(dark_f / max(float(vals.max()), 1e-3), 0.0, 1.0)
    w_dark = float(rec.get("darkWeight", 0.25))
    gnorm = np.clip(np.where(np.isfinite(g), g, gmax) / max(gmax, 1e-3), 0.0, 1.0)
    body_t = (1.0 - w_dark) * gnorm ** float(rec.get("bodyGamma", 0.85)) + w_dark * (1.0 - dn)
    v[body] = b0 + (b1 - b0) * np.clip(body_t[body], 0.0, 1.0)

    d0, d1 = rec.get("dripRange", [0.55, 1.0])
    drips = 0
    min_px = int(rec.get("dripMinPx", 12))
    for i in range(1, lab.max() + 1):
        comp = lab == i
        if comp.sum() < min_px:
            v[comp] = b0 + (b1 - b0) * np.clip(body_t[comp], 0.0, 1.0)
            continue
        drips += 1
        ys = yy[comp]
        top, bot = float(ys.min()), float(ys.max())
        t = (yy - top) / max(bot - top, 1.0)
        # 枝の頭は胴から続く値に合わせ、下へ行くほど後に出る。
        v[comp] = d0 + (d1 - d0) * np.clip(t[comp], 0.0, 1.0)

    amp = float(rec.get("noiseAmp", 0.06))
    n = fbm(FRAME_H, FRAME_W, rng) * amp
    v = np.where(S, np.clip(v + n, 0.0, 1.0), 1.0)
    # 芯（目と口）は必ず最初に出る（ノイズで遅れない）。芯の中も濃い順に coreRange へ並べる —
    # 芯をまとめて 0 付近に置くと、進み具合の立ち上がりが速い（1-(1-t)^2.2）ので一度に「ポン」と出る。
    c0, c1 = rec.get("coreRange", [0.0, 0.15])
    cv = dark_f[core]
    cn = (cv - cv.min()) / max(float(np.ptp(cv)), 1e-3)
    v[core] = c0 + (c1 - c0) * (1.0 - cn)
    smooth = int(rec.get("orderSmooth", 1))
    if smooth > 0:
        vs = box(np.where(S, v, np.nan_to_num(v)), smooth)
        v = np.where(S, vs, 1.0)
    info = dict(stainPx=int(S.sum()), droppedIslandPx=dropped, corePx=int(core.sum()), dripComponents=drips,
                dripPx=int(drip.sum()), geodesicMaxPx=round(gmax, 1))
    return v.astype(np.float32), info


# ---------------------------------------------------------------- 順の画（手形）

def hands_reveal(dark_f: np.ndarray, rec: dict, rng: np.random.Generator) -> tuple[np.ndarray, dict]:
    """手形の順の画（枠空間）。手形ごとに出る時刻を決め、手のひらの中心から外へ広がる。

    手形が重なって壁を埋める素材では、暗部の連結成分は数個の大きな塊にしかならない
    （塊ごとに時刻を振ると、大きな面がまとめて現れる）。だから手のひら 1 つ分の大きさで
    暗さを均し、その極大を手のひらの中心とする。各画素は染みの中だけを通っていちばん近い
    中心に属し、その中心の時刻から距離に応じて遅れて出る。手形がまばらな素材では、
    極大は成分ごとに 1 つになるので、成分ごとに時刻を振るのと同じになる。
    """
    on = float(rec.get("inThreshold", 0.04))
    S = dark_f > on
    sep = float(rec.get("separateThreshold", 0.45))
    hand_r = int(rec.get("handRadiusPx", 8))
    soft = box(dark_f * S, max(hand_r // 2, 1))
    peak = soft >= max_filter(soft, int(rec.get("seedSpacingPx", hand_r))) - 1e-6
    seeds = S & peak & (soft > sep)
    lab = components(seeds)                        # 同じ高さの平らな極大は 1 つに数える
    n = int(lab.max())

    # 中心ごとの時刻。近い側（画の左と下）から奥へ。暗い（大きい・濃い）手ほど少し早い。
    x0, y0, x1, y1 = rec.get("flowBox", [0, 0, FRAME_W, FRAME_H])
    jitter = float(rec.get("jitter", 0.08))
    t0, t1 = rec.get("handRange", [0.0, 0.86])
    near_x = float(rec.get("nearWeightX", 0.8))
    ys, xs = np.mgrid[0:FRAME_H, 0:FRAME_W]
    cnt = np.maximum(np.bincount(lab.ravel(), minlength=n + 1).astype(np.float32), 1.0)
    cx = np.bincount(lab.ravel(), xs.ravel().astype(np.float32), n + 1) / cnt
    cy = np.bincount(lab.ravel(), ys.ravel().astype(np.float32), n + 1) / cnt
    sv = np.bincount(lab.ravel(), soft.ravel(), n + 1) / cnt
    t_of = np.zeros(n + 1, np.float32)
    if n:
        xn = np.clip((cx[1:] - x0) / max(x1 - x0, 1), 0.0, 1.0)
        yn = np.clip((cy[1:] - y0) / max(y1 - y0, 1), 0.0, 1.0)
        depth = near_x * xn + (1.0 - near_x) * (1.0 - yn)
        depth -= 0.06 * (sv[1:] - sv[1:].mean()) / max(float(sv[1:].std()), 1e-3)
        depth = (depth - depth.min()) / max(float(np.ptp(depth)), 1e-3)
        t_of[1:] = t0 + (t1 - t0) * depth + rng.uniform(-jitter, jitter, n).astype(np.float32)

    # 染みの中だけを通って、いちばん近い中心へ属させる（複数の種からの測地的な距離）。
    near_lab = lab.copy()
    dist_to = np.where(lab > 0, 0.0, np.inf).astype(np.float32)
    for _ in range(int(rec.get("growPx", 60))):
        changed = False
        for dy, dx, c in NEIGHBORS:
            sd = _shift(dist_to, dy, dx, np.inf) + np.float32(c)
            sl = _shift(near_lab, dy, dx, 0)
            better = S & (sd < dist_to) & (sl > 0)
            if better.any():
                changed = True
                dist_to = np.where(better, sd, dist_to)
                near_lab = np.where(better, sl, near_lab)
        if not changed:
            break
    spread = float(rec.get("spread", 0.10))
    v = np.ones((FRAME_H, FRAME_W), np.float32)
    has = S & (near_lab > 0)
    # 手のひらの中心から外へ: 手のひら 1 つ分の半径で spread だけ遅れ、その外（指・垂れ・擦れ）はさらに後。
    rel = np.clip(np.where(np.isfinite(dist_to), dist_to, 0.0) / max(hand_r, 1), 0.0, 1.6)
    # 距離だけで並べると、暗い手のひらが丸い円盤として先に出て「丸い点」に見える。
    # 濃さを混ぜると、手形の形（濃い指と手のひら）が先に出て、薄い擦れが後を追う。
    w_dark = float(rec.get("darkWeight", 0.0))
    dn = np.clip(dark_f / max(float(dark_f[S].max()), 1e-3), 0.0, 1.0) if S.any() else dark_f
    inner_t = (1.0 - w_dark) * rel + w_dark * (1.0 - dn)
    v[has] = (t_of[near_lab] + spread * inner_t)[has]
    lone = S & ~has
    v[lone] = t1 + spread
    amp = float(rec.get("noiseAmp", 0.03))
    v = np.where(S, np.clip(v + fbm(FRAME_H, FRAME_W, rng) * amp, 0.0, 1.0), 1.0)
    smooth = int(rec.get("orderSmooth", 1))
    if smooth > 0:
        v = np.where(S, box(v, smooth), 1.0)
    return v.astype(np.float32), dict(handPx=int(S.sum()), hands=n, lonePx=int(lone.sum()))


# ---------------------------------------------------------------- press と人形

def press_mask(bulge: np.ndarray, idle: np.ndarray, domain_f: np.ndarray, rec: dict) -> tuple[np.ndarray, list[float]]:
    """盛り上がりの画が待機の画と違う所（顔）を広くぼかした形と、その重心（枠 uv）。"""
    d = np.abs(box(bulge, 2) - box(idle, 2)).max(-1)
    d_f = to_frame(d, 0.0) * (domain_f > 0.5)
    lo, hi = rec.get("diffRange", [0.03, 0.12])
    m = smoothstep(lo, hi, d_f)
    # 小さい点（布の皺の描き直し）は落とし、顔の塊だけを残す。
    m = box(m, int(rec.get("closeRadius", 6)))
    m = smoothstep(float(rec.get("keep", 0.35)), 0.75, m)
    # 顔の中で待機の画と似ている所（目の周り）が抜けて、混ぜた画に穴が開く。閉じる（膨らませて削る）。
    hole = int(rec.get("closeHolesPx", 0))
    if hole > 0:
        m = -max_filter(-max_filter(m, hole), hole)
    for _ in range(int(rec.get("blurPasses", 3))):
        m = box(m, int(rec.get("blurRadius", 10)))
    m = m / max(float(m.max()), 1e-3)
    m = smoothstep(0.0, float(rec.get("plateau", 0.55)), m)
    ys, xs = np.mgrid[0:FRAME_H, 0:FRAME_W]
    wsum = float(m.sum())
    cx, cy = float((m * xs).sum() / wsum), float((m * ys).sum() / wsum)
    return m.astype(np.float32), [cx, cy]


def doll_mask(doll: np.ndarray, cleans: list[np.ndarray], rec: dict) -> np.ndarray:
    """人形の画が、どの無人の幕とも違う所（人形）だけの合成マスク（枠空間・従来どおり白 = 差し替える）。"""
    d = None
    for c in cleans:
        di = np.abs(box(doll, 1) - box(c, 1)).max(-1)
        d = di if d is None else np.minimum(d, di)
    thr = float(rec.get("diffThreshold", 0.10))
    m = d > thr
    bx = rec.get("searchBox")
    if bx:
        x0, y0, x1, y1 = bx
        keep = np.zeros_like(m)
        keep[y0:y1, x0:x1] = True
        m &= keep
    lab = components(m)
    if lab.max() > 0:
        sizes = np.bincount(lab.ravel())
        sizes[0] = 0
        m = lab == int(np.argmax(sizes))
    fill = int(rec.get("fillPx", 6))
    m = ~dilate(~dilate(m, fill), fill)            # 閉じる（髪の隙間・袖の皺の穴を埋める）
    m = dilate(m, int(rec.get("dilatePx", 5)))
    f = to_frame(m.astype(np.float32), 0.0)
    for _ in range(2):
        f = box(f, int(rec.get("featherPx", 4)))
    return np.clip(f, 0.0, 1.0).astype(np.float32)


# ---------------------------------------------------------------- 本体

def build(recipe_path: Path, out_dir: Path) -> dict:
    rec = json.loads(recipe_path.read_text(encoding="utf-8"))
    roots = search_roots()
    rng = np.random.default_rng(int(rec.get("seed", 1)))
    out_dir.mkdir(parents=True, exist_ok=True)
    names = rec["outputs"]
    report: dict = {"recipe": str(recipe_path.relative_to(ROOT)) if recipe_path.is_relative_to(ROOT) else str(recipe_path),
                    "inputs": {}, "outputs": {}}

    def inp(rel: str) -> Path:
        p = find_input(rel, roots)
        report["inputs"][rel] = str(p)
        return p

    src = load_rgb_lin(inp(rec["source"]))
    sh, sw = src.shape[:2]
    cleans = [load_rgb_lin(inp(c)) for c in rec["clean"]]
    for c, rel in zip(cleans, rec["clean"]):
        if c.shape != src.shape:
            raise ValueError(f"解像度が合わない: {rel} {c.shape[1]}x{c.shape[0]} / 素材 {sw}x{sh}")

    # 比を取ってよい範囲（素材空間）。既存の合成マスク（枠空間）を素材へ戻したものと、箱の積。
    domain = np.ones((sh, sw), bool)
    if rec.get("domainMask"):
        mf = load_gray(inp(rec["domainMask"]))
        if mf.shape != (FRAME_H, FRAME_W):
            raise ValueError("domainMask は枠空間 640x360 で渡す")
        x0, y0, w, h = frame_rect(sw, sh)
        crop = Image.fromarray(mf[y0:y0 + h, x0:x0 + w], "F").resize((sw, sh), Image.BILINEAR)
        domain &= np.asarray(crop) > 0.5
    if rec.get("domainBox"):
        x0, y0, x1, y1 = rec["domainBox"]
        b = np.zeros_like(domain)
        b[y0:y1, x0:x1] = True
        domain &= b
    domain_f = to_frame(domain.astype(np.float32), 0.0)

    rgb = ratio_map(src, cleans, domain, rec.get("ratio", {}))
    if names.get("ratio"):
        p = out_dir / names["ratio"]
        save_rgb_lin(rgb, p)
        report["outputs"]["ratio"] = p.name
    y = (rgb * LUMA).sum(-1)
    dark_f = to_frame(1.0 - y, 0.0)
    report["ratioStats"] = {
        "minLuma": round(float(y.min()), 3),
        "darkPx": int((y < 0.965).sum()),
        "darkPctOfDomain": round(100.0 * float((y < 0.965).sum()) / max(int(domain.sum()), 1), 1),
    }

    kind = rec["kind"]
    if kind == "stain":
        v, info = stain_reveal(dark_f, rec.get("reveal", {}), rng)
    elif kind == "hands":
        v, info = hands_reveal(dark_f, rec.get("reveal", {}), rng)
    else:
        raise ValueError(f"kind は stain / hands: {kind}")
    report["reveal"] = info
    if names.get("reveal"):
        p = out_dir / names["reveal"]
        save_gray(v, p, encode_srgb=True)
        report["outputs"]["reveal"] = p.name

    pr = rec.get("press")
    if pr:
        bulge = load_rgb_lin(inp(pr["source"]))
        m, (cx, cy) = press_mask(bulge, src, domain_f, pr)
        p = out_dir / names["pressMask"]
        save_gray(m, p, encode_srgb=True)
        report["outputs"]["pressMask"] = p.name
        report["pressCenter"] = frame_uv(cx, cy)
        report["pressCenterPx"] = [round(cx, 1), round(cy, 1)]

    dl = rec.get("doll")
    if dl:
        doll = load_rgb_lin(inp(dl["source"]))
        dm = doll_mask(doll, [load_rgb_lin(inp(c)) for c in dl["clean"]], dl)
        p = out_dir / names["dollMask"]
        save_gray(dm, p, encode_srgb=False)
        report["outputs"]["dollMask"] = p.name
        report["dollMaskPx"] = int((dm > 0.5).sum())

    if names.get("maps"):
        (out_dir / names["maps"]).write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n",
                                             encoding="utf-8", newline="\n")
    return report


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--recipe", required=True)
    ap.add_argument("--out", default=str(ROOT / "Logs/gen-plate/bleed/out"))
    a = ap.parse_args()
    rep = build(Path(a.recipe).resolve(), Path(a.out))
    print(json.dumps(rep, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
