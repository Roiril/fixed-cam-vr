"""2 周目 A の右の幕の素材（LEDGER 0376）を、Codex の静止画 4 枚から組む。

    py -3.11 tools/gen-plate/stain3/build_right_burst.py real   # 本番 → tools/web-compositor/static-inputs/
    py -3.11 tools/gen-plate/stain3/build_right_burst.py twin   # ツイン → tools/twin/assets/

入力（SETS の work、無ければ Logs/gen-plate/stain2A_right_20261115/<名>/。作り方は README の「右の幕」と「作り直す」）:
  stain.png       右の幕に顔のシミを足した画（待機の 2 倍から）
  bulge.png       シミの所から顔が左へ伸び出した画（stain から）
  doll_laugh.png  右の幕の裏で人形が笑っている画（stain から）
  doll_watch.png  人形が身を引いて黙って見ている画（doll_laugh から）

出すもの:
  *_right_burst_*.mp4   640x480・24fps。シミ → 1 コマで伸び切った顔と笑う人形 → 1 コマでシミへ戻り人形が身を引く
  *_right_watch_*.png   動画の最後のコマと同じ画（隠れて見ている人形）
  *_right_mask_*.png    枠空間 640x360 の合成範囲（右の幕と人形の所だけ）
  *_both_ratio_*.png    左右のシミの比の画（線形の比を sRGB で符号化）
  *_both_mask_*.png     左右のシミの合成範囲（枠空間）
  *_right_ratio_*.png   右のシミだけの比の画（線形の比を sRGB で符号化）。*_right_ratio_mask_*.png がその合成範囲
  *_right_burst_*.json  尺と、人形の頭の位置（台本と検査が読む）
  <入力のフォルダ>/sheet_*.png  確かめる画

生成は部屋を少し描き直す。変えた所（元との差の塊）だけを残し、ほかは元の画素へ戻す（keep_base.py と同じ考え）。
"""
from __future__ import annotations

import argparse
import json
import math
import subprocess
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parents[3]
WORK = ROOT / "Logs/gen-plate/stain2A_right_20261115"
FPS = 24
W, H = 640, 480
FW, FH = 640, 360

SETS = {
    # 本番は 2026-11-15 12:50 の無人写真（plate_A_20261115_125052.jpg）に合わせる。待機はその写真に左のしみの比の画を
    # 掛けた画、stain.png はさらに右のしみの比の画を掛けた画（どちらも生成ではない。README「作り直す」）
    "real": dict(
        work="Logs/gen-plate/stainA_refit_20261115",
        base="tools/web-compositor/static-inputs/stain_A_idle_20261115_1250.png",
        right_ratio_src="tools/web-compositor/static-inputs/stain_A_right_ratio_20261115.png",  # 9/24 の部屋で作った右のしみ
        left_ratio="tools/web-compositor/static-inputs/stain_A_ratio_20261113.png",
        left_mask="tools/web-compositor/static-inputs/stain_A_press_leftmask_20260925.png",
        out_dir="tools/web-compositor/static-inputs",
        bulge_box=(282, 40, 512, 240),
        rail=((280, 224), (515, 234)),   # 手すりの芯（元の画素）。伸び出しの手前に元の手すりを貼り直す
        names=dict(burst="stain_A_right_burst_20261115_1250r2.mp4", watch="stain_A_right_watch_20261115_1250r2.png",
                   mask="stain_A_right_mask_20261115_1250r2.png", ratio="stain_A_both_ratio_20261115_1250.png",
                   both_mask="stain_A_both_mask_20261115_1250.png", right_ratio="stain_A_right_ratio_20261115_1250.png",
                   right_ratio_mask="stain_A_right_ratio_mask_20261115_1250.png",
                   meta="stain_A_right_burst_20261115_1250r2.json"),
    ),
    "twin": dict(
        base="tools/twin/assets/twin_stainA_idle.png",
        left_ratio="tools/twin/assets/twin_stainA_ratio.png",
        left_mask="tools/twin/assets/twin_stainA_mask.png",
        out_dir="tools/twin/assets",
        bulge_box=(250, 40, 512, 268),   # ツインの顔は角の少し左まで出て、布は手すりの下まで引かれる
        rail=((240, 215), (515, 209)),
        names=dict(burst="twin_stainA_right_burst.mp4", watch="twin_stainA_right_watch.png",
                   mask="twin_stainA_right_mask.png", ratio="twin_stainA_both_ratio.png",
                   both_mask="twin_stainA_both_mask.png", right_ratio="twin_stainA_right_ratio.png",
                   right_ratio_mask="twin_stainA_right_ratio_mask.png", meta="twin_stainA_right_burst.json"),
    ),
}

