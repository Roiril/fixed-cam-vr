#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""連絡の面に出す顔を版へ焼く。**スイ（AIエージェント）と、市松人形（侵食の行き先）の 2 枚。**

    py -3.11 tools/make-comms-face.py              # → Assets/Resources/Comms/{Sui,Doll}Face.png
    py -3.11 tools/make-comms-face.py --parts      # 分解した層を並べた検分用の 1 枚も出す
    py -3.11 tools/make-comms-face.py --only doll  # 片方だけ

出どころ
--------
| 版 | 素材 | ここに置いてある切り抜き |
|---|---|---|
| スイ | **paperdoll で作った「スイ」**（`paperdoll/chara/out/sui/expression.png` の 1 枚目） | `tools/comms-face/sui-neutral.png` |
| 市松人形 | **実物の写真から Codex に起こさせた線画**（`tools/doll-ref/out/gen_face_lineart.png`） | `tools/comms-face/doll-lineart.png` |

どちらもここへ置いてあるので、**素材の置き場が消えても焼き直せる**。

⚠⚠ **人形は写真からは焼けない。** 試して駄目だった順に 2 つ:

1. **正本の写真**（`plate_front.jpg`）— 背景も髪も暗くて外形が取れない（実測: 背景 L=26〜52 / 髪 L=27）
2. **明るい地に立つ資料の板**（`gen_standing.png`）— 外形は取れるが、**写実には彫るべき線が無い**。
   髪と顔の境目を自分で引いてみたが、出たのは「丸い頭に丸い顔が浮いている」だけの絵で、
   人形だと分からなかった（2026-08-17 のユーザー赤入れ「今はなんか変」）

⇒ **実物の写真を参照に、Codex へ閉じた線画を描かせる**（依頼の作法は `tools/doll-ref/PROMPT.md`）。
線画なら**スイと同じ形の素材**になるので、同じ規則がそのまま効く。
⚠ 線画は**資料であって正本ではない**（`memory/doll_reference_kit.md`）。正本はいまも写真。

ユーザー指定
------------
- 0071「輪郭だけ抽出し、髪は輪郭と同じ色で塗りつぶし、背景はない感じ」
- 0072「キャラクターの目が少し怖い」→ 瞳を彫る閾値を下げた
- **0073「日本人形の資料があると思うのでそれを使って、そこに顔をスイと似たような感じで表示」**

やり方は 2 行（**2 枚とも同じ**）
--------------------------------
    墨 ＝ 頭の外形を全部塗る − 元の絵の線
    背景（頭の外）は透明

**元の絵の線をそのまま「彫り込む」**。⚠ ただし**線の取り方は素材で違う**:

| | スイ | 市松人形 |
|---|---|---|
| 外形 | **色の差**で背景と分ける（白銀の髪は明るさでは背景と 28 しか違わない） | **線で囲まれた内側を塗る**（線画なので、囲みの外が紙） |
| 線 | 元の絵の**明るさの閾値**（描かれた線が暗い） | **線そのもの**（紙は 254・線は 0） |
| 目 | 瞳が明るいので目の中だけ閾値を緩める | そのままで彫れる |

⚠⚠ **顔の肌まで塗るのは誤りではない。** 参考のスクショは「白い紙 ＋ 青い墨」で、肌は紙の色。
この面は**暗い地に明るい墨**なので、明暗が逆になる ＝ **肌を墨で塗ったものが参考の紙にあたる**。
肌を抜くと（＝暗いまま残すと）顔が黒い穴になり、目も口も読めない
（2026-08-17 に 6 通り試して全部そうなった。層を分けて塗り分ける方向は全滅）。

⚠⚠ **2 枚は同じ大きさ・同じ座り**でなければならない。実行時に片方からもう片方へ溶けるので、
頭の位置と大きさがずれると「侵食」ではなく「絵が入れ替わった」に見える。
<see cref="MARGIN"/> と <see cref="compose"/> が 2 枚を同じ規則で収める。

⚠⚠ **実機では 110 画素そこそこにしかならない。**
面の枠は 1.5m 先の 0.15m 角 ＝ 見かけ 5.7°、Quest 3 は視野中心でおよそ 20 画素/度。
だから**線は必ず太らせる**（`LINE_W`）。細いまま焼くと縮小で溶けて灰色の靄になる。
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
SRC_DIR = ROOT / "tools" / "comms-face"
OUT_DIR = ROOT / "Assets" / "Resources" / "Comms"
PARTS_DIR = ROOT / "logs" / "comms-face"

