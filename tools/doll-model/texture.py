# テクスチャアトラスを作る。
#
#   上段 左半分 = 前から見た絵 / 上段 右半分 = 後ろから見た絵
#   下段（高さ ARM_H）= 袖から出た白磁の手。左右の腕ぶんを並べる
#
# 上段は **bake.py が殻の表面から逆に引いて焼く**（写真をパネルへ引き伸ばして並べるのではなく、
# 各テクセルが写真のどこに写っているかを解いてサンプルする）。理由と旧方式の壊れ方は bake.py。
#
# ⚠ **髪を手続きテクスチャへ置き換える `synth_hair()` は削除した**（2026-08-04）。
#   「前後で同じ縦縞にすれば継ぎ目は原理的に消える」という理屈は正しかったが、
#   前後のパネルは互いに位置合わせされていないので `hair[:, HALF_W:] = hair[:, :HALF_W]` が
#   **幾何的に破綻していた**（実測: front 側の x=230 は「顔」、back 側の同じ x は「後頭部の髪」）。
#   結果、縞は**背景の inpaint 領域と額**に塗られ（額で重み 0.71・輝度 189）、本物の後頭部の髪は
#   そのまま残っていた。おまけに合成した髪は明るい（gray≈156）ので、
#   `make_normal` の髪の異方性（`g < 0.33`）も `gloss_mask` の髪（`v < 85`）も**一度も発火していなかった**。
#   実写の髪には前髪の切り口・生え際・毛の流れが写っている。置き換えずに使う。
import cv2, numpy as np, os, json, geom, bake

HERE = os.path.dirname(os.path.abspath(__file__))
HALF_W, BODY_H = geom.HALF_W, geom.BODY_H
ARM_H, TEX_H = geom.ARM_H, geom.TEX_H

# 写真の中の「袖から出た手」の位置（比率）。front 写真から目で取った。
ARM_BOX = {"L": (0.010, 0.317, 0.118, 0.360),
           "R": (0.883, 0.322, 0.994, 0.365)}


def arm_strip():
    """袖から出た白磁の手。上段に写っていない（マスクの外）ので別に切り出す。
    UV は 左腕 = u∈[0,0.5] / 右腕 = u∈[0.5,1]、周方向が v。"""
    src = cv2.imread(os.path.join(HERE, "front.jpg"))
    H, W = src.shape[:2]
    strip = np.zeros((ARM_H, HALF_W * 2, 3), np.uint8)
    for k, (key, (ax0, ay0, ax1, ay1)) in enumerate(ARM_BOX.items()):
        piece = src[int(ay0 * H):int(ay1 * H), int(ax0 * W):int(ax1 * W)]
        if piece.size == 0:
            continue
        piece = cv2.resize(piece, (HALF_W, ARM_H), interpolation=cv2.INTER_AREA)
        strip[:, k * HALF_W:(k + 1) * HALF_W] = piece
    return strip


def gloss_mask(albedo):
    """**部位ごとの光沢の強さ**をアルベドの色から起こす（アルベドのアルファへ入れる）。

    素材が違えば光り方が違う、という当たり前を絵に入れるためのもの。全部を同じ鏡面で光らせると
    「一様に濡れたプラスチック」に見え、それ自体が CG の合図になる。

      白磁の顔・手 … 胡粉を塗った陶器。**最も鋭く強い**
      帯・襟      … 絹の織物。金銀糸が入るので中〜強
      髪          … 人毛。中程度（流れに沿った照りは法線側で作る）
      赤い着物    … 縮緬の絹。柔らかく弱い
      絞りの帯揚げ … 表面が凹凸なので**ほとんど光らない**
    """
    hsv = cv2.cvtColor(albedo, cv2.COLOR_BGR2HSV)
    h = hsv[:, :, 0].astype(np.float32)
    s = hsv[:, :, 1].astype(np.float32)
    v = hsv[:, :, 2].astype(np.float32)
    m = np.full(v.shape, 0.30, np.float32)                     # 既定（布）
    m[((h < 14) | (h > 165)) & (s > 90)] = 0.28                # 赤い縮緬
    m[(s > 45) & (s < 110) & (v > 150) & (h > 150)] = 0.16     # 絞り（淡いピンク）
    m[(s < 80) & (v > 130)] = 0.70                             # 帯・襟
    m[(s < 55) & (v > 178)] = 1.00                             # 白磁
    m[v < 85] = 0.52                                           # 髪
    m = cv2.GaussianBlur(m, (0, 0), 2.0)
    return np.clip(m * 255, 0, 255).astype(np.uint8)


