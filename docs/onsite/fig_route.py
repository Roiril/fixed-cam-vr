# -*- coding: utf-8 -*-
"""待機者に渡す資料の順路図（真上から見た平面図）。  py -3.11 docs/onsite/fig_route.py

fig_room.py（スタッフ用の斜投影）とは別物。こちらは体験者に「どちら回りか」だけを伝える。

⚠ 描くのは壁と順路だけ（2026-09-05 ユーザー判定）。カメラ・位置合わせの点・導入の線・
   床のテープの枠は描かない。足すたびに、伝えたい 1 本の線が読みにくくなる。

⚠⚠ 形はユーザーの手描き（2026-09-05・`canon/LEDGER.md` 0163）に合わせる。要点は 4 つ:
   1. **壁に沿った直角の道**。丸い輪だと壁との関係が消える（1 版目がそれで没）
   2. **L 字の内側を先に行く** — 上の腕の下を西へ、縦の腕の東を南へ。
      そこから南端を回って外側（西 → 北）へ出て、また入口へ戻る
   3. **入口は上の腕の東の端**。ここから入って、以後は同じ道を回る
   4. **L 字の 2 辺は同じ長さ**（0164）

写像:
    X(x) = OX + 60*x     x: 東西（+ が東）
    Y(z) = OY - 60*z     z: 南北（+ が北 = 図の上）
  実寸 1m = 60 単位。84mm 幅で刷るので 1 単位 = 0.792mm。
  ⚠ figure.mjs の検査は 1 単位 = 1mm として字の大きさを見る。実寸はその 0.79 倍。

⚠⚠ **壁は show.json から取らない。** live の `room.walls` は 2026-08-05 から
   横 0.59m / 縦 1.22m の非対称になっているが、**実物は 2 辺とも 0.955m の等長**。
   正本は `tools/walk-guide/build_walk_guide.py`（`Joint.blend` の実測値。同じ食い違いが
   そこにも書いてある）。0164 では 1.0m と置いていたが、実測値へ直した。
   食い違っているあいだは下で警告を出す。
順路は壁との最短距離を測ってから出す（目で見て決めない。壁を突き抜けた図を刷ると事故になる）。
"""
import io, json, math, os, sys

sys.stdout.reconfigure(encoding="utf-8")   # cp932 の端末で ⚠ を print すると落ちる

SHOW = r"C:\Users\kouga\Projects\Unity\fixed-cam-vr\tools\web-compositor\show.json"
DST = r"C:\Users\kouga\Projects\Unity\fixed-cam-vr\docs\onsite\fig-route.svg"

S, OX, OY = 60.0, 50.8, 50.8
VW, VH = 103, 99           # viewBox（描くものにぴったり合わせてある。余白は 4 単位）
MM_W = 84.0                # 紙の上の幅


def P(x, z):
    return OX + S * x, OY - S * z


def pt(p):
    a, b = P(*p)
    return f"{a:.2f},{b:.2f}"


# ---- 壁（角 → 東の端 / 角 → 南の端。2 辺とも 0.955m） ----------------------
CORNER = (-0.5, 0.5)
ARM = 0.955      # walk-guide の WALL_ARM と同じ値。片方だけ動かさない
walls = [(CORNER[0], CORNER[1], CORNER[0] + ARM, CORNER[1]),
         (CORNER[0], CORNER[1], CORNER[0], CORNER[1] - ARM)]

live = json.load(io.open(SHOW, encoding="utf-8"))["layout"]["room"]["walls"]
lens = [math.hypot(w["x2"] - w["x1"], w["z2"] - w["z1"]) for w in live]
if abs(lens[0] - lens[1]) > 0.05:
    print(f"警告: show.json の壁が非対称（{lens[0]:.2f}m / {lens[1]:.2f}m）。"
          f"図は実測の {ARM}m / {ARM}m で引いた。"
          f"CG の遮蔽と登録時のワイヤーは show.json の値で出ているので、卓の部屋の設定を見ること")

# ---- 順路（壁から D 離した直角の道。時計回りに 1 周） ----------------------
# 入口 → 内側を西 → 内側を南 → 南端を回って西 → 外側を北 → 外側を東 → 入口
# ⚠ 実際に歩く道は壁の芯から 0.24m（walk-guide の D_WALK）。図は文字を置く幅を取るために
#   0.28m で引いてある。読む人には見えない差だが、寸法の図として使わないこと
D = 0.28
E, W = CORNER[0] + ARM + D, CORNER[0] - D      # 東の端の外 / 西の面の外
N, Sth = CORNER[1] + D, CORNER[1] - ARM - D    # 北の面の外 / 南の端の外
IN_X, IN_Z = CORNER[0] + D, CORNER[1] - D      # 内側の 2 本
WAY = [
    (E, IN_Z),       # 0 入口（上の腕の東の端の外）
    (IN_X, IN_Z),    # 1
    (IN_X, Sth),     # 2
    (W, Sth),        # 3
    (W, N),          # 4
    (E, N),          # 5
]
ARROW_AT = [0, 1, 3, 4]     # 矢じりを置く辺（WAY[i] → WAY[i+1]）
R = 6.0                     # 角の丸み（単位）。入口の角だけ尖らせる
SHARP = {0}


def unit(a, b):
    d = (b[0] - a[0], b[1] - a[1])
    n = math.hypot(*d) or 1.0
    return (d[0] / n, d[1] / n), n


