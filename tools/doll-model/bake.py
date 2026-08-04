# アトラスを**殻の表面から逆に引いて**焼く（写真を貼るのではなく、各テクセルが写真の
# どこに写っているかを解いてサンプルする）。
#
# 旧方式（写真をパネルへ引き伸ばして並べる）で壊れていたもの:
#
#   1. 前後の継ぎ目が**頭ではブレンドされていなかった**。`blend_seam()` はパネルの端
#      （アトラス列 0..72 / 440..512）を混ぜていたが、「front の u=0 と back の u=0.5 が
#      人形の同じ側面」が成り立つのは **bbox の全幅を使う行＝袖が一番広い行だけ**。
#      実測: 頭のシルエットはアトラス列 167〜346 → **重なりゼロ**。側面から見て頭のまん中に
#      硬い縦線が出ていた。
#   2. front と back が互いに位置合わせされていなかった。実測で**頭の幅が 25〜28% 食い違う**。
#      bbox 全体を引き伸ばすのではなく、**高さごとの実測シルエット**へ合わせる（profile.json）。
#
# 継ぎ目が原理的に出ない理由: 前面シートの重みを「どれだけ横を向いているか」s=|nx| で
# 決め、シルエット（s=1）でちょうど front:back = 50:50 にする。背面シートも同じ式なので、
# 両シートが出会う縁で**同じ色に収束する**。
#
# USE_SIDE=True にすると sideA/sideB も混ぜる（側面に実データを乗せる第 2 段）。
# ⚠ 側面写真は赤の R 中央値が front 138 → side 167（+21%）とずれるので、**頭部だけ**に効かせる。
#   胴へ効かせると、いま正しく見えている着物の色を壊す。
import cv2, numpy as np, os, geom

HERE = os.path.dirname(os.path.abspath(__file__))

WRAP_P = 2.2          # 前後クロスフェードの鋭さ。小さいほど広く混ざる
USE_SIDE = True      # 側面写真を混ぜるか（第 2 段）
SIDE_K = 9.0          # 側面写真の効き方の鋭さ（真横の近くだけに効かせる）
SIDE_GAIN = 0.90      # 真横での側面写真の重み
SIDE_T_MAX = 0.30     # 側面写真を使う高さの上限（0=頭頂）。頭部だけに限る

# 撮影方向。⚠ sideA と sideB のどちらが +X かは**実写の顔の向き**で決まる。
# カメラを +X に置いて -X を向き上を +Z にすると画面右が +Y（人形の後ろ）になるので顔は画面左。
# sideB は顔が画面左、sideA は顔が画面右 → sideB=+X / sideA=-X。
# 間違えると側面に顔の輪郭が後頭部側から出るので、レンダを見ればすぐ分かる。
SIDE_OF = {"sideB": +1.0, "sideA": -1.0}


def load(name):
    img = cv2.imread(os.path.join(HERE, f"{name}.jpg"))
    m = cv2.imread(os.path.join(HERE, f"mask_{name}.png"), cv2.IMREAD_GRAYSCALE)
    # ⚠ マスクを数画素**収縮**する。縁の「人形と背景が混ざった画素」を引くと輪郭に暗い線が出る
    #    （頭では 1 列が写真の 16 画素しかないので特に目立つ）。
    er = max(3, int(min(img.shape[:2]) * 0.006)) | 1
    inner = cv2.erode(m, np.ones((er, er), np.uint8))
    return dict(img=img.astype(np.float32), inner=inner.astype(np.float32) / 255.0,
                pm=geom.photo_map(name))


def sample_xy(ph, px, py):
    col = cv2.remap(ph["img"], px.astype(np.float32).reshape(1, -1),
                    py.astype(np.float32).reshape(1, -1), cv2.INTER_LINEAR,
                    borderMode=cv2.BORDER_REPLICATE)[0]
    ok = cv2.remap(ph["inner"], px.astype(np.float32).reshape(1, -1),
                   py.astype(np.float32).reshape(1, -1), cv2.INTER_LINEAR,
                   borderMode=cv2.BORDER_CONSTANT, borderValue=0.0)[0]
    return col, ok


def sample_norm(ph, t, lat):
    """写真 ph を、高さ t∈[0,1]（0=頭頂）と、その高さでの横位置 lat∈[0,1] で引く。

    lat は**その高さの左端〜右端**を 0..1 とする（profile.json 由来）。写真ごとに人形の
    写り方（距離・画角）が違うので、bbox 全体ではなく高さごとに合わせる。
    """
    pm = ph["pm"]
    L = np.interp(t, pm["t"], pm["L"])
    R = np.interp(t, pm["t"], pm["R"])
    px = L + np.clip(lat, 0.0, 1.0) * (R - L)
    py = pm["y0"] + np.clip(t, 0.0, 1.0) * pm["span"]
    return sample_xy(ph, px, py)


