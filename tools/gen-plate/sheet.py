# -*- coding: utf-8 -*-
"""生成物を**塊へ分解して並べた 1 枚**を作る。

数値だけ見て「合格」と言わないための道具（`~/.claude/rules/work-style.md` §2）。
上段は 種 / 生成物 / 差 / 足された所、下段は塊を大きい順に切り出したもの。
「20 体置いた」を数字で読んで終わりにせず、**その 20 個が何なのかを目で突き合わせる**。
"""
from __future__ import annotations

import os

import numpy as np
from PIL import Image, ImageDraw, ImageFont

FG = (232, 226, 214)
DIM = (150, 146, 138)
OK = (140, 220, 160)
NG = (240, 130, 120)


def _font(size: int):
    for p in ("C:/Windows/Fonts/meiryo.ttc", "C:/Windows/Fonts/msgothic.ttc"):
        try:
            return ImageFont.truetype(p, size)
        except OSError:
            continue
    return ImageFont.load_default()


def _heat(d: np.ndarray) -> Image.Image:
    v = np.clip(d / 64.0, 0, 1)
    rgb = np.stack([v * 255, v * 120, (1 - v) * 90], axis=-1).astype(np.uint8)
    return Image.fromarray(rgb)


def build(seed: Image.Image, out: Image.Image, m: dict, man: dict, run_dir: str,
          checks) -> str:
    w, h = seed.size
    f, fs = _font(14), _font(12)
    mask = m.get("_added_mask")
    parts = m.get("components", [])[:8]

    top = []
    top.append(("種（暗くしたプレート）", seed))
    top.append(("生成物", out))
    top.append(("差", _heat(m["_diff"])))

    mk = Image.fromarray((mask * 255).astype(np.uint8)).convert("RGB")
    d = ImageDraw.Draw(mk)
    if man.get("keep_out"):
        d.rectangle([int(v) for v in man["keep_out"]], outline=(90, 120, 255), width=2)
    d.rectangle([int(v) for v in man["target"]], outline=(90, 255, 140), width=2)
    for i, c in enumerate(parts):
        d.rectangle([c["x0"], c["y0"], c["x1"], c["y1"]], outline=(255, 190, 60), width=1)
        d.text((c["x0"] + 2, c["y0"] + 1), str(i + 1), fill=(255, 210, 90), font=fs)
    top.append((f"足された所（塊 {m['parts']} 個 / 緑＝足す所 / 青＝置かない所）", mk))

    cw, ch = 420, int(420 * h / w)
    pad, lab = 8, 20
    crop_h = 150
    rows_h = ch + lab + pad + crop_h + lab + pad + 22 * (len(checks) + 1) + pad
    sheet = Image.new("RGB", (4 * (cw + pad) + pad, rows_h + pad), (17, 17, 19))
    dr = ImageDraw.Draw(sheet)

    for i, (name, im) in enumerate(top):
        x = pad + i * (cw + pad)
        sheet.paste(im.resize((cw, ch), Image.LANCZOS), (x, pad + lab))
        dr.text((x, pad + 2), name, fill=FG, font=f)

    # 塊を大きい順に切り出して並べる（番号・面積・寸法つき）
    y0 = pad + lab + ch + pad
    dr.text((pad, y0), "塊を大きい順に（番号は上の枠と対応）", fill=FG, font=f)
    x = pad
    for i, c in enumerate(parts):
        bw, bh = c["x1"] - c["x0"], c["y1"] - c["y0"]
        s = min(crop_h / max(1, bh), 110 / max(1, bw))
        cr = out.crop((c["x0"], c["y0"], c["x1"], c["y1"]))
        cr = cr.resize((max(8, int(bw * s)), max(8, int(bh * s))), Image.LANCZOS)
        sheet.paste(cr, (x, y0 + lab))
        dr.text((x, y0 + lab + crop_h + 2), f"{i + 1}: {bw}x{bh} {c['area']}px",
                fill=DIM, font=fs)
        x += cr.size[0] + 10
        if x > sheet.size[0] - 120:
            break

    y1 = y0 + lab + crop_h + lab + pad
    dr.text((pad, y1), f"{man['anomaly']} @ {man['site']}  place={man['place']}  "
                       f"scale={man['scale']}", fill=FG, font=f)
    for i, (name, ok, detail, why) in enumerate(checks):
        dr.text((pad, y1 + 22 * (i + 1)), f"{'OK ' if ok else 'NG '} {name}   {detail}",
                fill=OK if ok else NG, font=fs)
        dr.text((pad + 430, y1 + 22 * (i + 1)), why, fill=DIM, font=fs)

    path = os.path.join(run_dir, "sheet.png")
    sheet.save(path)
    return path
