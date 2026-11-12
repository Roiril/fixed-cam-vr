"""生成した静止画を、元の写真の上へ「変えた所だけ」戻す。

    py -3.11 tools/gen-plate/keep_base.py <元> <生成> <出力> [--box x0,y0,x1,y1] [--thr 14] [--grow 10] [--feather 8]
                                          [--sheet <並べた画>]

画像生成は部屋を少し描き直す（寸法も 1448x1086 などに変わる）。そのまま左半分のマスクで合成すると、
半分の境目と、描き直された壁・床が継ぎ目になる。ここでは生成を元の寸法へ戻し、明るさを元へ合わせてから、
元との差が出た塊（とその周り）だけを残して、ほかは元の画素へ戻す。

- `--box` は変えてよい範囲（元の画素）。外は差があっても元へ戻す。省くと全面
- 出力は元と同じ寸法の PNG。差の塊が 1 つも無ければ落ちる（生成が元と同じ＝失敗）
- 標準出力に、残した面積（%）と、戻した所の一致（元との差の平均。0 に近いはず）を出す
"""
from __future__ import annotations

import argparse
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageFilter


def _luma(a: np.ndarray) -> np.ndarray:
    return a[..., 0] * 0.299 + a[..., 1] * 0.587 + a[..., 2] * 0.114


def fit(base: Image.Image, gen: Image.Image, box, thr: float, grow: int, feather: int):
    w, h = base.size
    g = gen.convert("RGB").resize((w, h), Image.LANCZOS)
    b = np.asarray(base.convert("RGB"), dtype=np.float32)
    a = np.asarray(g, dtype=np.float32)

    # 明るさ合わせ: 差の小さい画素（＝描き直されただけの背景）で、色ごとの倍率と下駄を解く。
    d0 = np.abs(_luma(a) - _luma(b))
    calm = d0 < np.percentile(d0, 60)
    for c in range(3):
        x, y = a[..., c][calm], b[..., c][calm]
        k, o = np.polyfit(x, y, 1)
        a[..., c] = np.clip(a[..., c] * k + o, 0, 255)

    # 差の塊。細かい粒（描き直しの粗）はぼかして落とし、塊だけ残す。
    d = np.abs(_luma(a) - _luma(b))
    dimg = Image.fromarray(np.clip(d, 0, 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(4))
    m = (np.asarray(dimg, dtype=np.float32) > thr).astype(np.uint8) * 255
    mimg = Image.fromarray(m)
    mimg = mimg.filter(ImageFilter.MinFilter(5)).filter(ImageFilter.MaxFilter(5))   # 孤立した点を消す
    if grow > 0:
        mimg = mimg.filter(ImageFilter.MaxFilter(grow * 2 + 1))
    mask = np.asarray(mimg, dtype=np.float32) / 255.0
    if box is not None:
        x0, y0, x1, y1 = box
        lim = np.zeros_like(mask)
        lim[y0:y1, x0:x1] = 1.0
        mask *= lim
    if mask.max() <= 0:
        raise SystemExit("差の塊が無い: 生成が元と同じか、--thr が高すぎる")
    mask = np.asarray(Image.fromarray((mask * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(feather)),
                      dtype=np.float32) / 255.0
    if box is not None:
        mask *= lim   # ぼかしで範囲の外へ出た分を切る（境目は範囲の内側だけでなじむ）
        mask = np.asarray(Image.fromarray((mask * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(2)),
                          dtype=np.float32) / 255.0

    out = b * (1 - mask[..., None]) + a * mask[..., None]
    kept = float((mask > 0.5).mean() * 100)
    outside = mask < 0.01
    back = float(np.abs(out - b)[outside].mean()) if outside.any() else 0.0
    return Image.fromarray(np.clip(out, 0, 255).astype(np.uint8)), Image.fromarray((mask * 255).astype(np.uint8)), kept, back


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser()
    ap.add_argument("base")
    ap.add_argument("gen")
    ap.add_argument("out")
    ap.add_argument("--box", default="")
    ap.add_argument("--thr", type=float, default=14)
    ap.add_argument("--grow", type=int, default=10)
    ap.add_argument("--feather", type=int, default=8)
    ap.add_argument("--sheet", default="")
    args = ap.parse_args()

    for p in (args.base, args.gen):
        if not Path(p).is_file():
            raise SystemExit(f"無い: {p}")
    base = Image.open(args.base)
    gen = Image.open(args.gen)
    box = tuple(int(v) for v in args.box.split(",")) if args.box else None
    out, mask, kept, back = fit(base, gen, box, args.thr, args.grow, args.feather)
    tmp = Path(args.out).with_suffix(".tmp.png")
    out.save(tmp)
    tmp.replace(args.out)
    print(f"{Path(args.out).name}: {out.size[0]}x{out.size[1]} 残した面積 {kept:.1f}% / 戻した所の差 {back:.2f}")
    if args.sheet:
        w, h = base.size
        sheet = Image.new("RGB", (w * 4, h))
        for i, im in enumerate((base.convert("RGB"), gen.convert("RGB").resize((w, h), Image.LANCZOS), out,
                                mask.convert("RGB"))):
            sheet.paste(im, (i * w, 0))
        sheet.save(args.sheet)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
