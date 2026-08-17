# -*- coding: utf-8 -*-
"""生成物を種と突き合わせて合否を出す。

    py -3.11 tools/gen-plate/judge.py --run logs/gen-plate/<走行>
    py -3.11 tools/gen-plate/judge.py --run <走行> --out 別の候補.png

**線は場所に依らない形（種との比）で引いてある。** 部屋が変わっても引き直さない。
異変ごとに変わるのは、足す量の下限・上限と、塊の数と、面に応じた検査の有無だけ
（`anomaly/*.md` の頭書き）。

⚠⚠ **数値が全部緑でも絵は必ず開く。** 判定器は落とすためだけに使う。
実測で、1.000 / 1.000 / 差 0.0 の完璧な数値の出力が、人形が床に乗っておらず
切り抜きを並べたように見えたことがある。走行ごとに `sheet.png`（塊へ分解して並べた 1 枚）を
出すので、**それを開くまでが 1 周**。
"""
from __future__ import annotations

import argparse
import json
import os
import sys

from PIL import Image

import metrics
import sheet as sheet_mod

NAN = float("nan")


def _fmt(v, digits=2) -> str:
    return "—" if v != v else f"{v:.{digits}f}"


def _ok(v, test) -> bool:
    """測れなかった（nan）ものは判定に入れない。

    足していなければ粒も縁も存在しないので、そこは「足されている」が落とす。
    nan を False に倒すと、1 つの欠落が別の名前で何度も落ちて原因が読めなくなる。
    """
    return True if v != v else bool(test(v))


def build_checks(m: dict, man: dict, size_ok: bool) -> list[tuple]:
    """(名前, 合否, 表示, 何を見ているか) の並び。**線はここ 1 か所**。"""
    lo, hi = man["min_area_pct"], man["max_area_pct"]
    c = [
        ("寸法", size_ok, f"{man['size'][0]}x{man['size'][1]} を期待",
         "モデルは黙って拡大する"),
        # ⚠ 寸法が違う出力は**縮める時点で全画素が少し動く**ので、そのままの線では測れない
        #   （実測: 96% を保っていた回が、縮めた版では 86% に見える）。線を緩める代わりに
        #   「置かない側の差」（下）が本体の門として効く
        ("背景を塗り直していない",
         (m["bg_frac_place"] >= (0.35 if size_ok else 0.20)
          and m["bg_frac_keep"] >= (0.90 if size_ok else 0.60)),
         f"触っていない画素 置く側 {m['bg_frac_place'] * 100:.0f}% / "
         f"反対側 {m['bg_frac_keep'] * 100:.0f}%" + ("" if size_ok else "（縮めた版で測った参考値）"),
         "似た別の部屋を描き起こしていないか"),
        ("明るさを保つ",
         all(_ok(m[k], lambda v: 0.95 <= v <= 1.05)
             for k in ("tone_mean_place", "tone_mean_keep")),
         f"置く側 {_fmt(m['tone_mean_place'], 3)} / 反対側 {_fmt(m['tone_mean_keep'], 3)}",
         "生成の露出ずれ"),
        ("コントラストを保つ", _ok(m["tone_sd_place"], lambda v: 0.85 <= v <= 1.20),
         f"sd 比 {_fmt(m['tone_sd_place'], 3)}", "モデルは既定で「綺麗に」する"),
        ("置かない側を触っていない", m["keep_diff"] <= 8.0 and m["keep_added_pct"] <= 0.5,
         f"差 {m['keep_diff']:.1f} / 足された面積 {m['keep_added_pct']:.2f}%",
         "半分マスクの反対側"),
        ("継ぎ目に段差が無い", _ok(m["seam"], lambda v: v <= 4.0),
         f"{_fmt(m['seam'])}", "境目に線が出ると装置が 2 台に見える"),
        ("足されている", lo <= m["added_pct"] <= hi,
         f"{m['added_pct']:.1f}%（{lo:.1f}〜{hi:.1f}%）", "何も足していない / 塗り替えた"),
        ("塊の数", m["parts"] >= man["min_parts"],
         f"{m['parts']} 個（{man['min_parts']} 以上）", "1 つの塊に潰れていないか"),
        ("粒が乗っている", _ok(m["grain"], lambda v: v >= 0.45),
         f"{_fmt(m['grain'])}（周りの粒との比）", "つるつるだと CG に見える"),
        ("縁が刃物でない", _ok(m["edge"], lambda v: v <= 1.10),
         f"{_fmt(m['edge'])}（種の強い縁との比）", "切り抜きを貼ると縁だけ鋭い"),
        ("暗部が沈む",
         _ok(m["dark_added"], lambda v: v <= m["dark_bg"] * 1.8 + 8.0),
         f"足した所 {_fmt(m['dark_added'], 0)} / その場所の暗がり {_fmt(m['dark_bg'], 0)}",
         "暗がりで光っていると浮く"),
        ("周りより明るくない", _ok(m["bright"], lambda v: v <= 1.45),
         f"{_fmt(m['bright'])}（その場所との比）", "足したものだけ露出が違う"),
        ("周りより鮮やかでない", _ok(m["sat"], lambda v: v <= 2.2),
         f"{_fmt(m['sat'])}（その場所との比）", "彩度が高いと絵の具に見える"),
    ]
    if m["ground"] == m["ground"]:
        c.append(("接地の影がある", m["ground"] >= 1.5, f"直下が {_fmt(m['ground'])} 暗い",
                  "影が無いと切り抜きを並べたように見える"))
    if m["persp"] == m["persp"]:
        c.append(("手前ほど大きい", m["persp"] >= 0.15, f"相関 {_fmt(m['persp'])}",
                  "床のパースに乗っているか"))
    if not man.get("opaque", True) and m["see_through"] == m["see_through"]:
        c.append(("下地が透ける", m["see_through"] >= 0.50, f"相関 {_fmt(m['see_through'])}",
                  "染みや手形は面の凹凸を消さない"))
    return c


