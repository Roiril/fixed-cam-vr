#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""`menu glitch` が焼いたコマに、どの回を見ているかの帯を焼き込む。

    py -3.11 tools/caption-glitch-preview.py

⚠ **値は Unity が書いた `frames.tsv` から読む。ここで計算し直さない** —
二重に持つと、`GlitchEscalationLogic` の定数を直したときに帯だけ古い値を出す
（沈黙して食い違う。この codebase が何度も踏んだ型）。

出力は同じフォルダの `cap/f####.png`。そのあと:

    py -3.11 tools/make-preview-video.py Assets/Screenshots/glitch-preview/cap glitch
"""
from __future__ import annotations

import csv
import sys
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8")

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "Assets/Screenshots/glitch-preview"
BAR_H = 100   # ⚠ 2 行 + ゲージが入る高さ。詰めると 2 行目が切れる（実際に切れた）
# 日本語が要るので同梱の Source Han Sans を使う（環境のフォントに頼らない）。
FONT_CANDIDATES = [
    ROOT / "Assets/Art/Fonts/SourceHanSansJP-Normal.otf",
    Path("C:/Windows/Fonts/YuGothM.ttc"),
    Path("C:/Windows/Fonts/meiryo.ttc"),
]


def load_font(size: int):
    from PIL import ImageFont

    for p in FONT_CANDIDATES:
        if p.exists():
            try:
                return ImageFont.truetype(str(p), size)
            except OSError:
                continue
    return ImageFont.load_default()


def main() -> int:
    from PIL import Image, ImageDraw

    ledger = SRC / "frames.tsv"
    if not ledger.exists():
        print(f"⚠ 台帳が無い: {ledger}（先に .\\tools\\unity.ps1 menu glitch）")
        return 1

    rows: dict[int, dict[str, str]] = {}
    with ledger.open(encoding="utf-8") as f:
        for r in csv.DictReader(f, delimiter="\t"):
            rows[int(r["frame"])] = r
    if not rows:
        print("⚠ 台帳が空")
        return 1

    n_max = max(int(r["n"]) for r in rows.values())
    out = SRC / "cap"
    out.mkdir(exist_ok=True)
    for old in out.glob("f*.png"):
        old.unlink()

    big, small = load_font(26), load_font(19)
    made = 0
    for i in sorted(rows):
        src = SRC / f"f{i:04d}.png"
        if not src.exists():
            continue
        r = rows[i]
        # ⚠ ゲージは curve（実際に掛かる値）。progress（回数の直線）を出すと、
        #    7 回目で半分以上進んで見えるのに画はまだ軽い ＝ 帯が嘘をつく。
        n, prog = int(r["n"]), float(r["curve"])
        im = Image.open(src).convert("RGB")
        w, h = im.size
        canvas = Image.new("RGB", (w, h + BAR_H), (12, 12, 12))
        canvas.paste(im, (0, 0))
        d = ImageDraw.Draw(canvas)

        y = h + 12
        d.text((16, y), f"{n} 回目 / 全 {n_max}", font=big, fill=(240, 236, 228))
        d.text((215, y + 5),
               f"強さ {float(r['levelApplied']):.2f}"
               f"（台本 0.70）    尺 {float(r['holdSec']):.2f}s（台本 0.40）"
               f"    音量 {float(r['volume']):.2f}",
               font=small, fill=(190, 186, 180))
        d.text((215, y + 31),
               f"いま画に出ている強さ {float(r['levelNow']):.2f}",
               font=small, fill=(150, 146, 140))

        # 育ち具合のゲージ。数字より先に「どこまで来たか」が見える。
        gx, gy, gw, gh = 16, h + BAR_H - 16, w - 32, 6
        d.rectangle([gx, gy, gx + gw, gy + gh], fill=(46, 44, 42))
        d.rectangle([gx, gy, gx + int(gw * prog), gy + gh], fill=(213, 94, 0))

        canvas.save(out / f"f{i:04d}.png")
        made += 1

    print(f"{made} コマへ帯を焼いた → {out}")
    print("次: py -3.11 tools/make-preview-video.py Assets/Screenshots/glitch-preview/cap glitch")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
