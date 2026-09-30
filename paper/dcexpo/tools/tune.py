"""本文の文字サイズ・行間を振って、ページ下端の空きが最小になる組を探す。

    py -3.10 paper/dcexpo/tools/tune.py            # 探索して上位を表示
    py -3.10 paper/dcexpo/tools/tune.py --apply    # 最良の組を paper.css の :root へ書く

段組が自動で流れるので、文字数や図を変えると下端の空き・見出しの取り残しが動く。
手で 0.1pt ずつ触るより、この探索で「空きが少なく、見出しが取り残されない」組を選ぶ。
点数 = 最終頁以外の（左右の段の下端の空きの平均）の合計 [mm] ＋ 見出しの取り残し 1 件につき 40 ＋ 段の途中の 30mm 超の空白 ＋ 最終頁が本文の 45% に満たない分の罰。
低いほど良い。3 段階の刻みは下の GRID。
"""
import itertools
import re
import sys
from pathlib import Path

import fitz

sys.path.insert(0, str(Path(__file__).parent))
import build  # noqa: E402

sys.stdout.reconfigure(encoding="utf-8")

GRID = {
    "body-size": [f"{v:.2f}pt" for v in (9.3, 9.4, 9.5, 9.6, 9.7, 9.8, 9.9, 10.0)],
    "body-lh": [f"{v:.2f}" for v in (1.54, 1.58, 1.62, 1.66)],
}
PDF_T = build.OUT / "_tune.pdf"


def score(m):
    body = m[:-1]                              # 最終頁は空いてよい
    s = sum((p["gap_l"] + p["gap_r"]) / 2 for p in body)
    s += 40 * sum(p["orphan"] for p in m)
    # 段の途中に 30mm を超える空白があれば、超えた分の 2 倍を罰する（段組の釣り合いが崩れた形）
    s += sum(2 * max(0.0, p["blank_l"] - 30) + 2 * max(0.0, p["blank_r"] - 30) for p in m)
    # 最終頁がほぼ空（数行だけ溢れた）なのは避ける。本文の高さの 45% 未満なら、足りない分を罰する。
    body_h = 297 - build.M_TOP - build.M_BOTTOM
    fill = 1 - m[-1]["gap"] / body_h
    if fill < 0.45:
        s += (0.45 - fill) * 120
    return s


def main():
    rows = []
    keys = list(GRID)
    for combo in itertools.product(*GRID.values()):
        v = dict(zip(keys, combo))
        html = build.html_with_vars(v)
        build.render_pdf(html, PDF_T, quiet=True)
        doc = fitz.open(PDF_T)
        m = build.page_metrics(doc)
        rows.append((score(m), len(doc), v, m))
        gaps = " ".join(f"{p['gap_l']:.0f}/{p['gap_r']:.0f}" for p in m)
        print(f"{v['body-size']:>7} lh{v['body-lh']}  {len(doc)}p  score {rows[-1][0]:6.1f}  下端の空き(左/右mm) {gaps}", flush=True)
        doc.close()
    rows.sort(key=lambda r: r[0])
    print("\n上位 5:")
    for sc, n, v, m in rows[:5]:
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
