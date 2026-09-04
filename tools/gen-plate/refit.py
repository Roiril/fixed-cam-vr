# -*- coding: utf-8 -*-
"""生成済みの素材から**壁ごと**切り出して、当日のプレートの上に置く（`canon/LEDGER.md` 0137）。

    py -3.11 tools/gen-plate/refit.py --material tools/web-compositor/captures/gen_handsB_20260904_0651.png \
        --base-plate tools/web-compositor/captures/plate_B_20260823_194235.jpg \
        --day-plate  tools/web-compositor/captures/plate_B_<当日>.jpg \
        --quad 0,0,240,0,240,317,0,336 \
        --out tools/web-compositor/captures/gen_handsB_<当日>.png \
        --mask tools/web-compositor/masks/cue_hands_B_<当日>.png --preview logs/gen-plate/refit_B.png

ユーザーの判定（0137）: 「手形だけを抜くんじゃなく、壁ごと抜いてほしい。当日は B の横に壁がない可能性もあるから、
あたかも血の壁があるみたいな感じにする」／「マスク領域を床ぎりぎりまでもう少し下まで伸ばし、上も画面上端まで伸ばす。
壁と周囲をうまくブレンドする処理とかも」。

既定（`--mode wall`）: 面の四角形（`--quad` 4 点）を**そのままマスク**にし、素材の壁を不透明に貼る。
当日そこに壁が無くても、素材の壁が「そこにある」ように出る。

なじませ（3 つ。どれも「壁が板に見える」を潰すためのもの）:

  1. **辺ごとのぼかし**（`--feather-edges 上,右,下,左`・素材の画素）。画面の縁に接する辺（上・左）は 0、
     画に出る縦の継ぎ目（右）は広く、床と接する辺（下）は狭く。四角形の各辺までの距離で作るので、
     ガウスぼかしと違って**角が丸まらない**
  2. **接地の影**（`--contact` px ・`--contact-strength`）。下辺のすぐ上を暗くする。
     これが無いと、壁が床から浮いた板に見える（8/16 のプレートで実測）
  3. **角の陰**（`--corner` px ・`--corner-strength`）。右辺のすぐ内側を暗くして、切り口ではなく
     「壁がそこで折れている」ように見せる

明るさは当日も必ずある所（`--ref-box`・既定は手前の床）の平均で倍率を合わせる。
ずれは壁の縁の位相相関で推定する（同じ壁が無い・回っているときは相関が低いので 0 に倒す）。
マスクは枠空間 640x360（`make-diff-mask.py` と同じ contain-fit）で書く。

`--mode layer`: 跡の暗さ（素材 − 元のプレート）だけを層で持ち、当日の壁に足す。
当日に**同じ壁がある**ときだけ成立する（0137 の判定で既定から外した）。
"""
from __future__ import annotations

import argparse
import os
import sys

import numpy as np
from PIL import Image, ImageFilter

FRAME_W, FRAME_H = 640, 360   # tools/web-compositor/common.js の MW / MH と同じ


def _gray(img: Image.Image) -> np.ndarray:
    return np.asarray(img.convert("L"), dtype=np.float64)


def _grad(g: np.ndarray) -> np.ndarray:
    gx = np.zeros_like(g); gy = np.zeros_like(g)
    gx[:, 1:-1] = g[:, 2:] - g[:, :-2]
    gy[1:-1, :] = g[2:, :] - g[:-2, :]
    return np.hypot(gx, gy)


def _shift(a: np.ndarray, dx: int, dy: int) -> np.ndarray:
    """a を (dx, dy) だけ動かす（はみ出しは 0）。"""
    out = np.zeros_like(a)
    h, w = a.shape[:2]
    xs0, xs1 = max(0, dx), min(w, w + dx)
    ys0, ys1 = max(0, dy), min(h, h + dy)
    if xs1 > xs0 and ys1 > ys0:
        out[ys0:ys1, xs0:xs1] = a[ys0 - dy:ys1 - dy, xs0 - dx:xs1 - dx]
    return out


def _ncc(a: np.ndarray, b: np.ndarray) -> float:
    a = a - a.mean(); b = b - b.mean()
    d = float(np.sqrt((a * a).sum() * (b * b).sum()))
    return float((a * b).sum() / d) if d > 0 else 0.0


