# -*- coding: utf-8 -*-
"""**焼き直さずに**、既にある素材の異変を読めるようにする（`canon/LEDGER.md` 0143）。

    # まず振って比べる（判断はこの表でする）
    py -3.11 tools/gen-plate/boost.py --material <素材.png> --plate <素材を作ったプレート.jpg> \
        --mask <cue のマスク.png> --lap 2 --sweep 1,1.5,2,2.5,3

    # 決めたら 1 枚出す
    py -3.11 tools/gen-plate/boost.py --material <素材.png> --plate <プレート.jpg> --mask <マスク.png> \
        --expand 2.5 --out <出力.png>

なぜ要るか: 素材の中でははっきり顔に見える染みが、実機の post（露出 −1.75）を通ると読めなくなる。
バックルームズ（0141）は post を外して解いたが、**染みは前後のカットと地続き**なので外せない
（ユーザー判定 0143「ポストは掛けるけど違和感なく顔が見えるように。生成しなおすんじゃなく」）。

## 2 つの向きがあり、効くのは片方だけ

- `--gain`: 染めた量（プレート − 素材）を何倍にするか ＝ **暗くする向き**。
  ⚠⚠ **効かない**（2026-09-04 実測）。post の露出 −1.75 は暗部を潰すので、濃くしたぶんが黒へ張り付く。
  gain 3.5 で 36% が潰れ、届いた濃淡は 58 → 63 しか動かなかった
- `--expand`: マスクの中の**局所コントラストを中央値のまわりで広げる** ＝ **明部を持ち上げる向き**。
  ⭐ こちらが効く。post は暗部を潰すが明部は残すので、**顔を運ぶのは「染まらず残った淡い面」**になる
  （`canon/LEDGER.md` 0086 の「顔は染まらずに残った所」と同じ理屈）

どちらも**形は 1 画素も動かさない**（＝ 焼き直しではない）。下地（布の織り・襞）も同じ比で動くので潰れない。
"""
from __future__ import annotations

import argparse
import os
import sys

import numpy as np
from PIL import Image, ImageFilter

import metrics
import screen


def load_mask_src(mask_path: str, size) -> np.ndarray:
    """枠空間 640x360 のマスク → 素材座標（contain-fit の逆）。"""
    m = Image.open(mask_path).convert("L")
    fw, fh = m.size
    w, h = size
    fa, sa = fw / fh, w / h
    if sa > fa:
        iw, ih = fw, max(1, round(fw / sa))
    else:
        iw, ih = max(1, round(fh * sa)), fh
    ox, oy = (fw - iw) // 2, (fh - ih) // 2
    inner = m.crop((ox, oy, ox + iw, oy + ih)).resize(size, Image.LANCZOS)
    return np.asarray(inner, dtype=np.float64) / 255.0


def boost(mat: np.ndarray, plate: np.ndarray, alpha: np.ndarray,
          gain: float, floor: float) -> np.ndarray:
    """染めた量（プレート − 素材）をマスクの中で gain 倍する（暗くする向き。効きにくい）。"""
    dark = plate - mat
    out = plate - dark * (1.0 + (gain - 1.0) * alpha[..., None])
    return np.clip(out, floor, 255.0)


def expand(mat: np.ndarray, alpha: np.ndarray, k: float,
           floor: float, ceil: float, soft: float = 0.0) -> np.ndarray:
    """マスクの中の局所コントラストを中央値のまわりで広げる（明部を持ち上げる向き）。

    `soft > 0` なら**大きな形だけ**を広げる（ぼかした成分に効かせ、細かい模様は 1 倍のまま）。
    ⚠ soft なしだと幕の**縦の襞**まで一緒に強調されて、届いた画が筋だらけになる（2026-09-04 実測）。
    顔を作っているのは目・口・頬という大きな形なので、そこだけ持ち上げる方が自然に見える。
    """
    sel = alpha > 0.5
    mid = np.array([float(np.median(mat[..., c][sel])) for c in range(3)])
    kk = (k - 1.0) * alpha[..., None]
    if soft > 0:
        # ⚠ PIL の GaussianBlur は mode="F" を受け取らない。素材は 0..255 なので L で通す
        #   （低い周波数だけ取り出す用途なので 1 階調の丸めは効かない）。
        low = np.stack([np.asarray(
            Image.fromarray(np.clip(mat[..., c], 0, 255).astype(np.uint8), mode="L")
            .filter(ImageFilter.GaussianBlur(soft)), dtype=np.float64) for c in range(3)], axis=2)
        out = mat + (low - mid) * kk      # 細かい模様はそのまま、大きな形だけ広げる
    else:
        out = mat + (mat - mid) * kk
    return np.clip(out, floor, ceil)


