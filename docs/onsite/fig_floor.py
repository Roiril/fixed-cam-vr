# -*- coding: utf-8 -*-
"""現場の床を真上から見た配置図（平面図）。show.json の実データから起こす。

fig_room.py（斜投影・カメラ・順路・人まで入る説明図）に対して、こちらは
**壁と塗られた床だけ**の平面図。設営で床に何をどこまで塗るか・壁をどこに置くかを見る用。

写像:
    X(x) = CX + S*x     x: course の東西（+ が東）
    Y(z) = CY - S*z     z: course の南北（+ が北・上）
  1 ユーザー単位 = 1mm。S = 83.333mm/m（1.8m 四方の床が 150mm 四方＝縮尺 1:12）。

⚠ 壁は `layout.room.walls`（ShowRoomProxy が実機で人形を隠すのに使う実測モデル）から取る。
  旧 `layout.wall`（corner/endX/endZ）は卓の既定 1m×1m のままで、2026-08-05 に room 側だけが
  0.59m / 1.22m へ直された。fig_room.py はまだ旧値を直書きしているので形が違う。
"""
import io, json, os, urllib.request

# ---- 入力（動いている卓サーバがあればそのメモリを優先。無ければディスク）-----------
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DISK = os.path.join(ROOT, "tools", "web-compositor", "show.json")


def load_show():
    try:
        with urllib.request.urlopen("http://localhost:8099/state?rev=0", timeout=6) as r:
            return json.loads(r.read().decode("utf-8")), "卓（localhost:8099）"
    except Exception:
        with io.open(DISK, encoding="utf-8") as f:
            return json.load(f), "show.json"


show, src = load_show()
L = show["layout"]
G = L["grid"]
TILE, COLS, ROWS = G["tileM"], G["cols"], G["rows"]
CELLS = G["cells"]
FW, FD = L["floor"]["w"] / 2.0, L["floor"]["d"] / 2.0
WALLS = L.get("room", {}).get("walls", [])

# ---- 写像 -------------------------------------------------------------------
S = 83.3333                      # mm / m
CX, CY = 91.0, 89.0              # 床中心の図上座標
VB_W, VB_H = 178.0, 188.0


def X(x):
    return CX + S * x


def Y(z):
    return CY - S * z


def cell(r, c):
    """(r,c) のカメラ index。未塗り・範囲外は None。row0 = 北端、col0 = 西端。"""
    if not (0 <= r < ROWS and 0 <= c < COLS):
        return None
    row = CELLS[r] if r < len(CELLS) else ""
    ch = row[c] if c < len(row) else "."
    return int(ch) if "0" <= ch <= "8" else None


def cell_rect(r, c):
    """セル (r,c) の course 空間の矩形。ZoneLayoutSolver.CellRect と同じ式。"""
    hw, hd = COLS * TILE / 2.0, ROWS * TILE / 2.0
    return (-hw + c * TILE, -hw + (c + 1) * TILE, hd - (r + 1) * TILE, hd - r * TILE)


# ---- 塗りを矩形へまとめる（ZoneLayoutSolver.SolveGrid と同じ貪欲法）----------
def merge_tiles():
    used = [[False] * COLS for _ in range(ROWS)]
    out = []
    for r in range(ROWS):
        for c in range(COLS):
            v = cell(r, c)
            if used[r][c] or v is None:
                continue
            c1 = c
            while c1 + 1 < COLS and not used[r][c1 + 1] and cell(r, c1 + 1) == v:
                c1 += 1
            r1 = r
            while r1 + 1 < ROWS and all(
                    not used[r1 + 1][cc] and cell(r1 + 1, cc) == v for cc in range(c, c1 + 1)):
                r1 += 1
            for rr in range(r, r1 + 1):
                for cc in range(c, c1 + 1):
                    used[rr][cc] = True
            x0, _, _, z1 = cell_rect(r, c)
            _, x1, z0, _ = cell_rect(r1, c1)
            out.append((v, x0, x1, z0, z1))
    return out


