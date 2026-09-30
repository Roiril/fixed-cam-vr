# -*- coding: utf-8 -*-
"""図1（現場の配置）を論文の 1 段幅（76mm）で描く。

    py -3.11 paper/dcexpo/tools/fig_env.py     →  paper/dcexpo/figures/env-layout.svg

視点は現場写真（カメラAの側から L 字の角を見る向き）に合わせた斜投影。
L 字の壁は 2 辺とも 1.0m・高さ 1.8m、床は 1.8m 四方（.claude/memory/l_wall_geometry.md）。
カメラの置き方は写真どおり: A = 三脚 / B = 床置き / C = 棚のアーム。
順路は図4（route.jpg）と同じ向き: 区間1 = L の口を横切る → 区間2 = 縦の辺の外 → 区間3 = 横の辺の外（時計回り）。

写像（床の (x,z)・高さ h・単位 m → 図の mm）:
    r = (x + z) / √2        右方向（写真で見て右）
    f = (z - x) / √2        奥行き（大きいほど奥）
    X = OX + S*r
    Y = OY - D*f - V*h
L の角は (-0.5, 0.5)。口は (+x, -z) 側＝手前に開く。壁の 2 辺は
  横の辺 = 角 → (0.5, 0.5)（右奥へ）／縦の辺 = 角 → (-0.5, -0.5)（左手前へ）。
"""
import io
import math
from pathlib import Path

OUT = Path(__file__).resolve().parents[1] / "figures" / "env-layout.svg"

W, H = 76.0, 54.0                     # viewBox（mm）
S, D, V = 18.0, 9.5, 10.0             # 右 / 奥行き / 高さ（mm per m）
OX, OY = 38.0, 27.5
R2 = math.sqrt(2.0)


def P(x, z, h=0.0):
    r, f = (x + z) / R2, (z - x) / R2
    return OX + S * r, OY - D * f - V * h


def pt(x, z, h=0.0):
    a, b = P(x, z, h)
    return f"{a:.2f},{b:.2f}"


def poly(pts, attrs):
    d = " ".join(("M" if i == 0 else "L") + pt(*p) for i, p in enumerate(pts)) + " Z"
    return f'<path d="{d}" {attrs}/>'


FW = 0.9                               # 床は ±0.9m
CORNER = (-0.5, 0.5)
ARM_B = (0.5, 0.5)                     # 横の辺の端
ARM_L = (-0.5, -0.5)                   # 縦の辺の端
WH = 1.8

out = []
A = out.append
A('<?xml version="1.0" encoding="UTF-8"?>')
A(f'<svg xmlns="http://www.w3.org/2000/svg" width="{W:g}mm" height="{H:g}mm" viewBox="0 0 {W:g} {H:g}" role="img" aria-labelledby="t d">')
A('<title id="t">現場の配置：L字の壁のまわりの3区間と，固定カメラ3台</title>')
A('<desc id="d">床1.8 m四方の中央に，高さ1.8 mで手すり付きのL字の壁がある。L字の口の側に三脚のカメラA，'
  '縦の辺の外側に床置きのカメラB，横の辺の外側に棚のアームに付けたカメラCがある。'
  '体験者は区間1でL字の口を横切り，区間2で縦の辺の外側，区間3で横の辺の外側を，時計回りに歩く。</desc>')
A('''<style>
  text   { font-family: "BIZ UDPGothic", "Yu Gothic", Meiryo, sans-serif; fill: #000; }
  .thing { font-size: 2.7px; font-weight: bold; }
  .spec  { font-size: 2.2px; fill: #333; }
  .zone  { font-size: 2.8px; font-weight: bold; fill: #6b6b6b; }
  .halo  { paint-order: stroke; stroke: #fff; stroke-width: .7px; stroke-linejoin: round; }
  .lead  { fill: none; stroke: #000; stroke-width: .2; }
  .route { fill: none; stroke: #D55E00; stroke-width: .55; stroke-dasharray: 1.7 1.1; }
  .rail  { fill: none; stroke: #111; stroke-linecap: round; }
</style>''')
A('<defs><marker id="ar" viewBox="0 0 2.4 2" refX="2.2" refY="1" markerWidth="2.4" markerHeight="2"'
  ' markerUnits="userSpaceOnUse" orient="auto"><path d="M0,0 L2.4,1 L0,2 z" fill="#D55E00"/></marker></defs>')

