# -*- coding: utf-8 -*-
"""待機者に渡す資料の順路図（真上から見た平面図）。  py -3.11 docs/onsite/fig_route.py

fig_room.py（スタッフ用の斜投影）とは別物。こちらは体験者に「どちら回りか」だけを伝える。
⚠ 描くのは壁と順路だけ（2026-09-05 ユーザー判定）。カメラ・位置合わせの点・導入の線・
床のテープの枠は描かない。足すたびに、伝えたい 1 本の線が読みにくくなる。

⚠ 順路は**閉じた 1 本**で描く。3 本の弧に割ると、離れた矢印が 3 つ並ぶだけの図になって
   「回る」に見えない（2026-09-05 に 1 版目でそうなった）。

写像:
    X(x) = OX + 60*x     x: 東西（+ が東）
    Y(z) = OY - 60*z     z: 南北（+ が北 = 図の上）
  実寸 1m = 60 単位。74mm 幅で刷るので 1 単位 = 0.712mm。
  ⚠ figure.mjs の検査は 1 単位 = 1mm として字の大きさを見る。実寸はその 0.6 倍。

壁の座標は show.json の room.walls から取る（巻尺で測った値が入っている所）。
順路は壁との最短距離を測ってから出す（目で見て決めない。壁を突き抜けた図を刷ると事故になる）。
"""
import io, json, math, os

SHOW = r"C:\Users\kouga\Projects\Unity\fixed-cam-vr\tools\web-compositor\show.json"
DST = r"C:\Users\kouga\Projects\Unity\fixed-cam-vr\docs\onsite\fig-route.svg"

S, OX, OY = 60.0, 59.0, 60.8
VW, VH = 104, 128          # viewBox
MM_W = 74.0                # 紙の上の幅


def P(x, z):
    return OX + S * x, OY - S * z


def pt(x, z):
    a, b = P(x, z)
    return f"{a:.2f},{b:.2f}"


# ---- 壁 --------------------------------------------------------------------
L = json.load(io.open(SHOW, encoding="utf-8"))["layout"]
walls = [(w["x1"], w["z1"], w["x2"], w["z2"]) for w in L["room"]["walls"]]
if len(walls) != 2:
    raise SystemExit(f"壁が L 字（2 枚）でない: {walls}")
print("walls =", walls)

# ---- 順路（壁の外側を時計回りに 1 周する閉じた線） --------------------------
# 壁の南端（z=-0.72）と床の南の縁（z=-0.9）のあいだは 18cm しかない。
# 実際にはテープの外へ少しふくらんで回るので、図もそう描く。
WAY = [
    (0.34, -0.56),    # 0 南東（スタート）
    (-0.14, -0.98),   # 1 南（← 矢印）
    (-0.70, -0.88),
    (-0.82, -0.16),   # 3 西（← 矢印）
    (-0.72, 0.60),
    (-0.10, 0.84),    # 5 北（← 矢印）
    (0.36, 0.76),
    (0.58, 0.10),     # 7 東（← 矢印）
    (0.50, -0.34),
]
ARROW_AT = [1, 3, 5, 7]


def catmull(pts):
    """閉じた Catmull-Rom を 3 次ベジエの列にする。戻り値は [(p0,c1,c2,p1), ...]"""
    n = len(pts)
    segs = []
    for i in range(n):
        p0, p1 = pts[i], pts[(i + 1) % n]
        pm, pn = pts[(i - 1) % n], pts[(i + 2) % n]
        c1 = (p0[0] + (p1[0] - pm[0]) / 6.0, p0[1] + (p1[1] - pm[1]) / 6.0)
        c2 = (p1[0] - (pn[0] - p0[0]) / 6.0, p1[1] - (pn[1] - p0[1]) / 6.0)
        segs.append((p0, c1, c2, p1))
    return segs


def bez(p0, c1, c2, p1, t):
    u = 1 - t
    return (u ** 3 * p0[0] + 3 * u * u * t * c1[0] + 3 * u * t * t * c2[0] + t ** 3 * p1[0],
            u ** 3 * p0[1] + 3 * u * u * t * c1[1] + 3 * u * t * t * c2[1] + t ** 3 * p1[1])


def bez_d(p0, c1, c2, p1, t):
    u = 1 - t
    return (3 * u * u * (c1[0] - p0[0]) + 6 * u * t * (c2[0] - c1[0]) + 3 * t * t * (p1[0] - c2[0]),
            3 * u * u * (c1[1] - p0[1]) + 6 * u * t * (c2[1] - c1[1]) + 3 * t * t * (p1[1] - c2[1]))


