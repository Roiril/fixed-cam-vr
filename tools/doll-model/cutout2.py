# 人形の切り抜き（2 案目）。
#
# 1 案目（画像全体に GrabCut）は顔を背景として捨てた。実測すると
#   顔 H22 S25 V207 / 机 H40 S11 V210 で、**色では区別できない**（どちらも明るく低彩度）。
#   髪 V63/S43 と黒シャツ V50/S26 も同じく近い。
# 確実に取れるのは赤い着物だけ（S217 と突出）。そこで役割を分ける:
#
#   着物 = 赤マスク → 閉じる → 穴埋め（帯・襟・前身頃の白はここで入る）
#   頭部 = **頭だけに絞った矩形**で GrabCut（狭い矩形なら顔と背景の色分布が分かれる）
#   腕・手 = 取らない。袖から出る白磁の腕は CG 側で作る（テクスチャに要らない）
import cv2, numpy as np, os, json, math

HERE = os.path.dirname(os.path.abspath(__file__))

# 頭部（髪を含む外形）の目測。比率 (cx, cy, rx, ry)。
HEAD = {
    "front": (0.510, 0.169, 0.140, 0.151),
    "back":  (0.531, 0.170, 0.170, 0.155),
    "sideA": (0.407, 0.177, 0.337, 0.152),
    "sideB": (0.555, 0.182, 0.328, 0.163),
}


def red_mask(bgr):
    hsv = cv2.cvtColor(bgr, cv2.COLOR_BGR2HSV)
    h, s, v = hsv[:, :, 0], hsv[:, :, 1], hsv[:, :, 2]
    return (((h < 14) | (h > 165)) & (s > 90) & (v > 45)).astype(np.uint8) * 255


def fill_holes(m):
    """外周から flood fill して、届かなかった画素（＝内部の穴）を埋める。

    ⚠ 画像に 1px の背景の縁を足してから流す。前景が画像の角に触れていると
    開始点 (0,0) が前景になり、floodFill が何も塗らず**全面が穴と判定される**
    （裾が画像下端まで届く写真で実際に踏んだ）。"""
    h, w = m.shape
    pad = np.zeros((h + 2, w + 2), np.uint8)
    pad[1:-1, 1:-1] = m
    ff = pad.copy()
    cv2.floodFill(ff, np.zeros((h + 4, w + 4), np.uint8), (0, 0), 255)
    return m | cv2.bitwise_not(ff)[1:-1, 1:-1]


def largest(m):
    n, lab, stats, _ = cv2.connectedComponentsWithStats((m > 0).astype(np.uint8), 8)
    if n <= 1:
        return m
    big = 1 + int(np.argmax(stats[1:, cv2.CC_STAT_AREA]))
    return np.where(lab == big, 255, 0).astype(np.uint8)


def keep_big(m, min_ratio=0.004):
    """一定面積以上の成分を**すべて**残す。
    背面は帯（白い蝶結び）が胴を上下に分断するので、最大成分だけ残すと
    左袖しか生き残らない（実測）。小さな赤い本などは面積で落ちる。"""
    n, lab, stats, _ = cv2.connectedComponentsWithStats((m > 0).astype(np.uint8), 8)
    total = m.shape[0] * m.shape[1]
    out = np.zeros_like(m)
    for i in range(1, n):
        if stats[i, cv2.CC_STAT_AREA] >= total * min_ratio:
            out[lab == i] = 255
    return out


def head_mask(src, spec):
    """頭部は**おかっぱの形で塗る**（行ごとに幅を決める）。

    GrabCut を頭に掛ける案は捨てた。顔（H22 S25 V207）と机（H40 S11 V210）、髪（V63）と
    黒シャツ（V50）が色で分かれないため、矩形をわずかに動かすだけで
    「頭が丸ごと消える / 頭上に黒い柱が生える」の間を往復して収束しない（実測 4 回）。

    ⚠ 単純な楕円で塗ると **CG の頭が箱に見える**（写真と並べて判明）。実物のおかっぱは
    頭頂が丸く、頬から下はほぼ垂直に落ちて顎の下で終わり、毛先でわずかに内へ入る。
    その形を上から下へ幅の式で作る。色と質感はテクスチャ側が運ぶので、ここで要るのは輪郭だけ。"""
    H, W = src.shape[:2]
    cx, cy, rx, ry = spec[0] * W, spec[1] * H, spec[2] * W, spec[3] * H
    out = np.zeros((H, W), np.uint8)
    top, bot = cy - ry, cy + ry
    ROUND = 0.40          # ここまでが丸い頭頂。以降はほぼ垂直に落ちる
    for yy in range(max(0, int(top)), min(H, int(bot) + 1)):
        t = (yy - top) / max(1.0, bot - top)
        if t < ROUND:
            k = (ROUND - t) / ROUND
            w = rx * math.sqrt(max(0.0, 1.0 - k * k))
        else:
            k = (t - ROUND) / (1.0 - ROUND)
            w = rx * (1.0 - 0.20 * k * k)          # 毛先へ向けてわずかに絞る
        if w <= 1.0:
            continue
        cv2.line(out, (int(cx - w), yy), (int(cx + w), yy), 255, 1)
    return out


def build(name):
    src = cv2.imread(os.path.join(HERE, f"{name}.jpg"))
    H, W = src.shape[:2]

    robe = red_mask(src)
    k = max(5, W // 180) | 1
    robe = cv2.morphologyEx(robe, cv2.MORPH_CLOSE, np.ones((k, k), np.uint8))
    robe = cv2.morphologyEx(robe, cv2.MORPH_OPEN, np.ones((k // 2 | 1, k // 2 | 1), np.uint8))
    robe = keep_big(robe, 0.010)   # 本棚の赤い本を落とす

    head = head_mask(src, HEAD[name])
    hy, _hx = np.nonzero(head)
    head_top = int(hy.min()) if len(hy) else 0

    # 帯（背面の蝶結び）と首は赤を上下に分断する。**縦長のカーネル**で閉じて繋ぐ
    # ＝ 横に広い袖どうしを繋げずに、縦の分断だけを埋められる。
    # ⚠ close は**赤だけ**に掛ける。頭も一緒に閉じると、丸い頭頂が縦へ膨張して
    #    **平らな箱**になり、さらに画像の端では収縮で戻り切らず頭の上に柱が残る（実測）。
    vk = np.ones((max(9, int(H * 0.09)) | 1, max(3, int(W * 0.012)) | 1), np.uint8)
    full = cv2.bitwise_or(cv2.morphologyEx(robe, cv2.MORPH_CLOSE, vk), head)
    full[:head_top] = 0
    full = fill_holes(full)
    full = keep_big(full, 0.006)
    full = cv2.medianBlur(full, 9)
    cv2.imwrite(os.path.join(HERE, f"mask_{name}.png"), full)

    ys, xs = np.nonzero(full)
    info = dict(name=name, size=[W, H], ratio=round(float(full.mean() / 255), 3),
                bbox=[int(xs.min()), int(ys.min()), int(xs.max()), int(ys.max())])

    prev = src.copy()
    prev[full == 0] = (0, 200, 0)
    ph = 900
    cv2.imwrite(os.path.join(HERE, f"prev_{name}.jpg"),
                cv2.resize(prev, (int(W * ph / H), ph), interpolation=cv2.INTER_AREA),
                [cv2.IMWRITE_JPEG_QUALITY, 88])
    return info


print(json.dumps([build(n) for n in HEAD], ensure_ascii=False))
