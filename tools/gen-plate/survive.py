# -*- coding: utf-8 -*-
"""**顔は伝送を越えるか**（届いた画の中で「明るい顔の粒」を数える）。

    py -3.11 tools/gen-plate/survive.py --run logs/gen-plate/<走行> --lap 4

異変 `dolls-many` は自分で「怖さは**数**と、**こちらを向いていること**だけで作る」と宣言している。
どちらも**生成直後にしか測っていなかった**。彩度を 4 周かけて詰めてから「本番では無彩だった」と
分かった件（`runs.md` 2026-08-17）と同じ形の穴なので埋める。

⚠⚠ **差分マスク（種との差）を使わない。** 使うと、背景を塗り直された走行では
**画面全体が「足された所」になり、いちばん明るい所＝カーテンや棚を顔として拾う**。
2026-08-18 に実際に踏んだ — 視線 3 条件の比較が、2 条件とも壁の切り抜きを測っていた
（`heads.py` の左右対称度も同じ経路なので、同じ穴を持つ）。

⇒ ここは**届いた画そのもの**を見る。周りより明るい小さな粒を探すので、
背景が塗り直されていても、素材が別の日のものでも、測れる相手は変わらない。

  顔の粒の数    体験者が「こちらを見ている顔」として拾える数
  顔の明暗      粒とそのすぐ周りの差（**こちらを向いた顔は明るい面が見える**）
  標本          粒の幅 ÷ mip が 1 つに潰す幅。**2 を切ると中の造作は 1 つも残らない**

⚠ **素材を置かない画（プレートだけ）でも同じ数え方をする。** 部屋そのものが出す粒が
下駄になるので、引き算しないと「20 個見えた」が嘘になる。
"""
from __future__ import annotations

import argparse
import json
import os
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFont

import deliver
import legible
import metrics
import screen
import spec

CELL = 96
FACE_MIN_PX, FACE_MAX_PX = 9, 64      # 枠の画素。顔は 26px 前後（実測・周 4）
BG_KERNEL = 81                        # 背景を取る箱の幅（顔の 3 倍あれば足りる）


def _font(size: int):
    for p in ("C:/Windows/Fonts/meiryo.ttc", "C:/Windows/Fonts/msgothic.ttc"):
        try:
            return ImageFont.truetype(p, size)
        except OSError:
            continue
    return ImageFont.load_default()


def box_mean(g: np.ndarray, k: int) -> np.ndarray:
    """箱平均（積分画像）。scipy はこの機体に無いので自前で持つ。"""
    r = k // 2
    pad = np.pad(g, r + 1, mode="edge")
    s = pad.cumsum(0).cumsum(1)
    s = np.pad(s, ((1, 0), (1, 0)))
    h, w = g.shape
    y0, x0 = np.arange(h)[:, None], np.arange(w)[None, :]
    y1, x1 = y0 + 2 * r + 1, x0 + 2 * r + 1
    tot = s[y1, x1] - s[y0, x1] - s[y1, x0] + s[y0, x0]
    return tot / ((2 * r + 1) ** 2)


def src_box_to_frame(box, src_size, frame=(screen.FRAME_W, screen.FRAME_H)):
    """もとの画の箱 → 枠の箱。**`screen.contain_scale` から導く**（倍率を直書きしない）。"""
    c = screen.contain_scale(src_size, frame[0] / frame[1])
    w, h = src_size
    return (int(round(frame[0] * (0.5 + (box[0] / w - 0.5) * c[0]))),
            int(round(frame[1] * (0.5 + (box[1] / h - 0.5) * c[1]))),
            int(round(frame[0] * (0.5 + (box[2] / w - 0.5) * c[0]))),
            int(round(frame[1] * (0.5 + (box[3] / h - 0.5) * c[1]))))


def frame_box_to_src(box, src_size, frame=(screen.FRAME_W, screen.FRAME_H)):
    c = screen.contain_scale(src_size, frame[0] / frame[1])
    w, h = src_size
    fx = lambda x: w * (0.5 + (x / frame[0] - 0.5) / max(c[0], 1e-4))
    fy = lambda y: h * (0.5 + (y / frame[1] - 0.5) / max(c[1], 1e-4))
    return (int(round(fx(box[0]))), int(round(fy(box[1]))),
            int(round(fx(box[2]))), int(round(fy(box[3]))))


