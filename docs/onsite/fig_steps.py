# -*- coding: utf-8 -*-
"""到着後の手順（4 段）。gallery ref-05 の型 — 縦の細罫で段を区切り、
矢印には受け渡すものの名前を書く。"""
import io, os

W, H = 170, 92
COLS = [16, 55.5, 95, 134.5]     # 各段の左端
CW = 33                          # 段の幅
out = []
A = out.append

A('<?xml version="1.0" encoding="UTF-8"?>')
A(f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {W} {H}" width="{W}mm" height="{H}mm"')
A('     role="img" aria-labelledby="t d">')
A('<title id="t">到着してからの手順 4 段</title>')
A('<desc id="d">配信スマートフォンと卓を立ち上げ、HMD で床の 2 点をタッチして位置を合わせ、'
  '1 周通して動きを見る。人形の合成がずれていたら、卓でカメラを手で合わせる。'
  '段のあいだの矢印は、前の段が次の段へ渡すものを示す。</desc>')
A('''<style>
  text   { font-family: Helvetica, Arial, "Yu Gothic", sans-serif; fill: #000; }
  .step  { font-size: 3.2px; font-weight: bold; }
  .num   { font-size: 2.6px; font-weight: bold; fill: #6a6a6a; }
  .op    { font-size: 2.4px; }
  .desc  { font-size: 2.2px; fill: #444; }
  .pass  { font-size: 2.2px; font-style: italic; fill: #333; }
  .cap   { font-family: "Times New Roman", "Yu Mincho", serif; font-size: 3.4px; font-weight: bold; }
  .sep   { stroke: #b8b8b8; stroke-width: .25; stroke-dasharray: 1.6 1.4; }
  .ln    { fill: none; stroke: #000; stroke-width: .3; }
  .lnt   { fill: none; stroke: #000; stroke-width: .22; }
  .fl    { fill: none; stroke: #333; stroke-width: .3; }
  .f0 { fill: #fcfcfc; } .f1 { fill: #ededed; } .f2 { fill: #d8d8d8; }
  .acc  { fill: none; stroke: #CC79A7; stroke-width: .5; }
  .accf { fill: #CC79A7; }
</style>''')
A('<defs><marker id="ar" viewBox="0 0 2 1.6" refX="2" refY=".8" markerWidth="2" markerHeight="1.6"'
  ' markerUnits="userSpaceOnUse" orient="auto-start-reverse">'
  '<path d="M0,0 L2,.8 L0,1.6 z" fill="#333"/></marker></defs>')

TITLES = [
    ("1", "立ち上げる", ["配信スマホ 3 台を起動", "Quest を 5GHz に繋ぐ", "卓は起動済み"]),
    ("2", "位置を合わせる", ["右トリガー 2 秒長押し", "壁の両端を A で 2 点", "B で確定"]),
    ("3", "通してみる", ["線を横切ると導入", "A→B→C を 3 周", "3 分ほどで終わる"]),
    ("4", "カメラを合わせる", ["卓の 🎯 から", "傾き→位置→向き", "💾 このカメラに保存"]),
]

for i, (num, title, ops) in enumerate(TITLES):
    x = COLS[i]
    A(f'<text x="{x:.1f}" y="9" class="num">{num}</text>')
    A(f'<text x="{x + 5:.1f}" y="9.4" class="step">{title}</text>')
    for j, op in enumerate(ops):
        A(f'<text x="{x:.1f}" y="{70 + j * 4.2:.1f}" class="op">{op}</text>')
    if i < 3:
        sx = x + CW + 3.2
        A(f'<line x1="{sx:.1f}" y1="20" x2="{sx:.1f}" y2="{H - 14}" class="sep"/>')

# ---- 段 1 の絵: スマホ 3 台 + 電波 + ノート PC ------------------------------
x0 = COLS[0]
for k in range(3):
    px = x0 + 1.5 + k * 6.5
    A(f'<rect x="{px:.1f}" y="26" width="4.6" height="8.2" rx=".5" class="f0" stroke="#000" stroke-width=".3"/>')
    A(f'<circle cx="{px + 2.3:.1f}" cy="28" r=".5" fill="#333"/>')
    A(f'<path d="M{px + 2.3:.1f},24.6 q2.4,-2.4 4.8,0" class="lnt"/>')
A(f'<text x="{x0 + 1.5:.1f}" y="39.5" class="desc">A ・ B ・ C</text>')
# ノート PC
A(f'<path d="M{x0 + 3:.1f},58 L{x0 + 24:.1f},58 L{x0 + 26:.1f},61 L{x0 + 1:.1f},61 Z" class="f1" stroke="#000" stroke-width=".3"/>')
A(f'<rect x="{x0 + 5:.1f}" y="45" width="17" height="13" class="f0" stroke="#000" stroke-width=".3"/>')
A(f'<rect x="{x0 + 6.6:.1f}" y="46.6" width="13.8" height="7.4" class="f2"/>')
A(f'<text x="{x0 + 5:.1f}" y="64.2" class="desc">オペレータ卓（PC）</text>')
# スマホ → PC の無線
A(f'<path d="M{x0 + 12:.1f},41 q3,3 1.5,4" class="lnt"/>')

# ---- 段 2 の絵: HMD と床の 2 点 --------------------------------------------
x1 = COLS[1]
A(f'<path d="M{x1 + 6:.1f},26 q10,-3 20,0 q1.6,.6 1.2,3.4 q-.5,3.4 -3,3.4 q-3.4,0 -5,-2.2'
  f' q-1.6,-2 -3.2,-2 q-1.6,0 -3.2,2 q-1.6,2.2 -5,2.2 q-2.5,0 -3,-3.4 q-.4,-2.8 1.2,-3.4 Z"'
  ' class="f1" stroke="#000" stroke-width=".35"/>')
A(f'<text x="{x1 + 8:.1f}" y="39.5" class="desc">かぶる</text>')
# 床と 2 点
A(f'<path d="M{x1 + 2:.1f},58 L{x1 + 22:.1f},58 L{x1 + 31:.1f},48 L{x1 + 11:.1f},48 Z" class="f0" stroke="#000" stroke-width=".3"/>')
for k, (px, py) in enumerate([(x1 + 13, 53.4), (x1 + 24, 53.4)]):
    A(f'<path d="M{px:.1f},{py - 1.7:.1f} L{px + 1.5:.1f},{py + 1.2:.1f} L{px - 1.5:.1f},{py + 1.2:.1f} Z" class="accf"/>')
    A(f'<text x="{px:.1f}" y="{py - 3.0:.1f}" class="desc" text-anchor="middle">{k + 1}</text>')
A(f'<path d="M{x1 + 14.5:.1f},52 q5,-3.4 9,0" class="acc"/>')
A(f'<text x="{x1 + 2:.1f}" y="64.2" class="desc">壁の両端の床を順にタッチ</text>')

# ---- 段 3 の絵: 歩く人 + 前方のスクリーン ----------------------------------
x2 = COLS[2]
A(f'<rect x="{x2 + 6:.1f}" y="24" width="21" height="12.4" class="f2" stroke="#000" stroke-width=".3"/>')
A(f'<text x="{x2 + 16.5:.1f}" y="31.4" class="desc" text-anchor="middle">固定カメラの映像</text>')
A(f'<text x="{x2 + 6:.1f}" y="39.5" class="desc">目の前に出る画面</text>')
A(f'<path d="M{x2 + 2:.1f},58 L{x2 + 22:.1f},58 L{x2 + 31:.1f},48 L{x2 + 11:.1f},48 Z" class="f0" stroke="#000" stroke-width=".3"/>')
A(f'<g transform="translate({x2 + 12:.1f},50.6)">'
  '<path d="M-1.5,5.4 L-1.9,2.2 Q-1.9,.6 0,.6 Q1.9,.6 1.9,2.2 L1.5,5.4 Z" fill="#1a1a1a"/>'
  '<circle cx="0" cy="-1.2" r="1.5" fill="#1a1a1a"/></g>')
A(f'<path d="M{x2 + 15:.1f},55.4 q6,-1.6 10,-4.4" class="fl" marker-end="url(#ar)"/>')
A(f'<text x="{x2 + 2:.1f}" y="64.2" class="desc">歩くと領域ごとに切り替わる</text>')

# ---- 段 4 の絵: 卓の画面（映像 + ワイヤー + 人形） --------------------------
x3 = COLS[3]
A(f'<rect x="{x3 + 1:.1f}" y="24" width="31" height="23" class="f2" stroke="#000" stroke-width=".3"/>')
# 床の格子（ワイヤー）
A(f'<path d="M{x3 + 4:.1f},43 L{x3 + 29:.1f},43 L{x3 + 24:.1f},34 L{x3 + 9:.1f},34 Z" class="acc"/>')
for k in range(1, 3):
    t = k / 3
    A(f'<line x1="{x3 + 4 + (9 - 4) * t:.1f}" y1="{43 - 9 * t:.1f}"'
      f' x2="{x3 + 29 - (29 - 24) * t:.1f}" y2="{43 - 9 * t:.1f}" class="acc" stroke-width=".25"/>')
# 人形
A(f'<g transform="translate({x3 + 17:.1f},36.4)">'
  '<path d="M-1.4,4.8 L-1.7,2.0 Q-1.7,.6 0,.6 Q1.7,.6 1.7,2.0 L1.4,4.8 Z" fill="#1a1a1a"/>'
  '<circle cx="0" cy="-1.1" r="1.4" fill="#1a1a1a"/></g>')
A(f'<text x="{x3 + 1:.1f}" y="51.5" class="desc">実映像に部屋の線と人形を重ねる</text>')
A(f'<path d="M{x3 + 5:.1f},57 l3,-2.6 l0,1.3 l7,0 l0,2.6 l-7,0 l0,1.3 Z" class="f1" stroke="#000" stroke-width=".25"/>')
A(f'<path d="M{x3 + 28:.1f},57 l-3,-2.6 l0,1.3 l-7,0 l0,2.6 l7,0 l0,1.3 Z" class="f1" stroke="#000" stroke-width=".25"/>')
A(f'<text x="{x3 + 1:.1f}" y="64.2" class="desc">線が実物に重なるまでドラッグ</text>')

# ---- 段のあいだの矢印（渡すもの） ------------------------------------------
PASS = [
    ("3 台とも映っている", 0),
    ("原点と床の高さ", 1),
    ("合成のずれ", 2),
]
for text, i in PASS:
    sx = COLS[i] + CW + 3.2
    A(f'<path d="M{sx - 2.4:.1f},17 L{sx + 2.4:.1f},17" class="fl" marker-end="url(#ar)"/>')
    A(f'<text x="{sx:.1f}" y="14.2" class="pass" text-anchor="middle">{text}</text>')

A(f'<text x="{W / 2}" y="{H - 4}" class="cap" text-anchor="middle">'
  '図 2. 到着してからの手順。矢印は前の段が次の段へ渡すもの。</text>')
A('</svg>')

dst = r"C:\Users\kouga\Projects\Unity\fixed-cam-vr\docs\onsite\fig-steps.svg"
os.makedirs(os.path.dirname(dst), exist_ok=True)
io.open(dst, "w", encoding="utf-8", newline="\n").write("\n".join(out))
print("wrote", dst)