# 生成が描き換えてよい範囲（元の画素 640x480・x0, y0, x1, y1）。左の幕とその染みは外す
BOX_STAIN = (350, 50, 515, 238)    # 右の幕の顔のシミ
BOX_DOLL = (478, 236, 660, 500)    # 右の幕の縁の裏の人形と、その影（右と下は枠の外まで。枠の縁で薄まらない）
BOX_DOLL_EDIT = (440, 0, 578, 480)  # 人形の画が描き換える所（人形と、引かれた幕の縁）。座標合わせの突き合わせに使わない（右の棚は使う）
# 尺（秒）。笑い声 1 回（sfx_doll_laugh_once）に合わせる
# 伸び出しは動かさない（2026-11-15 ユーザー「最初フレームと最後フレームだけ」）。
# シミだけの画 → 伸び切った画 → シミだけの画を、どれも 1 コマで切り替える
T_BULGE = (2 / FPS, 1.75)  # 伸び切った画を出している間。前は 2 コマ（0.083 秒）のシミだけの画
T_SWAP = 1.90  # 人形が笑う → 身を引いて見る（1 コマで切り替える。重ねると 2 体に見える）
T_END = 2.25


def lin(a):
    a = a / 255.0
    return np.where(a <= 0.04045, a / 12.92, ((a + 0.055) / 1.055) ** 2.4)


def srgb(a):
    a = np.clip(a, 0, 1)
    return np.where(a <= 0.0031308, a * 12.92, 1.055 * a ** (1 / 2.4) - 0.055) * 255.0


def load(p: Path) -> np.ndarray:
    return np.asarray(Image.open(p).convert("RGB"), dtype=np.float32)


def _match(ref_g: np.ndarray, gen_g: np.ndarray, x0: int, y0: int, size: int, rx, ry):
    """ref の (x0, y0) の正方形が gen のどこにあるか。NCC の山を放物線で小数まで詰める。(dx, dy, r)
    rx / ry は探す幅。(負の向き, 正の向き) の組でもよい（枠の端まで点を取るため、ずれる向きの側だけ広く探す）。"""
    from numpy.lib.stride_tricks import sliding_window_view
    a = ref_g[y0:y0 + size, x0:x0 + size]
    if a.std() < 4:
        return None
    rxn, rxp = rx if isinstance(rx, tuple) else (rx, rx)
    ryn, ryp = ry if isinstance(ry, tuple) else (ry, ry)
    ys, xs = y0 - ryn, x0 - rxn
    if ys < 0 or xs < 0 or y0 + size + ryp > gen_g.shape[0] or x0 + size + rxp > gen_g.shape[1]:
        return None
    rx, ry = rxn, ryn
    reg = gen_g[ys:y0 + size + ryp, xs:x0 + size + rxp]
    win = sliding_window_view(reg, (size, size))           # (2ry+1, 2rx+1, size, size)
    a = (a - a.mean()) / a.std()
    m = win.mean(axis=(2, 3), keepdims=True)
    s = win.std(axis=(2, 3), keepdims=True) + 1e-6
    r = ((win - m) / s * a).mean(axis=(2, 3))
    iy, ix = np.unravel_index(np.argmax(r), r.shape)
    if not (0 < iy < r.shape[0] - 1 and 0 < ix < r.shape[1] - 1):
        return None                                         # 探す範囲の端に山がある = 当たっていない

    def para(l, c, rr):
        den = l - 2 * c + rr
        return 0.0 if abs(den) < 1e-9 else 0.5 * (l - rr) / den
    fy = para(r[iy - 1, ix], r[iy, ix], r[iy + 1, ix])
    fx = para(r[iy, ix - 1], r[iy, ix], r[iy, ix + 1])
    return ix - rx + fx, iy - ry + fy, float(r[iy, ix])