def bright_grains(g: np.ndarray, region: np.ndarray, sigma: float) -> list[dict]:
    """周りより明るい小さな粒（＝こちらを向いた顔）。"""
    top = g - box_mean(g, BG_KERNEL)
    seeds = metrics.clean((top > max(5.0 * sigma, 6.0)) & region)
    out = []
    for c in metrics.components(seeds, min_area=40):
        w, h = c["x1"] - c["x0"], c["y1"] - c["y0"]
        if not (FACE_MIN_PX <= w <= FACE_MAX_PX and FACE_MIN_PX <= h <= FACE_MAX_PX):
            continue
        if w > h * 2.5 or h > w * 2.5:
            continue
        sub = np.zeros_like(seeds)
        sub[c["y0"]:c["y1"], c["x0"]:c["x1"]] = seeds[c["y0"]:c["y1"], c["x0"]:c["x1"]]
        ring = legible._ring(sub, (c["x0"], c["y0"], c["x1"], c["y1"]),
                             pad=max(4, h // 2)) & region & ~sub
        if ring.sum() < 20:
            continue
        out.append(dict(box=(c["x0"], c["y0"], c["x1"], c["y1"]), w=w, h=h,
                        pop=float(g[sub].mean() - g[ring].mean()),
                        mask=sub))
    return out


def run(run_dir: str, lap: float, out_path: str) -> int:
    with open(os.path.join(run_dir, "manifest.json"), encoding="utf-8") as f:
        man = json.load(f)
    with open(os.path.join(spec.REPO, "tools", "web-compositor", "show.json"),
              encoding="utf-8") as f:
        show = json.load(f)

    material = deliver.material_from_run(run_dir, man)
    mask = os.path.join(spec.REPO, "tools", "web-compositor", "masks", "split_left_half.png")
    got, plain, inside, sigma = legible._delivered(material, man["plate"], mask, lap, show)
    h, w = got.shape

    m = np.asarray(Image.open(mask).convert("L").resize((w, h), Image.BILINEAR),
                   dtype=np.float64) / 255.0
    src_size = tuple(man["size"])
    reg = man.get("target_region") or [0, 0, src_size[0], src_size[1]]
    fb = src_box_to_frame(reg, src_size)
    region = np.zeros(got.shape, dtype=bool)
    region[max(0, fb[1]):fb[3], max(0, fb[0]):fb[2]] = True
    region &= inside & (m > 0.5)

    total = max(2.0, float((show.get("run") or {}).get("totalLaps", 3)))
    prog = min(max((lap - 1.0) / (total - 1.0), 0.0), 1.0)
    blocks = screen.FINE_BLOCKS + (screen.END_BLOCKS - screen.FINE_BLOCKS) * prog
    bpx = screen.FRAME_W / max(blocks, 1.0)

    grains = bright_grains(got, region, sigma)
    base = bright_grains(plain, region, sigma)     # ⭐ 部屋そのものが出す粒（下駄）

    med = lambda xs, k: float(np.median([x[k] for x in xs])) if xs else float("nan")
    print(f"== 顔は伝送を越えるか  {man['anomaly']} @ {man['site']}  周 {lap:.0f}  粒 {sigma:.2f}")
    print(f"  1 標本 = 枠の {bpx:.2f} 画素")
    print(f"  ⭐ 明るい顔の粒  **{len(grains)} 個**"
          f"（素材を置かない同じ部屋で {len(base)} 個 ＝ 差し引き **{len(grains) - len(base)} 個**）")
    if grains:
        print(f"     大きさ 中央値 {med(grains, 'h'):.0f}px ＝ **{med(grains, 'h') / bpx:.1f} 標本**"
              f"   明暗 中央値 {med(grains, 'pop'):+.1f}")
        if med(grains, "h") / bpx < 2.0:
            print("     ⚠⚠ **2 標本を切っている。** 顔の中の造作（目・口）は 1 つも残らない")
    else:
        print("     ⚠ 1 個も立っていない ＝ 体験者に「こちらを見ている顔」は届いていない")

    # --- 並べる（左 = 生成直後の同じ場所 / 右 = 届いた所） ---
    raw = Image.open(man["out"])
    gen = raw.convert("RGB")
    if list(raw.size) != man["size"]:
        gen = gen.resize(tuple(man["size"]), Image.LANCZOS)
    dimg = Image.fromarray(got.astype(np.uint8)).convert("RGB")

    cells = []
    for gr in sorted(grains, key=lambda x: -x["pop"])[:24]:
        b = gr["box"]
        pad = max(3, gr["h"] // 3)
        fbox = (max(0, b[0] - pad), max(0, b[1] - pad),
                min(w, b[2] + pad), min(h, b[3] + pad))
        sbox = frame_box_to_src(fbox, src_size)
        sbox = (max(0, sbox[0]), max(0, sbox[1]),
                min(src_size[0], max(sbox[0] + 2, sbox[2])),
                min(src_size[1], max(sbox[1] + 2, sbox[3])))
        cells.append((gen.crop(sbox).resize((CELL, CELL), Image.LANCZOS),
                      dimg.crop(fbox).resize((CELL, CELL), Image.NEAREST), gr))

    if cells:
        cols = min(6, len(cells))
        rws = (len(cells) + cols - 1) // cols
        cw = CELL * 2 + 12
        sheet = Image.new("RGB", (cols * cw + 8, rws * (CELL + 24) + 34), (17, 17, 19))
        d = ImageDraw.Draw(sheet)
        f, fs = _font(13), _font(11)
        d.text((8, 6), f"{man['anomaly']} @ {man['site']}  周 {lap:.0f}  "
                       f"顔の粒 {len(grains)} 個（部屋の下駄 {len(base)}）  "
                       f"左 = 生成直後 / 右 = 届いた所",
               fill=(232, 226, 214), font=f)
        for i, (a, bimg, gr) in enumerate(cells):
            x = 8 + (i % cols) * cw
            y = 30 + (i // cols) * (CELL + 24)
            sheet.paste(a, (x, y))
            sheet.paste(bimg, (x + CELL + 6, y))
            d.text((x, y + CELL + 4),
                   f"{i + 1}: 明暗 {gr['pop']:+.0f} / {gr['h'] / bpx:.1f} 標本",
                   fill=(150, 230, 170), font=fs)
        os.makedirs(os.path.dirname(out_path) or ".", exist_ok=True)
        sheet.save(out_path)
        print(f"  絵にした: {out_path}  ← **これを開くまでが 1 周**")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description="顔は伝送を越えるか")
    ap.add_argument("--run", required=True)
    ap.add_argument("--lap", type=float, default=4.0)
    ap.add_argument("--out", default=None)
    args = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")
    out = args.out or os.path.join(args.run, f"survive_lap{int(args.lap)}.png")
    return run(args.run, args.lap, out)


if __name__ == "__main__":
    raise SystemExit(main())