def head_hair_pixels(ph):
    """頭部（t<SIDE_T_MAX）の**髪の画素**だけを選ぶ。露出合わせの比較対象に使う。"""
    hsv = cv2.cvtColor(np.clip(ph["img"], 0, 255).astype(np.uint8), cv2.COLOR_BGR2HSV)
    v = hsv[:, :, 2].astype(np.float32)
    sat = hsv[:, :, 1].astype(np.float32)
    pm = ph["pm"]
    yy = np.arange(ph["img"].shape[0], dtype=np.float32)[:, None]
    t = (yy - pm["y0"]) / pm["span"]
    sel = (ph["inner"] > 0.5) & (v < 102) & (sat < 125) & (t > 0.0) & (t < SIDE_T_MAX)
    return ph["img"][sel].reshape(-1, 3)


def match_to(ph, ref, hair_only=False):
    """写真ごとの露出・ホワイトバランスを front へ寄せる。

    別々に撮った写真なので露出も白バランスも違う。合わせないと、重みが連続でも
    **色が連続でない**ので継ぎ目が帯として残る。

    ⚠ 統計は**その写真を実際に使う場所の中身**で取る。人形全体で取ると、写っている物の
      割合が違う（front は顔と着物が主・側面写真は髪が主）ので、露出差ではなく
      **中身の差**を補正してしまう。実測でこれをやると gain も shift も全チャンネルが
      クランプの上下限に張り付き（0.850 / 1.180・赤に +14〜+22 のシフト）、
      髪が**赤紫に転んで白茶ける**。側面写真は頭の髪にしか使わないので、そこで合わせる。
    """
    a = head_hair_pixels(ph) if hair_only else ph["img"][ph["inner"] > 0.5]
    b = head_hair_pixels(ref) if hair_only else ref["img"][ref["inner"] > 0.5]
    if len(a) < 500 or len(b) < 500:
        return
    for c in range(3):
        sa = a[:, c].std()
        gain = float(np.clip((b[:, c].std() / sa) if sa > 1e-3 else 1.0, 0.85, 1.18))
        shift = float(np.median(b[:, c])) - float(np.median(a[:, c])) * gain
        ph["img"][:, :, c] = np.clip(ph["img"][:, :, c] * gain + shift, 0, 255)


def bake():
    S = geom.Shell()
    names = ["front", "back"] + ([*SIDE_OF] if USE_SIDE else [])
    ph = {n: load(n) for n in names}
    for n in names:
        if n != "front":
            # back は人形全体に使うので全体で、側面写真は頭の髪にしか使わないのでそこで合わせる
            match_to(ph[n], ph["front"], hair_only=n.startswith("side"))

    W, BH = geom.TEX_W, geom.BODY_H
    out = np.zeros((BH, W, 3), np.float32)
    cover = np.zeros((BH, W), np.float32)

    u = (np.arange(W, dtype=np.float64) + 0.5) / W
    s_all, is_back = S.s_of_u(u)

    for row in range(BH):
        v = geom.BODY_V0 + (1.0 - (row + 0.5) / BH) * geom.BODY_VS
        py = S.y1 - (v - geom.BODY_V0) / (geom.BODY_VS - geom.HALF_TEXEL_V) * S.bh
        jf = S.row_of_py(py)
        if not (0.0 <= jf <= geom.ROWS):
            continue
        # ⚠ u は**弧長**で配ってあるので、列 → 写真の x は行ごとに解く（geom が唯一の定義）
        px_all = S.px_of_s(jf, s_all)
        d, nx, ny, py_r, cx, half_w = S.surface_row(jf, px_all)
        inside = np.abs(px_all - cx) <= half_w + 1e-9
        if not inside.any():
            continue

        t = np.clip((py_r - S.y0) / S.bh, 0.0, 1.0)
        tt = np.full(W, t)
        lat = np.clip((px_all - (cx - half_w)) / max(1e-6, 2.0 * half_w), 0.0, 1.0)
        sw = np.clip(np.abs(nx), 0.0, 1.0)         # 0=正面/背面を向く, 1=真横を向く

        # --- 前後のクロスフェード。真横(sw=1)でちょうど 50:50 になるので縁で一致する。
        # 側面写真を混ぜない高さ（胴）ではこれが継ぎ目対策のすべて。
        w_far = 0.5 * sw ** WRAP_P
        w_near = 1.0 - w_far
        w_front = np.where(is_back, w_far, w_near)
        w_back = np.where(is_back, w_near, w_far)

        cols, oks, ws = [], [], []
        # front は**写真の画素を直に引く**（殻は正面マスクから作られているので厳密に 1:1）。
        # 正規化を通すと顔がわずかに再サンプルされる。いま正しく見えている面は触らない。
        c, o = sample_xy(ph["front"], px_all, np.full(W, py_r))
        cols.append(c); oks.append(o); ws.append(w_front)
        c, o = sample_norm(ph["back"], tt, 1.0 - lat)   # 後ろから見るので左右が入れ替わる
        cols.append(c); oks.append(o); ws.append(w_back)

        if USE_SIDE:
            # ⚠ 押し出した殻を正面写真だけで塗ると、**頬がそのまま側面へ回り込む**。
            #    実物は髪が頭の側面を覆っていて、横から見えるのは細い顔の輪郭だけ。
            #    その情報は正面写真に無い（側面写真にしか無い）。真横を向く面ほど側面写真へ寄せる。
            depth_row = max(1e-6, float(np.max(d)))
            y = np.where(is_back, d, -d)
            nd = np.clip((y + depth_row) / (2.0 * depth_row), 0.0, 1.0)   # 0=前 1=後
            fade = float(np.clip((SIDE_T_MAX - t) / 0.06, 0.0, 1.0))      # 頭部だけ
            if fade > 0.0:
                gain = SIDE_GAIN * fade * sw ** SIDE_K
                take = np.zeros(W, np.float32)
                for nm, sgn in SIDE_OF.items():
                    side_w = gain * (np.sign(nx) == sgn)   # その側を向いている画素だけ
                    if side_w.max() <= 1e-6:
                        continue
                    latm = nd if sgn > 0 else 1.0 - nd
                    c, o = sample_norm(ph[nm], tt, latm)
                    cols.append(c); oks.append(o); ws.append(side_w)
                    take = take + side_w
                # 側面が乗った分だけ前後を薄める（⚠ 側面 2 枚の合計で 1 回だけ）
                ws[0] = ws[0] * (1.0 - take)
                ws[1] = ws[1] * (1.0 - take)

        acc = np.zeros((W, 3), np.float32)
        wsum = np.zeros(W, np.float32)
        for c, o, wv in zip(cols, oks, ws):
            wv = wv * o                     # その写真で人形が写っていない所は使わない
            acc += c * wv[:, None]
            wsum += wv
        good = (wsum > 1e-4) & inside
        out[row][good] = acc[good] / wsum[good][:, None]
        cover[row][good] = 1.0
    return out, cover


