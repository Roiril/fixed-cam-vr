# -*- coding: utf-8 -*-
"""**実在の市松人形**の資料を、写真から機械的に作る。

```
py -3.11 tools/doll-ref/build.py           # 板・寄り・色見本・measured.json
py -3.11 tools/doll-ref/build.py --grid    # 目盛を重ねた確認用（探針を置き直すとき）
```

出るもの（`tools/doll-ref/out/`）:

| | 何 |
|---|---|
| `plate_front/back/sideA/sideB.jpg` | 写真を人形の範囲へ切っただけの板。**これが正本** |
| `detail_*.jpg` | 顔・帯・帯結び・袖口・裾・手の寄り |
| `palette.png` `measured.json` | 写真から測った色。**生成の結果ではない** |

⚠⚠ **0 から作るキャラクターではない。** 実在の人形（`tools/doll-model/photos/`）を
そのまま資料にするのが目的で、良く見せる方向へ直さない。作り方は
`C:/Users/kouga/Projects/paperdoll/chara/` を参考にしたが、**正本の出どころが逆**:

| | paperdoll | ここ |
|---|---|---|
| 正本（identity の source of truth） | 生成した設定画 | **実物の写真** |
| 色 | 定義の palette へ寄せる | **写真から測る** |
| 生成の役割 | キャラクターを作る | **写真に無い角度・状態を補う**（参考であって正本ではない） |

⚠⚠ **背景は落とさない。** `tools/doll-model/mask_*.png`（`cutout2.py` の出力）を試したが、
**資料としては使えなかった**（実測・2026-08-15）:

- `back` はマスクが 2 つに割れていて、いちばん大きい塊が**袖と裾だけ**
- `sideA` / `sideB` は頭のまわりに**背景の矩形**が残る
- `front` は**手が落ちている**（撮影者の指と一緒に切られた）・脚の間に背景の黒が入る
- どの向きも下端が**台と机**を含む（最下部の数 % は一定幅の柱になる）

あれは 3D 化のためのシルエット用で、精度の要求が違う。**切り抜きが要るなら生成側でやる**
（`PROMPT.md` の「板の使い方」）。ここは切り出す範囲だけをマスクの外接矩形から取る。
"""
from __future__ import annotations

import argparse
import json
import os
import sys

import cv2
import numpy as np

sys.stdout.reconfigure(encoding="utf-8")

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(os.path.dirname(HERE), "doll-model")
OUT = os.path.join(HERE, "out")

VIEWS = ("front", "back", "sideA", "sideB")

# 板の高さ（画素）。長辺 1600 は生成 AI へ添付するのにちょうどよく、顔の情報も残る。
PLATE_H = 1600

# ---- 切り出す範囲（マスクの外接矩形からの微調整・**目で見て決めた**）------------
#   pad / pad_x … 外接矩形の外側へ広げる割合（人形の高さに対して）
#   cut_bottom  … 下端から捨てる割合。**台と机が写り込んでいる分**
#
# ⚠ **正面と背面は横へ大きく広げる。** `cutout2.py` のマスクは撮影者の指と一緒に
#    **手を切り落としている**ので、外接矩形のままだと T 字に開いた腕の先が板に入らない。
#    白磁の手はこの人形の見分けの 1 つなので、写真の端まで取る（はみ出しは clamp）。
TRIM = {
    "front": {"pad": 0.01, "pad_x": 0.14, "cut_bottom": 0.055},
    "back": {"pad": 0.01, "pad_x": 0.10, "cut_bottom": 0.030},
    "sideA": {"pad": 0.01, "pad_x": 0.02, "cut_bottom": 0.055},
    "sideB": {"pad": 0.01, "pad_x": 0.02, "cut_bottom": 0.050},
}

