# -*- coding: utf-8 -*-
"""**実機のスクリーンに出るまでの経路**を通した絵を作る（素材が届いた姿で判断するため）。

    py -3.11 tools/gen-plate/screen.py --cue gen_dolls_A_left --lap 4 --out logs/gen-plate/screen.png

`posted.py` は post だけを掛けていたが、実機の経路はそれだけではない
（`Assets/Art/Shaders/Streaming/ScreenComposite.shader` が正本）:

    素材 → contain-fit（4:3 を 16:9 の枠へ・letterbox）
         → **mip で痩せる**（周回の劣化。3 周目 A で 267 ブロック ＝ lod 1.68）
         → **色差だけさらに 1 段落とす**（4:2:0）
         → グレア（明るい所の光が回り込む）
         → マスクでライブと混ぜる（**マスクは枠空間**）
         → post（レンズ → センサ → ISP の順）
         → **夜間モード**（周回で色が抜ける。3 周目 A 以降は完全な無彩 ＋ 赤が明るく写る）

⚠⚠ **色は最後に全部消える場面がある。** `_Mono` は劣化と同じ進みなので、
帰りの A（この素材が出る唯一の場所）では **彩度が 0**。しかも輝度の重みが
Rec.601 → (0.52, 0.34, 0.14) へ変わるので **赤い着物は暗くならず明るく写る**。

計算は**リニア空間**で行う（`rules/streaming.md`「post は linear 空間で効く」）。
sRGB のまま計算した数値は丸ごと嘘になる。
"""
from __future__ import annotations

import argparse
import json
import os
import sys

import numpy as np
from PIL import Image

import spec

FRAME_W, FRAME_H = 1280, 720          # 枠は 16:9（`common.js` の MW/MH と同じ比）
FINE_BLOCKS, END_BLOCKS = 800.0, 267.0
GLARE = 0.55                          # CameraFeelFx の const
CHROMA_KILL = 0.85
NOISE_DARK, NOISE_FIXED = 0.10, 0.035
LUMA_601 = np.array([0.299, 0.587, 0.114])
LUMA_IR = np.array([0.52, 0.34, 0.14])
RNG = np.random.default_rng(20260817)


# --------------------------------------------------------------------- 色空間

def srgb_to_linear(a: np.ndarray) -> np.ndarray:
    a = a / 255.0
    return np.where(a <= 0.04045, a / 12.92, ((a + 0.055) / 1.055) ** 2.4)


def linear_to_srgb(a: np.ndarray) -> np.ndarray:
    a = np.clip(a, 0.0, 1.0)
    return np.where(a <= 0.0031308, a * 12.92, 1.055 * a ** (1 / 2.4) - 0.055) * 255.0


# --------------------------------------------------------------------- mip

def mip_chain(img: np.ndarray, levels: int = 8) -> list[np.ndarray]:
    """面積平均のピラミッド（GPU の mip と同じ作り方）。"""
    out = [img]
    cur = img
    for _ in range(levels):
        h, w = cur.shape[:2]
        if h < 2 or w < 2:
            break
        cur = cur[:h - h % 2, :w - w % 2]
        cur = (cur[0::2, 0::2] + cur[1::2, 0::2] + cur[0::2, 1::2] + cur[1::2, 1::2]) * 0.25
        out.append(cur)
    return out


def _bilinear(level: np.ndarray, u: np.ndarray, v: np.ndarray) -> np.ndarray:
    h, w = level.shape[:2]
    x = np.clip(u * w - 0.5, 0, w - 1)
    y = np.clip(v * h - 0.5, 0, h - 1)
    x0, y0 = np.floor(x).astype(int), np.floor(y).astype(int)
    x1, y1 = np.minimum(x0 + 1, w - 1), np.minimum(y0 + 1, h - 1)
    fx, fy = (x - x0)[..., None], (y - y0)[..., None]
    return ((level[y0, x0] * (1 - fx) + level[y0, x1] * fx) * (1 - fy)
            + (level[y1, x0] * (1 - fx) + level[y1, x1] * fx) * fy)


def sample_lod(chain: list[np.ndarray], u: np.ndarray, v: np.ndarray, lod: float) -> np.ndarray:
    """trilinear（2 段を補間）。"""
    lod = max(0.0, min(lod, len(chain) - 1.001))
    lo = int(np.floor(lod))
    f = lod - lo
    a = _bilinear(chain[lo], u, v)
    if f <= 1e-4:
        return a
    return a * (1 - f) + _bilinear(chain[min(lo + 1, len(chain) - 1)], u, v) * f


# --------------------------------------------------------------------- 経路

def contain_scale(src_size, frame_aspect: float) -> tuple[float, float]:
    sa = src_size[0] / src_size[1]
    return (sa / frame_aspect, 1.0) if sa > frame_aspect else (1.0, frame_aspect / sa)


