# 正面マスクの輪郭をそのまま前後へ押し出して人形の殻を作る（visual hull の 2 方向近似）。
#
# **幾何の式は geom.py にある**（テクスチャを焼く bake.py と共有するため）。ここは
# geom.Shell を格子状に評価して頂点・面・UV へ落とすだけ。
#
# ⚠ 式を 2 箇所に置くと沈黙して食い違う。ここに数式を書き足さないこと。
#
# ⚠ **格子でマスクを塗る方式は捨てた**（2026-08-03）。格子点が内か外かで四角を作ると
#    輪郭が階段になり、頭と髪が箱・袖が長方形・裾が柱、という形で全部に出た。
#    いまは **行ごとにマスクの実際の左右端を取り、その間を等分**して頂点を置く。
#
# ⚠ **髪の張り出し（HAIR_BULGE）の判定を写真から直に取るようにした**（2026-08-04）。
#    旧実装は `doll_albedo.png`（texture.py の出力）を `gray < 88` で見ていたが、
#    **赤い着物が gray=55 なので胴も「髪」と判定**され、実測で**胴の奥行きが側面写真より
#    1.22〜1.26 倍に膨らんでいた**（＝正面以外のどの角度でもシルエットが太かった）。
#    いまは彩度で切り分ける（髪 S<125 / 着物 S=216）。geom.hair_region() 参照。
#    ついでに「texture.py の出力を shell.py が読む」隠れた帰還路も消えた
#    （テクスチャを焼き直すたびに形が変わっていた）。
#
# 出力: shell.json（頂点 / 面 / UV / 腕の取り付け位置）→ Blender が読んで組み立てる
import json, os, numpy as np, geom

HERE = os.path.dirname(os.path.abspath(__file__))
S = geom.Shell()
ROWS, COLS = geom.ROWS, geom.COLS

verts, uvs = [], []
grid = [[[0, 0] for _ in range(COLS + 1)] for _ in range(ROWS + 1)]

for j in range(ROWS + 1):
    for i in range(COLS + 1):
        x, d, z, px, py, _, _ = S.point(j, i / COLS)
        # ⚠ u をパネル境界（0 / 0.5）へ張り付けない。テクスチャは Repeat + ミップなので
        #    バイリニアが反対側のパネルを吸い、袖先と頭頂で前後の絵が混ざる。
        #    半テクセル内側へ寄せ、Unity のインポートで Wrap = Clamp にする。
        s, vv = S.uv_of(px, py)
        for back in (0, 1):
            verts.append((x, d if back else -d, z))
            uvs.append((S.panel_u(s, back), vv))
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
# （1 面が u を 0.5 跨ぐと、そこにアトラス 1 枚が引き伸ばされる＝見下ろしで頭頂に円盤が出た）。

# 腕の取り付け位置（袖口）＝ マスクが最も横に広い行
widths = [2.0 * S.row(j)[1] for j in range(ROWS + 1)]
widest = int(np.argmax(widths))
arm_t = 1.0 - widest / ROWS
tip_x = widths[widest] / 2 * S.scale
with open(os.path.join(HERE, "shell.json"), "w") as f:
    json.dump(dict(height=geom.DOLL_H, verts=verts, uvs=uvs, faces=faces,
                   body_half=geom.BODY_HALF_N * geom.DOLL_H, arm_v0=geom.BODY_V0,
                   arm=dict(t=arm_t, z=(1.0 - arm_t) * geom.DOLL_H, tip_x=tip_x)), f)
print(f"verts={len(verts)} faces={len(faces)} arm_t={arm_t:.3f} tip_x={tip_x:.3f}m")
