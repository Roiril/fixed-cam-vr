"""博士の部屋の絵を、試作の舞台（room-stage.js）が使う層へ分ける。

    py -3.11 tools/visitor-ui/room-proto/build-layers.py

入力（gen/）:
    plate-a1.png       Codex が描いた部屋（博士とテレビ・テレビは消えている）
    plate-a1-lit.png   同じ部屋でテレビが点いた版（光の差だけを使う）
    ../doctor-centered-v1.png  博士の元の写真（口の動画の入力と同じ絵）
出力（layers/）:
    bg.jpg       博士を抜いた部屋（博士の後ろは周りの幕から埋める。ほぼ隠れる所）
    body.png     博士だけ（部屋の絵から切り抜いた全身・透過）
    face.jpg     博士の元の写真を部屋の絵の位置と倍率へ合わせる前の原寸（1672x941）
    light.jpg    テレビの光が部屋へ足す明るさ（点いた版 − 消えた版。小さく、ぼかしてある。博士の所は抜く）
    light-doc.jpg  同じ光の、博士に当たる分（博士の形の中だけ）
    glass.png    テレビのガラスの形（白い所が画面）
    tv-hi.jpg    テレビのまわりだけの精細な絵（gen/tv-hi-a1.png を部屋の絵の位置へ合わせたもの）
    doll-tilt.jpg  頭を傾けた人形の写真（gen/doll-tilt-a1.png の頭のまわりだけを元の写真へ戻したもの）
    room.json    寸法と位置
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

import cv2
import numpy as np

HERE = Path(__file__).resolve().parent
GEN = HERE / "gen"
OUT = HERE / "layers"

# 元の写真が部屋の絵に重なる位置（顔の窓の差が最小になる所を探して出した値）
FACE_RECT = (96, 126, 926, 521)
# 切り抜きの初期の箱（博士を囲む）
BODY_RECT = (200, 110, 700, 831)
LIGHT_SCALE = 4          # 光の層は 1/4 の大きさで持つ
# テレビの精細な絵が部屋の絵の中で占める矩形（Codex へ渡した切り出し。16:9）
TV_HI_RECT = (920, 275, 752, 423)
# 人形の頭の楕円（doll-file-v1.jpg の座標。中心 x, y・半径 x, y）
DOLL_HEAD = (643, 322, 78, 92)


def read(path: Path) -> np.ndarray:
    data = np.fromfile(str(path), dtype=np.uint8)
    img = cv2.imdecode(data, cv2.IMREAD_COLOR)
    if img is None:
        sys.exit(f"読めない: {path}")
    return img


def write(path: Path, img: np.ndarray, params=None) -> None:
    ok, buf = cv2.imencode(path.suffix, img, params or [])
    if not ok:
        sys.exit(f"書けない: {path}")
    tmp = path.with_suffix(path.suffix + ".tmp")
    buf.tofile(str(tmp))
    tmp.replace(path)


def body_matte(plate: np.ndarray) -> np.ndarray:
    h, w = plate.shape[:2]
    mask = np.full((h, w), cv2.GC_BGD, np.uint8)
    x, y, bw, bh = BODY_RECT
    mask[y:y + bh, x:x + bw] = cv2.GC_PR_FGD
    # 確かに博士の所（顔・胸・胴）
    cv2.ellipse(mask, (560, 330), (95, 125), 0, 0, 360, cv2.GC_FGD, -1)
    cv2.rectangle(mask, (330, 620), (800, h - 1), cv2.GC_FGD, -1)
    cv2.rectangle(mask, (470, 440), (650, 640), cv2.GC_FGD, -1)
    # 確かに幕の所（箱の中の上の両隅）
    cv2.rectangle(mask, (x, y), (x + 150, y + 250), cv2.GC_BGD, -1)
    cv2.rectangle(mask, (x + bw - 150, y), (x + bw, y + 250), cv2.GC_BGD, -1)
    bgd = np.zeros((1, 65), np.float64)
    fgd = np.zeros((1, 65), np.float64)
    cv2.grabCut(plate, mask, None, bgd, fgd, 6, cv2.GC_INIT_WITH_MASK)
    fg = np.where((mask == cv2.GC_FGD) | (mask == cv2.GC_PR_FGD), 255, 0).astype(np.uint8)
    # いちばん大きい塊だけ残し、穴を塞ぐ
    n, labels, stats, _ = cv2.connectedComponentsWithStats(fg, 8)
    if n > 1:
        keep = 1 + int(np.argmax(stats[1:, cv2.CC_STAT_AREA]))
        fg = np.where(labels == keep, 255, 0).astype(np.uint8)
    fg = cv2.morphologyEx(fg, cv2.MORPH_CLOSE, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (9, 9)))
    return fg


def main() -> None:
    OUT.mkdir(exist_ok=True)
    plate = read(GEN / "plate-a1.png")
    h, w = plate.shape[:2]
    matte = body_matte(plate)

    # 髪は幕と同じ暗さで境目が決まらない。縁を少し広げてから、やわらかく落とす
    soft = cv2.dilate(matte, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (7, 7)))
    soft = cv2.GaussianBlur(soft, (0, 0), 3.0)
    body = np.dstack([plate, soft])
    ys, xs = np.where(soft > 2)
    bx0, bx1, by0, by1 = int(xs.min()), int(xs.max()) + 1, int(ys.min()), int(ys.max()) + 1
    write(OUT / "body.png", body[by0:by1, bx0:bx1])

    # 博士の後ろを周りの幕から埋める
    hole = cv2.dilate(matte, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (41, 41)))
    bg = cv2.inpaint(plate, hole, 9, cv2.INPAINT_TELEA)
    # 埋めた所は縦の襞が流れるので、縦に強くぼかして襞の向きへ揃える
    streak = cv2.blur(bg, (3, 61))
    hole_f = cv2.GaussianBlur(hole, (0, 0), 6).astype(np.float32)[..., None] / 255.0
    bg = (bg * (1 - hole_f) + streak * hole_f).astype(np.uint8)
    write(OUT / "bg.jpg", bg, [cv2.IMWRITE_JPEG_QUALITY, 92])

    face = read(HERE.parent / "doctor-centered-v1.png")
    write(OUT / "face.jpg", face, [cv2.IMWRITE_JPEG_QUALITY, 93])

    room = {
        "plate": {"w": w, "h": h},
        "face": dict(zip("xywh", FACE_RECT)),
        "faceSource": {"w": face.shape[1], "h": face.shape[0]},
        "body": {"x": bx0, "y": by0, "w": bx1 - bx0, "h": by1 - by0},
    }

    lit_path = GEN / "plate-a1-lit.png"
    if lit_path.exists():
        lit = read(lit_path)
        if lit.shape != plate.shape:
            lit = cv2.resize(lit, (w, h), interpolation=cv2.INTER_AREA)
        diff = np.clip(lit.astype(np.int16) - plate.astype(np.int16), 0, 255).astype(np.uint8)
        gray = cv2.cvtColor(diff, cv2.COLOR_BGR2GRAY)
        # ガラス: 差がいちばん大きい、右側の大きな塊
        _, hot = cv2.threshold(gray, 90, 255, cv2.THRESH_BINARY)
        hot[:, : w // 2] = 0
        hot = cv2.morphologyEx(hot, cv2.MORPH_CLOSE, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (15, 15)))
        n, labels, stats, _ = cv2.connectedComponentsWithStats(hot, 8)
        if n <= 1:
            sys.exit("テレビのガラスが見つからない（点いた版の画面が光っていない）")
        keep = 1 + int(np.argmax(stats[1:, cv2.CC_STAT_AREA]))
        glass = np.where(labels == keep, 255, 0).astype(np.uint8)
        gx, gy, gw, gh = (int(stats[keep, k]) for k in (cv2.CC_STAT_LEFT, cv2.CC_STAT_TOP, cv2.CC_STAT_WIDTH, cv2.CC_STAT_HEIGHT))
        glass_soft = cv2.GaussianBlur(glass, (0, 0), 1.2)
        crop = glass_soft[gy:gy + gh, gx:gx + gw]
        write(OUT / "glass.png", np.dstack([np.full_like(crop, 255)] * 3 + [crop]))   # 形は透過で持つ（舞台が型抜きに使う）
        room["glass"] = {"x": gx, "y": gy, "w": gw, "h": gh}
        # 光: ガラスの中は抜く（画面そのものは舞台が描く）。顔の作りが変わった分を拾わないよう、大きくぼかす
        # 光は 2 枚に分ける。部屋の分（博士の所は抜く）と、博士の分（博士の形の中だけ）。
        # 1 枚にすると、ぼかした光が博士の輪郭の外へはみ出して縁取りに見えた
        diff = np.clip(diff.astype(np.int16) - 5, 0, 255).astype(np.uint8)   # 描き直しで出た小さな差は捨てる
        cut = cv2.dilate(glass, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (5, 5)))
        body_cut = cv2.dilate(matte, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (25, 25)))
        hole2 = (cut > 0) | (body_cut > 0)
        light = diff.copy()
        light[hole2] = 0
        # ぼかすと抜いた所の縁が暗く引かれるので、抜いた分で割り戻す
        keep_w = cv2.GaussianBlur((~hole2).astype(np.float32), (0, 0), 5.0)[..., None]
        fill = cv2.GaussianBlur(light.astype(np.float32), (0, 0), 5.0) / np.maximum(keep_w, 0.2)
        fill[cut > 0] = 0
        # ガラスのまわり（枠の内側の縁）には光を足さない。点いた版では枠の内側が青白く光っており、
        # それを割り戻しで持ち上げた分が、画面と枠の間の青い枠に見えた（0341）。ガラスから 30 画素ほどかけて戻す
        near = cv2.dilate(glass, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (45, 45)))
        fill *= 1.0 - cv2.GaussianBlur(near, (0, 0), 9.0).astype(np.float32)[..., None] / 255.0
        fill = np.clip(cv2.GaussianBlur(fill, (0, 0), 1.5), 0, 255).astype(np.uint8)
        small = cv2.resize(fill, (w // LIGHT_SCALE, h // LIGHT_SCALE), interpolation=cv2.INTER_AREA)
        write(OUT / "light.jpg", small, [cv2.IMWRITE_JPEG_QUALITY, 90])
        doc_light = cv2.GaussianBlur(diff, (0, 0), 2.5).astype(np.float32) * (soft.astype(np.float32)[..., None] / 255.0)
        doc_light = np.clip(doc_light, 0, 255).astype(np.uint8)[by0:by1, bx0:bx1]
        doc_light = cv2.resize(doc_light, ((bx1 - bx0) // 2, (by1 - by0) // 2), interpolation=cv2.INTER_AREA)
        write(OUT / "light-doc.jpg", doc_light, [cv2.IMWRITE_JPEG_QUALITY, 90])
        # 点いた版の画面の平均の色（光の層は、この色で照らされた部屋）
        mean = [float(v) for v in cv2.mean(lit, mask=glass)[:3]][::-1]
        room["lightSource"] = {"rgb": [round(v, 1) for v in mean]}

    # テレビのまわりの精細な絵（Codex が切り出しを描き直したもの）。ずれを測って部屋の絵の位置へ合わせる
    hi_path = GEN / "tv-hi-a1.png"
    if hi_path.exists():
        tx, ty, tw, th = TV_HI_RECT
        hi = read(hi_path)
        ref = cv2.resize(plate[ty:ty + th, tx:tx + tw], (hi.shape[1], hi.shape[0]), interpolation=cv2.INTER_CUBIC)
        g_ref = cv2.GaussianBlur(cv2.cvtColor(ref, cv2.COLOR_BGR2GRAY), (0, 0), 3).astype(np.float32)
        g_hi = cv2.GaussianBlur(cv2.cvtColor(hi, cv2.COLOR_BGR2GRAY), (0, 0), 3).astype(np.float32)
        warp = np.eye(2, 3, dtype=np.float32)
        cc, warp = cv2.findTransformECC(g_ref, g_hi, warp, cv2.MOTION_AFFINE,
                                        (cv2.TERM_CRITERIA_EPS | cv2.TERM_CRITERIA_COUNT, 200, 1e-6), None, 5)
        fitted = cv2.warpAffine(hi, warp, (hi.shape[1], hi.shape[0]), flags=cv2.INTER_LINEAR | cv2.WARP_INVERSE_MAP, borderMode=cv2.BORDER_REPLICATE)
        # 明るさを部屋の絵へ合わせる（色ごとの平均の比）
        gain = (ref.reshape(-1, 3).mean(axis=0) + 1) / (fitted.reshape(-1, 3).mean(axis=0) + 1)
        fitted = np.clip(fitted.astype(np.float32) * gain, 0, 255).astype(np.uint8)
        write(OUT / "tv-hi.jpg", fitted, [cv2.IMWRITE_JPEG_QUALITY, 92])
        room["tvHi"] = {"x": tx, "y": ty, "w": tw, "h": th}
        room["tvHiFit"] = {"corr": round(float(cc), 4), "shift": [round(float(warp[0, 2]), 2), round(float(warp[1, 2]), 2)],
                           "scale": [round(float(warp[0, 0]), 4), round(float(warp[1, 1]), 4)], "gain": [round(float(v), 3) for v in gain]}

    # 頭を傾けた人形（Codex）。頭のまわりだけを元の写真へ戻す（ほかの所が 1 画素も動かないように）
    tilt_path = GEN / "doll-tilt-a1.png"
    if tilt_path.exists():
        base = read(GEN / "doll-in.jpg")
        tilt = cv2.resize(read(tilt_path), (base.shape[1], base.shape[0]), interpolation=cv2.INTER_AREA)
        m = np.zeros(base.shape[:2], np.uint8)
        cv2.ellipse(m, DOLL_HEAD[:2], DOLL_HEAD[2:], 0, 0, 360, 255, -1)
        mf = cv2.GaussianBlur(m, (0, 0), 7).astype(np.float32)[..., None] / 255.0
        out = (base * (1 - mf) + tilt * mf).astype(np.uint8)
        write(OUT / "doll-tilt.jpg", out, [cv2.IMWRITE_JPEG_QUALITY, 93])
        outside = np.abs(base.astype(np.int16) - tilt.astype(np.int16)).mean(axis=2)[m == 0].mean()
        room["dollTilt"] = {"file": "doll-tilt.jpg", "outsideDiff": round(float(outside), 2)}

    (OUT / "room.json").write_text(json.dumps(room, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(room, ensure_ascii=False))


if __name__ == "__main__":
    main()