# ---- 焼き上がり（2 枚で共有。⚠ 揃っていないと侵食が「入れ替わり」に見える）------------
SIZE = 256           # 版の 1 辺（正方形）
MARGIN = 0.06        # 枠の内側に取る余白（1 辺に対する割合）
SUPERSAMPLE = 4      # 縮小前の倍率。縁のなめらかさはここで決まる
LINE_W = 2           # 線を太らせる半径（素材の画素）。⚠ 実機の 110 画素で残る太さ
RIM_KEEP = 2         # 外形の縁は必ず墨で残す幅。⚠ 無いと輪郭が線に食われて頭が欠ける
INK_MIN_AREA = 60    # 彫ったあとに残る墨の島の下限
LINE_MIN_AREA = 25   # これ未満の暗い点は線ではない（粒・にじみ）

# ---- 画風を測る 3 つ（2 枚を揃えるための物差し）------------------------------------
#
# ⭐ **「チープ」も「なんか変」も、感想ではなく数で出た**（`canon/LEDGER.md` 0075 / 0076）。
#   ⚠⚠ ただし **1 つでは足りない**。密度だけを見ていた版は 23.8%（スイ 24.3%）で
#   数値上そっくりなのに、並べると別物だった — **同じ量の墨を、細い線で全面へ散らすか、
#   太い線で数か所へ集めるか**が違ったから。⇒ 3 つ揃えて初めて画風が揃う。

#: 彫った線が頭の面積に占める割合。**量**。
DENSITY_FLOOR = 0.18
#: 線 1 本の太さ（頭の幅に対する割合）。**重さ**。スイは 2.04%。
#: ⚠ ここが低いと、110 画素まで縮んだとき線どうしが潰れ合って**灰色のもや**になる。
THICKNESS_FLOOR = 0.010
#: 線のかたまりの数。**散らばり**。スイは 13 個。
#: ⚠ 多いほど「細い線を全面に散らした」状態に近い。倍あったら描き直し。
MARK_CEILING = 20

# ---- スイ（`sui-neutral.png` の画素。素材の実測。外したら `--parts` の絵を見て直す）----
SUI_CROP = (8, 4, 392, 306)   # 髪の天辺 〜 顎の少し下。⚠ 外套から下は入れない（黒い台形になる）
SUI_BG_TOL = 10.0     # 背景（生成りの無地）からこれだけ離れたら「物がある」
#: 線とみなす明るさの上限。実測: 肌 231 / 髪 217 / 背景 245 / 睫毛 30〜90。
#: ⚠ **上げると髪が糸くずになる**（195 で束が全部ばらける）。165〜175 が持ち場。
SUI_LINE_MAX_L = 168.0
#: **目の中だけ**は緩める。瞳は青で明るく（実測 159）、この値のままだと睫毛の 1 本線しか彫れない。
#: ⚠⚠ **200 → 175 へ下げた**（`canon/LEDGER.md` 0072・赤入れ「目が少し怖い」）。
#: 200 だと瞳と睫毛と下瞼が 1 つの黒い塊になり、**目ではなく落ち窪んだ眼窩**に見える。
SUI_EYE_LINE_MAX_L = 175.0
SUI_EYE_LINE_W = 1        # 目の中の線の太さ。⚠ 髪と同じ 2 にすると隣の線と繋がって塊になる
SUI_EYE_CORE_MAX_L = 115.0  # 目の芯（睫毛）。顔の中でここまで暗いのは睫毛と瞳だけ
SUI_EYE_CORE_MIN_AREA = 120
SUI_EYE_ZONE_TOP = 0.72   # 目を探す縦の範囲。⚠ 顎の影を拾わない線
SUI_EYE_ZONE_PAD = 9      # 芯から瞳へ広げる幅