def sample_source(chain, uv_u, uv_v, scale, lod, chroma_bias) -> tuple[np.ndarray, np.ndarray]:
    """contain-fit して引く。枠外は 0（letterbox）。"""
    su = (uv_u - 0.5) / max(scale[0], 1e-4) + 0.5
    sv = (uv_v - 0.5) / max(scale[1], 1e-4) + 0.5
    inside = ((su >= 0) & (su <= 1) & (sv >= 0) & (sv <= 1)).astype(np.float64)
    su, sv = np.clip(su, 0, 1), np.clip(sv, 0, 1)
    col = sample_lod(chain, su, sv, lod)
    if chroma_bias > 0.001:                       # 色差だけ 1 段落とす（4:2:0）
        wide = sample_lod(chain, su, sv, lod + chroma_bias)
        y = (col * LUMA_601).sum(-1, keepdims=True)
        col = np.maximum(y + (wide - (wide * LUMA_601).sum(-1, keepdims=True)), 0.0)
    if GLARE > 0.001:                             # レンズの内面反射
        far = sample_lod(chain, su, sv, min(lod + 4.5, len(chain) - 1.001))
        col = col + np.maximum(far - np.maximum(col, 0.45), 0.0) * (GLARE * 2.2)
    return col * inside[..., None], inside


def post(col: np.ndarray, uv_u, uv_v, p: dict, mono: float, lod: float) -> np.ndarray:
    """レンズ → センサ → ISP。**順序は shader と同じ**（見た目の飾りではない）。"""
    dx, dy = uv_u - 0.5, uv_v - 0.5
    r2 = np.clip((dx * dx + dy * dy) * 4.0, 0, 1)

    vig = min(p["vignette"] + mono * 0.16, 1.0)
    col = col * (1.0 - vig * 0.58 * r2 * r2)[..., None]

    # センサ: 光ショット + 読み出し（粗い画ほど符号化が捨てる）
    y = np.maximum((col * LUMA_601).sum(-1), 0.0)
    grain_px = max(2.0 ** lod, 1.0)
    amp = (np.sqrt(y) * 0.030 + NOISE_DARK * 0.30) * (1.0 + mono * 2.6) / max(grain_px ** 0.6, 1.0)
    col = col + (RNG.random(col.shape[:2]) - 0.5)[..., None] * amp[..., None]
    col = col * (1.0 + (RNG.random(col.shape[:2]) - 0.5)[..., None] * NOISE_FIXED)

    # ISP
    col = np.maximum(col, 0.0) * (2.0 ** (p["exposure"] + mono * 0.95))
    col[..., 0] *= 1.0 + 0.25 * p["temperature"]
    col[..., 2] *= 1.0 - 0.25 * p["temperature"]
    col[..., 1] *= 1.0 + 0.25 * p["tint"]
    col[..., 0] *= 1.0 - 0.12 * p["tint"]
    col[..., 2] *= 1.0 - 0.12 * p["tint"]
    col = (col - 0.5) * (p["contrast"] + mono * 0.10) + 0.5
    col = col * (1.0 - p["lift"]) + p["lift"]

    luma = ((col * LUMA_601).sum(-1) * (1 - mono) + (col * LUMA_IR).sum(-1) * mono)
    sat = p["saturation"] + max(1.0 - p["saturation"], 0.0) * np.clip(luma * 3.33, 0, 1) * CHROMA_KILL
    sat = sat * (1.0 - mono)
    col = luma[..., None] + (col - luma[..., None]) * sat[..., None]
    return col


def render(live_path, overlay_path, mask_path, p: dict, blocks: float, mono: float,
           frame=(FRAME_W, FRAME_H)) -> np.ndarray:
    live = srgb_to_linear(np.asarray(Image.open(live_path).convert("RGB"), dtype=np.float64))
    src_size = (live.shape[1], live.shape[0])
    scale = contain_scale(src_size, frame[0] / frame[1])

    lod0 = 0.0
    chroma_bias = 0.0
    if blocks > 0.5:
        across = max(blocks * max(scale[0], 1e-3), 1.0)
        lod0 = max(np.log2(src_size[0] / across), 0.0)
        chroma_bias = 1.0

    uv_u, uv_v = np.meshgrid((np.arange(frame[0]) + 0.5) / frame[0],
                             (np.arange(frame[1]) + 0.5) / frame[1])
    dx, dy = uv_u - 0.5, uv_v - 0.5
    r2 = np.clip((dx * dx + dy * dy) * 4.0, 0, 1)
    lod = lod0 + np.clip(r2 - 0.45, 0, None).mean() * 0.7   # 像面湾曲（面内で平均して 1 値に）

    col, _ = sample_source(mip_chain(live), uv_u, uv_v, scale, lod, chroma_bias)
    if overlay_path:
        ov = srgb_to_linear(np.asarray(Image.open(overlay_path).convert("RGB"), dtype=np.float64))
        ov_col, _ = sample_source(mip_chain(ov), uv_u, uv_v,
                                  contain_scale((ov.shape[1], ov.shape[0]), frame[0] / frame[1]),
                                  lod, chroma_bias)
        if mask_path:
            m = np.asarray(Image.open(mask_path).convert("L").resize(frame, Image.BILINEAR),
                           dtype=np.float64) / 255.0
        else:
            m = np.ones(frame[::-1])
        col = col * (1 - m[..., None]) + ov_col * m[..., None]

    return post(col, uv_u, uv_v, p, mono, lod)


