# -*- coding: utf-8 -*-
"""走行をいくつか並べて 1 枚にする（場所をまたいで同じ不変部が効いているかを見るため）。

    py -3.11 tools/gen-plate/compare.py logs/gen-plate/r1_* --out logs/gen-plate/compare.png
"""
from __future__ import annotations

import argparse
import glob
import json
import os
import sys

from PIL import Image, ImageDraw, ImageFont

import judge
import metrics

FG = (232, 226, 214)
DIM = (152, 148, 140)
OK = (140, 220, 160)
NG = (240, 130, 120)


def _font(size: int):
    for p in ("C:/Windows/Fonts/meiryo.ttc", "C:/Windows/Fonts/msgothic.ttc"):
        try:
            return ImageFont.truetype(p, size)
        except OSError:
            continue
    return ImageFont.load_default()


def main() -> int:
    ap = argparse.ArgumentParser(description="走行を並べる")
    ap.add_argument("runs", nargs="+")
    ap.add_argument("--out", default="logs/gen-plate/compare.png")
    args = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")

    dirs = []
    for r in args.runs:
        dirs.extend(sorted(glob.glob(r)) if any(c in r for c in "*?") else [r])
    dirs = [d for d in dirs if os.path.exists(os.path.join(d, "out.png"))]
    if not dirs:
        print("out.png のある走行が無い")
        return 2

    cw = 300
    rows = []
    for d in dirs:
        with open(os.path.join(d, "manifest.json"), encoding="utf-8") as f:
            man = json.load(f)
        seed = Image.open(man["seed"]).convert("RGB")
        raw = Image.open(os.path.join(d, "out.png"))
        size_ok = list(raw.size) == man["size"]
        out = raw.convert("RGB")
        if not size_ok:
            out = out.resize(tuple(man["size"]), Image.LANCZOS)
        m = metrics.measure(seed, out, man)
        checks = judge.build_checks(m, man, size_ok)
        rows.append((os.path.basename(d), seed, out, m, checks))

    ch = int(cw * rows[0][1].size[1] / rows[0][1].size[0])
    pad, lab = 8, 18
    line_h = 17
    row_h = max(ch, line_h * 9) + lab + pad
    W = pad + 2 * (cw + pad) + 430
    sheet = Image.new("RGB", (W, row_h * len(rows) + pad), (17, 17, 19))
    d = ImageDraw.Draw(sheet)
    f, fs = _font(14), _font(12)

    for i, (name, seed, out, m, checks) in enumerate(rows):
        y = pad + i * row_h
        d.text((pad, y), name, fill=FG, font=f)
        sheet.paste(seed.resize((cw, ch), Image.LANCZOS), (pad, y + lab))
        sheet.paste(out.resize((cw, ch), Image.LANCZOS), (pad + cw + pad, y + lab))
        x = pad + 2 * (cw + pad)
        ng = [c for c in checks if not c[1]]
        d.text((x, y + lab), f"{'合格' if not ng else f'不合格 {len(ng)} 件'}",
               fill=OK if not ng else NG, font=f)
        keys = [("足された面積", f"{m['added_pct']:.1f}%"), ("塊", f"{m['parts']}"),
                ("粒", judge._fmt(m["grain"])), ("縁", judge._fmt(m["edge"])),
                ("接地", judge._fmt(m["ground"])), ("遠近", judge._fmt(m["persp"])),
                ("明るさ 置く側", judge._fmt(m["tone_mean_place"], 3)),
                ("反対側の差", f"{m['keep_diff']:.1f}")]
        for j, (k, v) in enumerate(keys):
            d.text((x, y + lab + line_h * (j + 1)), f"{k}: {v}", fill=DIM, font=fs)
        for j, c in enumerate(ng[:6]):
            d.text((x + 210, y + lab + line_h * (j + 1)), f"NG {c[0]}", fill=NG, font=fs)

    os.makedirs(os.path.dirname(args.out) or ".", exist_ok=True)
    sheet.save(args.out)
    print(args.out)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
