# -*- coding: utf-8 -*-
"""図1（現場の配置）を論文の 1 段幅（約 82mm）で読める大きさの文字で描く。

    py -3.11 paper/dcexpo/tools/fig_env.py     →  paper/dcexpo/figures/env-layout.svg

元は docs/onsite/fig_room.py（ハンドアウト用・170mm 幅前提）。同じ斜投影と実寸
（床 1.8m 四方・壁の 1 辺 1.0m・高さ 1.8m・三脚の高さ 1.35m）をそのまま使い、
論文に要らない運用向けの注記（位置合わせの 2 点・導入が始まる線）を外して文字を大きくした。
show.json の layout は git 管理外の現場設定なので読まず、値をここへ写してある。

写像: X = 89 + 36.667*x + 15.833*z / Y = 92 - 13.333*z - 16*h
高さだけ 16/m へ圧縮している（実縮尺だと壁が床の主題を隠すため）。高さはラベルの実寸で言う。
"""
import io
import math
from pathlib import Path

SX, SZX, OX = 36.667, 15.833, 89.0
SZY, SH, OY = 13.333, 16.0, 92.0
OUT = Path(__file__).resolve().parents[1] / "figures" / "env-layout.svg"


def P(x, z, h=0.0):
    return (OX + SX * x + SZX * z, OY - SZY * z - SH * h)


def pt(x, z, h=0.0):
    a, b = P(x, z, h)
    return f"{a:.2f},{b:.2f}"


def poly(pts, cls):
    d = " ".join(("M" if i == 0 else "L") + pt(*p) for i, p in enumerate(pts)) + " Z"
    return f'<path d="{d}" class="{cls}"/>'


FW = FD = 0.9                       # 床 1.8m 四方
CORNER = (-0.5, 0.5)                # 壁の角
START = (CORNER[0] + 0.42, CORNER[1] - 0.42, 0.245)   # 開始位置（WalkGuideLogic と同じ導出）
sp = {"x": START[0], "z": START[1], "r": START[2]}
WH, CH = 1.8, 1.35

out = []
A = out.append
A('<?xml version="1.0" encoding="UTF-8"?>')
A('<svg xmlns="http://www.w3.org/2000/svg" viewBox="6 18 154 94" role="img" aria-labelledby="t">')
A('<title id="t">現場の配置：床の 3 領域と固定カメラ 3 台</title>')
A('''<style>
  text   { font-family: "BIZ UDPGothic", "Yu Gothic", Meiryo, sans-serif; fill: #000; }
  .thing { font-size: 5.2px; font-weight: bold; }
  .halo  { paint-order: stroke; stroke: #fff; stroke-width: 1.6px; stroke-linejoin: round; }
  .spec  { font-size: 4.3px; fill: #333; }
  .zone  { font-size: 7.4px; font-weight: bold; fill: #6a6a6a; }
  .zn    { font-size: 5.2px; font-weight: bold; fill: #444; }
  .ln    { fill: none; stroke: #000; stroke-width: .35; }
  .lead  { fill: none; stroke: #000; stroke-width: .28; }
  .flow  { fill: none; stroke: #333; stroke-width: .5; stroke-dasharray: 2 1.4; }
  .f0 { fill: #fcfcfc; stroke: #000; stroke-width: .35; }
  .f1 { fill: #f2f2f2; stroke: #9a9a9a; stroke-width: .25; }
  .f2 { fill: #dcdcdc; stroke: #9a9a9a; stroke-width: .25; }
  .f3 { fill: #c2c2c2; stroke: #9a9a9a; stroke-width: .25; }
  .wall { fill: #ffffff; fill-opacity: .6; stroke: #000; stroke-width: .4; }
  .accd { fill: none; stroke: #CC79A7; stroke-width: .5; stroke-dasharray: 1.4 1; }
</style>''')
A('<defs><marker id="ar" viewBox="0 0 2 1.6" refX="2" refY=".8" markerWidth="2.4" markerHeight="2"'
  ' markerUnits="userSpaceOnUse" orient="auto-start-reverse"><path d="M0,0 L2,.8 L0,1.6 z" fill="#333"/></marker></defs>')

# ① 床と 3 領域
A(poly([(-FW, -FD), (FW, -FD), (FW, FD), (-FW, FD)], "f0"))
A(poly([(-0.45, 0.45), (0.9, 0.45), (0.9, -0.9), (-0.9, -0.9), (-0.9, -0.6), (-0.45, -0.6)], "f1"))
A(poly([(-0.9, -0.6), (-0.45, -0.6), (-0.45, 0.9), (-0.9, 0.9)], "f2"))
A(poly([(-0.45, 0.45), (0.9, 0.45), (0.9, 0.9), (-0.45, 0.9)], "f3"))
for label, zx, zz in [("A", 0.70, -0.72), ("B", -0.67, -0.30), ("C", 0.68, 0.62)]:
    x, y = P(zx, zz)
    A(f'<text x="{x:.2f}" y="{y + 2.4:.2f}" class="zone" text-anchor="middle">{label}</text>')

