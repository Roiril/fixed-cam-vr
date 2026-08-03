# 正面マスクの輪郭をそのまま前後へ押し出して人形の殻を作る（visual hull の 2 方向近似）。
#
# 手続きモデリング（寸法を数値で読んで組む）は形が破綻した。こちらは
#   - シルエットが**写真そのもの**になる
#   - UV が平面投影なのでテクスチャが原理的にずれない
# 押し出しの深さは 側面写真の半幅プロファイル D(z) と、胴中心からの左右距離で変調する
# （中央＝胴は厚く、外側＝袖は薄い板。T ポーズで横へ張り出した袖は実物も薄い）。
#
# 出力: shell.json（頂点 / 面 / UV / 腕の取り付け位置）→ Blender が読んで組み立てる
import cv2, numpy as np, json, os

HERE = os.path.dirname(os.path.abspath(__file__))
COLS, ROWS = 48, 96          # 格子。人形が映像で数百画素なので、これで階段は見えない
SLEEVE_THIN = 0.34           # 袖（外側）の厚みを胴の何倍まで落とすか

m = cv2.imread(os.path.join(HERE, "mask_front.png"), cv2.IMREAD_GRAYSCALE)
H, W = m.shape
ys, xs = np.nonzero(m)
x0, x1, y0, y1 = int(xs.min()), int(xs.max()), int(ys.min()), int(ys.max())
bw, bh = x1 - x0, y1 - y0

# 側面の半幅プロファイル（全高 1 に正規化）→ 高さ t での前後半径
side = json.load(open(os.path.join(HERE, "profile.json"), encoding="utf-8"))["sideB"]["rows"]
side_t = np.array([r["t"] for r in side])
side_h = np.array([r["half"] for r in side])


def depth_at(t):
    return float(np.interp(t, side_t, side_h))


# 距離変換: 各画素の「輪郭までの距離」。これで断面を楕円状に膨らませる
dist = cv2.distanceTransform((m > 0).astype(np.uint8), cv2.DIST_L2, 5)

# 格子点（正規化座標 u:0..1 が bbox 左右 / v:0..1 が bbox 下上）
grid_in = np.zeros((ROWS + 1, COLS + 1), bool)
grid_px = np.zeros((ROWS + 1, COLS + 1, 2), np.int32)
for j in range(ROWS + 1):
    for i in range(COLS + 1):
        px = int(round(x0 + bw * i / COLS))
        py = int(round(y1 - bh * j / ROWS))       # j=0 が足元
        grid_px[j, i] = (px, py)
        grid_in[j, i] = m[min(H - 1, max(0, py)), min(W - 1, max(0, px))] > 0

# 行ごとの左右端（袖の張り出しを測って、厚みの変調に使う）
row_span = []
for j in range(ROWS + 1):
    py = int(round(y1 - bh * j / ROWS))
    cols = np.nonzero(m[min(H - 1, max(0, py))])[0]
    if len(cols):
        row_span.append(((cols.min() + cols.max()) / 2, (cols.max() - cols.min()) / 2))
    else:
        row_span.append(((x0 + x1) / 2, 1.0))

DOLL_H = 0.40
scale = DOLL_H / bh                 # px → m


def depth_of(j, i):
    """その格子点での押し出し半深さ (m)。"""
    t = 1.0 - j / ROWS                              # 0=頭頂 1=足元
    d = depth_at(t) * DOLL_H                        # 側面写真から
    cx, half = row_span[j]
    px = grid_px[j, i][0]
    lateral = min(1.0, abs(px - cx) / max(half, 1.0))
    d *= 1.0 - (1.0 - SLEEVE_THIN) * lateral ** 1.6  # 外へ行くほど薄い板に
    # 輪郭からの距離で丸める（縁で 0、内側で最大）＝ 断面が楕円に近づく
    r = dist[min(H - 1, max(0, grid_px[j, i][1])), min(W - 1, max(0, px))]
    rn = min(1.0, r / max(1.0, half * 0.9))
    return d * float(np.sqrt(max(0.0, 1.0 - (1.0 - rn) ** 2)))


verts, uvs, index = [], [], {}


def vid(j, i, back):
    key = (j, i, back)
    if key in index:
        return index[key]
    px, py = grid_px[j, i]
    x = (px - (x0 + x1) / 2) * scale
    z = (y1 - py) * scale
    y = depth_of(j, i) * (1 if back else -1)
    verts.append((x, y, z))
    u = (px - x0) / bw * 0.5 + (0.5 if back else 0.0)
    v = (y1 - py) / bh
    uvs.append((u, v))
    index[key] = len(verts) - 1
    return index[key]


faces = []
for j in range(ROWS):
    for i in range(COLS):
        quad = [(j, i), (j, i + 1), (j + 1, i + 1), (j + 1, i)]
        if not all(grid_in[a, b] for a, b in quad):
            continue
        f = [vid(a, b, False) for a, b in quad]
        faces.append([f[0], f[1], f[2], f[3]])           # 前面（-Y 向き）
        b = [vid(a, c, True) for a, c in quad]
        faces.append([b[3], b[2], b[1], b[0]])           # 背面（+Y 向き）

# 縁を閉じる: 前後どちらかが欠けている辺に側面の帯を張る
edge_use = {}
for j in range(ROWS):
    for i in range(COLS):
        quad = [(j, i), (j, i + 1), (j + 1, i + 1), (j + 1, i)]
        if not all(grid_in[a, b] for a, b in quad):
            continue
        for k in range(4):
            a, b = quad[k], quad[(k + 1) % 4]
            e = (a, b) if a < b else (b, a)
            edge_use[e] = edge_use.get(e, 0) + 1
border = [e for e, n in edge_use.items() if n == 1]
for a, b in border:
    fa, fb = vid(*a, False), vid(*b, False)
    ba, bb = vid(*a, True), vid(*b, True)
    faces.append([fa, fb, bb, ba])

# 腕の取り付け位置（袖口）。front マスクの最大幅の行 ＝ 腕が水平に出ている高さ
widest = int(np.argmax([h for _, h in row_span]))
arm_t = 1.0 - widest / ROWS
arm_half_x = row_span[widest][1] * scale
data = dict(
    height=DOLL_H, verts=verts, uvs=uvs, faces=faces,
    arm=dict(t=arm_t, z=(1.0 - arm_t) * DOLL_H, tip_x=arm_half_x),
    bbox_px=[x0, y0, x1, y1],
)
with open(os.path.join(HERE, "shell.json"), "w") as f:
    json.dump(data, f)
print(f"verts={len(verts)} faces={len(faces)} arm_t={arm_t:.3f} tip_x={arm_half_x:.3f}m "
      f"depth(mid)={depth_at(0.5)*DOLL_H:.3f}m")
