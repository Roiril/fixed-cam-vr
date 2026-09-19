"""闇の目の版を焼く（canon/LEDGER.md 0238「ほんとうに参考画像を真似して」）。

入力: tools/eyes-ref/gen/*.png（Codex が描いた「壊れた画像データでできた目」。黒地・目 1 つ・中央）
出力: Assets/Resources/Eyes/EyeGlitch.png（2048x2048 RGBA・2x2 の 4 個体）と tools/eyes-ref/gen/atlas.json

各個体は次の形に揃える（シェーダ AnomalyEyes.shader の TEX_* と対）:
  - 目（alpha > 0 の画素の箱）の横幅が 1024 のタイルの 0.92 を占め、縦は横の TEX_RATIO 倍に**引き伸ばす**
    （写真の目は 0.55〜0.75 なので、伸縮は最大 2 割）
  - 目の中心はタイルの中心
  - 黒の地は alpha 0（輝度の閾値 + 小さな膨張）。目の中の黒い欠けも alpha 0 になる ＝ 闇がそのまま透ける

⚠ 個体が 4 に満たなければ、ある個体を**行の刻み直し（datamosh）で別の個体に化けさせて**埋める。
   同じ画素をそのまま 2 度置くと、群れの中で同じ目が 2 つ並んだときに「コピー」に見える。

使い方: py -3.11 tools/make-eye-glitch.py [--ratio 0.66] [--check]
  --check は焼いた版の各タイルを参考画像と同じ物差し（measure_ref と同じ 4 指標）で測って出すだけ。
"""
from __future__ import annotations

import argparse
import glob
import io
import json
import os
import sys

import numpy as np
from PIL import Image, ImageFilter

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
GEN_DIR = os.path.join(ROOT, "tools", "eyes-ref", "gen")
OUT_PNG = os.path.join(ROOT, "Assets", "Resources", "Eyes", "EyeGlitch.png")
OUT_JSON = os.path.join(GEN_DIR, "atlas.json")
TILE = 1024
COLS = 2
ROWS = 2
EYE_W = 0.92          # タイル幅に対する目の横幅（シェーダ TEX_EYE_W と対）
LIT_V = 0.10          # これより暗い画素は地（alpha 0）
SEED = 20260919


def log(msg: str) -> None:
    sys.stdout.write(msg + "\n")


def load_rgb(path: str) -> np.ndarray:
    return np.asarray(Image.open(path).convert("RGB")).astype(np.float32) / 255.0


def coverage(rgb: np.ndarray) -> np.ndarray:
    """点灯している画素 = alpha 1。黒の地と目の中の黒い欠けは 0。"""
    v = rgb.max(axis=2)
    lit = (v > LIT_V).astype(np.uint8) * 255
    # 1 画素の孤立点は残す（参考画像の柱は 1〜2 画素の行でできている）が、
    # 縁のにじみ（JPEG のリンギング）は落とす: 2px の開きを 1 度だけ
    im = Image.fromarray(lit, "L").filter(ImageFilter.MinFilter(3)).filter(ImageFilter.MaxFilter(3))
    a = np.asarray(im).astype(np.float32) / 255.0
    # 開きで消えた 1 画素の点を戻す（点は点のまま残す）
    a = np.maximum(a, (v > LIT_V * 2.0).astype(np.float32) * (v > 0.25))
    return a


def eye_box(alpha: np.ndarray) -> tuple[int, int, int, int]:
    """目の本体の箱。上下の柱を含めないよう、行ごとの点灯数が最大の 12% を超える行だけを目とみなす。"""
    rows = alpha.sum(axis=1)
    cols = alpha.sum(axis=0)
    ry = np.where(rows > rows.max() * 0.12)[0]
    cx = np.where(cols > cols.max() * 0.06)[0]
    return int(cx.min()), int(ry.min()), int(cx.max()) + 1, int(ry.max()) + 1