# ---- 市松人形（`doll-lineart.png` の画素）------------------------------------------
#
# ⚠⚠ **素材は写真ではなく「写真から起こした線画」**（2026-08-17・`canon/LEDGER.md` 0074・
#    ユーザー赤入れ「人形の見た目をもう少し見やすく理解しやすくしてほしい。今はなんか変」）。
#    写真から直に焼くと**丸い顔が丸い頭に浮いているだけ**の絵になり、人形だと分からなかった。
#    原因は §「やり方は 2 行」の前提 — **写実には彫るべき線が無い**こと。
#    ⇒ 実物の写真を参照として Codex に**閉じた線画**を描かせ、それを彫る（`tools/doll-ref/PROMPT.md`）。
#    ⚠ これは**資料であって正本ではない**（`memory/doll_reference_kit.md`）。
#      正本はいまも `tools/doll-ref/out/plate_front.jpg` の写真で、線画はそこから起こした 1 枚。
DOLL_CROP = (175, 45, 1105, 1120)   # 髪の天辺 〜 襟の V（肩が広がる前で切る）
#: 線とみなす明るさの上限。線画なので紙（254）と線は離れているが、**細い線ほど淡く出る**ので
#: 高めに取る。⚠ 128 まで下げると髪の房が半分落ちて、のっぺりした塗り絵に戻る。
DOLL_LINE_MAX_L = 200.0
#: 塗りつぶす前に線を閉じる量の**上限**。⚠ 実際の値は <see cref="_seal"/> が自動で探す。
#: 生成された線画は**頭頂の輪郭に十数画素の隙間が空いていることがある**（2026-08-17 に踏んだ）。
#: 隙間があると塗りが外へ漏れて、**頭が真っ暗な絵**になる。
DOLL_CLOSE_MAX = 12
#: 線を太らせる量。⚠ **4**。スイの線の重さ（頭の幅の 2.0%）へ寄せる値で、
#: 2 では実機の 110 画素で細く、6 では前髪の束が繋がって黒い帯になる（絵で比べた）。
DOLL_LINE_W = 4


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


def _load(path: Path, crop: tuple[int, int, int, int]) -> tuple[np.ndarray, np.ndarray]:
    rgb = np.asarray(Image.open(path).convert("RGB")).astype(np.float32)
    x0, y0, x1, y1 = crop
    c = rgb[y0:y1, x0:x1]
    return c, 0.299 * c[..., 0] + 0.587 * c[..., 1] + 0.114 * c[..., 2]


def _silhouette(c: np.ndarray, tol: float) -> np.ndarray:
    """頭の外形。⚠ **明るさでは切れない**（スイの髪は背景と 28 しか違わない）ので色の差で見る。"""
    bg = np.median(c[:8, :8].reshape(-1, 3), axis=0)
    sil = (np.abs(c - bg).max(axis=2) > tol).astype(np.uint8)
    sil = cv2.morphologyEx(sil, cv2.MORPH_CLOSE, _disk(2))
    return _fill_holes(_largest(sil))


def _carve(sil: np.ndarray, line: np.ndarray) -> np.ndarray:
    """外形を塗って線を彫る。⚠ 外形の縁は線に食わせない（食うと頭の輪郭が欠ける）。"""
    line = (line & cv2.erode(sil, _disk(RIM_KEEP))).astype(np.uint8)
    return _drop_specks((sil & (1 - line)).astype(np.uint8), INK_MIN_AREA), line


def build_sui() -> dict[str, np.ndarray]:
    c, lum = _load(SRC_DIR / "sui-neutral.png", SUI_CROP)
    h, w, _ = c.shape
    sil = _silhouette(c, SUI_BG_TOL)

    # 目のあたり — 睫毛（顔の中で飛び抜けて暗い）を 2 つ拾って、瞳のぶんまで広げる。
    # ⚠ 顎の下の影も同じくらい暗いので、**外形の上から 72% までに限る**。
    ys, _ = np.nonzero(sil)
    zone = np.zeros((h, w), np.uint8)
    zone[ys.min():ys.min() + int((ys.max() - ys.min()) * SUI_EYE_ZONE_TOP), :] = 1
    core = cv2.morphologyEx(((lum < SUI_EYE_CORE_MAX_L) & (sil > 0) & (zone > 0)).astype(np.uint8),
                            cv2.MORPH_CLOSE, _disk(3))
    n, lab, cst, _ = cv2.connectedComponentsWithStats(core, 8)
    picks = sorted(((cst[i, cv2.CC_STAT_AREA], i) for i in range(1, n)
                    if cst[i, cv2.CC_STAT_AREA] >= SUI_EYE_CORE_MIN_AREA), reverse=True)[:2]
    eyes = np.zeros((h, w), np.uint8)
    for _, i in picks:
        eyes[lab == i] = 1
    eyes = cv2.dilate(eyes, _disk(SUI_EYE_ZONE_PAD))

    # 線 — 元の絵で暗い所。**これが「輪郭の抽出」そのもの**（別に描き起こさない）。
    line = (((lum < SUI_LINE_MAX_L) | ((lum < SUI_EYE_LINE_MAX_L) & (eyes > 0)))
            & (sil > 0)).astype(np.uint8)
    line = _drop_specks(line, LINE_MIN_AREA)
    # ⚠ 目の中と外で太らせる量を分ける（目は元から面積があるので、同じだけ太らせると塊になる）。
    inside = (line & eyes).astype(np.uint8)
    outside = (line & (1 - eyes)).astype(np.uint8)
    line = np.clip(cv2.dilate(outside, _disk(LINE_W))
                   + cv2.dilate(inside, _disk(SUI_EYE_LINE_W)), 0, 1).astype(np.uint8)

    ink, line = _carve(sil, line)
    return {"src": c.astype(np.uint8), "sil": sil, "zone": eyes, "line": line, "ink": ink}


