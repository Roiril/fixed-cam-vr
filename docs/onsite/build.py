# -*- coding: utf-8 -*-
"""現地手順書を組み立てる。  python docs/onsite/build.py

  fig_room.py  … 配置図（show.json の layout から生成）
  fig_steps.py … 手順図
  index.body.html … 本文（ここを直す）
  → index.html（単一ファイル・CSS と SVG をインライン展開）

図を直したら  node ~/.claude/skills/figure/tools/figure.mjs both docs/onsite/fig-room.svg
で検査して PNG を目で見ること（機械検査は重なりしか見ていない）。
"""
import io, os, re, runpy, sys

HERE = os.path.dirname(os.path.abspath(__file__))
CSS = r"C:\Users\kouga\.claude\skills\visual-deliverable\assets\report.css"

for f in ("fig_room.py", "fig_steps.py"):
    runpy.run_path(os.path.join(HERE, f), run_name="__main__")

def inline_svg(name):
    s = io.open(os.path.join(HERE, name), encoding="utf-8").read()
    s = re.sub(r"<\?xml[^>]*\?>\s*", "", s)
    return re.sub(r'\swidth="[\d.]+mm"\s+height="[\d.]+mm"', ' width="100%"', s, count=1)

body = io.open(os.path.join(HERE, "index.body.html"), encoding="utf-8").read()
if "<!--FIG_STEPS-->" in body:
    body = body.replace("<!--FIG_STEPS-->", inline_svg("fig-steps.svg"))
    body = body.replace("<!--FIG_ROOM-->", inline_svg("fig-room.svg"))

css = io.open(CSS, encoding="utf-8").read()
# ⚠ report.css の冒頭コメントに「<style> … </style> に展開する」という説明文が入っている。
#   HTML パーサは CSS コメントを見ないので、この閉じタグでスタイルが終わり本文に化ける。
css = css.replace("</style>", "<\/style>")

html = f"""<!DOCTYPE html>
<html lang="ja">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>廻リ視 — 到着したらこの順で（2026-08-05）</title>
<style>
{css}
figure svg {{ background: #fff; border: 1px solid var(--line); display: block; }}
ol {{ margin: var(--sp-2) 0 var(--sp-4); padding-left: 1.4em; }}
table th {{ white-space: normal; }}
</style>
</head>
<body>
{body}</body>
</html>
"""
dst = os.path.join(HERE, "index.html")
io.open(dst, "w", encoding="utf-8", newline="\n").write(html)
print("wrote", dst, len(html), "bytes")