def datamosh(rgb: np.ndarray, alpha: np.ndarray, rng: np.random.Generator, strength: float) -> tuple[np.ndarray, np.ndarray]:
    """行の刻み直し・ブロックの複製・欠け・画素ノイズ。個体を増やすときと、生成物の壊れ方が足りないときに。"""
    h, w, _ = rgb.shape
    out = rgb.copy()
    oa = alpha.copy()
    # 1) 帯ごとの横ずれ（帯の高さ 2〜40px・裾の重い分布）
    y = 0
    while y < h:
        bh = int(rng.integers(2, 40))
        if rng.random() < 0.35 * strength:
            sh = int(np.sign(rng.random() - 0.5) * (abs(rng.standard_normal()) ** 2.2) * 24 * strength)
            out[y:y + bh] = np.roll(out[y:y + bh], sh, axis=1)
            oa[y:y + bh] = np.roll(oa[y:y + bh], sh, axis=1)
        y += bh
    # 2) ブロックの複製（8 の倍数の矩形を近くへ写す）
    for _ in range(int(40 * strength)):
        bw, bh = int(rng.integers(1, 12)) * 8, int(rng.integers(1, 6)) * 8
        sx, sy = int(rng.integers(0, w - bw)), int(rng.integers(0, h - bh))
        dx = int(np.clip(sx + rng.integers(-64, 64), 0, w - bw))
        dy = int(np.clip(sy + rng.integers(-24, 24), 0, h - bh))
        if oa[sy:sy + bh, sx:sx + bw].mean() < 0.3:
            continue
        out[dy:dy + bh, dx:dx + bw] = out[sy:sy + bh, sx:sx + bw]
        oa[dy:dy + bh, dx:dx + bw] = oa[sy:sy + bh, sx:sx + bw]
    # 3) 欠け（黒い矩形）
    for _ in range(int(18 * strength)):
        bw, bh = int(rng.integers(1, 10)) * 8, int(rng.integers(1, 4)) * 8
        sx, sy = int(rng.integers(0, w - bw)), int(rng.integers(0, h - bh))
        oa[sy:sy + bh, sx:sx + bw] = 0.0
    # 4) 画素ノイズ（青緑・白・赤の 1 画素）
    n = int(1500 * strength)
    xs, ys = rng.integers(0, w, n), rng.integers(0, h, n)
    palette = np.array([[0.2, 0.8, 0.8], [1.0, 1.0, 1.0], [0.9, 0.15, 0.25], [0.3, 0.5, 1.0]], np.float32)
    pick = palette[rng.integers(0, len(palette), n)]
    keep = oa[ys, xs] > 0.5
    out[ys[keep], xs[keep]] = pick[keep]
    return out, oa


def fit_tile(rgb: np.ndarray, alpha: np.ndarray, ratio: float) -> tuple[np.ndarray, np.ndarray]:
    """目の箱をタイルの中央へ、横幅 EYE_W・縦は横の ratio 倍に引き伸ばして置く。柱は目の外側にそのまま付いてくる。"""
    x0, y0, x1, y1 = eye_box(alpha)
    ew, eh = x1 - x0, y1 - y0
    sx = TILE * EYE_W / ew
    sy = TILE * EYE_W * ratio / eh
    rgba = np.dstack([rgb, alpha[..., None]])
    im = Image.fromarray((np.clip(rgba, 0, 1) * 255).astype(np.uint8), "RGBA")
    nw, nh = int(round(im.width * sx)), int(round(im.height * sy))
    im = im.resize((nw, nh), Image.LANCZOS)
    # 目の箱の中心をタイルの中心へ
    cx, cy = (x0 + x1) * 0.5 * sx, (y0 + y1) * 0.5 * sy
    tile = Image.new("RGBA", (TILE, TILE), (0, 0, 0, 0))
    tile.paste(im, (int(round(TILE / 2 - cx)), int(round(TILE / 2 - cy))), im)
    arr = np.asarray(tile).astype(np.float32) / 255.0
    return arr[..., :3], arr[..., 3]