def fill_outside(img, cover):
    """人形の外側を、いちばん近い人形の画素で埋める（背景が縁へ滲むと輪郭が浮く）。

    inpaint は使わない。背面の帯（白い蝶結び）で大きな滲みを出した実績があるうえ、
    ここは輪郭さえ保てればよい。
    """
    hole = (cover < 0.5).astype(np.uint8)
    if hole.sum() == 0 or (cover >= 0.5).sum() == 0:
        return img
    _, lab = cv2.distanceTransformWithLabels(hole, cv2.DIST_L2, 3,
                                             labelType=cv2.DIST_LABEL_PIXEL)
    ys, xs = np.nonzero(cover >= 0.5)
    idx = np.zeros(int(lab.max()) + 1, np.int64)
    idx[lab[ys, xs]] = ys * img.shape[1] + xs
    filled = img.reshape(-1, 3)[idx[lab]].reshape(img.shape)
    return np.where(cover[:, :, None] >= 0.5, img, filled)


def soften_crown(img, S):
    """頭頂だけ**周方向にぼかす**。

    頭頂は球で閉じてあるので行の半幅が 0 へ収束する。そこへ弧長で UV を配ると、写真の
    数画素がパネル 1 枚ぶんへ拡大され、**放射状の筋（円錐の先端）**になる。
    しかも頭頂を正面から見ている写真は 1 枚も無い（4 枚とも真横から掠める）ので、
    そこに描くべき細部は**そもそも手元に無い**。無いものを拡大して見せるより均す。

    u は断面の周方向なので、水平方向のぼかしがそのまま周方向のぼかしになる
    （縦＝毛の流れは保たれる）。u=0 と u=1 は同じ稜線なので端は巻き込む。
    """
    out = img.copy()
    BH = geom.BODY_H
    for row in range(BH):
        v = geom.BODY_V0 + (1.0 - (row + 0.5) / BH) * geom.BODY_VS
        py = S.y1 - (v - geom.BODY_V0) / (geom.BODY_VS - geom.HALF_TEXEL_V) * S.bh
        t = (py - S.y0) / S.bh
        if t > CROWN_T:
            continue
        k = int(round(CROWN_BLUR * (1.0 - max(0.0, t) / CROWN_T) ** 2)) | 1
        if k < 3:
            continue
        line = out[row][None, :, :]
        out[row] = cv2.blur(np.hstack([line, line, line]), (k, 1))[0, img.shape[1]:2 * img.shape[1]]
    return out


CROWN_T = 0.055        # 頭頂からこの高さまでを均す（全高に対する割合）
CROWN_BLUR = 121       # 頭頂での周方向ぼかし幅（テクセル）


def body_atlas():
    body, cover = bake()
    body = fill_outside(body, cover)
    return np.clip(soften_crown(body, geom.Shell()), 0, 255).astype(np.uint8), cover


if __name__ == "__main__":
    b, cv_ = body_atlas()
    cv2.imwrite(os.path.join(HERE, "bake_body.png"), b)
    print(f"baked {b.shape} covered={cv_.mean() * 100:.1f}%  USE_SIDE={USE_SIDE}")