# ---- ① 床（厚みのある板）と 3 区間 ----
TH = 1.1                                                   # 板の厚み（mm）
n_, e_, s_, w_ = P(-FW, FW), P(FW, FW), P(FW, -FW), P(-FW, -FW)   # 奥・右・手前・左
A(f'<path d="M{w_[0]:.2f},{w_[1]:.2f} L{s_[0]:.2f},{s_[1]:.2f} L{s_[0]:.2f},{s_[1] + TH:.2f} L{w_[0]:.2f},{w_[1] + TH:.2f} Z" fill="#a9a9a9" stroke="#6f6f6f" stroke-width=".25" stroke-linejoin="round"/>')
A(f'<path d="M{s_[0]:.2f},{s_[1]:.2f} L{e_[0]:.2f},{e_[1]:.2f} L{e_[0]:.2f},{e_[1] + TH:.2f} L{s_[0]:.2f},{s_[1] + TH:.2f} Z" fill="#8c8c8c" stroke="#6f6f6f" stroke-width=".25" stroke-linejoin="round"/>')
# 区間1（L の口〜手前）・区間2（縦の辺の外）・区間3（横の辺の外）
A(poly([(-FW, -FW), (FW, -FW), (FW, FW), (-FW, FW)], 'fill="#f3f3f3" stroke="none"'))
A(poly([(-0.9, -0.6), (-0.45, -0.6), (-0.45, 0.9), (-0.9, 0.9)], 'fill="#dcdcdc" stroke="none"'))
A(poly([(-0.45, 0.45), (0.9, 0.45), (0.9, 0.9), (-0.45, 0.9)], 'fill="#c6c6c6" stroke="none"'))
A(poly([(-FW, -FW), (FW, -FW), (FW, FW), (-FW, FW)], 'fill="none" stroke="#6f6f6f" stroke-width=".3" stroke-linejoin="round"'))


# ---- ② 棚とアーム（カメラC）と床置きのカメラB は壁より後ろに描く ----
def phone(cx, cy, w, h, ang=0.0, lens="left"):
    lx = -w / 2 + 0.55 if lens == "left" else w / 2 - 0.55
    return (f'<g transform="translate({cx:.2f},{cy:.2f}) rotate({ang:g})">'
            f'<rect x="{-w / 2:.2f}" y="{-h / 2:.2f}" width="{w:.2f}" height="{h:.2f}" rx=".5" fill="#fff" stroke="#000" stroke-width=".35"/>'
            f'<circle cx="{lx:.2f}" cy="0" r=".42" fill="#333"/></g>')


# カメラC: 棚（前後 2 本の柱と板）＋アーム
CX, CZ = 1.25, 1.15
for zz in (CZ - 0.32, CZ + 0.32):
    a, b = P(CX, zz), P(CX, zz, 2.3)
    A(f'<line x1="{a[0]:.2f}" y1="{a[1]:.2f}" x2="{b[0]:.2f}" y2="{b[1]:.2f}" stroke="#8a8a8a" stroke-width=".3"/>')
for hh in (0.8, 1.55, 2.3):
    a, b = P(CX, CZ - 0.32, hh), P(CX, CZ + 0.32, hh)
    A(f'<line x1="{a[0]:.2f}" y1="{a[1]:.2f}" x2="{b[0]:.2f}" y2="{b[1]:.2f}" stroke="#8a8a8a" stroke-width=".3"/>')
armA = P(CX, CZ - 0.32, 2.15)
armB = (armA[0] - 4.2, armA[1] + 1.2)
A(f'<path d="M{armA[0]:.2f},{armA[1]:.2f} L{armB[0]:.2f},{armB[1]:.2f}" class="rail" stroke-width=".55"/>')
A(f'<circle cx="{armA[0]:.2f}" cy="{armA[1]:.2f}" r=".5" fill="#111"/>')
C_PH = (armB[0] - 2.2, armB[1] + 1.4)
A(phone(C_PH[0], C_PH[1], 4.6, 2.5, ang=24, lens="left"))

def contact(cx, cy, rx, ry=None):
    return f'<ellipse cx="{cx:.2f}" cy="{cy:.2f}" rx="{rx:g}" ry="{ry or rx * .33:g}" fill="none" stroke="#9a9a9a" stroke-width=".25"/>'