def measure(rgb: np.ndarray, alpha: np.ndarray) -> dict:
    """measure_ref.py と同じ物差し（点灯した画素の色の割合・彩度・輪郭の内側の闇）。"""
    lit = alpha > 0.5
    r, g, b = rgb[..., 0], rgb[..., 1], rgb[..., 2]
    v = rgb.max(axis=2)
    mn = rgb.min(axis=2)
    s = np.where(v > 1e-6, (v - mn) / np.maximum(v, 1e-6), 0)
    d = np.maximum(v - mn, 1e-6)
    hue = np.zeros_like(v)
    m = v == r
    hue[m] = (60 * ((g - b) / d) % 360)[m]
    m = (v == g) & ~(v == r)
    hue[m] = (60 * ((b - r) / d) + 120)[m]
    m = (v == b) & ~(v == r) & ~(v == g)
    hue[m] = (60 * ((r - g) / d) + 240)[m]
    hl, sl = hue[lit], s[lit]
    white = sl < 0.25
    cyan = (~white) & (hl >= 150) & (hl <= 215)
    red = (~white) & ((hl >= 330) | (hl <= 20))
    inside_dark = inside_all = 0
    for yy in range(lit.shape[0]):
        idx = np.where(lit[yy])[0]
        if len(idx) < 2:
            continue
        seg = lit[yy, idx[0]:idx[-1] + 1]
        inside_all += len(seg)
        inside_dark += int((~seg).sum())
    return {
        "lit": float(lit.mean()),
        "white": float(white.mean()) if lit.any() else 0.0,
        "cyan": float(cyan.mean()) if lit.any() else 0.0,
        "red": float(red.mean()) if lit.any() else 0.0,
        "sat": float(sl.mean()) if lit.any() else 0.0,
        "dark_inside": inside_dark / max(inside_all, 1),
    }


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--ratio", type=float, default=0.60, help="版の目の 縦/横（シェーダ TEX_RATIO と対）")
    ap.add_argument("--mosh", type=float, default=0.0, help="生成物にさらに掛ける壊し（0 = 掛けない）")
    ap.add_argument("--check", action="store_true", help="焼かずに測るだけ")
    args = ap.parse_args()

    srcs = sorted(p for p in glob.glob(os.path.join(GEN_DIR, "glitch_*.png")))
    if not srcs:
        log("no tools/eyes-ref/gen/glitch_*.png")
        return 1
    rng = np.random.default_rng(SEED)
    tiles: list[tuple[str, np.ndarray, np.ndarray]] = []
    for p in srcs:
        rgb = load_rgb(p)
        a = coverage(rgb)
        if args.mosh > 0:
            rgb, a = datamosh(rgb, a, rng, args.mosh)
        tiles.append((os.path.basename(p), *fit_tile(rgb, a, args.ratio)))
    # 4 個体に満たなければ刻み直しで化けさせて埋める
    k = 0
    while len(tiles) < COLS * ROWS:
        name, rgb, a = tiles[k % len(srcs)]
        rgb2, a2 = datamosh(rgb, a, rng, 1.0)
        # 左右反転も混ぜる（同じ目が 2 つ並んで見えない）
        if k % 2 == 0:
            rgb2, a2 = rgb2[:, ::-1], a2[:, ::-1]
        tiles.append((name + "+mosh%d" % k, rgb2, a2))
        k += 1

    meta = {"tile": TILE, "cols": COLS, "rows": ROWS, "eye_w": EYE_W, "ratio": args.ratio, "variants": []}
    atlas = np.zeros((TILE * ROWS, TILE * COLS, 4), np.float32)
    for i, (name, rgb, a) in enumerate(tiles[: COLS * ROWS]):
        cx, cy = i % COLS, i // COLS
        atlas[cy * TILE:(cy + 1) * TILE, cx * TILE:(cx + 1) * TILE, :3] = rgb
        atlas[cy * TILE:(cy + 1) * TILE, cx * TILE:(cx + 1) * TILE, 3] = a
        m = measure(rgb, a)
        meta["variants"].append({"index": i, "source": name, **m})
        log("tile %d %-24s lit %.3f  cyan %.2f white %.2f red %.2f  sat %.2f  dark_inside %.2f"
            % (i, name, m["lit"], m["cyan"], m["white"], m["red"], m["sat"], m["dark_inside"]))
    if args.check:
        return 0
    os.makedirs(os.path.dirname(OUT_PNG), exist_ok=True)
    # 前乗算しない（シェーダが a を掛ける）。alpha 0 の画素の色は 0 にしておく（mip で滲まない）
    atlas[..., :3] *= atlas[..., 3:4]
    Image.fromarray((np.clip(atlas, 0, 1) * 255).astype(np.uint8), "RGBA").save(OUT_PNG, optimize=True)
    with io.open(OUT_JSON, "w", encoding="utf-8", newline="\n") as f:
        json.dump(meta, f, ensure_ascii=False, indent=1)
    log("wrote %s (%dx%d) and %s" % (os.path.relpath(OUT_PNG, ROOT), atlas.shape[1], atlas.shape[0],
                                      os.path.relpath(OUT_JSON, ROOT)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