def make_normal(albedo):
    """アルベドの**中周波**から法線を起こす。

    着物は単一アルベドの赤い絹なので、輝度 ≒ 陰影が成り立つ。低周波（部屋全体の光）は
    形の情報ではないので落とし、高周波（織り目・金箔の粒）はノイズなので弱める。
    残る中周波＝布の襞が、これでライトに反応するようになる。

    ⚠ 撮影時の陰影はアルベドにも残っているので、強くすると**二重に**なる。
    既定は控えめ。顔の目・眉や帯の柄も凹凸として拾ってしまうが、この強さなら破綻しない。
    """
    g = cv2.cvtColor(albedo, cv2.COLOR_BGR2GRAY).astype(np.float32) / 255.0
    lo = cv2.GaussianBlur(g, (0, 0), 20.0)      # 部屋の光
    hi = cv2.GaussianBlur(g, (0, 0), 3.2)       # 織り目・目・眉のような細部は落とす
    mid = (hi - lo) * 0.5 + 0.5
    gx = cv2.Sobel(mid, cv2.CV_32F, 1, 0, ksize=5)
    gy = cv2.Sobel(mid, cv2.CV_32F, 0, 1, ksize=5)
    s = 0.85          # 強くすると顔の目や帯の柄まで凹凸になり、彫刻のように見える

    # **髪は上から下へ流れる。** 横方向の法線変化を強め、縦方向を抑えると、毛の筋に沿った
    # 縦の溝ができ、ハイライトが**横に走る帯**になる（人毛の照りの出方）。
    # 等方の鏡面のままだと、髪が「つるつるのヘルメット」に見える。
    hair = cv2.GaussianBlur((g < 0.33).astype(np.float32), (0, 0), 3.0)
    gx = gx * (1.0 + 1.7 * hair)
    gy = gy * (1.0 - 0.55 * hair)

    nx, ny, nz = -gx * s, gy * s, np.ones_like(gx)
    ln = np.sqrt(nx * nx + ny * ny + nz * nz)
    # OpenCV は BGR 順で書くので [B=z, G=y, R=x] を並べる
    out = np.stack([(nz / ln * 0.5 + 0.5), (ny / ln * 0.5 + 0.5), (nx / ln * 0.5 + 0.5)], -1)
    return np.clip(out * 255, 0, 255).astype(np.uint8)


body, cover = bake.body_atlas()
atlas = np.zeros((TEX_H, HALF_W * 2, 3), np.uint8)
atlas[:BODY_H] = body
atlas[BODY_H:] = arm_strip()

cv2.imwrite(os.path.join(HERE, "doll_normal.png"), make_normal(atlas))
# アルベドの**アルファに光沢マスク**を入れる（テクスチャを 1 枚増やさずに部位を分ける）
cv2.imwrite(os.path.join(HERE, "doll_albedo.png"), np.dstack([atlas, gloss_mask(atlas)]))
with open(os.path.join(HERE, "atlas.json"), "w") as f:
    json.dump(dict(width=geom.TEX_W, height=TEX_H, body_h=BODY_H, arm_h=ARM_H,
                   body_v0=geom.BODY_V0), f)

g = cv2.cvtColor(atlas[:BODY_H], cv2.COLOR_BGR2GRAY)
print(f"atlas {atlas.shape} body_v0={geom.BODY_V0:.4f} covered={cover.mean() * 100:.1f}% "
      f"髪の異方性が効く画素(g<0.33)={(g < 84).mean() * 100:.1f}%")
