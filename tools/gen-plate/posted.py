# -*- coding: utf-8 -*-
"""**実機の post を通した後**の指標を測る（追う価値のある欠点を選ぶため）。

    py -3.11 tools/gen-plate/posted.py logs/gen-plate/r4_* logs/gen-plate/q_*

素材は「生映像の位置」に入り、そのあと実機のポスト処理（彩度を抜く・ヴィネット・
コントラスト・色温度）を浴びてから体験者の目に入る（`rules/streaming.md`
「合成はポスト FX の前。浴びないと必ず浮く」）。

    種 = dim(プレート)  →  生成  →  undim  →  素材  →  **実機で post**  →  体験者

だから**生成直後の数値は、体験者が見る状態の数値ではない。**
post を通すと消える欠点（彩度が浮く 等）を追い続けても、体験は 1 ミリも良くならない。
逆に post を通しても残る欠点（縁の鋭さ・接地の影の不在）は、そこでしか直せない。

この道具は生成を必要としない。**手持ちの走行を測り直すだけ**で、
どの指標を追うべきかが決まる。
"""
from __future__ import annotations

import argparse
import glob
import importlib.util
import json
import os
import sys

from PIL import Image

import judge
import metrics
import spec

KEYS = [("grain", "粒"), ("edge", "縁"), ("ground", "接地"), ("bright", "明るさ"),
        ("sat", "彩度"), ("dark_added", "足した所の暗部"), ("see_through", "透け")]


def _gen_tone():
    path = os.path.join(spec.REPO, "tools", "gen-tone.py")
    s = importlib.util.spec_from_file_location("gen_tone", path)
    m = importlib.util.module_from_spec(s)
    s.loader.exec_module(m)
    return m


_GT = None


def to_screen(im: Image.Image, man: dict):
    """素材へ戻して（undim）、実機の post を丸ごと掛ける ＝ 体験者が見る状態。"""
    global _GT
    if _GT is None:
        _GT = _gen_tone()
    post = _GT.load_post(man.get("cam"))
    scale = float(man.get("scale", 0.5))
    exp_only = not man.get("full_post", False)
    return _GT.dim(_GT.undim(im, post, scale, exp_only), post, 1.0, False)


def measure_posted(seed: Image.Image, out: Image.Image, man: dict) -> dict:
    """post を通した状態で測る（`judge.py` が彩度・明るさ・暗部にこれを使う）。"""
    return metrics.measure(to_screen(seed, man), to_screen(out, man), man)


def through_post(run_dir: str, gt) -> tuple[dict, dict, dict]:
    """生成直後と、実機の post を浴びた後で、同じ指標を測る。"""
    with open(os.path.join(run_dir, "manifest.json"), encoding="utf-8") as f:
        man = json.load(f)
    seed = Image.open(man["seed"]).convert("RGB")
    raw = Image.open(man["out"])
    out = raw.convert("RGB")
    if list(raw.size) != man["size"]:
        out = out.resize(tuple(man["size"]), Image.LANCZOS)

    post = gt.load_post(man.get("cam"))
    scale = float(man.get("scale", 0.5))
    exp_only = not man.get("full_post", False)

    # 素材へ戻し（undim）、実機の post を丸ごと掛ける
    to_screen = lambda im: gt.dim(gt.undim(im, post, scale, exp_only), post, 1.0, False)
    return man, metrics.measure(seed, out, man), \
        metrics.measure(to_screen(seed), to_screen(out), man)


def main() -> int:
    ap = argparse.ArgumentParser(description="post を通した後の指標")
    ap.add_argument("runs", nargs="+")
    args = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")

    dirs = []
    for r in args.runs:
        dirs.extend(sorted(glob.glob(r)) if any(c in r for c in "*?") else [r])
    dirs = [d for d in dirs if os.path.exists(os.path.join(d, "out.png"))]

    gt = _gen_tone()
    rows = []
    for d in dirs:
        try:
            man, a, b = through_post(d, gt)
        except Exception as e:                      # 走行が壊れていても止めない
            print(f"{os.path.basename(d)}: 測れない（{e}）")
            continue
        rows.append((os.path.basename(d), a, b))
        print(f"== {os.path.basename(d)}  ({man['anomaly']} @ {man['site']})")
        for k, ja in KEYS:
            if a[k] != a[k]:
                continue
            arrow = "→" if abs(a[k] - b[k]) > 1e-6 else "＝"
            print(f"   {ja:12s} 生成直後 {judge._fmt(a[k])} {arrow} post 後 {judge._fmt(b[k])}")

    if len(rows) >= 2:
        print("\n== まとめ（post を通したときの動き）")
        for k, ja in KEYS:
            pairs = [(a[k], b[k]) for _, a, b in rows if a[k] == a[k] and b[k] == b[k]]
            if not pairs:
                continue
            d = [abs(y - x) / max(1e-6, abs(x)) for x, y in pairs]
            print(f"   {ja:12s} 変化の中央値 {sorted(d)[len(d) // 2] * 100:5.1f}%  "
                  f"（{len(pairs)} 走行）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