def _seal(raw: np.ndarray) -> tuple[np.ndarray, int]:
    """
    線を閉じてから紙の側を塗り、その否定を外形として返す。

    ⚠⚠ **輪郭に隙間があると塗りが外へ漏れて、頭が真っ暗な絵になる。**
    しかも**スクリプトは何も言わずに通る** — 2026-08-17 に生成した線画の頭頂に十数画素の
    隙間があり、密度の数値（47%・狙いは 24%）を見て初めて気づいた。
    ⇒ **閉じる量を自動で上げながら、漏れていないかを毎回確かめる。**
    漏れの判定は「外形が外接矩形をどれだけ埋めているか」— 頭と肩なら 5 割は超える。
    """
    h, w = raw.shape
    for close in range(1, DOLL_CLOSE_MAX + 1):
        closed = cv2.dilate(raw, _disk(close))
        flood = closed.copy()
        cv2.floodFill(flood, np.zeros((h + 2, w + 2), np.uint8), (0, 0), 1)
        sil = (~((flood > 0) & (closed == 0))).astype(np.uint8)
        ys, xs = np.nonzero(sil)
        box = (xs.max() - xs.min() + 1) * (ys.max() - ys.min() + 1)
        if sil.sum() / max(box, 1) > 0.5:
            return sil, close
    raise SystemExit("線画の輪郭が閉じていない（塗りが外へ漏れる）。"
                     f"{DOLL_CLOSE_MAX} 画素まで広げても塞がらなかった — 線を引き直す")


def build_doll() -> dict[str, np.ndarray]:
    """線画から焼く。**外形は「線で囲まれた内側」＝ 塗りつぶしで取る**（明るさでは取れない）。"""
    rgb = np.asarray(Image.open(SRC_DIR / "doll-lineart.png").convert("RGB")).astype(np.float32)
    lum = 0.299 * rgb[..., 0] + 0.587 * rgb[..., 1] + 0.114 * rgb[..., 2]
    raw = (lum < DOLL_LINE_MAX_L).astype(np.uint8)
    sil, _ = _seal(raw)

    x0, y0, x1, y1 = DOLL_CROP
    sil, raw = sil[y0:y1, x0:x1], raw[y0:y1, x0:x1]
    src = rgb[y0:y1, x0:x1].astype(np.uint8)

    ink, line = _carve(sil, cv2.dilate(raw, _disk(DOLL_LINE_W)))
    return {"src": src, "sil": sil, "zone": raw, "line": line, "ink": ink}


def compose(ink: np.ndarray) -> Image.Image:
    """正方形の版へ収める。**RGB は白・A が墨の量**（色は実行時に掛ける）。

    ⚠ 2 枚を**同じ規則**で収めること（横を基準に合わせ、上端を余白に置く）。
    片方だけ詰め方を変えると、溶けるときに頭が跳ねる。
    """
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


def style_metrics(line: np.ndarray, sil: np.ndarray) -> tuple[float, float, int]:
    """
    画風を 3 つの数で出す（**量 / 重さ / 散らばり**）。

    ⚠⚠ <b>1 つでは足りない。</b> 量だけを合わせた版は数値上スイとそっくり（23.8% 対 24.3%）
    なのに、並べると別物だった — 同じ量の墨を**細い線で全面へ散らす**か
    **太い線で数か所へ集める**かが違ったから（`canon/LEDGER.md` 0076）。

    ⚠ 頭の外接矩形で正規化する。切り抜きの取り方で値が動くと 2 枚を比べられない。
    """
    ys, xs = np.nonzero(sil)
    head_w = max(int(xs.max() - xs.min()), 1)
    density = float(line.sum()) / max(float(sil.sum()), 1.0)
    dt = cv2.distanceTransform(line.astype(np.uint8), cv2.DIST_L2, 5)
    thickness = (2.0 * float(dt[line > 0].mean()) / head_w) if line.any() else 0.0
    n, _, st, _ = cv2.connectedComponentsWithStats(line.astype(np.uint8), 8)
    marks = sum(1 for i in range(1, n) if st[i, cv2.CC_STAT_AREA] >= 40)
    return density, thickness, marks


