# -*- coding: utf-8 -*-
"""**当日、何枚焼けばよいか**（手元の走行を、いまの計器で判定し直して数える）。

    py -3.11 tools/gen-plate/yield.py [--anomaly dolls-many]

歩留まりはプロンプトでは動かないと決まっている（4 手試して全部だめ・`runs.md`）。
**枚数で解く**しかないので、では何枚かを手元の走行から出す。

⚠⚠ **保存済みの `judge.json` を読まない。** あれは焼いた当時の計器の答えで、
線も指標も動いている（2026-08-18 に縁の測り方と線が変わった）。**その場で測り直す。**

⚠ 手元の走行は無作為標本ではない（不変部を変えながら焼いた回・試験用の回が混ざる）。
だから **`--only-current` で「いまの不変部で焼いた回」だけに絞れる**ようにしてある。
それでも「この場所は荒れる」という**桁**を掴む以上のことはできない。
"""
from __future__ import annotations

import argparse
import json
import os
import sys

import numpy as np
from PIL import Image

import judge
import metrics
import spec

# いまの不変部にしか無い文言（これがプロンプトに在れば「いまの形で焼いた回」）
CURRENT_MARK = "入力画像より鮮やかにしない"


def judge_run(run_dir: str) -> dict | None:
    mp = os.path.join(run_dir, "manifest.json")
    if not os.path.exists(mp):
        return None
    with open(mp, encoding="utf-8") as f:
        man = json.load(f)
    if not os.path.exists(man["out"]) or not os.path.exists(man["seed"]):
        return None
    seed = Image.open(man["seed"]).convert("RGB")
    raw = Image.open(man["out"])
    out = raw.convert("RGB")
    if list(raw.size) != man["size"]:
        out = out.resize(tuple(man["size"]), Image.LANCZOS)
    m = metrics.measure(seed, out, man)
    checks = judge.build_checks(m, man, list(raw.size) == man["size"])
    ng = [c[0] for c in checks if not c[1]]
    cur = False
    pp = os.path.join(run_dir, "prompt.txt")
    if os.path.exists(pp):
        cur = CURRENT_MARK in open(pp, encoding="utf-8").read()
    return dict(run=os.path.basename(run_dir), site=man["site"], anomaly=man["anomaly"],
                ng=ng, ok=not ng, current=cur)


def main() -> int:
    ap = argparse.ArgumentParser(description="当日は何枚焼けばよいか")
    ap.add_argument("--anomaly", default="dolls-many")
    ap.add_argument("--only-current", action="store_true",
                    help="いまの不変部で焼いた回だけ数える")
    args = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")

    root = os.path.join(spec.REPO, "logs", "gen-plate")
    rows = []
    for name in sorted(os.listdir(root)):
        d = os.path.join(root, name)
        if not os.path.isdir(d):
            continue
        try:
            r = judge_run(d)
        except Exception as e:                       # 壊れた走行は数えない（黙って落とさない）
            print(f"  （読めない走行 {name}: {e}）")
            continue
        if r and r["anomaly"] == args.anomaly and (r["current"] or not args.only_current):
            rows.append(r)

    if not rows:
        print("数える走行が無い")
        return 2

    print(f"== {args.anomaly}  走行 {len(rows)} 件"
          f"{'（いまの不変部だけ）' if args.only_current else ''}   いまの計器で測り直した値")
    print(f"\n{'場所':16s}{'n':>4}{'通った':>7}{'割合':>7}"
          f"{'3 枚で 1 枚も通らない':>22}{'9 割ほしいなら':>16}")
    print("-" * 74)
    for site in sorted({r["site"] for r in rows}):
        g = [r for r in rows if r["site"] == site]
        k = sum(1 for r in g if r["ok"])
        p = k / len(g)
        miss = (1 - p) ** 3
        need = "—" if p <= 0 else f"{int(np.ceil(np.log(0.10) / np.log(1 - p)))} 枚" if p < 1 else "1 枚"
        print(f"{site:16s}{len(g):4d}{k:7d}{p * 100:6.0f}%{miss * 100:21.0f}%{need:>16}")

    k = sum(1 for r in rows if r["ok"])
    p = k / len(rows)
    print(f"{'すべて':16s}{len(rows):4d}{k:7d}{p * 100:6.0f}%"
          f"{(1 - p) ** 3 * 100:21.0f}%"
          f"{('—' if p <= 0 else f'{int(np.ceil(np.log(0.10) / np.log(1 - p)))} 枚'):>16}")

    print(f"\n{'落ちた門':28s}{'件数':>5}{'割合':>7}")
    print("-" * 42)
    cnt = {}
    for r in rows:
        for x in r["ng"]:
            cnt[x] = cnt.get(x, 0) + 1
    for name, c in sorted(cnt.items(), key=lambda kv: -kv[1]):
        print(f"{name:28s}{c:5d}{c / len(rows) * 100:6.0f}%")

    # --- どの門を必須にするかで、必要な枚数が桁で変わる ---
    #   ⚠ この分け方は `runs.md` の実測から来ている。判定器の中では全部が同じ重みなので、
    #     「全部通す」を要求すると**体験者に届かない欠陥のために焼き直す**ことになる。
    lens = {
        "寸法": "届かない。判定が out_fit.png を書くので素材にはそれを使う（`runs.md` 08-17）",
        "周りより鮮やかでない": "届かない。この素材が出るのは帰りの A だけで、そこは完全な無彩",
        "置かない側を触っていない": "届かない。半分マスクなので反対側はライブが出る",
    }
    print(f"\n{'必須にする門':34s}{'通る':>6}{'割合':>7}{'9 割ほしいなら':>16}")
    print("-" * 64)

    def yield_of(drop: set) -> tuple[int, float]:
        k = sum(1 for r in rows if not (set(r["ng"]) - drop))
        return k, k / len(rows)

    steps = [("全部", set())]
    acc = set()
    for name in lens:
        acc = acc | {name}
        steps.append((f"{name} を外す", set(acc)))
    for label, drop in steps:
        k, p = yield_of(drop)
        need = "—" if p <= 0 else ("1 枚" if p >= 1 else
                                   f"{int(np.ceil(np.log(0.10) / np.log(1 - p)))} 枚")
        print(f"{label:34s}{k:6d}{p * 100:6.0f}%{need:>16}")

    print("\n外してよい理由（`runs.md` の実測）:")
    for name, why in lens.items():
        print(f"  - **{name}** … {why}")
    print("  ⚠ ただし**印としては読む** — 3 つとも「モデルが編集ではなく描き起こした」ときに一緒に出る。"
          "\n    落ちた回は、届く門も落ちていないか必ず見る")

    print("\n⚠ 手元の走行は無作為標本ではない（不変部を変えながら焼いた回が混ざる）。"
          "桁を掴む以上のことはできない")
    print("⚠ ここで通っても**届いた画の合否は別**（`deliver.py`）。当日は 2 つとも通す")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