def rounded(way):
    """角を丸めた閉じた道。(path 文字列, 標本点の列) を返す"""
    n = len(way)
    segs, samples = [], []
    for i in range(n):
        c = way[i]
        din, lin = unit(way[i - 1], c)
        dout, lout = unit(c, way[(i + 1) % n])
        r = 0.0 if i in SHARP else min(R / S, lin / 2, lout / 2)
        a = (c[0] - din[0] * r, c[1] - din[1] * r)
        b = (c[0] + dout[0] * r, c[1] + dout[1] * r)
        segs.append((a, c, b))
    d = "M" + pt(segs[0][2])
    for i in range(n):
        nxt = segs[(i + 1) % n]
        d += f" L{pt(nxt[0])}"
        samples += [(segs[i][2][0] + (nxt[0][0] - segs[i][2][0]) * k / 20,
                     segs[i][2][1] + (nxt[0][1] - segs[i][2][1]) * k / 20) for k in range(21)]
        if nxt[0] != nxt[2]:
            d += f" Q{pt(nxt[1])} {pt(nxt[2])}"
            for k in range(11):
                t = k / 10
                u = 1 - t
                samples.append((u * u * nxt[0][0] + 2 * u * t * nxt[1][0] + t * t * nxt[2][0],
                                u * u * nxt[0][1] + 2 * u * t * nxt[1][1] + t * t * nxt[2][1]))
    return d + " Z", samples


def seg_dist(p, a, b):
    ax, az = a
    dx, dz = b[0] - ax, b[1] - az
    n = dx * dx + dz * dz
    t = 0.0 if n == 0 else max(0.0, min(1.0, ((p[0] - ax) * dx + (p[1] - az) * dz) / n))
    return math.hypot(p[0] - (ax + t * dx), p[1] - (az + t * dz))


PATH, SAMPLES = rounded(WAY)

MIN_CLEAR = 0.15   # m。これを割ると図の上で順路が壁に噛む
worst = min(seg_dist(p, (w[0], w[1]), (w[2], w[3])) for p in SAMPLES for w in walls)
print(f"壁との最短 {worst * 100:.1f}cm")
if worst < MIN_CLEAR:
    raise SystemExit(f"順路が壁に近すぎる（{worst * 100:.1f}cm）")

out = []
A = out.append
A('<?xml version="1.0" encoding="UTF-8"?>')
A(f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {VW} {VH}"'
  f' width="{MM_W}mm" height="{VH * MM_W / VW:.1f}mm" role="img" aria-labelledby="rt rd">')
A('<title id="rt">歩く順路</title>')
A('<desc id="rd">壁を真上から見た図。L 字の壁があり、その外周を一周する破線の道が描いてある。'
  '入口は横に伸びた腕の東の端。そこから壁の内側を西へ進み、縦の腕の東側を南へ下り、'
  '南端を回って壁の外側を西から北へ上がり、壁の北側を東へ戻って入口へ着く。'
  '矢印はこの一方向だけを指し、同じ道を繰り返す。</desc>')
A('''<style>
  text  { font-family: "Yu Gothic", "Noto Sans JP", Meiryo, sans-serif; }
  .thing{ font-size: 4.6px; font-weight: bold; fill: #000; }
  .sub  { font-size: 4.6px; font-weight: bold; fill: #1a1a1a; }
  .wall { fill: none; stroke: #000; stroke-width: 2.4; stroke-linejoin: round; stroke-linecap: round; }
  .flow { fill: none; stroke: #333; stroke-width: 1.2; stroke-dasharray: 3.6 2.6; stroke-linecap: butt; }
  .lead { fill: none; stroke: #000; stroke-width: .35; }
</style>''')

# 順路
A(f'<path d="{PATH}" class="flow"/>')

# 進む向き（矢じりは辺の中ほどに置く）
for i in ARROW_AT:
    a, b = WAY[i], WAY[(i + 1) % len(WAY)]
    m = ((a[0] + b[0]) / 2, (a[1] + b[1]) / 2)
    d, _ = unit(a, b)
    # 図の Y は南北が反転しているので、画面上の向きは (dx, -dz)。
    # ⚠ rotate() は Y が下向きの座標系での角。符号を反転させると道が逆回りに見える
    ang = math.degrees(math.atan2(-d[1], d[0]))
    x, y = P(*m)
    A(f'<g transform="translate({x:.2f},{y:.2f}) rotate({ang:.1f})">'
      f'<path d="M-3.6,-2.9 L3.6,0 L-3.6,2.9 Z" fill="#333"/></g>')

# 壁
A(f'<path d="M{pt((walls[0][2], walls[0][3]))} L{pt((walls[0][0], walls[0][1]))}'
  f' L{pt((walls[1][2], walls[1][3]))}" class="wall"/>')

# 入口（道の角の上の点。丸めていないので線とちょうど重なる）
# ラベルは点の真下へ右揃えで置く。近いので引き出し線は要らない
ex, ey = P(*WAY[0])
A(f'<circle cx="{ex:.2f}" cy="{ey:.2f}" r="2.8" fill="#000"/>')
A(f'<text x="{ex + 3.4:.2f}" y="{ey + 11.0:.2f}" class="sub" text-anchor="end">最初は</text>')
A(f'<text x="{ex + 3.4:.2f}" y="{ey + 17.4:.2f}" class="sub" text-anchor="end">ここから入る</text>')

# 壁のラベル（縦の腕と道のあいだ。引き出し線で壁を指す）
lx, ly = P(-0.335, -0.05)
A(f'<line x1="{lx - 1.0:.2f}" y1="{ly - 1.6:.2f}" x2="{lx - 6.4:.2f}" y2="{ly - 1.6:.2f}" class="lead"/>')
A(f'<text x="{lx:.2f}" y="{ly:.2f}" class="thing">壁</text>')

A('</svg>')

os.makedirs(os.path.dirname(DST), exist_ok=True)
io.open(DST, "w", encoding="utf-8", newline="\n").write("\n".join(out))
print("wrote", DST)