# ---- 寄り（正面 / 背面の板の**正規化座標** x0,y0,x1,y1）------------------------
DETAILS = [
    ("face", "front", 0.30, 0.00, 0.70, 0.20, "顔。白磁の照り・眉・眼・唇・おかっぱの前髪"),
    ("collar", "front", 0.30, 0.17, 0.72, 0.34, "襟元。生成りの半襟・桃色の絞り・金の飾り紐と房"),
    ("obi", "front", 0.30, 0.30, 0.72, 0.46, "帯（前）。萌黄地に菊と唐草、赤の差し色"),
    ("hem", "front", 0.28, 0.62, 0.75, 1.00, "裾。前身頃の合わせと裏地の生成り"),
    ("sleeve", "front", 0.00, 0.24, 0.26, 0.55, "袖。地紋と金の粒、袖口の生成りの帯"),
    ("obi_back", "back", 0.31, 0.22, 0.72, 0.44, "帯結び（後）。文庫。銀糸の織り"),
    ("nape", "back", 0.30, 0.00, 0.72, 0.28, "後頭部。おかっぱの切り口と襟足"),
]

# ---- 色を測る探針（`plate_front.jpg` の正規化座標・半径は高さ比）----------------
# ⚠⚠ **机上で置かない。** `--grid` で目盛を焼いて、板を見ながら置く。
#    最初に当てずっぽうで置いた 11 点のうち **6 点が別のものの上に落ちていた**
#    （眉が顔、帯が袖の影、手が着物）。中央値なので少しの外れは効かないが、
#    場所を外すと**その色が丸ごと嘘になる**。
PROBES = [
    ("髪",               0.550, 0.035, 0.012),
    ("顔（白磁）",       0.455, 0.175, 0.010),
    ("眉",               0.452, 0.083, 0.004),
    ("眼（虹彩）",       0.448, 0.115, 0.006),
    ("唇",               0.497, 0.177, 0.0035),
    ("半襟（生成り）",   0.468, 0.262, 0.005),
    ("絞り（桃）",       0.468, 0.338, 0.004),
    ("飾り紐（金）",     0.572, 0.320, 0.0025),
    ("房（白）",         0.599, 0.410, 0.004),
    ("帯（萌黄）",       0.547, 0.465, 0.006),
    ("着物（赤・身頃）", 0.600, 0.650, 0.030),
    ("着物（赤・袖）",   0.250, 0.620, 0.035),
    ("袖口（生成り）",   0.127, 0.347, 0.003),
    ("手（白磁）",       0.077, 0.327, 0.005),
]

# ---- 柄のあるところは「1 点」で測れない（矩形の中の**主要な色**を出す）------------
#
# ⚠ 絞りは桃と生成りの鹿の子、帯は萌黄地に金の菊と赤の差し色、着物は赤地に金の粒。
#    探針を 1 つ置くと**どちらか片方だけ**が出て、しかもどちらが出たかは目で見ないと分からない
#    （実際に絞りで 3 回外した — 赤・生成り・赤）。**面積の割合ごと出す。**
REGIONS = [
    ("胸元の絞り", 0.42, 0.30, 0.62, 0.40, 2),
    ("帯（前）",   0.42, 0.44, 0.66, 0.53, 3),
    ("着物（赤地）", 0.42, 0.60, 0.68, 0.85, 2),
]


def load(view: str):
    img = cv2.imread(os.path.join(SRC, "photos", f"{view}.jpg"))
    mask = cv2.imread(os.path.join(SRC, f"mask_{view}.png"), cv2.IMREAD_GRAYSCALE)
    if img is None:
        raise FileNotFoundError(f"{view}: 写真が無い（tools/doll-model/photos/）")
    return img, mask