# ---- 塗りの違う面どうしの境界（外周は床の輪郭が受け持つので出さない）--------
def region_borders():
    segs = []
    for c in range(1, COLS):                                  # 縦の境界
        run = None
        for r in range(ROWS + 1):
            a, b = (cell(r, c - 1), cell(r, c)) if r < ROWS else (None, None)
            on = a is not None and b is not None and a != b
            if on and run is None:
                run = r
            elif not on and run is not None:
                x = cell_rect(0, c)[0]
                segs.append((x, cell_rect(r - 1, 0)[2], x, cell_rect(run, 0)[3]))
                run = None
    for r in range(1, ROWS):                                  # 横の境界
        run = None
        for c in range(COLS + 1):
            a, b = (cell(r - 1, c), cell(r, c)) if c < COLS else (None, None)
            on = a is not None and b is not None and a != b
            if on and run is None:
                run = c
            elif not on and run is not None:
                z = cell_rect(r, 0)[3]
                segs.append((cell_rect(0, run)[0], z, cell_rect(0, c - 1)[1], z))
                run = None
    return segs


# ---- 壁の footprint（中心線 ± 厚さ/2。room-model.js の wallFootprint と同じ）--
def wall_poly(w):
    dx, dz = w["x2"] - w["x1"], w["z2"] - w["z1"]
    ln = (dx * dx + dz * dz) ** 0.5
    if ln <= 0:
        return []
    ux, uz = dx / ln, dz / ln
    nx, nz = -uz, ux
    cx, cz = (w["x1"] + w["x2"]) / 2.0, (w["z1"] + w["z2"]) / 2.0
    hl, ht = ln / 2.0, max(0.005, w.get("thick", 0.04)) / 2.0
    return [(cx - ux * hl - nx * ht, cz - uz * hl - nz * ht),
            (cx + ux * hl - nx * ht, cz + uz * hl - nz * ht),
            (cx + ux * hl + nx * ht, cz + uz * hl + nz * ht),
            (cx - ux * hl + nx * ht, cz - uz * hl + nz * ht)]


# ---- 面のグレー（fig_room.py と同じ 3 段。有彩色にしない）--------------------
FILL = {0: "#f2f2f2", 1: "#dcdcdc", 2: "#c2c2c2", 3: "#aaaaaa"}
CAM_ID = [c.get("id", str(i)) for i, c in enumerate(show.get("cameras", []))]

out = []
A = out.append

A('<?xml version="1.0" encoding="UTF-8"?>')
A(f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {VB_W:.0f} {VB_H:.0f}"'
  f' width="{VB_W:.0f}mm" height="{VB_H:.0f}mm" role="img" aria-labelledby="t d">')
A('<title id="t">現場の床を真上から見た配置</title>')
A('<desc id="d">1.8 メートル四方の床を真上から見た平面図。床は 15 センチ角のタイル単位で 3 つの領域に'
  '塗り分けられ、それぞれを 1 台のカメラが担当する。北西の角から東へ 0.59 メートル、南へ 1.22 メートルの'
  'L 字パーテーションが床の上に立つ。壁と塗り分けのほかには何も描いていない。</desc>')
A('''<style>
  text  { font-family: Helvetica, Arial, "Yu Gothic", sans-serif; fill: #000; }
  .zone { font-size: 4.2px; font-weight: bold; fill: #6a6a6a; }
  .dim  { font-size: 2.4px; fill: #333; }
  .cap  { font-family: "Times New Roman", "Yu Mincho", serif; font-size: 3.2px; font-weight: bold; }
  .edge { fill: none; stroke: #9a9a9a; stroke-width: .2; }
  .out  { fill: none; stroke: #000; stroke-width: .3; }
  .wall { fill: #000; }
  .dl   { fill: none; stroke: #333; stroke-width: .2; }
</style>''')

# ① 塗られた床
A('<g id="floor">')
for v, x0, x1, z0, z1 in merge_tiles():
    A(f'<rect x="{X(x0):.2f}" y="{Y(z1):.2f}" width="{(x1 - x0) * S:.2f}"'
      f' height="{(z1 - z0) * S:.2f}" fill="{FILL.get(v, "#999")}"/>')
for x0, z0, x1, z1 in region_borders():
    A(f'<path d="M{X(x0):.2f},{Y(z0):.2f} L{X(x1):.2f},{Y(z1):.2f}" class="edge"/>')
A(f'<rect x="{X(-FW):.2f}" y="{Y(FD):.2f}" width="{2 * FW * S:.2f}"'
  f' height="{2 * FD * S:.2f}" class="out"/>')
A('</g>')

# ② 壁
A('<g id="walls">')
for w in WALLS:
    p = wall_poly(w)
    if p:
        A('<path d="' + " ".join(("M" if i == 0 else "L") + f"{X(x):.2f},{Y(z):.2f}"
                                 for i, (x, z) in enumerate(p)) + ' Z" class="wall"/>')
