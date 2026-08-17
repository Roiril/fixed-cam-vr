# -*- coding: utf-8 -*-
"""走行をまとめて測り、**条件ごとに中央値で比べる**（1 枚の当たり外れに騙されないため）。

    py -3.11 tools/gen-plate/stats.py --by site "logs/gen-plate/*"
    py -3.11 tools/gen-plate/stats.py --by tag  "logs/gen-plate/e2*" "logs/gen-plate/r4*"

同じプロンプトでも回ごとに落ち方が違うので、**2〜3 枚の比較で「効いた」と言わない。**
`--by` は manifest のキー（site / anomaly / blur / no_refs / place / scale）か
`tag`（走行フォルダ名の先頭 `_` までを条件名にする）。
"""
from __future__ import annotations

import argparse
import glob
import json
import os
import statistics as st
import sys

from PIL import Image

import metrics
import posted

COLS = [("bg_frac_place", "背景 置く側", 100.0), ("bg_frac_keep", "背景 反対側", 100.0),
        ("keep_diff", "反対側の差", 1.0), ("edge", "縁", 1.0), ("grain", "粒", 1.0),
        ("ground", "接地", 1.0), ("added_pct", "面積%", 1.0)]
POST_COLS = [("sat", "彩度(post後)", 1.0)]


def one(d: str) -> dict | None:
    try:
        with open(os.path.join(d, "manifest.json"), encoding="utf-8") as f:
            man = json.load(f)
        raw = Image.open(man["out"])
    except (OSError, ValueError):
        return None
    seed = Image.open(man["seed"]).convert("RGB")
    out = raw.convert("RGB")
    if list(raw.size) != man["size"]:
        out = out.resize(tuple(man["size"]), Image.LANCZOS)
    m = metrics.measure(seed, out, man)
    m.update({k: v for k, v in posted.measure_posted(seed, out, man).items()
              if k in ("sat", "bright")})
    m["_man"] = man
    m["_size_ok"] = list(raw.size) == man["size"]
    return m


def main() -> int:
    ap = argparse.ArgumentParser(description="走行を条件ごとにまとめる")
    ap.add_argument("runs", nargs="+")
    ap.add_argument("--by", default="site")
    args = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")

    dirs = []
    for r in args.runs:
        dirs.extend(sorted(glob.glob(r)) if any(c in r for c in "*?") else [r])

    groups: dict[str, list] = {}
    for d in dirs:
        m = one(d)
        if not m:
            continue
        if args.by == "tag":
            key = os.path.basename(d).split("_")[0]
        else:
            key = str(m["_man"].get(args.by))
        groups.setdefault(key, []).append(m)

    cols = COLS + POST_COLS
    head = f"{'条件':<18}{'n':>3}  " + "".join(f"{ja:>13}" for _, ja, _ in cols)
    print(head)
    print("-" * len(head))
    for key, ms in sorted(groups.items(), key=lambda kv: -len(kv[1])):
        cells = []
        for k, _, mul in cols:
            vals = [m[k] * mul for m in ms if m.get(k) == m.get(k)]
            cells.append(f"{st.median(vals):13.2f}" if vals else f"{'—':>13}")
        print(f"{key:<18}{len(ms):>3}  " + "".join(cells))
    print("\n※ どれも中央値。**n が 3 未満の行は読まない**（回ごとの振れの方が大きい）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
