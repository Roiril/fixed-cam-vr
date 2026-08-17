#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""AIエージェントの顔（スイ）を、連絡の面へ出す 1 枚の版へ焼く。

    py -3.11 tools/make-comms-face.py            # → Assets/Resources/Comms/SuiFace.png
    py -3.11 tools/make-comms-face.py --parts    # 分解した層を並べた検分用の 1 枚も出す

出どころ
--------
素材は **paperdoll で作った「スイ」**（`C:/Users/kouga/Projects/paperdoll/chara/out/sui/`）の
表情差分の 1 枚目（素）。取り込んだ切り抜きが `tools/comms-face/sui-neutral.png` で、
**paperdoll が無くても焼き直せる**ようにここへ置いてある（設定は `chara/characters/sui.md`）。

ユーザー指定（2026-08-17・`canon/LEDGER.md` 0071）:
「輪郭だけ抽出し、髪は輪郭と同じ色で塗りつぶし、背景はない感じ」。

やり方は 2 行
-------------
    墨 ＝ 頭の外形を全部塗る − 元の絵の線
    背景（頭の外）は透明

**元の絵の線をそのまま「彫り込む」**。線は元の絵で暗い所なので、拾うのは明るさの閾値 1 つだけ。
髪の分け目・前髪の束・睫毛・瞳・鼻・口・顎の影が、**描かれたとおりの形で**残る。

⚠⚠ **顔の肌まで塗るのは誤りではない。** 参考のスクショは「白い紙 ＋ 青い墨」で、肌は紙の色。
この面は**暗い地に明るい墨**なので、明暗が逆になる ＝ **肌を墨で塗ったものが参考の紙にあたる**。
肌を抜くと（＝暗いまま残すと）顔が黒い穴になり、目も口も読めない
（2026-08-17 に 6 通り試して全部そうなった。層を分けて塗り分ける方向は全滅）。

⚠⚠ **実機では 110 画素そこそこにしかならない。**
面の枠は 1.5m 先の 0.15m 角 ＝ 見かけ 5.7°、Quest 3 は視野中心でおよそ 20 画素/度。
だから**線は必ず太らせる**（<see cref="LINE_W"/>）。細いまま焼くと縮小で溶けて灰色の靄になる。
閾値を上げて線を増やすのも同じ理由で駄目で、髪が糸くずの束に見える（`--parts` の絵で分かる）。