def frame_box(src_box, src_size, frame=(1280, 720)):
    w, h = src_size
    fw, fh = frame
    fa, sa = fw / fh, w / h
    if sa > fa:
        iw, ih = fw, max(1, round(fw / sa))
    else:
        iw, ih = max(1, round(fh * sa)), fh
    ox, oy = (fw - iw) // 2, (fh - ih) // 2
    return [ox + int(src_box[0] / w * iw), oy + int(src_box[1] / h * ih),
            ox + int(src_box[2] / w * iw), oy + int(src_box[3] / h * ih)]


def render_lap(live, overlay, mask, lap, out_path, show):
    """実機の経路を通す（`screen.py` と同じ式・同じ順序）。"""
    p = dict(exposure=-1.05, contrast=1.12, saturation=0.52, temperature=0.48,
             tint=0.0, lift=0.02, vignette=0.38)
    for k, v in (show.get("post") or {}).items():
        if k in p and isinstance(v, (int, float)):
            p[k] = float(v)
    total = max(2.0, float((show.get("run") or {}).get("totalLaps", 3)))
    prog = min(max((lap - 1.0) / (total - 1.0), 0.0), 1.0)
    blocks = screen.FINE_BLOCKS + (screen.END_BLOCKS - screen.FINE_BLOCKS) * prog
    col = screen.render(live, overlay, mask, p, blocks, prog)
    img = screen.linear_to_srgb(col).astype(np.uint8)
    Image.fromarray(img).save(out_path)
    return img