def _poly_terms(x: np.ndarray, y: np.ndarray, deg: int) -> np.ndarray:
    x, y = x / W, y / H
    t = [np.ones_like(x)]
    for d in range(1, deg + 1):
        for i in range(d + 1):
            t.append(x ** (d - i) * y ** i)
    return np.stack(t, -1)


def register(a: np.ndarray, ref: np.ndarray, skip=(), deg: int = 3) -> tuple[np.ndarray, dict]:
    """生成は部屋を縮めて・ずらして描き直すことがある（2026-11-15 の右の幕: 下へ行くほど上へずれ、床で約 38px）。
    変えていないはずの所の正方形を ref と突き合わせ、ref の座標 → 生成の座標を 3 次の多項式で解いて、ref の座標へ引き戻す。
    skip は突き合わせに使わない箱（生成が描き換える所）。"""
    import cv2
    rg, ag = ref.mean(2), a.mean(2)
    pts = []
    size, step = 40, 24
    for y0 in list(range(4, H - size - 4, step)) + [H - size - 4]:
        for x0 in list(range(4, W - size - 4, step)) + [W - size - 4]:
            if any(x0 < b[2] and x0 + size > b[0] and y0 < b[3] and y0 + size > b[1] for b in skip):
                continue
            # 枠の端の近くは、はみ出さない側の幅だけで探す（最低 3 画素）。端まで点が無いと多項式が外挿になる
            rx = (min(16, x0), min(16, W - x0 - size))
            ry = (min(44, y0), min(44, H - y0 - size))
            if min(*rx, *ry) < 3:
                continue
            m = _match(rg, ag, x0, y0, size, rx, ry)
            if m and m[2] > 0.8:
                pts.append((x0 + size / 2, y0 + size / 2, m[0], m[1]))
    p = np.array(pts, np.float64)
    X = _poly_terms(p[:, 0], p[:, 1], deg)
    keep = np.ones(len(p), bool)
    for _ in range(6):
        cx, *_ = np.linalg.lstsq(X[keep], p[keep, 2], rcond=None)
        cy, *_ = np.linalg.lstsq(X[keep], p[keep, 3], rcond=None)
        err = np.hypot(X @ cx - p[:, 2], X @ cy - p[:, 3])
        keep = err < max(1.0, 2.5 * np.median(err[keep]))
    gy, gx = np.mgrid[0:H, 0:W].astype(np.float64)
    T = _poly_terms(gx, gy, deg)
    mx = (gx + T @ cx).astype(np.float32)
    my = (gy + T @ cy).astype(np.float32)
    out = cv2.remap(np.ascontiguousarray(a), mx, my, cv2.INTER_CUBIC, borderMode=cv2.BORDER_REPLICATE)
    info = {"points": int(keep.sum()), "of": len(p), "residualPx": round(float(np.median(err[keep])), 2),
            "maxShiftPx": round(float(np.hypot(mx - gx, my - gy).max()), 1)}
    print(f"  register: {info['points']}/{info['of']} 点・残差の中央値 {info['residualPx']}px・最大のずれ {info['maxShiftPx']}px")
    return np.clip(out, 0, 255), info


def fit(gen: Path, ref: np.ndarray, skip=()) -> np.ndarray:
    """生成を 640x480 へ戻して ref の座標へ引き戻し（register）、変わっていない所で色を ref へ合わせる。"""
    a = np.asarray(Image.open(gen).convert("RGB").resize((W, H), Image.LANCZOS), dtype=np.float32)
    a, _ = register(a, ref, skip)
    d = np.abs(a - ref).mean(2)
    calm = d < np.percentile(d, 55)
    for c in range(3):
        k, o = np.polyfit(a[..., c][calm], ref[..., c][calm], 1)
        a[..., c] = np.clip(a[..., c] * k + o, 0, 255)
    return a


def blur(m: np.ndarray, r: float) -> np.ndarray:
    im = Image.fromarray(np.clip(m * 255, 0, 255).astype(np.uint8))
    return np.asarray(im.filter(ImageFilter.GaussianBlur(r)), dtype=np.float32) / 255.0