A(contact(*P(CX, CZ), 4.6))

# カメラB: 床置き。壁にもたせた小さな端末とケーブル
bx, by = P(-1.4, -1.0)
A(contact(bx, by + 1.6, 4.2))
A(f'<path d="M{bx - 6.5:.2f},{by + 2.4:.2f} Q{bx - 3:.2f},{by + 3.4:.2f} {bx - 1.6:.2f},{by + 1.2:.2f}" fill="none" stroke="#555" stroke-width=".22"/>')
A(f'<path d="M{bx - 2.2:.2f},{by + 1.4:.2f} L{bx + 2.2:.2f},{by + 1.4:.2f} L{bx + 2.9:.2f},{by - 1.7:.2f} L{bx - 1.5:.2f},{by - 1.7:.2f} Z" fill="#fff" stroke="#000" stroke-width=".35" stroke-linejoin="round"/>')
A(f'<circle cx="{bx + 1.9:.2f}" cy="{by - 1.0:.2f}" r=".4" fill="#333"/>')

# ---- ③ 順路（区間2・3 は壁の向こう側なので壁より先に描く）----
def route(p0, p1, bend=0.0):
    (x0, z0), (x1, z1) = p0, p1
    dx, dz = x1 - x0, z1 - z0
    n = math.hypot(dx, dz) or 1
    cx = (x0 + x1) / 2 - dz / n * bend
    cz = (z0 + z1) / 2 + dx / n * bend
    return f'<path d="M{pt(x0, z0)} Q{pt(cx, cz)} {pt(x1, z1)}" class="route" marker-end="url(#ar)"/>'


A(route((-0.72, -0.45), (-0.72, 0.55)))
A(route((-0.45, 0.72), (0.30, 0.72)))

# ---- ④ L 字の壁（幕・柱・上の桟・手すり）----
def wall(p0, p1, fill):
    (x0, z0), (x1, z1) = p0, p1
    A(poly([(x0, z0, 0), (x1, z1, 0), (x1, z1, WH), (x0, z0, WH)], f'fill="{fill}" fill-opacity=".66" stroke="none"'))
    n = 9
    for k in range(1, n):
        t = k / n
        xa, za = x0 + (x1 - x0) * t, z0 + (z1 - z0) * t
        a, b = P(xa, za, 0.04), P(xa, za, WH - 0.04)
        A(f'<line x1="{a[0]:.2f}" y1="{a[1]:.2f}" x2="{b[0]:.2f}" y2="{b[1]:.2f}" stroke="#7a7a7a" stroke-width=".2" stroke-opacity=".55"/>')
    for hh, sw in ((WH, .5), (0.95, .6)):
        a, b = P(x0, z0, hh), P(x1, z1, hh)
        A(f'<line x1="{a[0]:.2f}" y1="{a[1]:.2f}" x2="{b[0]:.2f}" y2="{b[1]:.2f}" class="rail" stroke-width="{sw}"/>')


wall(CORNER, ARM_L, "#bcbcbc")
wall(CORNER, ARM_B, "#d4d4d4")
for (x, z) in (CORNER, ARM_L, ARM_B):
    a, b = P(x, z, 0), P(x, z, WH + 0.06)
    A(f'<line x1="{a[0]:.2f}" y1="{a[1]:.2f}" x2="{b[0]:.2f}" y2="{b[1]:.2f}" class="rail" stroke-width=".5"/>')
    A(f'<ellipse cx="{a[0]:.2f}" cy="{a[1] + .1:.2f}" rx="1.3" ry=".5" fill="#6a6a6a" stroke="#111" stroke-width=".2"/>')

# ---- ⑤ 区間1 の順路・体験者・三脚のカメラA ----
A(route((0.62, 0.38), (-0.38, -0.62)))

px, py = P(0.12, -0.10)
ph = 15.0
A(f'<path d="M{px - 1.5:.2f},{py:.2f} L{px - 2.1:.2f},{py - ph + 7.2:.2f} Q{px - 2.1:.2f},{py - ph + 3.4:.2f} {px:.2f},{py - ph + 3.4:.2f} '
  f'Q{px + 2.1:.2f},{py - ph + 3.4:.2f} {px + 2.1:.2f},{py - ph + 7.2:.2f} L{px + 1.5:.2f},{py:.2f} Z" fill="#1a1a1a"/>')
