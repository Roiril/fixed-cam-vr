# レンダした頭を**実物写真の隣**に、どちらもフル解像度で置く。
#
# visual-verification.md §10:「差分（前より良くなったか）ではなく絶対（髪に見えるか）で見る」
# 「実物と並べる」。前セッションはこれをやらずに壊れているものを完了と報告した。
#
# ⚠ 出力は横 2000px 以上になる。**縮小して見ない。**片側ずつ開いて判定すること。
#   このファイルが作るのは「どこを見るか」を決めるための並置であって、判定用の 1 枚ではない。
#
#   py -3.11 compare_head.py            # head_side.png と実物 sideB を並べる
#   py -3.11 compare_head.py front      # head_front.png と実物 front
import cv2, numpy as np, os, sys, geom

HERE = os.path.dirname(os.path.abspath(__file__))
PAIRS = {"side": ("head_side.png", "sideB"), "front": ("head_front.png", "front"),
         "back": ("head_back.png", "back")}
which = sys.argv[1] if len(sys.argv) > 1 else "side"
render_name, photo_name = PAIRS[which]


def real_head(name):
    im = cv2.imread(os.path.join(HERE, f"{name}.jpg"))
    m = cv2.imread(os.path.join(HERE, f"mask_{name}.png"), cv2.IMREAD_GRAYSCALE)
    ys, xs = np.nonzero(m)
    y0, y1, x0, x1 = ys.min(), ys.max(), xs.min(), xs.max()
    h = y1 - y0
    # head_check.py のカメラは「上から 22% ぶんを画面いっぱい」に撮る。同じ範囲を切る。
    return im[y0:y0 + int(h * 0.235), x0:x1 + 1]


r = cv2.imread(os.path.join(HERE, render_name))
p = real_head(photo_name)
H = max(r.shape[0], p.shape[0])
def fit(a):
    s = H / a.shape[0]
    return cv2.resize(a, (int(a.shape[1] * s), H), interpolation=cv2.INTER_AREA)
r, p = fit(r), fit(p)
gap = np.full((H, 24, 3), 40, np.uint8)
out = np.hstack([r, gap, p])
for txt, x in (("CG", 20), ("REAL", r.shape[1] + 44)):
    cv2.putText(out, txt, (x, 46), cv2.FONT_HERSHEY_SIMPLEX, 1.4, (255, 255, 255), 3)
path = os.path.join(HERE, f"cmp_{which}.png")
cv2.imwrite(path, out)
print(f"{path}  {out.shape[1]}x{out.shape[0]}  (do not judge from a downscaled view)")