def estimate_shift(base_g: np.ndarray, day_g: np.ndarray, box, pad: int = 24) -> tuple[int, int, float, float]:
    """当日 ≈ 元を (dx, dy) 動かしたもの、の (dx, dy) と、動かす前後の相関を返す。"""
    h, w = base_g.shape
    x0, y0, x1, y1 = box
    x0 = max(0, x0 - pad); y0 = max(0, y0 - pad); x1 = min(w, x1 + pad); y1 = min(h, y1 + pad)
    a = _grad(base_g)[y0:y1, x0:x1]
    b = _grad(day_g)[y0:y1, x0:x1]
    hh, ww = a.shape
    win = np.outer(np.hanning(hh), np.hanning(ww))
    A = np.fft.fft2(a * win); B = np.fft.fft2(b * win)
    R = A * np.conj(B)
    R /= (np.abs(R) + 1e-9)
    r = np.real(np.fft.ifft2(R))
    py, px = np.unravel_index(int(np.argmax(r)), r.shape)
    if py > hh // 2: py -= hh
    if px > ww // 2: px -= ww
    c0 = _ncc(a, b)
    best = (0, 0, c0)
    for dx, dy in ((px, py), (-px, -py)):
        c = _ncc(_shift(a, dx, dy), b)
        if c > best[2]:
            best = (int(dx), int(dy), c)
    return best[0], best[1], best[2], c0


def edge_distances(size, quad, dx: int, dy: int) -> list[np.ndarray]:
    """四角形の 4 辺それぞれについて、内側を正とする符号つき距離（画素）。

    辺の順は 上（p0→p1）/ 右（p1→p2）/ 下（p2→p3）/ 左（p3→p0）。
    ガウスぼかしと違い**辺ごとに幅を変えられる**（角が丸まらない）ので、
    画面の縁に接する辺だけぼかさない、が書ける。
    """
    w, h = size
    pts = [(quad[i] + dx, quad[i + 1] + dy) for i in range(0, 8, 2)]
    cx = sum(p[0] for p in pts) / 4.0
    cy = sum(p[1] for p in pts) / 4.0
    ys, xs = np.mgrid[0:h, 0:w]
    out = []
    for i in range(4):
        ax, ay = pts[i]
        bx, by = pts[(i + 1) % 4]
        ex, ey = bx - ax, by - ay
        n = float(np.hypot(ex, ey)) or 1.0
        d = (ex * (ys - ay) - ey * (xs - ax)) / n
        s = (ex * (cy - ay) - ey * (cx - ax)) / n
        out.append(d if s >= 0 else -d)
    return out


def quad_alpha(size, quad, dx: int, dy: int, feather) -> tuple[np.ndarray, list[np.ndarray]]:
    """四角形のマスク（辺ごとのぼかし込み）と、辺までの距離。"""
    dists = edge_distances(size, quad, dx, dy)
    a = np.ones(dists[0].shape)
    for d, f in zip(dists, feather):
        a = np.minimum(a, np.clip(d / f, 0.0, 1.0) if f > 0 else (d >= 0).astype(float))
    return a, dists


def to_frame_mask(alpha: np.ndarray, feather: float) -> Image.Image:
    """素材座標のマスク（0..1）→ 枠空間 640x360（contain-fit・R チャンネル）。"""
    h, w = alpha.shape
    am = Image.fromarray((alpha * 255 + 0.5).astype(np.uint8), mode="L")
    fa = FRAME_W / FRAME_H
    sa = w / h
    if sa > fa:
        fw, fh = FRAME_W, max(1, round(FRAME_W / sa))
    else:
        fw, fh = max(1, round(FRAME_H * sa)), FRAME_H
    ox, oy = (FRAME_W - fw) // 2, (FRAME_H - fh) // 2
    am = am.resize((fw, fh), Image.LANCZOS)
    if feather > 0:
        am = am.filter(ImageFilter.GaussianBlur(feather))
    canvas = Image.new("L", (FRAME_W, FRAME_H), 0)
    canvas.paste(am, (ox, oy))
    return canvas