def seg_dist(p, a, b):
    ax, az = a
    dx, dz = b[0] - ax, b[1] - az
    n = dx * dx + dz * dz
    t = 0.0 if n == 0 else max(0.0, min(1.0, ((p[0] - ax) * dx + (p[1] - az) * dz) / n))
    return math.hypot(p[0] - (ax + t * dx), p[1] - (az + t * dz))


SEGS = catmull(WAY)

MIN_CLEAR = 0.15   # m。これを割ると図の上で順路が壁に噛む
worst, worst_i = 9.9, -1
for i, s in enumerate(SEGS):
    d = min(seg_dist(bez(*s, t=k / 100), (w[0], w[1]), (w[2], w[3]))
            for k in range(101) for w in walls)
    if d < worst:
        worst, worst_i = d, i
print(f"壁との最短 {worst * 100:.1f}cm（区間 {worst_i}）")
if worst < MIN_CLEAR:
    raise SystemExit(f"順路が壁に近すぎる（{worst * 100:.1f}cm・区間 {worst_i}）")

out = []
A = out.append
A('<?xml version="1.0" encoding="UTF-8"?>')
A(f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {VW} {VH}"'
  f' width="{MM_W}mm" height="{VH * MM_W / VW:.1f}mm" role="img" aria-labelledby="rt rd">')
A('<title id="rt">歩く順路</title>')
A('<desc id="rd">壁を真上から見た図。L 字の壁を、外側から時計回りに一周する破線の輪が描いてある。'
  'スタートは壁の南東。そこから壁の南端の外を西へ回り、壁の西側を北へ上がり、'
  '壁の北側を東へ進み、壁の東端の外を南へ下ってスタートへ戻る。矢印はこの一方向だけを指す。</desc>')
A('''<style>
  text  { font-family: "Yu Gothic", "Noto Sans JP", Meiryo, sans-serif; }
  .thing{ font-size: 4.6px; font-weight: bold; fill: #000; }
  .sub  { font-size: 4.2px; font-weight: bold; fill: #333; }
  .wall { fill: none; stroke: #000; stroke-width: 2.6; stroke-linejoin: round; stroke-linecap: round; }
  .flow { fill: none; stroke: #333; stroke-width: 1.1; stroke-dasharray: 3.4 2.4; }
  .lead { fill: none; stroke: #000; stroke-width: .35; }
</style>''')

# 順路（閉じた 1 本）
d = "M" + pt(*SEGS[0][0])
for _, c1, c2, p1 in SEGS:
    d += f" C{pt(*c1)} {pt(*c2)} {pt(*p1)}"
A(f'<path d="{d} Z" class="flow"/>')

# 進む向き（矢じりは輪の上に直接置く。marker-mid だと全部の節に付く）
for i in ARROW_AT:
    s = SEGS[i]
    p = bez(*s, t=0.02)
    v = bez_d(*s, t=0.02)
    # 図の Y は南北が反転しているので、画面上の向きは (vx, -vz)。
    # ⚠ rotate() は Y が下向きの座標系での角。ここで符号を反転させると輪が逆回りに見える
    a = math.degrees(math.atan2(-v[1], v[0]))
    x, y = P(*p)
    A(f'<g transform="translate({x:.2f},{y:.2f}) rotate({a:.1f})">'
      f'<path d="M-3.4,-2.6 L3.4,0 L-3.4,2.6 Z" fill="#333"/></g>')

# 壁
A(f'<path d="M{pt(walls[0][2], walls[0][3])} L{pt(walls[0][0], walls[0][1])}'
  f' L{pt(walls[1][2], walls[1][3])}" class="wall"/>')

# スタート（輪の上の点）
sx, sy = P(*WAY[0])
A(f'<circle cx="{sx:.2f}" cy="{sy:.2f}" r="2.4" fill="#000"/>')
A(f'<text x="{sx + 4.0:.2f}" y="{sy + 1.6:.2f}" class="sub">スタート</text>')

# 壁のラベル（L 字の内側。引き出し線で壁を指す）
lx, ly = P(-0.26, 0.14)
wx, _ = P(-0.50, 0.14)
A(f'<line x1="{lx - 1.4:.2f}" y1="{ly - 1.5:.2f}" x2="{wx + 1.6:.2f}" y2="{ly - 1.5:.2f}" class="lead"/>')
A(f'<text x="{lx:.2f}" y="{ly:.2f}" class="thing">壁</text>')

A('</svg>')

os.makedirs(os.path.dirname(DST), exist_ok=True)
io.open(DST, "w", encoding="utf-8", newline="\n").write("\n".join(out))
print("wrote", DST)
