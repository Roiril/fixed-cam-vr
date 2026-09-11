# -*- coding: utf-8 -*-
"""現場の配置図（斜投影）。show.json の実データから起こす。

写像:
    X(x,z,h) = 89 + 36.667*x + 15.833*z
    Y(x,z,h) = 92 - 13.333*z - 16*h
  x: course の東西（+ が東）/ z: 南北（+ が北・奥）/ h: 床からの高さ [m]
  ⚠ 高さだけ 16/m へ圧縮している（実スケールだと 1.8m の壁が 1.8m 四方の床と同じ丈になり、
    床の区分・順路・線という図の主題が壁の裏に潰れる）。丈はラベルの実寸で言う。
"""
import io, json, math, os

SX, SZX, OX = 36.667, 15.833, 89.0
SZY, SH, OY = 13.333, 16.0, 92.0

def P(x, z, h=0.0):
    return (OX + SX * x + SZX * z, OY - SZY * z - SH * h)

def pt(x, z, h=0.0):
    a, b = P(x, z, h)
    return f"{a:.2f},{b:.2f}"

def poly(pts, cls):
    d = " ".join(("M" if i == 0 else "L") + pt(*p) for i, p in enumerate(pts)) + " Z"
    return f'<path d="{d}" class="{cls}"/>'

show = json.load(io.open(
    r"C:\Users\kouga\Projects\Unity\fixed-cam-vr\tools\web-compositor\show.json", encoding="utf-8"))
L = show["layout"]
FW, FD = L["floor"]["w"] / 2, L["floor"]["d"] / 2
ln = L["lines"][0]
# 開始位置の円。show.json に無ければ WalkGuideLogic と同じ導出（角から LaneOffsetM=0.42 ずつ内側・半径 0.245）。
# 2026-09 から show.json は円を持たない（hasStartSpot=false）ので、こちらが通常の経路。
if L.get("hasStartSpot") and L.get("startSpot"):
    sp = L["startSpot"]
else:
    _c = L["wall"]["corner"]
    sp = {"x": _c[0] + 0.42, "z": _c[1] - 0.42, "radiusM": 0.245}
r = sp["radiusM"]

out = []
A = out.append

A('<?xml version="1.0" encoding="UTF-8"?>')
A('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 16 170 100" width="170mm" height="100mm"')
A('     role="img" aria-labelledby="t d">')
A('<title id="t">現場の配置 — 3 つの領域とカメラ 3 台</title>')
A('<desc id="d">1.8m 四方の床が A・B・C の 3 領域に分かれ、領域ごとに 1 台のスマートフォンが撮る。'
  '体験者は開始位置に立ち、床に引いた線を横切ると導入が始まる。その後 A から B、C の順に 3 周する。'
  'L 字パーテーションの両端の床が、位置合わせでタッチする 2 点。</desc>')
A('''<style>
  text   { font-family: Helvetica, Arial, "Yu Gothic", sans-serif; fill: #000; }
  .thing { font-size: 2.9px; font-weight: bold; }
  .spec  { font-size: 2.3px; fill: #333; }
  .zone  { font-size: 4.6px; font-weight: bold; fill: #6a6a6a; }
  .zn    { font-size: 2.9px; font-weight: bold; fill: #6a6a6a; }
  .cap   { font-family: "Times New Roman", "Yu Mincho", serif; font-size: 3.4px; font-weight: bold; }
  .ln    { fill: none; stroke: #000; stroke-width: .3; }
  .lead  { fill: none; stroke: #000; stroke-width: .2; }
  .flow  { fill: none; stroke: #333; stroke-width: .35; stroke-dasharray: 1.8 1.2; }
  .f0 { fill: #fcfcfc; stroke: #000; stroke-width: .3; }
  .f1 { fill: #f2f2f2; stroke: #9a9a9a; stroke-width: .2; }
  .f2 { fill: #dcdcdc; stroke: #9a9a9a; stroke-width: .2; }
  .f3 { fill: #c2c2c2; stroke: #9a9a9a; stroke-width: .2; }
  .wall { fill: #ffffff; fill-opacity: .6; stroke: #000; stroke-width: .3; }
  .acc  { fill: none; stroke: #CC79A7; stroke-width: .8; }
  .accf { fill: #CC79A7; }
  .accd { fill: none; stroke: #CC79A7; stroke-width: .35; stroke-dasharray: 1.2 .9; }
</style>''')
A('<defs><marker id="ar" viewBox="0 0 2 1.6" refX="2" refY=".8" markerWidth="2" markerHeight="1.6"'
  ' markerUnits="userSpaceOnUse" orient="auto-start-reverse">'
  '<path d="M0,0 L2,.8 L0,1.6 z" fill="#333"/></marker></defs>')

