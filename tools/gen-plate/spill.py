# -*- coding: utf-8 -*-
"""マスクの境目より右へ、足したものがどれだけ溢れているか（`canon/LEDGER.md` 0120）。

    py -3.11 tools/gen-plate/spill.py --run logs/gen-plate/<走行> [--run ...]

**`judge.py` の「置かない側を触っていない」とは別のものを測る。** あちらは `place`（焼くときに
指定した置き場所）を基準にするので、place を狭めると同じ絵でも数字が変わる。
体験者に効くのは **合成マスクの境目 1 本**（`split_left_half` ＝ 枠 0.5 ＝ 素材 x=320）で、
そこを跨いだ塊は**実機で縦にスパッと切れる**。

⚠ 「右半分に人形が見える」ことではない。右半分は必ずライブなので人形は 1 体も出ない。
   起きるのは**切れる**こと。ユーザーの言う「右半分にあふれる」はこれ（0120）。
"""
from __future__ import annotations

import argparse
import json
import os
import sys

import numpy as np
from PIL import Image

import metrics

# 素材座標。枠空間 0.5（split_left_half の境目）と一致する。
# 素材 4:3 は枠 16:9 へ contain-fit されるので 素材 320 → 枠 80 + 320×0.75 = 320。
CUT = 320


def measure(run: str, cut: int = CUT) -> dict:
    man = json.load(open(os.path.join(run, "manifest.json"), encoding="utf-8"))
    seed = Image.open(man["seed"]).convert("RGB")
    gen_p = os.path.join(run, "out_fit.png")
    if not os.path.exists(gen_p):
        gen_p = os.path.join(run, "out.png")
    gen = Image.open(gen_p).convert("RGB")
    if gen.size != seed.size:
        gen = gen.resize(seed.size, Image.LANCZOS)

    d = np.abs(np.asarray(gen, dtype=np.float64)
               - np.asarray(seed, dtype=np.float64)).max(axis=2)
    added = metrics.clean(d > metrics.ADD_THR)

    h, w = added.shape
    tot = int(added.sum())
    right = int(added[:, cut:].sum())
    cols = np.nonzero(added.any(axis=0))[0]
    parts = metrics.components(added)
    straddle = [c for c in parts if c["x0"] < cut < c["x1"]]

    return dict(run=os.path.basename(run), place=man.get("place"),
                added_px=tot, right_px=right,
                right_pct=100.0 * right / max(1, tot),
                max_x=int(cols.max()) if len(cols) else 0,
                straddle=len(straddle),
                straddle_area=int(sum(c["area"] for c in straddle)))


def main() -> int:
    ap = argparse.ArgumentParser(description="合成マスクの境目を越えた分を測る")
    ap.add_argument("--run", action="append", required=True, help="走行フォルダ（複数可）")
    ap.add_argument("--cut", type=int, default=CUT, help=f"境目の素材 x 座標（既定 {CUT}）")
    a = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")

    rows = [measure(r, a.cut) for r in a.run]
    print(f"   {'走行':26s} {'place':10s} {'越えた画素':>10s} {'足した所の%':>11s} "
          f"{'右端 x':>7s} {'跨ぐ塊':>6s} {'跨ぐ面積':>9s}")
    ok = True
    for r in rows:
        mark = "OK" if r["straddle"] == 0 else "NG"
        if r["straddle"]:
            ok = False
        print(f"{mark} {r['run']:26s} {str(r['place']):10s} {r['right_px']:10d} "
              f"{r['right_pct']:10.1f}% {r['max_x']:7d} {r['straddle']:6d} "
              f"{r['straddle_area']:9d}")
    print()
    print(f"※ 境目 = 素材 x={a.cut}。**跨ぐ塊が 0 のものだけ採る**（0120）。"
          "越えた画素があっても、跨ぐ塊が 0 なら切れてはいない")
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