def run(run_dir: str, out_path: str | None, quiet_sheet: bool) -> int:
    with open(os.path.join(run_dir, "manifest.json"), encoding="utf-8") as f:
        man = json.load(f)
    out_path = out_path or man["out"]
    if not os.path.exists(out_path):
        print(f"生成物が無い: {out_path}")
        return 2

    seed = Image.open(man["seed"]).convert("RGB")
    raw = Image.open(out_path)
    size_ok = list(raw.size) == man["size"]
    out = raw.convert("RGB")
    if not size_ok:
        out = out.resize(tuple(man["size"]), Image.LANCZOS)

    m = metrics.measure(seed, out, man)
    checks = build_checks(m, man, size_ok)
    ng = [c for c in checks if not c[1]]

    print(f"== {man['anomaly']} @ {man['site']}  place={man['place']}  "
          f"{os.path.basename(out_path)} ==")
    if not size_ok:
        fit = os.path.join(run_dir, "out_fit.png")
        out.save(fit)
        print(f"  （{raw.size} で返ってきたので {tuple(man['size'])} へ縮めて測った。"
              f"素材にするのは {os.path.basename(fit)} の方）")
    for name, ok, detail, why in checks:
        print(f"  {'OK ' if ok else 'NG '} {name:22s} {detail}")
    print(f"  → {'合格' if not ng else f'不合格 {len(ng)} 件'}")
    if m["keep_diff"] == 0.0 and man.get("keep_out"):
        print("  ⚠ 置かない側の差が厳密に 0。生成ではなく合成した疑い"
              "（走行フォルダに置き土産が無いか見る）")

    result = dict(out=out_path, ng=[c[0] for c in ng],
                  metrics={k: v for k, v in m.items() if not k.startswith("_")
                           and k != "components"})
    with open(os.path.join(run_dir, "judge.json"), "w", encoding="utf-8") as f:
        json.dump(result, f, ensure_ascii=False, indent=2, default=str)

    if not quiet_sheet:
        p = sheet_mod.build(seed, out, m, man, run_dir, checks)
        print(f"  絵にした: {p}  ← **これを開くまでが 1 周**")
    return 0 if not ng else 1


def main() -> int:
    ap = argparse.ArgumentParser(description="生成物の合否")
    ap.add_argument("--run", required=True)
    ap.add_argument("--out", default=None, help="manifest の out 以外を測るとき")
    ap.add_argument("--no-sheet", action="store_true")
    args = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")
    return run(args.run, args.out, args.no_sheet)


if __name__ == "__main__":
    raise SystemExit(main())