def plate(view: str) -> np.ndarray:
    """写真を人形の範囲へ切り、**全高を PLATE_H へ揃える**。背景は落とさない。"""
    img, mask = load(view)
    if mask is None or mask.shape[:2] != img.shape[:2]:
        raise ValueError(f"{view}: マスクが無い / 寸法が違う")
    # ⚠ **全部の塊の和**を取る（`back` はマスクが割れていて、最大の塊は袖と裾だけ）。
    ys, xs = np.nonzero(mask > 127)
    y0, y1, x0, x1 = int(ys.min()), int(ys.max()) + 1, int(xs.min()), int(xs.max()) + 1
    h = y1 - y0
    t = TRIM[view]
    pad, padx = int(h * t["pad"]), int(h * t["pad_x"])
    y1 -= int(h * t["cut_bottom"])
    y0, x0 = max(0, y0 - pad), max(0, x0 - padx)
    y1, x1 = min(img.shape[0], y1 + pad), min(img.shape[1], x1 + padx)

    crop = img[y0:y1, x0:x1]
    s = PLATE_H / crop.shape[0]
    return cv2.resize(crop, (max(1, int(round(crop.shape[1] * s))), PLATE_H),
                      interpolation=cv2.INTER_AREA)


def with_grid(im: np.ndarray) -> np.ndarray:
    """0.1 刻みの目盛。**探針を置き直すときだけ**使う。"""
    g = im.copy()
    h, w = g.shape[:2]
    for i in range(1, 10):
        x, y = int(w * i / 10), int(h * i / 10)
        cv2.line(g, (x, 0), (x, h), (0, 180, 255), 1)
        cv2.line(g, (0, y), (w, y), (0, 180, 255), 1)
        cv2.putText(g, f"{i / 10:.1f}", (x + 3, 16), cv2.FONT_HERSHEY_SIMPLEX, 0.45, (0, 120, 255), 1)
        cv2.putText(g, f"{i / 10:.1f}", (3, y - 4), cv2.FONT_HERSHEY_SIMPLEX, 0.45, (0, 120, 255), 1)
    return g


def probe(im: np.ndarray, nx: float, ny: float, nr: float):
    """正規化座標の円の**中央値**（(R,G,B), 画素数）。平均だと縁や金の粒に引かれる。"""
    h, w = im.shape[:2]
    cx, cy, r = int(nx * w), int(ny * h), max(2, int(nr * h))
    yy, xx = np.ogrid[:h, :w]
    sel = (xx - cx) ** 2 + (yy - cy) ** 2 <= r * r
    px = im[sel]
    if len(px) < 8:
        return None, 0
    med = np.median(px, axis=0)
    return (int(med[2]), int(med[1]), int(med[0])), int(len(px))


def hexs(rgb) -> str:
    return "#{:02x}{:02x}{:02x}".format(*rgb)


# ---- 撮影の癖を打ち消す（白の基準）----------------------------------------------
#
# ⚠⚠ **写真は暗くて緑に転んでいる。** 人形が乗っている白い机を測ると `#c1c9c0`
#    （白なら 235 前後・しかも G が R/B より 8〜10 高い）。**測った生の値をそのまま
#    「この人形の色」として渡すと、暗く緑がかった別の人形になる。**
#
# だから机の白を基準にした補正値も併記する。**生の値は消さない** — 補正は推定であって、
# 実物を測り直したわけではない（正しくやるならグレーカードを一緒に撮る）。
WHITE_REF = [(0.02, 0.93, 0.18, 0.99), (0.82, 0.93, 0.98, 0.99)]   # 机（板の正規化座標）
WHITE_TARGET = 235.0


def white_gain(im):
    """机の白から、チャンネルごとの倍率（B, G, R）を出す。"""
    h, w = im.shape[:2]
    px = np.concatenate([im[int(y0 * h):int(y1 * h), int(x0 * w):int(x1 * w)].reshape(-1, 3)
                         for x0, y0, x1, y1 in WHITE_REF])
    med = np.median(px, axis=0)
    return (WHITE_TARGET / np.maximum(med, 1.0)), med


def corrected(rgb, gain) -> str:
    """(R,G,B) に倍率（B,G,R 順）を掛けた HEX。"""
    return hexs((min(255, int(round(rgb[0] * gain[2]))),
                 min(255, int(round(rgb[1] * gain[1]))),
                 min(255, int(round(rgb[2] * gain[0])))))