# ② 開始位置
A('<path d="' + " ".join(
    ("M" if i == 0 else "L") + pt(sp["x"] + sp["r"] * math.cos(a), sp["z"] + sp["r"] * math.sin(a))
    for i, a in enumerate([k * math.pi / 18 for k in range(36)])) + ' Z" class="accd"/>')

# ③ 壁（L 字）
for (x1, z1, x2, z2) in [(-0.5, 0.5, 0.5, 0.5), (-0.5, 0.5, -0.5, -0.5)]:
    A(f'<path d="M{pt(x1, z1)} L{pt(x2, z2)} L{pt(x2, z2, WH)} L{pt(x1, z1, WH)} Z" class="wall"/>')


# ④ 順路（壁の後に描く）
def arc(p0, p1, bulge):
    (x0, z0), (x1, z1) = p0, p1
    dx, dz = x1 - x0, z1 - z0
    n = math.hypot(dx, dz) or 1
    cx = (x0 + x1) / 2 - dz / n * bulge
    cz = (z0 + z1) / 2 + dx / n * bulge
    return f'<path d="M{pt(x0, z0)} Q{pt(cx, cz)} {pt(x1, z1)}" class="flow" marker-end="url(#ar)"/>'


A(arc((-0.02, -0.72), (-0.62, -0.46), 0.10))
A(arc((-0.76, 0.10), (-0.58, 0.80), 0.06))
A(arc((0.58, 0.72), (0.74, -0.34), 0.14))

# ⑤ 人（開始位置・全高 1.55m）
hx, hy = P(sp["x"], sp["z"], 1.55)
foot = P(sp["x"], sp["z"])[1] - hy
A(f'<g transform="translate({hx:.2f},{hy:.2f})">'
  f'<path d="M-1.7,{foot:.1f} L-2.2,7.4 Q-2.2,3.4 0,3.4 Q2.2,3.4 2.2,7.4 L1.7,{foot:.1f} Z" fill="#1a1a1a"/>'
  '<circle cx="0" cy="1.2" r="1.8" fill="#1a1a1a"/></g>')

# ⑥ カメラ 3 台（三脚＋スマートフォン）
for label, cx, cz in [("A", -1.05, 1.05), ("B", -0.68, -1.05), ("C", 1.05, 1.05)]:
    fx, fy = P(cx, cz)
    tx, ty = P(cx, cz, CH)
    for ddx, ddy in ((-2.4, 0.0), (2.4, 0.0), (0.0, -1.1)):
        A(f'<line x1="{fx + ddx:.2f}" y1="{fy + ddy:.2f}" x2="{tx:.2f}" y2="{ty:.2f}" class="ln"/>')
    A(f'<ellipse cx="{fx:.2f}" cy="{fy:.2f}" rx="3.2" ry="1.1" fill="none" stroke="#9a9a9a" stroke-width=".25"/>')
    A(f'<rect x="{tx - 1.5:.2f}" y="{ty - 5.2:.2f}" width="3.0" height="5.2" rx=".4" fill="#fff" stroke="#000" stroke-width=".4"/>')
    A(f'<circle cx="{tx:.2f}" cy="{ty - 4.3:.2f}" r=".55" fill="#333"/>')
    A(f'<text x="{tx + 2.6:.2f}" y="{ty - 3.0:.2f}" class="zn">{label}</text>')


# ⑦ ラベル
def lead(x1, y1, x2, y2):
    A(f'<line x1="{x1:.2f}" y1="{y1:.2f}" x2="{x2:.2f}" y2="{y2:.2f}" class="lead"/>')


ax, ay = P(-1.05, 1.05, CH)
lead(51, 30.5, ax - 1.8, ay - 3.4)
A('<text x="50" y="26.2" class="thing" text-anchor="end">スマートフォン</text>')
A('<text x="50" y="31.2" class="spec" text-anchor="end">×3・高さ 1.35 m</text>')

wx, wy = P(0.5, 0.5, WH)
lead(118, 30.5, wx + 0.4, wy + 3)
A('<text x="119" y="26.2" class="thing">L 字の壁</text>')
A('<text x="119" y="31.2" class="spec">高さ 1.8 m</text>')

sx, sy = P(sp["x"], sp["z"] - sp["r"] - 0.04)
A(f'<text x="{sx:.2f}" y="{sy + 7.2:.2f}" class="thing halo" text-anchor="middle">開始位置</text>')

A('</svg>')
OUT.write_text("\n".join(out), encoding="utf-8", newline="\n")
print("wrote", OUT)
