"""2 周目 A の右の幕の素材（LEDGER 0376）を、Codex の静止画 4 枚から組む。

    py -3.11 tools/gen-plate/stain3/build_right_burst.py real   # 本番 → tools/web-compositor/static-inputs/
    py -3.11 tools/gen-plate/stain3/build_right_burst.py twin   # ツイン → tools/twin/assets/

入力（Logs/gen-plate/stain2A_right_20261115/<real|twin>/。作り方は README の「右の幕」）:
  stain.png       右の幕に顔のシミを足した画（待機の 2 倍から）
  bulge.png       シミの所から顔が左へ伸び出した画（stain から）
  doll_laugh.png  右の幕の裏で人形が笑っている画（stain から）
  doll_watch.png  人形が身を引いて黙って見ている画（doll_laugh から）

出すもの:
  *_right_burst_*.mp4   640x480・24fps。シミが出る → 顔が左へ伸び出しながら人形が笑う → 顔が戻り人形が身を引く
  *_right_watch_*.png   動画の最後のコマと同じ画（隠れて見ている人形）
  *_right_mask_*.png    枠空間 640x360 の合成範囲（右の幕と人形の所だけ）
  *_both_ratio_*.png    左右のシミの比の画（線形の比を sRGB で符号化）
  *_both_mask_*.png     左右のシミの合成範囲（枠空間）
  *_right_burst_*.json  尺と、人形の頭の位置（台本と検査が読む）
  Logs/gen-plate/stain2A_right_20261115/<名>/sheet_*.png  確かめる画

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
    "real": dict(
        base="tools/web-compositor/static-inputs/stain_A_idle_20261112.png",
        left_ratio="tools/web-compositor/static-inputs/stain_A_ratio_20261113.png",
        left_mask="tools/web-compositor/static-inputs/stain_A_press_leftmask_20260925.png",
        out_dir="tools/web-compositor/static-inputs",
        bulge_box=(282, 40, 512, 240),
        rail=((250, 229), (515, 237)),   # 手すりの芯（元の画素）。伸び出しの手前に元の手すりを貼り直す
        names=dict(burst="stain_A_right_burst_20261115.mp4", watch="stain_A_right_watch_20261115.png",
                   mask="stain_A_right_mask_20261115.png", ratio="stain_A_both_ratio_20261115.png",
                   both_mask="stain_A_both_mask_20261115.png", meta="stain_A_right_burst_20261115.json"),
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
                   both_mask="twin_stainA_both_mask.png", meta="twin_stainA_right_burst.json"),
    ),
}

# 生成が描き換えてよい範囲（元の画素 640x480・x0, y0, x1, y1）。左の幕とその染みは外す
BOX_STAIN = (350, 50, 515, 238)    # 右の幕の顔のシミ
BOX_DOLL = (478, 236, 630, 476)    # 右の幕の縁の裏の人形と、その影
# 尺（秒）。笑い声 1 回（sfx_doll_laugh_once）に合わせる
T_STAIN = 0.30       # 右のシミが滲み出る
T_OUT = (0.05, 0.55)  # 顔が左へ伸び出す（ばねで少し行き過ぎる）
T_BACK = (1.50, 2.00)  # 顔が戻る
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


def fit(gen: Path, ref: np.ndarray) -> np.ndarray:
    """生成を 640x480 へ戻し、変わっていない所で色を ref へ合わせる。"""
    a = np.asarray(Image.open(gen).convert("RGB").resize((W, H), Image.LANCZOS), dtype=np.float32)
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


def ease_out(t):
    t = np.clip(t, 0, 1)
    return 1 - (1 - t) ** 3


def spring(t):
    """0→1。少し行き過ぎて戻る（頭を突き出す勢い）。"""
    t = np.clip(t, 0, 1)
    return 1 - math.exp(-6.0 * t) * math.cos(7.5 * t) if t < 1 else 1.0


def smooth(e0: float, e1: float, x: float) -> float:
    t = min(1.0, max(0.0, (x - e0) / (e1 - e0)))
    return t * t * (3 - 2 * t)


def warp_bulge(b: np.ndarray, s: np.ndarray, mb: np.ndarray, root: float, tip: float, p: float, wob: float,
               alpha: float = 1.0):
    """伸び出しの途中。root（右の付け根）から左へ p の割合まで伸ばした画を作る。

    表示の x ∈ [root - p·L, root] を、伸び切った画の x ∈ [tip, root] へ線形に写す。L = root - tip。
    """
    if p <= 0.001:
        return s.copy(), np.zeros(mb.shape, np.float32)
    L = root - tip
    xs = np.arange(W, dtype=np.float32)
    src = root - (root - xs) / p
    src = np.clip(src, 0, W - 1)
    ys = np.arange(H, dtype=np.float32)
    # 笑いに合わせて顔が細かく揺れる（縦 wob 画素）
    dy = wob * np.sin((root - xs) / max(L, 1) * math.pi)
    yy = np.clip(ys[:, None] - dy[None, :], 0, H - 1)
    xx = np.broadcast_to(src[None, :], (H, W))
    x0 = np.floor(xx).astype(int)
    y0 = np.floor(yy).astype(int)
    x1 = np.minimum(x0 + 1, W - 1)
    y1 = np.minimum(y0 + 1, H - 1)
    fx = (xx - x0)[..., None]
    fy = (yy - y0)[..., None]

    def samp(img):
        if img.ndim == 2:
            img = img[..., None]
        v = (img[y0, x0] * (1 - fx) * (1 - fy) + img[y0, x1] * fx * (1 - fy)
             + img[y1, x0] * (1 - fx) * fy + img[y1, x1] * fx * fy)
        return v

    bw = samp(b)
    mw = samp(mb)[..., 0]
    valid = (xs >= root - p * L - 2).astype(np.float32)[None, :]
    mw = mw * valid
    a = np.clip(mw * alpha, 0, 1)
    return bw, a


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


def build(name: str, laugh_sec: float | None) -> dict:
    cfg = SETS[name]
    wd = WORK / name
    base = load(ROOT / cfg["base"])
    S = fit(wd / "stain.png", base)
    B = fit(wd / "bulge.png", S)
    D = fit(wd / "doll_laugh.png", S)
    Wt = fit(wd / "doll_watch.png", D)

    ms = soft_mask(S, base, BOX_STAIN, 0.025, 0.07)
    S = base * (1 - ms[..., None]) + S * ms[..., None]
    mb = soft_mask(B, S, cfg["bulge_box"], 0.02, 0.07, close=6)
    md = soft_mask(D, S, BOX_DOLL, 0.03, 0.08, close=6)
    mw = np.maximum(soft_mask(Wt, S, BOX_DOLL, 0.03, 0.08, close=6), md)
    B = S * (1 - mb[..., None]) + B * mb[..., None]
    D = S * (1 - md[..., None]) + D * md[..., None]
    Wt = S * (1 - mw[..., None]) + Wt * mw[..., None]

    rm = rail_mask(cfg["rail"])[..., None]
    ys, xs = np.nonzero(mb > 0.35)
    tip, root = float(xs.min()), float(xs.max())
    # 伸び出しは右のシミの所から出て、シミの所へ戻る。付け根（幕の右端）まで縮めると、顔が幕の端へ
    # 押し潰されて平らなシミと 2 つ並んで見えた（2026-11-15）。p0 = シミの左端まで伸ばした割合。
    sys_, sxs = np.nonzero(ms > 0.35)
    p0 = float(np.clip((root - float(sxs.min())) / max(root - tip, 1.0), 0.0, 0.9))
    dys, dxs = np.nonzero(md > 0.5)
    wys, wxs = np.nonzero(mw > 0.5)

    t_end = T_END if laugh_sec is None else max(T_END, laugh_sec)
    n = int(round(t_end * FPS))
    frames = []
    for i in range(n):
        t = i / FPS
        a_s = float(ease_out(t / T_STAIN))
        img = base * (1 - a_s * ms[..., None]) + S * (a_s * ms[..., None])
        # 伸び出し: 出る → 笑いに合わせて脈打つ → 戻る
        if t < T_BACK[0]:
            g = spring((t - T_OUT[0]) / (T_OUT[1] - T_OUT[0])) if t >= T_OUT[0] else 0.0
            # 出るときはシミの右寄り（p0 の半分）から伸び出す。p0 から出すと伸びる距離が 50 画素しかなく、動きに見えない
            p = 0.5 * p0 + (1.0 - 0.5 * p0) * g
            alpha = min(1.0, g / 0.25)
            if t > T_OUT[1]:
                p = 1.0 + 0.035 * math.sin((t - T_OUT[1]) * 2 * math.pi * 4.2)
        else:
            u = min(1.0, (t - T_BACK[0]) / (T_BACK[1] - T_BACK[0]))
            p = 1.0 - (1.0 - p0) * float(ease_out(u))
            alpha = 1.0 - smooth(0.45, 1.0, u)
        if t < T_OUT[0] or alpha <= 0.001:
            p, alpha = 0.0, 0.0
        wob = 1.2 * math.sin(t * 2 * math.pi * 4.2) if T_OUT[1] < t < T_BACK[0] else 0.0
        bw, ab = warp_bulge(B, S, mb, root, tip, p, wob, alpha)
        img = img * (1 - ab[..., None]) + bw * ab[..., None]
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
    right_lin = np.clip(lin(S) / np.maximum(lin(base), 1e-3), 0, 1)
    msr = ms[..., None]
    both = left_lin * (1 - msr) + (left_lin * right_lin) * msr
    Image.fromarray(srgb(both).astype(np.uint8)).save(out / names["ratio"])
    lm = np.asarray(Image.open(ROOT / cfg["left_mask"]).convert("L"), dtype=np.float32) / 255.0
    bm = np.maximum(lm, to_frame(blur(grow(ms, 4), 3)))
    Image.fromarray(np.clip(bm * 255, 0, 255).astype(np.uint8)).convert("RGB").save(out / names["both_mask"])

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
