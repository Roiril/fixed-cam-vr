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
import cv2, numpy as np, json, os, math

HERE = os.path.dirname(os.path.abspath(__file__))
ROWS, COLS = 144, 34         # 縦の刻み / 各行の横分割
SLEEVE_THIN = 0.50           # 胴から張り出した部分（袖）の厚みを胴の何倍まで落とすか
BODY_HALF_N = 0.135          # 胴の半幅（全高 = 1 に正規化）。これより外を「張り出し」と見る
DOLL_H = 0.40
HAIR_BULGE = 0.24            # 髪は顔より前後に張り出す（下の説明）
FOOT_CUT = 0.014             # 足元の最下部を切る割合（台の潰れた面が乱れて見えるため）

m = cv2.imread(os.path.join(HERE, "mask_front.png"), cv2.IMREAD_GRAYSCALE)
H, W = m.shape
ys, xs = np.nonzero(m)
x0, x1, y0, y1 = int(xs.min()), int(xs.max()), int(ys.min()), int(ys.max())
bw, bh = x1 - x0, y1 - y0
scale = DOLL_H / bh

# アトラスの下段（手のストリップ）ぶん v が詰まる。texture.py と対で直すこと。
try:
    _atlas = json.load(open(os.path.join(HERE, "atlas.json"), encoding="utf-8"))
    BODY_V0 = float(_atlas["body_v0"])
    ATLAS_W, ATLAS_H = int(_atlas["width"]), int(_atlas["height"])
except Exception:
    BODY_V0, ATLAS_W, ATLAS_H = 0.0, 1024, 1024
BODY_VS = 1.0 - BODY_V0

side = json.load(open(os.path.join(HERE, "profile.json"), encoding="utf-8"))["sideB"]["rows"]
side_t = np.array([r["t"] for r in side])
side_h = np.array([r["half"] for r in side])
# ⚠ 側面プロファイルは 48 行しかない。それを 144 行へ線形補間すると 8mm ごとに折れ、
#    行間の深さが最大 7mm 段differ する（行の刻み 2.8mm に対し 15〜68 度の法線の振れ）。
#    上から照らすと**横縞**として出る。補間の前に均す。
side_h = np.convolve(np.pad(side_h, 2, mode="edge"), np.ones(5) / 5.0, mode="valid")

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

# **髪は顔より前後に張り出している。** 一枚の殻を同じ厚みで押し出すと、髪と顔が同じ面に
# 乗るので、頭が「兜」に見える（実物は髪が張り出し、その内側に顔が窪む）。
# アトラスの暗い領域＝髪として、そこだけ押し出しを厚くする。境界で段差が出ないよう大きくぼかす。
try:
    _alb = cv2.imread(os.path.join(HERE, "doll_albedo.png"), cv2.IMREAD_COLOR)
    _g = cv2.cvtColor(_alb, cv2.COLOR_BGR2GRAY)
    HAIR_MAP = cv2.GaussianBlur((_g < 88).astype(np.float32), (0, 0), 14.0)
    HAIR_H, HAIR_W = HAIR_MAP.shape
except Exception:
    HAIR_MAP, HAIR_H, HAIR_W = None, 1, 1


def hair_at(uu, vv):
    """アトラス座標での「髪らしさ」0..1。"""
    if HAIR_MAP is None:
        return 0.0
    px = int(min(HAIR_W - 1, max(0, uu * HAIR_W)))
    py = int(min(HAIR_H - 1, max(0, (1.0 - vv) * HAIR_H)))
    return float(HAIR_MAP[py, px])


body_half_px = BODY_HALF_N * bh
foot_px = bh * FOOT_CUT
y1_mesh = y1 - foot_px            # 足元の台の潰れた面を切る（UV は元の bbox のまま）
bh_mesh = bh - foot_px
verts, uvs = [], []
grid = [[[0, 0] for _ in range(COLS + 1)] for _ in range(ROWS + 1)]

HALF_TEXEL_U = 0.5 / ATLAS_W          # UV をパネル境界から半テクセル内へ寄せる
HALF_TEXEL_V = 0.5 / ATLAS_H

