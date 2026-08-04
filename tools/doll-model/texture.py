# テクスチャアトラスを作る。
#
#   上段 左半分 = 前から見た絵 / 上段 右半分 = 後ろから見た絵（左右反転）
#   下段（高さ ARM_H）= 袖から出た白磁の手。左右の腕ぶんを並べる
#
# モデル側は法線の前後で上段を貼り分け、腕メッシュだけ下段を引く。
# UV は「マスク bbox を 0..1 に正規化した座標」なので、bbox をそのまま矩形へ引き伸ばせば
# モデルの (x, z) から直に引ける。**v は下段のぶんだけ詰まる**ので shell.py と対で直すこと。
#
# 直している問題:
#   - マスクの外は inpaint で埋める（背景の白い机や緑が縁に滲むと暗い映像でも輪郭が浮く）
#   - **前面と背面の露出差**を合わせる（別々に撮った写真なので、そのままだと側面で色が飛ぶ）
#   - **側面の縫い目**をクロスフェードで消す（前面の左端と背面の左端は人形の同じ側面）
import cv2, numpy as np, os, json

HERE = os.path.dirname(os.path.abspath(__file__))
HALF_W, BODY_H = 512, 1024
ARM_H = 160                       # 下段（手）の高さ
TEX_H = BODY_H + ARM_H
SEAM = 72                         # 側面のクロスフェード幅 (px)

# 写真の中の「袖から出た手」の位置（比率）。front 写真から目で取った。
ARM_BOX = {"L": (0.010, 0.317, 0.118, 0.360),
           "R": (0.883, 0.322, 0.994, 0.365)}


def panel(name, flip):
    src = cv2.imread(os.path.join(HERE, f"{name}.jpg"))
    m = cv2.imread(os.path.join(HERE, f"mask_{name}.png"), cv2.IMREAD_GRAYSCALE)
    ys, xs = np.nonzero(m)
    x0, x1, y0, y1 = int(xs.min()), int(xs.max()), int(ys.min()), int(ys.max())

    crop = src[y0:y1 + 1, x0:x1 + 1].copy()
    cm = m[y0:y1 + 1, x0:x1 + 1].copy()

    # 行ごとに左端〜右端を塗りつぶす。背面は帯（白い蝶結び）がマスクの穴になっていて、
    # そのまま inpaint すると背中の中央に大きな白い滲みが出る（実測）。
    # 左端・右端は変えないので輪郭は保たれる。テクスチャ用のマスクなので形には影響しない。
    for yy in range(cm.shape[0]):
        cols = np.nonzero(cm[yy])[0]
        if len(cols):
            cm[yy, cols.min():cols.max() + 1] = 255

    # ⚠ マスクを数画素**収縮**してから埋める。頂点の u は行の実際の左右端と一致するので、
    #    縁の「人形と背景が混ざった画素」をそのまま引いてしまい、輪郭に暗い線が出る
    #    （頭では 1 列が写真の 16 画素しかないので特に目立つ）。ミップが上がるほど内側へ滲む。
    er = max(3, int(min(crop.shape[:2]) * 0.006)) | 1
    cm_in = cv2.erode(cm, np.ones((er, er), np.uint8))

    # 背景を人形の色で埋める。inpaint は重いので縮小して解き、拡大して合成。
    sc = 400 / crop.shape[0]
    small = cv2.resize(crop, (max(8, int(crop.shape[1] * sc)), 400), interpolation=cv2.INTER_AREA)
    sm = cv2.resize(cm_in, (small.shape[1], small.shape[0]), interpolation=cv2.INTER_NEAREST)
    filled = cv2.inpaint(small, cv2.bitwise_not(sm), 9, cv2.INPAINT_TELEA)
    filled = cv2.resize(filled, (crop.shape[1], crop.shape[0]), interpolation=cv2.INTER_LINEAR)
    out = np.where(cm_in[:, :, None] > 0, crop, filled)

    out = cv2.resize(out, (HALF_W, BODY_H), interpolation=cv2.INTER_AREA)
    if flip:
        out = cv2.flip(out, 1)
    inside = cv2.resize(cm, (HALF_W, BODY_H), interpolation=cv2.INTER_NEAREST)
    if flip:
        inside = cv2.flip(inside, 1)
    return out, inside


def match_exposure(dst, dst_in, ref, ref_in):
    """前面の写真に合わせて背面の露出・色かぶりを寄せる。

    2 枚は別々に撮られていて露出もホワイトバランスも違う。合わせないと**側面で色が飛ぶ**
    （繋ぎ目が線として見える）。人形が写っている画素だけで統計を取る。"""
    out = dst.astype(np.float32)
    dm = dst_in > 0
    rm = ref_in > 0
    if dm.sum() < 100 or rm.sum() < 100:
        return dst
    for c in range(3):
        a, b = out[:, :, c][dm], ref[:, :, c][rm].astype(np.float32)
        sd = a.std()
        gain = (b.std() / sd) if sd > 1e-3 else 1.0
        gain = float(np.clip(gain, 0.92, 1.10))   # 効かせすぎると赤が飛ぶ
        # 中央値も合わせる（平均だけだと、面積の大きい赤に引きずられて白い帯がずれる）
        med_shift = float(np.median(b)) - float(np.median(a))
        out[:, :, c] = (out[:, :, c] - a.mean()) * gain + a.mean() + med_shift
    return np.clip(out, 0, 255).astype(np.uint8)


def blend_seam(atlas):
    """人形の側面で前面と背面をクロスフェードする。

    front の u=0 と back の u=0.5 は**人形の同じ左側面**（back は左右反転済み）。
    そこが不連続だと、側面から見たとき縦の線として出る。
    アトラス上では front x=0 と back x=HALF_W、front x=HALF_W-1 と back x=W-1 が対。"""
    a = atlas.astype(np.float32)
    for i in range(SEAM):
        w = 0.5 * (1.0 - i / SEAM) ** 1.4      # 縁で half-and-half、内側へ行くほど元の色
        for pf, pb in ((i, HALF_W + i), (HALF_W - 1 - i, 2 * HALF_W - 1 - i)):
            f = a[:BODY_H, pf].copy()
            b = a[:BODY_H, pb].copy()
            a[:BODY_H, pf] = f * (1 - w) + b * w
            a[:BODY_H, pb] = b * (1 - w) + f * w
    return np.clip(a, 0, 255).astype(np.uint8)


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


atlas = np.zeros((TEX_H, HALF_W * 2, 3), np.uint8)
f_img, f_in = panel("front", flip=False)
b_img, b_in = panel("back", flip=True)     # 背面は後ろから見るので左右が入れ替わる
b_img = match_exposure(b_img, b_in, f_img, f_in)
atlas[:BODY_H, :HALF_W] = f_img
atlas[:BODY_H, HALF_W:] = b_img
atlas = blend_seam(atlas)
atlas[BODY_H:] = arm_strip()

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


cv2.imwrite(os.path.join(HERE, "doll_normal.png"), make_normal(atlas))
# アルベドの**アルファに光沢マスク**を入れる（テクスチャを 1 枚増やさずに部位を分ける）
rgba = np.dstack([atlas, gloss_mask(atlas)])
cv2.imwrite(os.path.join(HERE, "doll_albedo.png"), rgba)
with open(os.path.join(HERE, "atlas.json"), "w") as f:
    json.dump(dict(width=HALF_W * 2, height=TEX_H, body_h=BODY_H, arm_h=ARM_H,
                   body_v0=ARM_H / TEX_H), f)
print(f"atlas {atlas.shape} body_v0={ARM_H / TEX_H:.4f}")