def main() -> int:
    ap = argparse.ArgumentParser(description="焼き直さずに素材の異変を読めるようにする")
    ap.add_argument("--material", required=True)
    ap.add_argument("--plate", required=True, help="素材を作ったときのプレート（--gain の基準）")
    ap.add_argument("--mask", required=True, help="cue のマスク（枠空間 640x360）")
    ap.add_argument("--gain", type=float, default=1.0, help="染めた量を何倍にするか（暗くする向き）")
    ap.add_argument("--expand", type=float, default=1.0,
                    help="⭐ 局所コントラストを中央値のまわりで何倍に広げるか（明部を持ち上げる向き）")
    ap.add_argument("--floor", type=float, default=8.0, help="これより暗くしない")
    ap.add_argument("--ceil", type=float, default=235.0, help="これより明るくしない")
    ap.add_argument("--out", default="")
    ap.add_argument("--lap", type=float, default=2.0, help="どの周として post を通すか")
    ap.add_argument("--live", default="", help="ライブ側（既定は --plate）")
    ap.add_argument("--sweep", default="", help="値を振って比べる（例 1,1.5,2,2.5,3）")
    ap.add_argument("--soft", type=float, default=0.0,
                    help="⭐ 大きな形だけを広げる（ぼかしの半径・素材の画素）。0 = 細かい模様も一緒に広げる")
    ap.add_argument("--sweep-what", choices=["gain", "expand"], default="expand")
    ap.add_argument("--sweep-dir", default="logs/gen-plate/boost")
    a = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")

    mat_img = Image.open(a.material).convert("RGB")
    size = mat_img.size
    mat = np.asarray(mat_img, dtype=np.float64)
    plate = np.asarray(Image.open(a.plate).convert("RGB").resize(size, Image.LANCZOS), dtype=np.float64)
    alpha = load_mask_src(a.mask, size)

    sel = alpha > 0.5
    ys, xs = np.nonzero(sel)
    if len(ys) == 0:
        raise SystemExit("マスクが空")
    sbox = [int(xs.min()), int(ys.min()), int(xs.max()) + 1, int(ys.max()) + 1]
    print(f"マスクの箱: 素材 {sbox} → 枠 {frame_box(sbox, size)}")

    # 測る所は素材から自動で決める（顔の面 = マスクの中の明部 / 目・口 = 暗部 / 周り = マスクの外）
    lum = mat.mean(axis=2)
    pale = sel & (lum >= np.percentile(lum[sel], 80))
    hole = sel & (lum <= np.percentile(lum[sel], 20))
    ring = ((~sel) & metrics.box_mask(alpha.shape, [sbox[0] - 30, sbox[1], sbox[0], sbox[3]])) \
        | ((~sel) & metrics.box_mask(alpha.shape, [sbox[2], sbox[1], sbox[2] + 30, sbox[3]]))
    print(f"測る所: 顔の面 {int(pale.sum())} 画素 / 目・口 {int(hole.sum())} / 周りの幕 {int(ring.sum())}")

    show = screen.load_show()
    live = a.live or a.plate

    # ⚠⚠ 届いた画は 16:9 の枠で、素材（4:3）は中央に収まっている。**黒帯を切ってから戻す**。
    #    枠ごと縮めると座標が横に 1.33 倍ずれ、明部と暗部を取り違える（2026-09-04 に踏んだ）。
    inner = frame_box([0, 0, size[0], size[1]], size)

    def delivered(img):
        s = Image.fromarray(img).convert("L").crop(tuple(inner)).resize(size, Image.LANCZOS)
        s = np.asarray(s, dtype=np.float64)
        return float(s[pale].mean()), float(s[hole].mean()), float(s[ring].mean())

    if a.sweep:
        os.makedirs(a.sweep_dir, exist_ok=True)
        what = a.sweep_what
        print()
        print(f"{what:>6s} {'素材 面-穴':>10s} {'届 面':>7s} {'届 穴':>7s} {'届 幕':>7s} "
              f"{'面-穴':>7s} {'面-幕':>7s} {'黒潰れ%':>8s}")
        for g in [float(v) for v in a.sweep.split(",")]:
            out = (expand(mat, alpha, g, a.floor, a.ceil, a.soft) if what == "expand"
                   else boost(mat, plate, alpha, g, a.floor))
            tmp = os.path.join(a.sweep_dir, f"mat_{what}{g:.1f}s{a.soft:.0f}.png")
            Image.fromarray((out + 0.5).astype(np.uint8)).save(tmp)
            ol = out.mean(axis=2)
            raw_gap = ol[pale].mean() - ol[hole].mean()
            img = render_lap(live, tmp, a.mask, a.lap,
                             os.path.join(a.sweep_dir, f"seen_{what}{g:.1f}s{a.soft:.0f}.png"), show)
            dp, dh, dr = delivered(img)
            crushed = 100.0 * float((ol[sel] <= a.floor + 1).mean())
            print(f"{g:6.1f} {raw_gap:10.0f} {dp:7.1f} {dh:7.1f} {dr:7.1f} "
                  f"{dp - dh:7.1f} {dp - dr:7.1f} {crushed:8.1f}")
        print()
        print(f"絵: {a.sweep_dir}/seen_{what}*.png（届いた画）/ mat_{what}*.png（素材）")
        print("⚠ 数値が良くても、**届いた画を開いて顔に見えるか**を確かめるまでが 1 周")
        return 0

    out = mat
    if a.gain != 1.0:
        out = boost(out, plate, alpha, a.gain, a.floor)
    if a.expand != 1.0:
        out = expand(out, alpha, a.expand, a.floor, a.ceil, a.soft)
    dst = a.out or (os.path.splitext(a.material)[0] + f"_e{a.expand:.1f}.png")
    Image.fromarray((out + 0.5).astype(np.uint8)).save(dst)
    ol = out.mean(axis=2)
    print(f"{dst}  gain {a.gain:.1f} / expand {a.expand:.1f}  "
          f"素材の 面-穴 {ol[pale].mean() - ol[hole].mean():.0f} 階調")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
