"""2 周目 A の右の幕を動画生成で作るときの首尾フレーム（左のしみ無し）を組む。

    py -3.11 tools/gen-plate/stain3/video_inputs.py [--out <フォルダ>]

左のしみは別の層が重ねるので、入力には左に何も無い画を使う（2026-11-15 ユーザー
「左の顔のシミは別なので、左には何もないのを入力にしよ」）。生成はしない。
build_right_burst.py の real の素材（bulge.png / doll_laugh.png / doll_watch.png）の変えた所だけを、
無人写真 × 右のしみの比の画の上へ戻す。

出すもの（<out>/）:
  A_stain.png / B_burst.png / C_watch.png        640x480（4:3）
  *_16x9.png                                     2560x1440。3 倍（1920x1440）を中央に置き、左右 320px は黒
  sheet.png                                      3 枚を並べた確認用
"""
from __future__ import annotations

import argparse
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

sys.path.insert(0, str(Path(__file__).resolve().parent))
import build_right_burst as rb  # noqa: E402

PLATE = "tools/web-compositor/captures/plate_A_20261115_125052.jpg"
OUT = "Logs/gen-plate/stain2A_right_video_20261115"


def to_16x9(img: Image.Image) -> Image.Image:
    assert img.size == (rb.W, rb.H), img.size
    big = img.resize((rb.W * 3, rb.H * 3), Image.LANCZOS)
    canvas = Image.new("RGB", (2560, 1440), (0, 0, 0))
    canvas.paste(big, (320, 0))
    return canvas


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=OUT)
    args = ap.parse_args()
    out = rb.ROOT / args.out
    out.mkdir(parents=True, exist_ok=True)

    cfg = rb.SETS["real"]
    wd = rb.ROOT / cfg["work"]
    base = rb.load(rb.ROOT / cfg["base"])          # 無人写真 × 左のしみ（生成画の位置合わせはこれに対して行う）
    plate = rb.load(rb.ROOT / PLATE)               # 無人写真（左に何も無い）
    rr = rb.lin(rb.load(rb.ROOT / cfg["right_ratio_src"]))
    ms = rb.blur(rb.grow((rr.mean(2) < 0.985).astype(np.float32), 2), 1.5)

    S = rb.srgb(rb.lin(base) * rr)                 # 位置合わせの相手（左のしみあり）
    S0 = rb.srgb(rb.lin(plate) * rr)               # 出す下地（左のしみなし）
    S = base * (1 - ms[..., None]) + S * ms[..., None]
    S0 = plate * (1 - ms[..., None]) + S0 * ms[..., None]

    bb = cfg["bulge_box"]
    rail_top = int(min(cfg["rail"][0][1], cfg["rail"][1][1])) - 8
    print("bulge.png"); B = rb.fit(wd / "bulge.png", S, [(bb[0], bb[1], bb[2], min(bb[3], rail_top))])
    print("doll_laugh.png"); D = rb.fit(wd / "doll_laugh.png", S, [rb.BOX_DOLL_EDIT])
    print("doll_watch.png"); Wt = rb.fit(wd / "doll_watch.png", S, [rb.BOX_DOLL_EDIT])

    mb = rb.soft_mask(B, S, bb, 0.02, 0.07, close=6) * rb.above_rail(cfg["rail"])
    md = rb.soft_mask(D, S, rb.BOX_DOLL, 0.03, 0.08, close=6)
    mw = np.maximum(rb.soft_mask(Wt, S, rb.BOX_DOLL, 0.03, 0.08, close=6), md)
    rm = rb.rail_mask(cfg["rail"])[..., None]

    def put(img, src, m):
        return img * (1 - m[..., None]) + src * m[..., None]

    A = S0
    Bf = put(put(S0, B, mb), D, md)
    Bf = Bf * (1 - rm) + S0 * rm
    C = put(S0, Wt, mw)

    imgs = {"A_stain": A, "B_burst": Bf, "C_watch": C}
    sheet = Image.new("RGB", (rb.W * 3, rb.H + 30), (20, 20, 20))
    dr = ImageDraw.Draw(sheet)
    for k, (name, arr) in enumerate(imgs.items()):
        im = Image.fromarray(np.clip(arr, 0, 255).astype(np.uint8))
        im.save(out / f"{name}.png")
        wide = to_16x9(im)
        wide.save(out / f"{name}_16x9.png")
        w = np.asarray(wide)
        assert wide.size == (2560, 1440)
        assert w[:, :320].max() == 0 and w[:, -320:].max() == 0, "黒帯が黒でない"
        sheet.paste(im, (k * rb.W, 30))
        dr.text((k * rb.W + 8, 8), f"{name}  mean={arr.mean():.1f}", fill=(255, 255, 255))
        print(f"{name}: 4:3 {im.size} / 16:9 {wide.size} 黒帯 OK / 平均 {arr.mean():.1f}")
    sheet.save(out / "sheet.png")
    print(out)


if __name__ == "__main__":
    main()
