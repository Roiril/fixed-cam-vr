# 正面マスクの輪郭をそのまま前後へ押し出して人形の殻を作る（visual hull の 2 方向近似）。
#
# ⚠ **格子でマスクを塗る方式は捨てた**（2026-08-03）。格子点が内か外かで四角を作ると
#    輪郭が階段になり、写真と並べたとき次が全部そのせいで壊れて見えた:
#      頭と髪が箱に見える / 袖が長方形になる / 裾が急に細って柱になる
#    いまは **行ごとにマスクの実際の左右端を取り、その間を等分**して頂点を置く。
#    横方向の階段が消え、輪郭が写真そのものになる（縦は ROWS 段の刻みだけ残る）。
#
# 押し出しの深さは 3 つの掛け算:
#   1. 側面写真の半幅プロファイル D(z)   … 高さごとの厚み
#   2. 胴の幅からの張り出し              … 袖は薄い板・胴は厚い
#   3. 行の中の位置（楕円断面）          … 縁で 0・中央で最大
#
# 出力: shell.json（頂点 / 面 / UV / 腕の取り付け位置）→ Blender が読んで組み立てる
import cv2, numpy as np, json, os

HERE = os.path.dirname(os.path.abspath(__file__))
ROWS, COLS = 144, 34         # 縦の刻み / 各行の横分割
SLEEVE_THIN = 0.30           # 胴から張り出した部分（袖）の厚みを胴の何倍まで落とすか
BODY_HALF_N = 0.135          # 胴の半幅（全高 = 1 に正規化）。これより外を「張り出し」と見る
DOLL_H = 0.40

m = cv2.imread(os.path.join(HERE, "mask_front.png"), cv2.IMREAD_GRAYSCALE)
H, W = m.shape
ys, xs = np.nonzero(m)
x0, x1, y0, y1 = int(xs.min()), int(xs.max()), int(ys.min()), int(ys.max())
bw, bh = x1 - x0, y1 - y0
scale = DOLL_H / bh

side = json.load(open(os.path.join(HERE, "profile.json"), encoding="utf-8"))["sideB"]["rows"]
side_t = np.array([r["t"] for r in side])
side_h = np.array([r["half"] for r in side])

# 行ごとの左右端。1 行だけ見ると縁がざらつくので、上下 1 行を足した中央値で均す。
raw = []
for j in range(ROWS + 1):
    py = int(round(y1 - bh * j / ROWS))
    cols = np.nonzero(m[min(H - 1, max(0, py))])[0]
    raw.append((float(cols.min()), float(cols.max())) if len(cols) else None)

for j in range(ROWS + 1):                      # 空行は近い行で埋める
    if raw[j] is not None:
        continue
    for k in range(1, ROWS + 2):
        if j - k >= 0 and raw[j - k] is not None:
            raw[j] = raw[j - k]; break
        if j + k <= ROWS and raw[j + k] is not None:
            raw[j] = raw[j + k]; break
    if raw[j] is None:
        raw[j] = ((x0 + x1) / 2, (x0 + x1) / 2)

span = []
for j in range(ROWS + 1):
    lo, hi = max(0, j - 1), min(ROWS, j + 1)
    ls = sorted(raw[k][0] for k in range(lo, hi + 1))
    rs = sorted(raw[k][1] for k in range(lo, hi + 1))
    span.append((ls[len(ls) // 2], rs[len(rs) // 2]))

body_half_px = BODY_HALF_N * bh
verts, uvs = [], []
grid = [[[0, 0] for _ in range(COLS + 1)] for _ in range(ROWS + 1)]

for j in range(ROWS + 1):
    py = y1 - bh * j / ROWS
    xl, xr = span[j]
    cx = (xl + xr) / 2
    t = 1.0 - j / ROWS                                   # 0 = 頭頂 / 1 = 足元
    depth = float(np.interp(t, side_t, side_h)) * DOLL_H
    for i in range(COLS + 1):
        u = i / COLS
        px = xl + (xr - xl) * u
        lat = max(0.0, abs(px - cx) / max(body_half_px, 1.0) - 1.0)
        lat = min(1.0, lat / 1.2)
        d = depth * (1.0 - (1.0 - SLEEVE_THIN) * lat)
        d *= float(np.sqrt(max(0.0, 1.0 - (2.0 * u - 1.0) ** 2))) ** 0.85
        x = (px - (x0 + x1) / 2) * scale
        z = (y1 - py) * scale
        uu = (px - x0) / bw * 0.5
        vv = (y1 - py) / bh
        for back in (0, 1):
            verts.append((x, d if back else -d, z))
            uvs.append((uu + 0.5 * back, vv))
            grid[j][i][back] = len(verts) - 1

faces = []
for j in range(ROWS):
    for i in range(COLS):
        f = [grid[j][i][0], grid[j][i + 1][0], grid[j + 1][i + 1][0], grid[j + 1][i][0]]
        faces.append(f)                                               # 前面（-Y 向き）
        b = [grid[j][i][1], grid[j][i + 1][1], grid[j + 1][i + 1][1], grid[j + 1][i][1]]
        faces.append([b[3], b[2], b[1], b[0]])                        # 背面（+Y 向き）

# 縁を閉じる（左右の側面 + 上端 + 下端）
for j in range(ROWS):
    for i in (0, COLS):
        f0, f1 = grid[j][i][0], grid[j + 1][i][0]
        b0, b1 = grid[j][i][1], grid[j + 1][i][1]
        faces.append([f0, f1, b1, b0] if i == 0 else [b0, b1, f1, f0])
for i in range(COLS):
    for j, flip in ((ROWS, False), (0, True)):
        f0, f1 = grid[j][i][0], grid[j][i + 1][0]
        b0, b1 = grid[j][i][1], grid[j][i + 1][1]
        faces.append([b0, b1, f1, f0] if flip else [f0, f1, b1, b0])

# 腕の取り付け位置（袖口）＝ マスクが最も横に広い行
widest = int(np.argmax([r - l for l, r in span]))
arm_t = 1.0 - widest / ROWS
tip_x = (span[widest][1] - span[widest][0]) / 2 * scale
with open(os.path.join(HERE, "shell.json"), "w") as f:
    json.dump(dict(height=DOLL_H, verts=verts, uvs=uvs, faces=faces,
                   body_half=BODY_HALF_N * DOLL_H,
                   arm=dict(t=arm_t, z=(1.0 - arm_t) * DOLL_H, tip_x=tip_x)), f)
print(f"verts={len(verts)} faces={len(faces)} arm_t={arm_t:.3f} tip_x={tip_x:.3f}m")