for j in range(ROWS + 1):
    py = y1_mesh - bh_mesh * j / ROWS
    xl, xr = span[j]
    cx = (xl + xr) / 2
    half_w = max(1.0, (xr - xl) / 2)
    t = 1.0 - j / ROWS                                   # 0 = 頭頂 / 1 = 足元
    depth = float(np.interp(t, side_t, side_h)) * DOLL_H
    sleeve_d = depth * SLEEVE_THIN
    # 上下の端は深さを 0 へ落として自然に閉じる。フタ（前後を直結する面）を張ると
    # **1 面が u を 0.5 跨いでアトラス 1 枚ぶんを引き伸ばす**（見下ろしで頭頂に円盤が出た）。
    cap = min(1.0, min(j, ROWS - j) / 1.15)
    for i in range(COLS + 1):
        u = i / COLS
        px = xl + (xr - xl) * u
        dx = abs(px - cx)
        # ⚠ 断面は**胴と袖で別々の楕円**にする。行の全幅（袖先から袖先）で 1 つの楕円を
        #    張ると、幅の狭い胴はその「てっぺん」だけを使うことになり、実測で胴の法線の
        #    傾きが中央値 5.9 度しか出ない ＝ どの向きから照らしても胴が 1 色になる。
        if dx <= body_half_px:
            q = dx / body_half_px
            # 縁でも少し厚みを残す（0.92）。ここを 1.0 にすると胴と袖の境目が折れる
            d = depth * math.sqrt(max(0.0, 1.0 - 0.92 * q * q))
        else:
            outer = max(1.0, half_w - body_half_px)
            q = min(1.0, (dx - body_half_px) / outer)
            # ⚠ 4 乗にして**袖口の近くまで厚みを保つ**。2 乗（素の楕円）だと袖の端で
            #    厚みが 0 へ向かうので、そこを通る腕が必ず布から飛び出して見える。
            d = sleeve_d * math.sqrt(max(0.0, 1.0 - q ** 4))
        # ⚠ **行の左右端では必ず 0 にする**。ここが 0 でないと前後が閉じず、
        #    側面から見たときに人形が縦に裂けて見える（胴だけの行＝裾で踏んだ）。
        # 端の**ごく近く**だけで 0 へ落とす。ここを緩やかにすると（係数が小さいと）
        # 袖口の手前から厚みが痩せ、そこを通る腕が布から出てしまう。
        edge = math.sqrt(max(0.0, 1.0 - (dx / half_w) ** 2))
        d *= min(1.0, edge * 7.0) * cap
        x = (px - (x0 + x1) / 2) * scale
        z = (y1_mesh - py) * scale
        # ⚠ u をパネル境界（0 / 0.5）へ張り付けない。テクスチャは Repeat + ミップなので
        #    バイリニアが反対側のパネルを吸い、袖先と頭頂で前後の絵が混ざる。
        uu = HALF_TEXEL_U + (px - x0) / bw * (0.5 - 2.0 * HALF_TEXEL_U)
        vv = BODY_V0 + min((y1 - py) / bh, 1.0) * (BODY_VS - HALF_TEXEL_V)
        # 髪はここで初めて顔より前後へ出る（頭が「兜」に見えなくなる）
        d *= 1.0 + HAIR_BULGE * hair_at(uu, vv)
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

# 左右の縁は、断面の式が i=0 / i=COLS で d=0 を返すので前後の頂点が一致し、
# remove_doubles で自然に閉じる（面積ゼロの帯を張る必要はない）。
# 上下の端も cap で d を 0 へ落としてあるので閉じる。**フタは張らない**
# （1 面が u を 0.5 跨ぐと、そこにアトラス 1 枚が引き伸ばされる）。

# 腕の取り付け位置（袖口）＝ マスクが最も横に広い行
widest = int(np.argmax([r - l for l, r in span]))
arm_t = 1.0 - widest / ROWS
tip_x = (span[widest][1] - span[widest][0]) / 2 * scale
with open(os.path.join(HERE, "shell.json"), "w") as f:
    json.dump(dict(height=DOLL_H, verts=verts, uvs=uvs, faces=faces,
                   body_half=BODY_HALF_N * DOLL_H, arm_v0=BODY_V0,
                   arm=dict(t=arm_t, z=(1.0 - arm_t) * DOLL_H, tip_x=tip_x)), f)
print(f"verts={len(verts)} faces={len(faces)} arm_t={arm_t:.3f} tip_x={tip_x:.3f}m")