def grow(m: np.ndarray, r: int) -> np.ndarray:
    im = Image.fromarray((m > 0.5).astype(np.uint8) * 255)
    return np.asarray(im.filter(ImageFilter.MaxFilter(r * 2 + 1)), dtype=np.float32) / 255.0


def shrink(m: np.ndarray, r: int) -> np.ndarray:
    im = Image.fromarray((m > 0.5).astype(np.uint8) * 255)
    return np.asarray(im.filter(ImageFilter.MinFilter(r * 2 + 1)), dtype=np.float32) / 255.0


def keep_largest(m: np.ndarray, min_px: int) -> np.ndarray:
    """小さい塊（描き直しの粒）を落とす。4 近傍の連結成分。"""
    b = m > 0.5
    lab = np.zeros(b.shape, np.int32)
    n = 0
    sizes = [0]
    h, w = b.shape
    for y in range(h):
        for x in range(w):
            if b[y, x] and lab[y, x] == 0:
                n += 1
                stack = [(y, x)]
                lab[y, x] = n
                c = 0
                while stack:
                    cy, cx = stack.pop()
                    c += 1
                    for ny, nx in ((cy + 1, cx), (cy - 1, cx), (cy, cx + 1), (cy, cx - 1)):
                        if 0 <= ny < h and 0 <= nx < w and b[ny, nx] and lab[ny, nx] == 0:
                            lab[ny, nx] = n
                            stack.append((ny, nx))
                sizes.append(c)
    keep = np.array([s >= min_px for s in sizes])
    keep[0] = False
    return keep[lab].astype(np.float32)


def box_mask(box, feather: float) -> np.ndarray:
    m = np.zeros((H, W), np.float32)
    m[box[1]:box[3], box[0]:box[2]] = 1
    return blur(m, feather) if feather > 0 else m


def soft_mask(a: np.ndarray, b: np.ndarray, box, lo: float, hi: float, close: int = 5, feather: float = 2.5) -> np.ndarray:
    """a と b の差の大きさから、変わった所の柔らかいマスクを作る。box の外は 0（縁は 6 画素で落とす）。"""
    d = blur(np.abs(a - b).mean(2) / 255.0, 2.0)
    t = np.clip((d - lo) / (hi - lo), 0, 1)
    m = t * t * (3 - 2 * t)
    hard = shrink(grow(m, close), close)       # 塊の中の穴を塞ぐ
    m = np.maximum(m, blur(hard, feather))
    return np.clip(m * box_mask(box, 6), 0, 1)


def to_frame(m: np.ndarray) -> np.ndarray:
    """素材 640x480（4:3）を枠 640x360（16:9）へ contain-fit で置く。外は 0。"""
    im = Image.fromarray(np.clip(m * 255, 0, 255).astype(np.uint8)).resize((480, 360), Image.BILINEAR)
    out = np.zeros((FH, FW), np.float32)
    out[:, 80:560] = np.asarray(im, dtype=np.float32) / 255.0
    return out


def rail_mask(rail, half: float = 5.0) -> np.ndarray:
    """手すりの帯。引き伸ばすと生成の手すりがずれて段が付くので、元の手すりで上書きする。"""
    (x0, y0), (x1, y1) = rail
    xs = np.arange(W, dtype=np.float32)
    yc = y0 + (y1 - y0) * (xs - x0) / (x1 - x0)
    ys = np.arange(H, dtype=np.float32)[:, None]
    d = np.abs(ys - yc[None, :])
    m = np.clip((half + 1.0 - d) / 2.0, 0, 1)
    m[:, : int(x0)] = 0
    m[:, int(x1):] = 0
    return m.astype(np.float32)


def above_rail(rail, half: float = 5.0, gap: float = 1.0) -> np.ndarray:
    """手すりの帯の上端より上で 1、上端から下で 0（1 画素で落とす）。手すりの x の外は 1。"""
    (x0, y0), (x1, y1) = rail
    xs = np.arange(W, dtype=np.float32)
    top = y0 + (y1 - y0) * (xs - x0) / (x1 - x0) - half - gap
    ys = np.arange(H, dtype=np.float32)[:, None]
    m = np.clip(top[None, :] - ys + 0.5, 0, 1)
    m[:, : int(x0)] = 1
    m[:, int(x1):] = 1
    return m.astype(np.float32)