⚠ **数値が通っても絵は必ず開く**（`~/.claude/rules/work-style.md` §2）。
このスクリプトは壊れた顔でも黙って通る。`--parts` の 1 枚を見るまでが 1 周。
"""
from __future__ import annotations

import argparse
import sys
from pathlib import Path

import cv2
import numpy as np
from PIL import Image

sys.stdout.reconfigure(encoding="utf-8")

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "tools" / "comms-face" / "sui-neutral.png"
OUT = ROOT / "Assets" / "Resources" / "Comms" / "SuiFace.png"
PARTS = ROOT / "logs" / "comms-face" / "parts.png"

# ---- 切り抜き（`sui-neutral.png` の画素）------------------------------------------
# 髪の天辺 〜 顎の少し下。⚠ **外套から下は入れない** — 濃紺の塊なので、
# 入れると顔ではなく「黒い台形」が枠を埋める。首の途中で切れるのは肖像として普通の形。
CROP = (8, 4, 392, 306)          # (x0, y0, x1, y1)

# ---- 判定の線（素材の実測。外したら `--parts` の絵を見て直す）----------------------
BG_TOL = 10.0        # 背景（生成りの無地）からこれだけ離れたら「物がある」
#: 線とみなす明るさの上限。実測: 肌 231 / 髪 217 / 背景 245 / 睫毛 30〜90。
#: ⚠ **上げると髪が糸くずになる**（195 で束が全部ばらける）。165〜175 が持ち場。
LINE_MAX_L = 168.0
#: **目の中だけ**は緩める。瞳は青で明るく（実測 159）、この値のままだと睫毛の 1 本線しか
#: 彫れない。ここを上げると瞳ごと彫れて、目がはっきりする。
#: ⚠ 全体を上げてはいけない（髪の束がばらけて糸くずになる）。**目の周りに限る**。
#: ⚠⚠ **200 → 175 へ下げた**（2026-08-17・ユーザー赤入れ「キャラクターの目が少し怖い」）。
#: 200 だと瞳と睫毛と下瞼が 1 つの黒い塊になり、**目ではなく落ち窪んだ眼窩**に見える。
#: 175 なら睫毛と瞳の芯だけが残って、元の絵の伏し目がちな目つきがそのまま出る。
EYE_LINE_MAX_L = 175.0
#: 目の中の線を太らせる量。⚠ <see cref="LINE_W"/> と**別にしてある** — 目は元から
#: 面積があるので、髪と同じだけ太らせると隣の線と繋がって塊になる（上と同じ赤入れ）。
EYE_LINE_W = 1
#: 目の芯（睫毛）とみなす暗さ。⚠ 顔の中でここまで暗いのは睫毛と瞳だけ（肌 231 / 髪 217）。
EYE_CORE_MAX_L = 115.0
EYE_CORE_MIN_AREA = 120   # これ未満の暗い塊は睫毛ではない
EYE_ZONE_TOP = 0.72       # 目を探す縦の範囲（外形の高さに対する割合）。⚠ 顎の影を拾わない線
EYE_ZONE_PAD = 9          # 芯から瞳へ広げる幅（素材の画素）
LINE_MIN_AREA = 25   # これ未満の暗い点は線ではない（粒・にじみ）
LINE_W = 2           # 線を太らせる半径（素材の画素）。⚠ 実機の 110 画素で残る太さ
RIM_KEEP = 2         # 外形の縁は必ず墨で残す幅。⚠ 無いと輪郭が線に食われて頭が欠ける
INK_MIN_AREA = 60    # 彫ったあとに残る墨の島の下限

# ---- 焼き上がり ----------------------------------------------------------------
SIZE = 256           # 版の 1 辺（正方形）
MARGIN = 0.06        # 枠の内側に取る余白（1 辺に対する割合）
SUPERSAMPLE = 4      # 縮小前の倍率。縁のなめらかさはここで決まる


def _disk(r: int) -> np.ndarray:
    k = max(1, int(r) * 2 + 1)
    return cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (k, k))


def _largest(mask: np.ndarray) -> np.ndarray:
    n, lab, st, _ = cv2.connectedComponentsWithStats(mask.astype(np.uint8), 8)
    if n <= 1:
        return mask.astype(np.uint8)
    return (lab == 1 + int(np.argmax(st[1:, cv2.CC_STAT_AREA]))).astype(np.uint8)


def _drop_specks(mask: np.ndarray, min_area: int) -> np.ndarray:
    n, lab, st, _ = cv2.connectedComponentsWithStats(mask.astype(np.uint8), 8)
    out = np.zeros_like(mask, dtype=np.uint8)
    for i in range(1, n):
        if st[i, cv2.CC_STAT_AREA] >= min_area:
            out[lab == i] = 1
    return out


def _fill_holes(mask: np.ndarray) -> np.ndarray:
    """外から届かない穴を埋める（髪の隙間から覗く背景など）。"""
    h, w = mask.shape
    ff = mask.astype(np.uint8).copy()
    pad = np.zeros((h + 2, w + 2), np.uint8)
    cv2.floodFill(ff, pad, (0, 0), 1)
    return (mask.astype(np.uint8) | (1 - ff)).astype(np.uint8)


def build(src_path: Path) -> dict[str, np.ndarray]:
    """素材 1 枚から層を起こす。返すのは検分用の全段。"""
    rgb = np.asarray(Image.open(src_path).convert("RGB")).astype(np.float32)
    x0, y0, x1, y1 = CROP
    c = rgb[y0:y1, x0:x1]
    h, w, _ = c.shape
    lum = 0.299 * c[..., 0] + 0.587 * c[..., 1] + 0.114 * c[..., 2]

    # 1. 外形 — 背景は四隅の中央値。⚠ **明るさでは切れない**（髪 217 / 背景 245 で近い）。
    #    色ごとの差の最大値で見ると、白銀の髪でもちゃんと分かれる。
    bg = np.median(c[:8, :8].reshape(-1, 3), axis=0)
    sil = (np.abs(c - bg).max(axis=2) > BG_TOL).astype(np.uint8)
    sil = cv2.morphologyEx(sil, cv2.MORPH_CLOSE, _disk(2))
    sil = _fill_holes(_largest(sil))

    # 2. 目のあたり — 睫毛（顔の中で飛び抜けて暗い）を 2 つ拾って、瞳のぶんまで広げる。
    #    ⚠ 顎の下の影も同じくらい暗いので、**外形の上から 72% までに限る**。
    ys, _ = np.nonzero(sil)
    zone = np.zeros((h, w), np.uint8)
    zone[ys.min():ys.min() + int((ys.max() - ys.min()) * EYE_ZONE_TOP), :] = 1
    core = cv2.morphologyEx(((lum < EYE_CORE_MAX_L) & (sil > 0) & (zone > 0)).astype(np.uint8),
                            cv2.MORPH_CLOSE, _disk(3))
    n, lab, cst, _ = cv2.connectedComponentsWithStats(core, 8)
    picks = sorted(((cst[i, cv2.CC_STAT_AREA], i) for i in range(1, n)
                    if cst[i, cv2.CC_STAT_AREA] >= EYE_CORE_MIN_AREA), reverse=True)[:2]
    eyes = np.zeros((h, w), np.uint8)
    for _, i in picks:
        eyes[lab == i] = 1
    eyes = cv2.dilate(eyes, _disk(EYE_ZONE_PAD))

    # 3. 線 — 元の絵で暗い所。**これが「輪郭の抽出」そのもの**（別に描き起こさない）。
    line = (((lum < LINE_MAX_L) | ((lum < EYE_LINE_MAX_L) & (eyes > 0))) & (sil > 0)).astype(np.uint8)
    line = _drop_specks(line, LINE_MIN_AREA)
    # ⚠ 目の中と外で太らせる量を分ける（目は元から面積があるので、同じだけ太らせると塊になる）。
    inside = (line & eyes).astype(np.uint8)
    outside = (line & (1 - eyes)).astype(np.uint8)
    line = np.clip(cv2.dilate(outside, _disk(LINE_W))
                   + cv2.dilate(inside, _disk(EYE_LINE_W)), 0, 1).astype(np.uint8)
    # ⚠ 外形の縁は線に食わせない。食わせると睫毛や髪の分け目が輪郭まで届いた所で
    #    頭の縁が欠け、縮めたときに「輪郭が途切れた頭」になる。
    line = (line & cv2.erode(sil, _disk(RIM_KEEP))).astype(np.uint8)

    # 4. 墨 — 外形を塗って線を彫る。
    ink = _drop_specks((sil & (1 - line)).astype(np.uint8), INK_MIN_AREA)
    return {"src": c.astype(np.uint8), "sil": sil, "eyes": eyes, "line": line, "ink": ink}


def compose(ink: np.ndarray) -> Image.Image:
    """正方形の版へ収める。**RGB は白・A が墨の量**（色は実行時に掛ける）。"""
    ys, xs = np.nonzero(ink)
    bx0, bx1, by0, by1 = xs.min(), xs.max() + 1, ys.min(), ys.max() + 1
    bw, bh = bx1 - bx0, by1 - by0

    big = SIZE * SUPERSAMPLE
    inner = int(round(big * (1.0 - MARGIN * 2.0)))
    # ⚠ 合わせるのは**横**。頭は縦に切れて枠の下辺へ抜けてよい（肖像の切り方）が、
    #    横が切れると輪郭が失われて誰の顔か分からなくなる。
    scale = inner / bw
    dw, dh = int(round(bw * scale)), int(round(bh * scale))
    resized = cv2.resize(ink[by0:by1, bx0:bx1].astype(np.float32), (dw, dh),
                         interpolation=cv2.INTER_NEAREST)

    canvas = np.zeros((big, big), np.float32)
    ox = (big - dw) // 2
    oy = int(round(big * MARGIN))
    ph = min(dh, big - oy)
    canvas[oy:oy + ph, ox:ox + dw] = resized[:ph]
    # 面積平均で縮める ＝ 縁がなめらかな alpha になる（実機の 110 画素まで縮んでも溶けない）。
    small = cv2.resize(canvas, (SIZE, SIZE), interpolation=cv2.INTER_AREA)
    alpha = np.clip(small * 255.0 + 0.5, 0, 255).astype(np.uint8)
    return Image.fromarray(np.dstack([np.full((SIZE, SIZE, 3), 255, np.uint8), alpha]), "RGBA")


# 検分の地と墨（`CommsPanel` が実際に書いている色）。
PANEL_BG = (13, 11, 10)
INK_RGB = (209, 199, 184)     # HmdTextStyle.Ink


def _on_panel(face: Image.Image, px: int, box: int) -> Image.Image:
    im = Image.new("RGB", face.size, PANEL_BG)
    im.paste(Image.new("RGB", face.size, INK_RGB), (0, 0), face)
    return im.resize((px, px), Image.LANCZOS).resize((box, box), Image.NEAREST)


def parts_sheet(st: dict[str, np.ndarray], face: Image.Image) -> Image.Image:
    """層を 1 つずつ並べた 1 枚（**これを見るまでが 1 周**）。"""
    h, w = st["sil"].shape
    tiles = [Image.fromarray(st["src"])]
    for k in ("sil", "eyes", "line", "ink"):
        tiles.append(Image.fromarray(np.dstack([(1 - st[k]) * 255] * 3).astype(np.uint8)))
    box = max(w, h)
    # 焼き上がりと、**実機に近い大きさ**（110 画素 ＝ 1.5m 先の 0.15m 角）。
    tiles.append(_on_panel(face, SIZE, box))
    tiles.append(_on_panel(face, 110, box))

    # ⚠ **引き伸ばさない。** 正方形の枡へ入れるために縦横比を変えると、
    #    そこで判断した「顔の丸さ」が嘘になる（縦長の顔が丸く見える）。余白で合わせる。
    sheet = Image.new("RGB", (len(tiles) * (box + 8) + 8, box + 16), (170, 170, 170))
    for i, t in enumerate(tiles):
        x = 8 + i * (box + 8) + (box - t.width) // 2
        sheet.paste(t, (x, 8 + (box - t.height) // 2))
    return sheet


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--parts", action="store_true", help="分解した層を並べた 1 枚も出す")
    args = ap.parse_args()

    if not SRC.exists():
        print(f"素材が無い: {SRC}")
        return 1
    st = build(SRC)
    face = compose(st["ink"])
    OUT.parent.mkdir(parents=True, exist_ok=True)
    face.save(OUT)

    cover = float(np.asarray(face)[..., 3].mean()) / 255.0
    print(f"焼いた: {OUT.relative_to(ROOT)}  {SIZE}x{SIZE}  墨の面積 {cover * 100:.1f}%")
    print(f"  外形 {st['sil'].mean() * 100:.1f}% / 線 {st['line'].mean() * 100:.1f}%"
          f" / 墨 {st['ink'].mean() * 100:.1f}%（切り抜きに対する割合）")

    if args.parts:
        PARTS.parent.mkdir(parents=True, exist_ok=True)
        parts_sheet(st, face).save(PARTS)
        print(f"  検分用: {PARTS.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