# 検分の地と墨（`CommsPanel` が実際に書いている色）。
PANEL_BG = (13, 11, 10)
INK_RGB = (209, 199, 184)     # HmdTextStyle.Ink


def _on_panel(face: Image.Image, px: int, box: int) -> Image.Image:
    im = Image.new("RGB", face.size, PANEL_BG)
    im.paste(Image.new("RGB", face.size, INK_RGB), (0, 0), face)
    return im.resize((px, px), Image.LANCZOS).resize((box, box), Image.NEAREST)


def parts_row(st: dict[str, np.ndarray], face: Image.Image, box: int) -> list[Image.Image]:
    tiles = [Image.fromarray(st["src"])]
    for k in ("sil", "zone", "line", "ink"):
        tiles.append(Image.fromarray(np.dstack([(1 - st[k]) * 255] * 3).astype(np.uint8)))
    # 焼き上がりと、**実機に近い大きさ**（110 画素 ＝ 1.5m 先の 0.15m 角）。
    tiles.append(_on_panel(face, SIZE, box))
    tiles.append(_on_panel(face, 110, box))
    return tiles


def parts_sheet(rows: list[list[Image.Image]]) -> Image.Image:
    """層を 1 つずつ並べた 1 枚（**これを見るまでが 1 周**）。1 行が 1 枚の版。

    ⚠ **引き伸ばさない。** 正方形の枡へ入れるために縦横比を変えると、
    そこで判断した「顔の丸さ」が嘘になる。余白で合わせる。
    """
    box = max(t.width for r in rows for t in r + [])
    box = max(box, max(t.height for r in rows for t in r))
    cols = max(len(r) for r in rows)
    sheet = Image.new("RGB", (cols * (box + 8) + 8, len(rows) * (box + 8) + 8), (170, 170, 170))
    for j, row in enumerate(rows):
        for i, t in enumerate(row):
            x = 8 + i * (box + 8) + (box - t.width) // 2
            y = 8 + j * (box + 8) + (box - t.height) // 2
            sheet.paste(t, (x, y))
    return sheet


SUBJECTS = {
    "sui": (build_sui, "SuiFace.png", "スイ（AIエージェント）"),
    "doll": (build_doll, "DollFace.png", "市松人形（侵食の行き先）"),
}


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--parts", action="store_true", help="分解した層を並べた 1 枚も出す")
    ap.add_argument("--only", choices=sorted(SUBJECTS), help="片方だけ焼く")
    args = ap.parse_args()

    names = [args.only] if args.only else list(SUBJECTS)
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    rows: list[list[Image.Image]] = []
    for name in names:
        build, out_name, label = SUBJECTS[name]
        st = build()
        face = compose(st["ink"])
        face.save(OUT_DIR / out_name)
        cover = float(np.asarray(face)[..., 3].mean()) / 255.0
        density, thickness, marks = style_metrics(st["line"], st["sil"])
        warn = []
        if density < DENSITY_FLOOR:
            warn.append("線が少ない（のっぺりする）")
        if thickness < THICKNESS_FLOOR:
            warn.append("線が細い（縮むと灰色のもやになる）")
        if marks > MARK_CEILING:
            warn.append("線が散らばりすぎ（太い線に束ねる）")
        print(f"焼いた: {(OUT_DIR / out_name).relative_to(ROOT)}  {SIZE}x{SIZE}"
              f"  墨の面積 {cover * 100:.1f}%  — {label}")
        print(f"  外形 {st['sil'].mean() * 100:.1f}% / 線 {st['line'].mean() * 100:.1f}%"
              f" / 墨 {st['ink'].mean() * 100:.1f}%（切り抜きに対する割合）")
        print(f"  画風: 量 {density * 100:.1f}%（≥{DENSITY_FLOOR * 100:.0f}）"
              f" / 重さ {thickness * 100:.2f}%（≥{THICKNESS_FLOOR * 100:.1f}）"
              f" / 散らばり {marks} 個（≤{MARK_CEILING}）"
              + ("  ⚠ " + " / ".join(warn) if warn else ""))
        if args.parts:
            rows.append(parts_row(st, face, max(st["sil"].shape)))

    if rows:
        PARTS_DIR.mkdir(parents=True, exist_ok=True)
        out = PARTS_DIR / ("parts.png" if not args.only else f"parts_{args.only}.png")
        parts_sheet(rows).save(out)
        print(f"  検分用: {out.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
