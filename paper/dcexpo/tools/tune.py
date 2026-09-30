"""本文の文字サイズ・行間を振って、組版の点数が最も良い組を探す。

    py -3.10 paper/dcexpo/tools/tune.py            # 探索して上位を表示
    py -3.10 paper/dcexpo/tools/tune.py --apply    # 最良の組を paper.css の :root へ書く

点数（低いほど良い）:
  ・最終頁以外の「本文の窓の余り」の合計 [mm]   … 行の区切りで詰められなかった下端の空き
  ・図の置き場所の規則違反（引用から離れている等）1 件につき 200
  ・ページ数 1 頁につき 30                       … 図の頁が決まっているので、無駄に増やさない
  ・最終頁が本文の高さの 45% に満たない分の罰    … 数行だけ溢れた頁を避ける
"""
import itertools
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import build  # noqa: E402

sys.stdout.reconfigure(encoding="utf-8")

GRID = {
    "body-size": [f"{v:.2f}pt" for v in (9.0, 9.1, 9.2, 9.3, 9.4, 9.5)],
    "body-lh": [f"{v:.2f}" for v in (1.56, 1.60, 1.64, 1.68)],
}
PDF_T = build.OUT / "_tune.pdf"


def score(rep):
    if rep is None:
        return 1e9
    pages = rep["pages"]
    s = sum(p["slack_mm"] for p in pages[:-1])
    s += 200 * len(build.check_layout(rep, quiet=True))
    s += 30 * len(pages)
    body_h = 297 - build.M_TOP - build.M_BOTTOM
    fill = 1 - pages[-1]["slack_mm"] / body_h
    if fill < 0.45:
        s += (0.45 - fill) * 120
    return s


def main():
    rows = []
    keys = list(GRID)
    for combo in itertools.product(*GRID.values()):
        v = dict(zip(keys, combo))
        html = build.html_with_vars(v)
        rep = build.render_pdf(html, PDF_T, quiet=True)
        sc = score(rep)
        n = len(rep["pages"]) if rep else 0
        slack = " ".join(f"{p['slack_mm']:.0f}" for p in rep["pages"]) if rep else "-"
        rows.append((sc, n, v))
        print(f"{v['body-size']:>7} lh{v['body-lh']}  {n}p  score {sc:7.1f}  窓の余り(mm) {slack}", flush=True)
    rows.sort(key=lambda r: r[0])
    print("\n上位 5:")
    for sc, n, v in rows[:5]:
        print(f"  {v['body-size']} lh{v['body-lh']}  {n}p  score {sc:.1f}")
    for f in (build.ROOT / "_tune.html", PDF_T):
        if f.exists():
            f.unlink()
    if "--apply" in sys.argv:
        best = rows[0][2]
        css = (build.ROOT / "paper.css").read_text(encoding="utf-8")
        for k, val in best.items():
            css, n = re.subn(rf"(--{k}:\s*)[^;]+;", rf"\g<1>{val};", css, count=1)
            assert n == 1, k
        with open(build.ROOT / "paper.css", "w", encoding="utf-8", newline="\n") as f:
            f.write(css)
        print("paper.css へ書いた:", best)


if __name__ == "__main__":
    main()