A('</g>')

# ③ 領域の名（面の上に直接。凡例は作らない）
for label, zx, zz in [("A", 0.22, -0.22), ("B", -0.70, 0.15), ("C", 0.30, 0.70)]:
    A(f'<text x="{X(zx):.2f}" y="{Y(zz) + 1.5:.2f}" class="zone" text-anchor="middle">{label}</text>')

# ④ 寸法（床の 2 辺と、壁 2 本の実長）
A(f'<path d="M{X(-FW):.2f},{Y(-FD) + 6:.2f} L{X(FW):.2f},{Y(-FD) + 6:.2f}" class="dl"/>')
A(f'<text x="{CX:.2f}" y="{Y(-FD) + 4.6:.2f}" class="dim" text-anchor="middle">'
  f'{2 * FW:.2f} m</text>')
A(f'<path d="M{X(-FW) - 6:.2f},{Y(FD):.2f} L{X(-FW) - 6:.2f},{Y(-FD):.2f}" class="dl"/>')
A(f'<text x="{X(-FW) - 7.4:.2f}" y="{CY:.2f}" class="dim" text-anchor="middle"'
  f' transform="rotate(-90 {X(-FW) - 7.4:.2f} {CY:.2f})">{2 * FD:.2f} m</text>')

for w in WALLS:
    ln = ((w["x2"] - w["x1"]) ** 2 + (w["z2"] - w["z1"]) ** 2) ** 0.5
    mx, mz = (w["x1"] + w["x2"]) / 2.0, (w["z1"] + w["z2"]) / 2.0
    if abs(w["z2"] - w["z1"]) < 1e-6:                     # 東西に伸びる腕 → 上に置く
        A(f'<text x="{X(mx):.2f}" y="{Y(mz) - 2.4:.2f}" class="dim" text-anchor="middle">'
          f'{ln:.2f} m</text>')
    else:                                                 # 南北に伸びる腕 → 左に置く
        tx, ty = X(mx) - 5.2, Y(mz)
        A(f'<text x="{tx:.2f}" y="{ty:.2f}" class="dim" text-anchor="middle"'
          f' transform="rotate(-90 {tx:.2f} {ty:.2f})">{ln:.2f} m</text>')

# ⑤ 方位（course space の +Z を北と呼ぶ）
A(f'<path d="M{X(FW) - 4:.2f},{Y(FD) - 3:.2f} L{X(FW) - 4:.2f},{Y(FD) - 9:.2f}" class="dl"/>')
A(f'<path d="M{X(FW) - 5.2:.2f},{Y(FD) - 7.6:.2f} L{X(FW) - 4:.2f},{Y(FD) - 9.6:.2f}'
  f' L{X(FW) - 2.8:.2f},{Y(FD) - 7.6:.2f} Z" fill="#333"/>')
A(f'<text x="{X(FW) - 4:.2f}" y="{Y(FD) - 1:.2f}" class="dim" text-anchor="middle">北</text>')

# ⑥ キャプション
A(f'<text x="{VB_W / 2:.2f}" y="{VB_H - 4:.2f}" class="cap" text-anchor="middle">'
  '図. 現場の床を真上から見た配置。塗りはカメラの担当領域、黒帯は L 字パーテーション。</text>')
A('</svg>')

dst = os.path.join(os.path.dirname(os.path.abspath(__file__)), "fig-floor.svg")
with io.open(dst, "w", encoding="utf-8", newline="\n") as f:
    f.write("\n".join(out) + "\n")

painted = sum(1 for r in range(ROWS) for c in range(COLS) if cell(r, c) is not None)
print(f"{dst}  ({src} から)")
print(f"  塗られたタイル {painted}/{ROWS * COLS}（{TILE * 100:.0f}cm 角）"
      f" / 床 {2 * FW:.2f}x{2 * FD:.2f} m")
for w in WALLS:
    ln = ((w["x2"] - w["x1"]) ** 2 + (w["z2"] - w["z1"]) ** 2) ** 0.5
    print(f"  壁 {w['id']}: ({w['x1']:+.2f},{w['z1']:+.2f}) -> ({w['x2']:+.2f},{w['z2']:+.2f})"
          f"  長さ {ln:.2f} m / 厚さ {w.get('thick', 0.04) * 1000:.0f} mm")
print("  カメラ id:", ", ".join(CAM_ID))