def region_colors(im: np.ndarray, x0, y0, x1, y1, k: int):
    """矩形の中の主要な色を k 個、**面積の割合つき**で返す（多い順）。"""
    h, w = im.shape[:2]
    box = im[int(y0 * h):int(y1 * h), int(x0 * w):int(x1 * w)].reshape(-1, 3).astype(np.float32)
    box = box[::5]
    crit = (cv2.TERM_CRITERIA_EPS + cv2.TERM_CRITERIA_MAX_ITER, 24, 1.0)
    cv2.setRNGSeed(7)                       # **決定論**（走行ごとに色が変わらない）
    _, lab, cen = cv2.kmeans(box, k, None, crit, 5, cv2.KMEANS_PP_CENTERS)
    out = []
    for i in range(k):
        share = float((lab == i).mean())
        b, g, r = cen[i]
        out.append({"hex": hexs((int(r), int(g), int(b))), "rgb": [int(r), int(g), int(b)],
                    "share": round(share, 3)})
    return sorted(out, key=lambda d: -d["share"])


def probe_map(im: np.ndarray, probes, regions=()) -> np.ndarray:
    """探針がどこに落ちたかを描く。**測った色が信用できるかは、この 1 枚でしか分からない。**"""
    g = im.copy()
    h, w = g.shape[:2]
    for name, x0, y0, x1, y1, _k in regions:
        cv2.rectangle(g, (int(x0 * w), int(y0 * h)), (int(x1 * w), int(y1 * h)), (0, 0, 0), 4)
        cv2.rectangle(g, (int(x0 * w), int(y0 * h)), (int(x1 * w), int(y1 * h)), (255, 255, 255), 2)
    for i, p in enumerate(probes):
        if p["at"] is None:
            continue
        nx, ny, nr = p["at"]
        h, w = g.shape[:2]
        c = (int(nx * w), int(ny * h))
        r = max(3, int(nr * h))
        cv2.circle(g, c, r, (0, 0, 0), 3)
        cv2.circle(g, c, r, (255, 255, 255), 1)
        cv2.putText(g, str(i + 1), (c[0] + r + 4, c[1] + 5),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.7, (0, 0, 0), 4)
        cv2.putText(g, str(i + 1), (c[0] + r + 4, c[1] + 5),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.7, (255, 255, 255), 1)
    return g