def main() -> int:
    ap = argparse.ArgumentParser(description="生成素材を壁ごと（または跡の層で）当日のプレートへ貼り直す")
    ap.add_argument("--material", required=True, help="生成済みの素材（undim 済み・元のプレートと同じ明るさ）")
    ap.add_argument("--base-plate", required=True, help="素材を作ったときのプレート")
    ap.add_argument("--day-plate", required=True, help="当日のプレート")
    ap.add_argument("--quad", default="", help="面の四角形 x0,y0,x1,y1,x2,y2,x3,y3（左上→右上→右下→左下）")
    ap.add_argument("--box", default="", help="面の箱 x0,y0,x1,y1（--quad が無いとき）")
    ap.add_argument("--mode", choices=["wall", "layer"], default="wall",
                    help="wall = 壁ごと不透明に貼る（既定・0137） / layer = 跡の暗さだけ当日の壁へ足す")
    ap.add_argument("--out", required=True, help="当日の素材（元のプレートと同じ寸法）")
    ap.add_argument("--mask", required=True, help="当日のマスク（枠空間 640x360）")
    ap.add_argument("--ref-box", default="0,360,640,480",
                    help="明るさを合わせる参照の箱（当日も必ずある所。既定は手前の床）")
    ap.add_argument("--no-match", action="store_true", help="明るさの倍率合わせをしない")
    # なじませ
    ap.add_argument("--feather-edges", default="0,10,3,0",
                    help="辺ごとのぼかし幅（素材の画素）上,右,下,左。画面の縁に接する辺は 0")
    ap.add_argument("--floor-overlap", type=float, default=12.0,
                    help="下辺を床へ食い込ませる画素。当日のプレートの床の線が素材より下にあると、"
                         "そこに明るい隙間が出て一目で嘘に見える（実測 11px）。少し潜らせて接地の影で隠す")
    ap.add_argument("--contact", type=float, default=14.0, help="接地の影の高さ（下辺から上へ・素材の画素）")
    ap.add_argument("--contact-strength", type=float, default=0.45, help="接地の影の濃さ（0..1）")
    ap.add_argument("--corner", type=float, default=18.0, help="角の陰の幅（右辺から内へ・素材の画素）")
    ap.add_argument("--corner-strength", type=float, default=0.18, help="角の陰の濃さ（0..1）")
    ap.add_argument("--feather", type=float, default=1.0, help="マスクの縁の追加ぼかし（枠空間の px）")
    ap.add_argument("--shift", default="", help="ずれを手で与える dx,dy（推定を使わない）")
    ap.add_argument("--min-corr", type=float, default=0.5, help="推定の相関がこれ未満ならずれ 0 とみなす")
    # layer モードだけ
    ap.add_argument("--margin", type=int, default=12)
    ap.add_argument("--margin-bottom", type=int, default=4)
    ap.add_argument("--t0", type=float, default=10.0)
    ap.add_argument("--t1", type=float, default=50.0)
    ap.add_argument("--preview", default="", help="確認用の 1 枚（当日プレート / 当日素材 / マスク重ね / 合成）")
    a = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")

    mat = Image.open(a.material).convert("RGB")
    base = Image.open(a.base_plate).convert("RGB")
    day = Image.open(a.day_plate).convert("RGB")
    size = base.size
    if mat.size != size:
        mat = mat.resize(size, Image.LANCZOS)
    if day.size != size:
        day = day.resize(size, Image.LANCZOS)

    if a.quad:
        quad = [int(float(v)) for v in a.quad.split(",")]
        if len(quad) != 8:
            print("--quad は 8 つの数（4 点）", file=sys.stderr); return 2
        box = [min(quad[0::2]), min(quad[1::2]), max(quad[0::2]), max(quad[1::2])]
    elif a.box:
        box = [int(float(v)) for v in a.box.split(",")]
        quad = [box[0], box[1], box[2], box[1], box[2], box[3], box[0], box[3]]
    else:
        print("--quad か --box が要る", file=sys.stderr); return 2

    M = np.asarray(mat, dtype=np.float64)
    B = np.asarray(base, dtype=np.float64)
    D = np.asarray(day, dtype=np.float64)

    if a.shift:
        dx, dy = (int(float(v)) for v in a.shift.split(","))
        corr = c0 = float("nan")
        how = "手で与えた"
    else:
        dx, dy, corr, c0 = estimate_shift(_gray(base), _gray(day), box)
        how = f"推定（相関 {c0:.3f} → {corr:.3f}）"
        if corr < a.min_corr:
            print(f"⚠ 縁の相関が低い（{corr:.3f} < {a.min_corr}）: 当日そこに同じ壁が無いか、回っている。ずれ 0 で置く")
            dx, dy = 0, 0

    # 明るさの倍率（当日も必ずある所で合わせる）
    gain = np.ones(3)
    if not a.no_match:
        rx0, ry0, rx1, ry1 = [int(float(v)) for v in a.ref_box.split(",")]
        mb = B[ry0:ry1, rx0:rx1].reshape(-1, 3).mean(axis=0)
        md = D[ry0:ry1, rx0:rx1].reshape(-1, 3).mean(axis=0)
        gain = np.clip(md / np.maximum(mb, 1.0), 0.5, 2.0)

    if a.mode == "wall":
        feather = [float(v) for v in a.feather_edges.split(",")]
        if len(feather) != 4:
            print("--feather-edges は 4 つ（上,右,下,左）", file=sys.stderr); return 2
        if a.floor_overlap:
            # 下の 2 点（p2 = 右下 / p3 = 左下）だけ下へ伸ばす
            quad = list(quad)
            quad[5] += int(a.floor_overlap)
            quad[7] += int(a.floor_overlap)
        alpha, dists = quad_alpha(size, quad, dx, dy, feather)
        Ms = np.stack([_shift(M[:, :, c], dx, dy) for c in range(3)], axis=2)
        wall = Ms * gain

        # なじませ 2: 接地の影（下辺のすぐ上を暗くする）／ 3: 角の陰（右辺の内側）
        shade = np.ones(alpha.shape)
        if a.contact > 0 and a.contact_strength > 0:
            t = np.clip(1.0 - dists[2] / a.contact, 0.0, 1.0)      # 下辺 = dists[2]
            shade *= 1.0 - a.contact_strength * (t ** 2)
        if a.corner > 0 and a.corner_strength > 0:
            t = np.clip(1.0 - dists[1] / a.corner, 0.0, 1.0)       # 右辺 = dists[1]
            shade *= 1.0 - a.corner_strength * (t ** 2)
        wall = wall * shade[:, :, None]

        out = np.clip(np.where(alpha[:, :, None] > 0, wall, D), 0, 255)
        note = (f"壁ごと（四角形 {quad}・ずれ後・床へ {a.floor_overlap:.0f}px 食い込ませた） "
                f"ぼかし 上{feather[0]:.0f}/右{feather[1]:.0f}/"
                f"下{feather[2]:.0f}/左{feather[3]:.0f} px・接地の影 {a.contact:.0f}px×{a.contact_strength:.2f}・"
                f"角の陰 {a.corner:.0f}px×{a.corner_strength:.2f}")
    else:
        L = M - B
        Ls = np.stack([_shift(L[:, :, c], dx, dy) for c in range(3)], axis=2)
        dark = np.clip(-(Ls.mean(axis=2)), 0, None)
        alpha = np.clip((dark - a.t0) / max(1e-6, a.t1 - a.t0), 0.0, 1.0)
        x0, y0, x1, y1 = box
        keep = np.zeros(alpha.shape, dtype=bool)
        keep[max(0, y0 + dy + a.margin):min(size[1], y1 + dy - a.margin_bottom),
             max(0, x0 + dx + a.margin):min(size[0], x1 + dx - a.margin)] = True
        alpha = alpha * keep
        out = np.clip(D + Ls, 0, 255)
        note = "跡の層だけ"

    out_img = Image.fromarray((out + 0.5).astype(np.uint8))
    out_img.save(a.out)
    canvas = to_frame_mask(alpha, a.feather)
    os.makedirs(os.path.dirname(os.path.abspath(a.mask)), exist_ok=True)
    Image.merge("RGB", (canvas, canvas, canvas)).save(a.mask)

    cover = float((np.asarray(canvas) > 127).mean() * 100)
    print(f"ずれ (dx, dy) = ({dx}, {dy})  {how}")
    print(f"明るさの倍率（参照 {a.ref_box}）: R {gain[0]:.3f} / G {gain[1]:.3f} / B {gain[2]:.3f}")
    print(note)
    print(f"当日の素材 {a.out} / マスク {a.mask}（枠 {FRAME_W}x{FRAME_H}・白 {cover:.1f}%）")

    if a.preview:
        al = alpha[:, :, None]
        comp = D * (1 - al) + out * al
        pv = Image.new("RGB", (size[0] * 2 + 8, size[1] * 2 + 8), (24, 24, 24))
        pv.paste(day, (0, 0))
        pv.paste(out_img, (size[0] + 8, 0))
        ov = D * (1 - al) + np.array([255.0, 60.0, 60.0]) * al
        pv.paste(Image.fromarray((ov + 0.5).astype(np.uint8)), (0, size[1] + 8))
        pv.paste(Image.fromarray((comp + 0.5).astype(np.uint8)), (size[0] + 8, size[1] + 8))
        pv.save(a.preview)
        print(f"確認用: {a.preview}（左上 当日プレート / 右上 当日の素材 / 左下 マスク / 右下 合成）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