# ---- ① 床 -----------------------------------------------------------------
A(poly([(-FW, -FD), (FW, -FD), (FW, FD), (-FW, FD)], "f0"))

# ---- ② 領域（layout.grid の塗りから起こす。A は凸型なので 6 点） -----------
A(poly([(-0.45, 0.45), (0.9, 0.45), (0.9, -0.9), (-0.9, -0.9), (-0.9, -0.6), (-0.45, -0.6)], "f1"))
A(poly([(-0.9, -0.6), (-0.45, -0.6), (-0.45, 0.9), (-0.9, 0.9)], "f2"))
A(poly([(-0.45, 0.45), (0.9, 0.45), (0.9, 0.9), (-0.45, 0.9)], "f3"))

for label, zx, zz in [("A", 0.45, -0.78), ("B", -0.67, -0.30), ("C", 0.68, 0.62)]:
    x, y = P(zx, zz)
    A(f'<text x="{x:.2f}" y="{y:.2f}" class="zone" text-anchor="middle">{label}</text>')

# ---- ③ 順路（立体より先に描く） -------------------------------------------
def arc(p0, p1, bulge=0.16):
    (x0, z0), (x1, z1) = p0, p1
    dx, dz = x1 - x0, z1 - z0
    n = math.hypot(dx, dz) or 1
    cx = (x0 + x1) / 2 - dz / n * bulge
    cz = (z0 + z1) / 2 + dx / n * bulge
    return f'<path d="M{pt(x0, z0)} Q{pt(cx, cz)} {pt(x1, z1)}" class="flow" marker-end="url(#ar)"/>'


# ---- ④ 開始位置と通過ライン ------------------------------------------------
A('<path d="' + " ".join(
    ("M" if i == 0 else "L") + pt(sp["x"] + r * math.cos(a), sp["z"] + r * math.sin(a))
    for i, a in enumerate([k * math.pi / 18 for k in range(36)])) + ' Z" class="accd"/>')
A(f'<path d="M{pt(ln["x1"], ln["z1"])} L{pt(ln["x2"], ln["z2"])}" class="acc"/>')

# ---- ⑤ 壁（輪郭 + 薄塗り。奥のカメラが透ける） -----------------------------
WH = 1.8
for (x1, z1, x2, z2) in [(-0.5, 0.5, 0.5, 0.5), (-0.5, 0.5, -0.5, -0.5)]:
    A(f'<path d="M{pt(x1, z1)} L{pt(x2, z2)} L{pt(x2, z2, WH)} L{pt(x1, z1, WH)} Z" class="wall"/>')

for px, pz in [(-0.5, 0.5), (0.5, 0.5)]:
    x, y = P(px, pz)
    A(f'<path d="M{x:.2f},{y - 1.7:.2f} L{x + 1.6:.2f},{y + 1.3:.2f} L{x - 1.6:.2f},{y + 1.3:.2f} Z" class="accf"/>')

# ---- 順路（壁の後に描く）--------------------------------------------------
A(arc((-0.02, -0.72), (-0.62, -0.46), bulge=0.10))   # A → B（壁 w2 の南端を回る）
A(arc((-0.76, 0.10), (-0.58, 0.80), bulge=0.06))     # B → C（壁 w2 の西側）
A(arc((0.58, 0.72), (0.74, -0.34), bulge=0.14))      # C → A（壁 w1 の東端の外）