def palette_sheet(entries) -> np.ndarray:
    """色見本。**生成しない**（paperdoll の `color` と同じ — 測った値を機械で描く）。"""
    cell, padx, pady, cols = 200, 44, 44, 5
    rows = (len(entries) + cols - 1) // cols
    w = padx + cols * (cell + padx)
    h = pady + rows * (cell + 100)
    sheet = np.full((h, w, 3), 238, np.uint8)
    for i, e in enumerate(entries):
        cx = padx + (i % cols) * (cell + padx)
        cy = pady + (i // cols) * (cell + 100)
        r, g, b = e["rgb"]
        cv2.rectangle(sheet, (cx, cy), (cx + cell, cy + cell), (b, g, r), -1)
        cv2.rectangle(sheet, (cx, cy), (cx + cell, cy + cell), (170, 168, 164), 2)
        wb = e.get("hex_wb")
        if wb:
            c = tuple(int(wb[j:j + 2], 16) for j in (1, 3, 5))
            cv2.rectangle(sheet, (cx, cy + cell // 2), (cx + cell, cy + cell), (c[2], c[1], c[0]), -1)
            cv2.rectangle(sheet, (cx, cy), (cx + cell, cy + cell), (170, 168, 164), 2)
            cv2.line(sheet, (cx, cy + cell // 2), (cx + cell, cy + cell // 2), (255, 255, 255), 1)
        cv2.putText(sheet, f"{i + 1}. {e['hex']}", (cx, cy + cell + 26),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.55, (40, 40, 40), 2)
        if wb:
            cv2.putText(sheet, f"   {wb}", (cx, cy + cell + 52),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.55, (120, 120, 120), 2)
    return sheet


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--grid", action="store_true", help="目盛つきの板も出す（探針の置き直し用）")
    a = ap.parse_args()

    os.makedirs(OUT, exist_ok=True)
    plates = {}
    for v in VIEWS:
        p = plate(v)
        plates[v] = p
        cv2.imwrite(os.path.join(OUT, f"plate_{v}.jpg"), p, [cv2.IMWRITE_JPEG_QUALITY, 92])
        if a.grid:
            cv2.imwrite(os.path.join(OUT, f"grid_{v}.jpg"), with_grid(p),
                        [cv2.IMWRITE_JPEG_QUALITY, 88])
        print(f"  plate_{v}.jpg  {p.shape[1]}x{p.shape[0]}")

    for name, view, x0, y0, x1, y1, note in DETAILS:
        im = plates[view]
        h, w = im.shape[:2]
        d = im[int(y0 * h):int(y1 * h), int(x0 * w):int(x1 * w)]
        cv2.imwrite(os.path.join(OUT, f"detail_{name}.jpg"), d, [cv2.IMWRITE_JPEG_QUALITY, 94])
        print(f"  detail_{name}.jpg  {d.shape[1]}x{d.shape[0]}  {note}")

    front = plates["front"]
    probes = []
    for name, nx, ny, nr in PROBES:
        rgb, n = probe(front, nx, ny, nr)
        if rgb is None:
            print(f"  ⚠ 探針が板の外: {name}")
            continue
        probes.append({"name": name, "hex": hexs(rgb), "rgb": list(rgb), "px": n,
                       "at": [nx, ny, nr]})
    regions = []
    for name, x0, y0, x1, y1, k in REGIONS:
        cols = region_colors(front, x0, y0, x1, y1, k)
        regions.append({"name": name, "box": [x0, y0, x1, y1], "colors": cols})
        for j, c in enumerate(cols):
            probes.append({"name": f"{name}・{j + 1}（面積 {int(c['share'] * 100)}%）",
                           "hex": c["hex"], "rgb": c["rgb"], "px": -1, "at": None})

    gain, white_med = white_gain(front)
    for e in probes:
        e["hex_wb"] = corrected(e["rgb"], gain)

    cv2.imwrite(os.path.join(OUT, "palette.png"), palette_sheet(probes))
    cv2.imwrite(os.path.join(OUT, "probe_map.jpg"), probe_map(front, probes, REGIONS),
                [cv2.IMWRITE_JPEG_QUALITY, 90])
    print("  palette.png / probe_map.jpg")

    measured = {
        "note": "実物の写真から測った値。**生成の結果ではない**。tools/doll-ref/build.py が書く",
        "source": "tools/doll-model/photos/*.jpg（切り出す範囲だけ mask_*.png の外接矩形から）",
        "plate_height_px": PLATE_H,
        "views": {v: {"w": int(plates[v].shape[1]), "h": int(plates[v].shape[0])} for v in VIEWS},
        "probes": [p for p in probes if p["at"] is not None],
        "regions": regions,
        "white_balance": {
            "note": "机の白を 235 とみなした倍率。**推定**であって測り直した色ではない",
            "measured_bgr": [round(float(v), 1) for v in white_med],
            "gain_bgr": [round(float(v), 3) for v in gain],
        },
    }
    with open(os.path.join(OUT, "measured.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(measured, f, ensure_ascii=False, indent=2)

    print(f"\n机の白: BGR {[round(float(v)) for v in white_med]} → 倍率 "
          f"{[round(float(v), 2) for v in gain]}（**写真は暗く緑に転んでいる**）")
    print("測った色（生 / 机の白で合わせた推定）:")
    for i, p in enumerate(probes):
        print(f"   {i + 1:2d}. {p['name']:24s} {p['hex']} / {p['hex_wb']}")
    print(f"\n→ {OUT}\n⚠ `probe_map.jpg` を開いて、探針が狙った物の上に乗っているか必ず見る。")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