A(f'<circle cx="{px:.2f}" cy="{py - ph + 1.7:.2f}" r="1.7" fill="#1a1a1a"/>')

AX, AZ = 1.9, -1.45
fa, ta = P(AX, AZ), P(AX, AZ, 1.35)
A(contact(fa[0], fa[1] + 0.3, 4.4))
for ddx, ddy in ((-3.4, 0.9), (3.4, 0.9), (0.0, -1.7)):
    A(f'<line x1="{fa[0] + ddx:.2f}" y1="{fa[1] + ddy:.2f}" x2="{ta[0]:.2f}" y2="{ta[1]:.2f}" stroke="#000" stroke-width=".35"/>')
A(f'<circle cx="{ta[0]:.2f}" cy="{ta[1]:.2f}" r=".55" fill="#111"/>')
A(phone(ta[0], ta[1] - 1.9, 5.0, 2.6, ang=-14, lens="right"))


# 床 1.8 m（左手前の辺に沿わせた寸法線。数字は線の途切れに置く）
d0, d1 = P(-FW, -1.12), P(FW, -1.12)
dm = P(0.0, -1.12)
ang = math.degrees(math.atan2(d1[1] - d0[1], d1[0] - d0[0]))
ux, uy = math.cos(math.radians(ang)), math.sin(math.radians(ang))
GAP = 4.6
for a, b in ((d0, (dm[0] - ux * GAP, dm[1] - uy * GAP)), ((dm[0] + ux * GAP, dm[1] + uy * GAP), d1)):
    A(f'<line x1="{a[0]:.2f}" y1="{a[1]:.2f}" x2="{b[0]:.2f}" y2="{b[1]:.2f}" stroke="#000" stroke-width=".22"/>')
for q in (d0, d1):
    A(f'<line x1="{q[0] - .5:.2f}" y1="{q[1] + .5 * 0.53:.2f}" x2="{q[0] + .5:.2f}" y2="{q[1] - .5 * 0.53:.2f}" stroke="#000" stroke-width=".22"/>')
A(f'<text transform="translate({dm[0]:.2f},{dm[1] + .8:.2f}) rotate({ang:.1f})" class="spec" text-anchor="middle">1.8 m</text>')

# ---- ⑥ ラベル（モノ = 太字、仕様 = 小、区間 = 灰の太字）----
def lead(x1, y1, x2, y2):
    A(f'<line x1="{x1:.2f}" y1="{y1:.2f}" x2="{x2:.2f}" y2="{y2:.2f}" class="lead"/>')


def label(x, y, name, spec=None, anchor="start"):
    A(f'<text x="{x:.2f}" y="{y:.2f}" class="thing" text-anchor="{anchor}">{name}</text>')
    if spec:
        A(f'<text x="{x:.2f}" y="{y + 3.1:.2f}" class="spec" text-anchor="{anchor}">{spec}</text>')


# L 字の壁（左上・縦の辺の上の桟を指す）
tx, ty = P(*ARM_L, WH)
label(2.5, 6.2, "L字の壁", "高さ 1.8 m・幕と桟")
lead(20.0, 6.0, tx - 0.4, ty - 0.2)
# 手すり
hx, hy = P(-0.5, -0.05, 0.95)
label(2.5, 19.0, "手すり")
lead(12.2, 18.1, hx - 0.3, hy)

# カメラ
label(50.5, 47.6, "カメラA", "三脚")
lead(fa[0] + 4.4, fa[1] - 0.2, 50.0, 46.0)
label(8.5, 40.2, "カメラB", "床置き", anchor="middle")
lead(bx - 0.6, by + 3.4, 8.5, 37.6)
label(65.0, 39.4, "カメラC", "棚のアーム", anchor="middle")
lead(P(CX, CZ)[0], P(CX, CZ)[1] + 1.6, 65.0, 36.7)

# 区間（灰の太字。2・3 は壁の向こう側なので，床の外の空きから順路に添える）
for text, x, y in (("区間1", 36.0, 35.2), ("区間2", 17.0, 24.0), ("区間3", 58.0, 24.0)):
    A(f'<text x="{x:.2f}" y="{y:.2f}" class="zone" text-anchor="middle">{text}</text>')

A('</svg>')
OUT.write_text("\n".join(out), encoding="utf-8", newline="\n")
print("wrote", OUT)