# ---- ⑥ 人（開始位置。ラベルは付けない） ------------------------------------
# 全高 1.55m ぶん（= 24.8 単位）を頭・肩・胴で埋める。頭だけ浮かせると壁に貼り付いて見える。
hx, hy = P(sp["x"], sp["z"], 1.55)
foot = P(sp["x"], sp["z"])[1] - hy          # 頭頂から足元までの図上の長さ
A(f'<g transform="translate({hx:.2f},{hy:.2f})">'
  f'<path d="M-1.7,{foot:.1f} L-2.2,7.4 Q-2.2,3.4 0,3.4 Q2.2,3.4 2.2,7.4 L1.7,{foot:.1f} Z" fill="#1a1a1a"/>'
  '<circle cx="0" cy="1.2" r="1.8" fill="#1a1a1a"/></g>')

# ---- ⑦ カメラ 3 台（三脚 + スマホ） ----------------------------------------
CH = 1.35
for label, cx, cz in [("A", -1.05, 1.05), ("B", -0.68, -1.05), ("C", 1.05, 1.05)]:
    fx, fy = P(cx, cz)
    tx, ty = P(cx, cz, CH)
    for ddx, ddy in ((-2.4, 0.0), (2.4, 0.0), (0.0, -1.1)):
        A(f'<line x1="{fx + ddx:.2f}" y1="{fy + ddy:.2f}" x2="{tx:.2f}" y2="{ty:.2f}" class="ln"/>')
    A(f'<ellipse cx="{fx:.2f}" cy="{fy:.2f}" rx="3.2" ry="1.1" fill="none" stroke="#9a9a9a" stroke-width=".2"/>')
    A(f'<rect x="{tx - 1.5:.2f}" y="{ty - 5.2:.2f}" width="3.0" height="5.2" rx=".4"'
      ' fill="#fff" stroke="#000" stroke-width=".35"/>')
    A(f'<circle cx="{tx:.2f}" cy="{ty - 4.3:.2f}" r=".55" fill="#333"/>')
    A(f'<text x="{tx + 2.4:.2f}" y="{ty - 4.2:.2f}" class="zn">{label}</text>')

# ---- ⑧ ラベル（引き出し線 + 語） -------------------------------------------
def lead(x1, y1, x2, y2):
    A(f'<line x1="{x1:.2f}" y1="{y1:.2f}" x2="{x2:.2f}" y2="{y2:.2f}" class="lead"/>')

ax, ay = P(-1.05, 1.05, CH)
lead(42, 26, ax - 1.8, ay - 3.4)
A('<text x="41" y="25.4" class="thing" text-anchor="end">配信スマートフォン</text>')
A('<text x="41" y="28.6" class="spec" text-anchor="end">×3（領域ごとに 1 台）・高さ 1.35m</text>')

wx, wy = P(0.5, 0.5, WH)
lead(120, 24, wx + 0.4, wy + 3)
A('<text x="121" y="23.4" class="thing">パーテーション</text>')
A('<text x="121" y="26.6" class="spec">L 字・高さ 1.8m</text>')

rx, ry = P(-0.5, 0.5)
lead(36, 68, rx - 2.0, ry + 0.6)
A('<text x="35" y="67.4" class="thing" text-anchor="end">位置合わせの 2 点</text>')
A('<text x="35" y="70.6" class="spec" text-anchor="end">壁の両端の床。順にタッチ</text>')

lx, ly = P((ln["x1"] + ln["x2"]) / 2 + 0.22, (ln["z1"] + ln["z2"]) / 2 + 0.22)
lead(137, 90, lx, ly)
A('<text x="138" y="89.4" class="thing">導入が始まる線</text>')
A('<text x="138" y="92.6" class="spec">被った体験者が横切ると始まる</text>')

sx, sy = P(sp["x"], sp["z"] - r - 0.04)
A(f'<text x="{sx:.2f}" y="{sy + 6.4:.2f}" class="thing" text-anchor="middle">開始位置</text>')

A('<text x="85" y="113" class="cap" text-anchor="middle">'
  '図 1. 現場の配置。破線の矢印は体験者が歩く順路（A→B→C を 3 周）。</text>')
A('</svg>')

dst = r"C:\Users\kouga\Projects\Unity\fixed-cam-vr\docs\onsite\fig-room.svg"
os.makedirs(os.path.dirname(dst), exist_ok=True)
io.open(dst, "w", encoding="utf-8", newline="\n").write("\n".join(out))
print("wrote", dst)
