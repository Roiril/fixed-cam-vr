"""図の割り当て頁を試す。1 回 6 秒。

    py -3.10 paper/dcexpo/tools/assign.py "1b 2t 3b 4b 5t 6t 7t 8t"     # 図 1〜8 を 頁+位置（t=上端 b=下端）で指定して適用
    py -3.10 paper/dcexpo/tools/assign.py                               # いまの割り当てで報告だけ出す

例 "2t" = 2 頁の上端。適用すると paper.html の data-page / data-pos を書き換え、組版の報告
（図ごとの割り当て頁・引用頁、頁ごとの本文の長さと窓の余り）を表で出す。
図の量が多く、置き方で本文の窓が潰れる（build.py は本文 45mm×段数 未満を NG にする）。
図を動かすと本文の流れも動いて引用頁が変わるので、数回の反復が要る。
"""
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import build  # noqa: E402

sys.stdout.reconfigure(encoding="utf-8")
HTML = build.ROOT / "paper.html"

if len(sys.argv) > 1:
    spec = dict()
    for i, tok in enumerate(sys.argv[1].split(), 1):
        m = re.fullmatch(r"(\d)([tb])", tok)
        assert m, f"'{tok}' は 頁+t|b の形で"
        spec[f"fig{i}"] = (int(m.group(1)), "top" if m.group(2) == "t" else "bottom")
    s = HTML.read_text(encoding="utf-8")
    s = re.sub(r'<figure id="(fig\d)" data-page="\d+" data-pos="\w+"',
               lambda m: f'<figure id="{m.group(1)}" data-page="{spec[m.group(1)][0]}" data-pos="{spec[m.group(1)][1]}"', s)
    with open(HTML, "w", encoding="utf-8", newline="\n") as f:
        f.write(s)

rep = build.render_pdf(quiet=True)
for f in rep["figs"]:
    print(f"{f['id']}: p{f['page']} {f['pos']:6s} / 引用 p{f['cited_page']}")
print("頁: 本文(mm, 全段の合計) / 窓の余り(mm)")
for p in rep["pages"]:
    print(f"  p{p['page']}: {p['text_mm']:.0f} / {p['slack_mm']:.1f}")
ng = build.check_layout(rep, quiet=True)
print("NG:" if ng else "OK")
for m in ng:
    print(" -", m)