def build(name: str, laugh_sec: float | None) -> dict:
    cfg = SETS[name]
    wd = ROOT / cfg["work"] if cfg.get("work") else WORK / name
    base = load(ROOT / cfg["base"])
    right_src = cfg.get("right_ratio_src")
    if right_src:
        # 右のしみを前の回の比の画のまま使う。差から取り直すと薄い垂れが閾値で落ちる
        rr = lin(load(ROOT / right_src))
        S = srgb(lin(base) * rr)
        ms = blur(grow((rr.mean(2) < 0.985).astype(np.float32), 2), 1.5)   # 比の画の 1 は 254 で書かれている
    else:
        rr = None
        S = fit(wd / "stain.png", base)
        ms = soft_mask(S, base, BOX_STAIN, 0.025, 0.07)
    bb = cfg["bulge_box"]   # 手すりは描き換えないので、突き合わせには手すりの上端から下を使う
    print("bulge.png"); B = fit(wd / "bulge.png", S, [(bb[0], bb[1], bb[2], min(bb[3], int(min(cfg["rail"][0][1], cfg["rail"][1][1])) - 8))])
    print("doll_laugh.png"); D = fit(wd / "doll_laugh.png", S, [BOX_DOLL_EDIT])
    print("doll_watch.png"); Wt = fit(wd / "doll_watch.png", S, [BOX_DOLL_EDIT])
    S = base * (1 - ms[..., None]) + S * ms[..., None]
    # 伸び出しは手すりの上端で終える。手すりとそこから下は待機の画のまま（生成の手すりを 1 画素も残さない）
    mb = soft_mask(B, S, cfg["bulge_box"], 0.02, 0.07, close=6) * above_rail(cfg["rail"])
    md = soft_mask(D, S, BOX_DOLL, 0.03, 0.08, close=6)
    mw = np.maximum(soft_mask(Wt, S, BOX_DOLL, 0.03, 0.08, close=6), md)
    B = S * (1 - mb[..., None]) + B * mb[..., None]
    D = S * (1 - md[..., None]) + D * md[..., None]
    Wt = S * (1 - mw[..., None]) + Wt * mw[..., None]

    rm = rail_mask(cfg["rail"])[..., None]
    ys, xs = np.nonzero(mb > 0.35)
    tip, root = float(xs.min()), float(xs.max())
    dys, dxs = np.nonzero(md > 0.5)
    wys, wxs = np.nonzero(mw > 0.5)

    t_end = T_END if laugh_sec is None else max(T_END, laugh_sec)
    n = int(round(t_end * FPS))
    frames = []
    for i in range(n):
        t = i / FPS
        img = base * (1 - ms[..., None]) + S * ms[..., None]
        # 伸び出し: 伸び切った画をそのまま置く。途中の形・揺れ・薄めは作らない
        if T_BULGE[0] <= t < T_BULGE[1]:
            img = img * (1 - mb[..., None]) + B * mb[..., None]
        img = img * (1 - rm) + S * rm
        # 人形: 最初のコマから笑っている → 身を引いて見る
        if t < T_SWAP:
            doll, dm = D, md
            jy = int(round(1.0 * math.sin(t * 2 * math.pi * 4.2)))  # 笑いで肩が揺れる
            if jy:
                doll = np.roll(D, jy, axis=0)
                dm = np.roll(md, jy, axis=0)
            img = img * (1 - dm[..., None]) + doll * dm[..., None]
        else:
            mm = np.maximum(md, mw)[..., None]
            img = img * (1 - mm) + Wt * mm
        frames.append(np.clip(img, 0, 255).astype(np.uint8))
    # 最後のコマ = 見ている画
    final = base * (1 - ms[..., None]) + S * ms[..., None]
    mm = np.maximum(md, mw)[..., None]
    final = final * (1 - mm) + Wt * mm
    frames[-1] = np.clip(final, 0, 255).astype(np.uint8)
    for k in range(1, 4):
        frames.append(frames[-1])

    out = ROOT / cfg["out_dir"]
    names = cfg["names"]
    Image.fromarray(frames[-1]).save(out / names["watch"])
    tmp = wd / "frames"
    tmp.mkdir(exist_ok=True)
    for f in tmp.glob("*.png"):
        f.unlink()
    for i, f in enumerate(frames):
        Image.fromarray(f).save(tmp / f"f{i:04d}.png")
    subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-framerate", str(FPS), "-i", str(tmp / "f%04d.png"),
                    "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "16", "-g", str(FPS), "-movflags", "+faststart",
                    str(out / names["burst"])], check=True)

    # 合成範囲（動画と見ている画の共通）。変わりうる所の全部を少し広げる
    region = np.maximum.reduce([ms, mb, md, mw])
    region = blur(grow(region, 6), 4)
    Image.fromarray(np.clip(to_frame(region) * 255, 0, 255).astype(np.uint8)).convert("RGB").save(out / names["mask"])

    # 左右のシミの比の画（CG の人形がついてくる間）
    left = np.asarray(Image.open(ROOT / cfg["left_ratio"]).convert("RGB"), dtype=np.float32)
    left_lin = lin(left)
    right_lin = rr if rr is not None else np.clip(lin(S) / np.maximum(lin(base), 1e-3), 0, 1)
    msr = ms[..., None]
    both = left_lin * (1 - msr) + (left_lin * right_lin) * msr
    Image.fromarray(srgb(both).astype(np.uint8)).save(out / names["ratio"])
    lm = np.asarray(Image.open(ROOT / cfg["left_mask"]).convert("L"), dtype=np.float32) / 255.0
    bm = np.maximum(lm, to_frame(blur(grow(ms, 4), 3)))
    Image.fromarray(np.clip(bm * 255, 0, 255).astype(np.uint8)).convert("RGB").save(out / names["both_mask"])
    # 右のシミだけの比の画と合成範囲（0378: 左のシミと別に、右のシミが 1 秒で滲む・左を消した後も右だけ残る）
    Image.fromarray(srgb(np.ones_like(left_lin) * (1 - msr) + right_lin * msr).astype(np.uint8)).save(out / names["right_ratio"])
    rm_ = to_frame(blur(grow(ms, 4), 3))
    Image.fromarray(np.clip(rm_ * 255, 0, 255).astype(np.uint8)).convert("RGB").save(out / names["right_ratio_mask"])

    meta = {
        "durationSec": round(len(frames) / FPS, 3), "fps": FPS,
        "bulgeTipX": tip, "bulgeRootX": root,
        "dollLaughBox": [int(dxs.min()), int(dys.min()), int(dxs.max()), int(dys.max())],
        "dollWatchBox": [int(wxs.min()), int(wys.min()), int(wxs.max()), int(wys.max())],
        "meanLuma": {"watch": float(frames[-1].mean()), "base": float(base.mean())},
    }
    (out / names["meta"]).write_text(json.dumps(meta, ensure_ascii=False, indent=2), encoding="utf-8")

    # 確かめる画: 元・各段の結果・マスク
    pick = [0, int(0.15 * FPS), int(0.35 * FPS), int(0.6 * FPS), int(1.2 * FPS), int(2.05 * FPS), int(2.3 * FPS), len(frames) - 1]
    sheet = Image.new("RGB", (W * 4, H * 2 + 40 * 2), (20, 20, 20))
    dr = ImageDraw.Draw(sheet)
    for k, fi in enumerate(pick):
        x, y = (k % 4) * W, (k // 4) * (H + 40)
        sheet.paste(Image.fromarray(frames[fi]), (x, y + 40))
        dr.text((x + 8, y + 10), f"{name} t={fi / FPS:.2f}s frame {fi}", fill=(255, 255, 255))
    sheet.save(wd / "sheet_frames.png")
    ms_img = Image.fromarray(np.clip(np.stack([ms, mb, np.maximum(md, mw)], 2) * 255, 0, 255).astype(np.uint8))
    ms_img.save(wd / "sheet_masks.png")
    return meta


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser()
    ap.add_argument("set", choices=sorted(SETS))
    ap.add_argument("--laugh-sec", type=float, default=None, help="笑い声 1 回の長さ。動画の尺をこれ以上にする")
    a = ap.parse_args()
    meta = build(a.set, a.laugh_sec)
    print(json.dumps(meta, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