# --------------------------------------------------------------------- CLI

def load_show():
    with open(os.path.join(spec.REPO, "tools", "web-compositor", "show.json"),
              encoding="utf-8") as f:
        return json.load(f)


def main() -> int:
    ap = argparse.ArgumentParser(description="実機の経路を通した絵")
    ap.add_argument("--cue", help="show.json の cue id（素材とマスクを引く）")
    ap.add_argument("--live", help="ライブ側に置くプレート（既定は cue と同じカメラの plate）")
    ap.add_argument("--overlay", help="cue を使わず素材を直接指定")
    ap.add_argument("--mask")
    ap.add_argument("--lap", type=float, default=4.0, help="何周目として劣化させるか（1..4）")
    ap.add_argument("--no-mono", action="store_true", help="夜間モードを切って見る（比較用）")
    ap.add_argument("--out", default="logs/gen-plate/screen.png")
    args = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")

    show = load_show()
    p = dict(exposure=-1.05, contrast=1.12, saturation=0.52, temperature=0.48,
             tint=0.0, lift=0.02, vignette=0.38)
    for k, v in (show.get("post") or {}).items():
        if k in p and isinstance(v, (int, float)):
            p[k] = float(v)

    web = os.path.join(spec.REPO, "tools", "web-compositor")
    overlay, mask, live = args.overlay, args.mask, args.live
    if args.cue:
        cue = next((c for c in show.get("cues", []) if c.get("id") == args.cue), None)
        if not cue:
            raise SystemExit(f"cue が無い: {args.cue}")
        overlay = overlay or os.path.join(web, cue["sourceUrl"].lstrip("/"))
        mask = mask or (os.path.join(web, cue["maskUrl"].lstrip("/")) if cue.get("maskUrl") else None)
    if not live:
        raise SystemExit("--live（ライブ側のプレート）が要る")

    total = max(2.0, float((show.get("run") or {}).get("totalLaps", 3)))
    progress = min(max((args.lap - 1.0) / (total - 1.0), 0.0), 1.0)
    blocks = FINE_BLOCKS + (END_BLOCKS - FINE_BLOCKS) * progress
    mono = 0.0 if args.no_mono else progress

    col = render(live, overlay, mask, p, blocks, mono)
    os.makedirs(os.path.dirname(args.out) or ".", exist_ok=True)
    img = linear_to_srgb(col).astype(np.uint8)
    Image.fromarray(img).save(args.out)
    print(f"{args.out}  周 {args.lap:.0f} → 劣化 {progress:.2f} / {blocks:.0f} ブロック / "
          f"夜間モード {mono:.2f}")

    if mask:
        print(seam_report(img, mask))
    return 0


def seam_report(img: np.ndarray, mask_path: str) -> str:
    """**継ぎ目**を届いた画で測る（素材と実写が半分ずつ出る場面の唯一の合否）。

    素材そのものがどれだけ良くても、**隣の実写と繋がらなければ 2 枚の写真を貼った画**になる。
    ここが本番で最初に見える破綻で、素材の中を見ている限り 1 度も現れない。
    """
    g = np.asarray(Image.fromarray(img).convert("L"), dtype=np.float64)
    h, w = g.shape
    m = np.asarray(Image.open(mask_path).convert("L").resize((w, h), Image.BILINEAR),
                   dtype=np.float64) / 255.0
    col = m.mean(axis=0)
    if col.max() - col.min() < 0.2:
        return "  （全面マスクなので継ぎ目は無い）"
    x = int(np.argmin(np.abs(col - 0.5)))
    band = max(8, w // 16)
    lo, hi = max(1, x - band), min(w - 2, x + band)
    left, right = g[:, lo:x - 8].mean(), g[:, x + 8:hi].mean()
    step = float(np.abs(g[:, x - 1] - g[:, x + 1]).mean())
    # ⚠ ここは**参考値**。合否は `deliver.py` が 1 か所で持つ
    #   （この生の値には「その列にもともとある縦の構造」が下駄として乗っている。
    #   恒等の入力＝素材がプレートそのものでも 5.1 出た。同じ数字を 2 か所に置かない）
    return (f"  継ぎ目 x={x}（枠の {x / w:.2f}）  素材側 {left:.1f} / 実写側 {right:.1f}"
            f"  比 {left / max(right, 1e-6):.2f}  勾配 {step:.1f}"
            f"  ← 合否は deliver.py で見る")


if __name__ == "__main__":
    raise SystemExit(main())
