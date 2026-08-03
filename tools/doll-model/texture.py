# テクスチャアトラスを作る。
#   左半分 = 前から見た絵 / 右半分 = 後ろから見た絵（左右反転）
# モデル側は法線の前後で貼り分ける。UV は「マスク bbox を 0..1 に正規化した座標」なので、
# ここで bbox をそのまま矩形へ引き伸ばしておけば、モデルの (x, y) から直に引ける。
#
# マスクの外は inpaint で埋める（背景の白い机や緑が縁に滲むと、暗い映像でも輪郭が浮く）。
import cv2, numpy as np, os

HERE = os.path.dirname(os.path.abspath(__file__))
HALF_W, TEX_H = 512, 1024


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

    # 背景を人形の色で埋める。inpaint は重いので縮小して解き、拡大して合成。
    sc = 400 / crop.shape[0]
    small = cv2.resize(crop, (max(8, int(crop.shape[1] * sc)), 400), interpolation=cv2.INTER_AREA)
    sm = cv2.resize(cm, (small.shape[1], small.shape[0]), interpolation=cv2.INTER_NEAREST)
    filled = cv2.inpaint(small, cv2.bitwise_not(sm), 7, cv2.INPAINT_TELEA)
    filled = cv2.resize(filled, (crop.shape[1], crop.shape[0]), interpolation=cv2.INTER_LINEAR)
    out = np.where(cm[:, :, None] > 0, crop, filled)

    out = cv2.resize(out, (HALF_W, TEX_H), interpolation=cv2.INTER_AREA)
    if flip:
        out = cv2.flip(out, 1)
    return out


atlas = np.zeros((TEX_H, HALF_W * 2, 3), np.uint8)
atlas[:, :HALF_W] = panel("front", flip=False)
atlas[:, HALF_W:] = panel("back", flip=True)   # 背面は後ろから見るので左右が入れ替わる
cv2.imwrite(os.path.join(HERE, "doll_albedo.png"), atlas)
print("atlas", atlas.shape)
