# -*- coding: utf-8 -*-
"""生成した 4 面図を、**実写の輪郭へ押し込んで**テクスチャの素材にする。

```
py -3.11 tools/doll-ref/make_texture_source.py            # 作って tools/doll-ref/tex/ へ
py -3.11 tools/doll-ref/make_texture_source.py --install  # tools/doll-model/ へ入れる（要 --backup 済み）
```

## なぜ「押し込む」のか

生成した絵はきれいに均一な光で、背景も撮影者の手も無い。**テクスチャの素材としてはこちらが正しい**
（写真は暗く緑に転んでいて、しかも顎の下・袖の下・裾の内側に**影が焼き込まれている**。
3D に貼ると `ShowActor.shader` の陰影と二重になる）。

⚠⚠ **ただし形は生成に決めさせない。** 実測（2026-08-15）: 同じ参照から 2 回生成したら、
頭の大きさが全高の 1/4 → 1/5 へ動いた。押し出しモデルの値打ちは
**輪郭が実物の写真そのもの**であることなので、そこを生成に置き換えると「似た別の人形」になる。

⇒ **生成した絵を、実写のマスクの輪郭へ 1 行ずつ引き伸ばす**（行ごとの左端・右端を合わせる）。
出てくるのは「実物の形・生成の光」で、`texture.py` からは**きれいに撮り直した写真**に見える。
だから `profile.json` も `shell.py` も 1 行も変えなくてよい。

## 出来ないこと

行ごとの横方向しか合わせないので、**縦のずれ**（帯の高さ・顔の位置が数 % 動く）は残る。
帯のような横に細い模様は、その分だけ上下へ滑る。`--check` の重ね合わせで確認すること。
"""
from __future__ import annotations

import argparse
import os
import shutil
import sys

import cv2
import numpy as np

sys.stdout.reconfigure(encoding="utf-8")

HERE = os.path.dirname(os.path.abspath(__file__))
MODEL = os.path.join(os.path.dirname(HERE), "doll-model")
SHEET = os.path.join(HERE, "out", "tex_4view.png")
TEX = os.path.join(HERE, "tex")
VIEWS = ("front", "back", "sideA", "sideB")

# ⚠⚠ **実写のマスクは下端に台と机を含む**（最下部の数 % が一定幅の柱になる）。
#    生成した絵にはそれが無いので、そのまま輪郭を合わせると**全部が数 % 下へ滑る**
#    （生成の裾が、実写の台の位置へ引き伸ばされる）。人形の部分だけで合わせる。
#    値は tools/doll-ref/build.py の TRIM と同じ。
CUT_BOTTOM = {"front": 0.055, "back": 0.030, "sideA": 0.055, "sideB": 0.050}


def doll_only(mask):
    """実写のマスクから台と机のぶんを落とす（人形の部分だけ返す）。"""
    ys = np.nonzero((mask > 127).any(axis=1))[0]
    return ys.min(), ys.max() + 1


def sheet_panels(path: str):
    """4 面図を 4 枚へ切る（背景が平らなので塊で切れる）。左から順に返す。"""
    im = cv2.imread(path)
    if im is None:
        raise FileNotFoundError(path)
    h, w = im.shape[:2]
    bg = np.median(np.concatenate([im[:30, :30].reshape(-1, 3), im[:30, -30:].reshape(-1, 3)]), axis=0)
    m = (np.linalg.norm(im.astype(np.float32) - bg, axis=2) > 24).astype(np.uint8)
    m = cv2.morphologyEx(m, cv2.MORPH_CLOSE, np.ones((9, 9), np.uint8))
    n, lab, st, _ = cv2.connectedComponentsWithStats(m, 8)
    keep = [i for i in range(1, n) if st[i, cv2.CC_STAT_AREA] > h * w * 0.005]
    keep.sort(key=lambda i: st[i, cv2.CC_STAT_LEFT])
    out = []
    for i in keep:
        x, y, ww, hh = st[i, cv2.CC_STAT_LEFT], st[i, cv2.CC_STAT_TOP], st[i, cv2.CC_STAT_WIDTH], st[i, cv2.CC_STAT_HEIGHT]
        pad = 6
        x0, y0 = max(0, x - pad), max(0, y - pad)
        x1, y1 = min(w, x + ww + pad), min(h, y + hh + pad)
        out.append((im[y0:y1, x0:x1], (lab[y0:y1, x0:x1] == i).astype(np.uint8) * 255))
    return out


def rows_profile(mask: np.ndarray, n: int = 128):
    """高さを n 等分し、各段の 左端 / 右端 / 幅 を [0,1] で返す（空の段は補間）。"""
    ys, xs = np.nonzero(mask > 127)
    y0, y1 = ys.min(), ys.max() + 1
    x0, x1 = xs.min(), xs.max() + 1
    L = np.full(n, np.nan)
    R = np.full(n, np.nan)
    for k in range(n):
        a = y0 + int((y1 - y0) * k / n)
        b = max(a + 1, y0 + int((y1 - y0) * (k + 1) / n))
        band = mask[a:b] > 127
        cols = np.nonzero(band.any(axis=0))[0]
        if len(cols):
            L[k], R[k] = cols.min(), cols.max() + 1
    ok = ~np.isnan(L)
    idx = np.arange(n)
    L = np.interp(idx, idx[ok], L[ok])
    R = np.interp(idx, idx[ok], R[ok])
    return dict(L=L, R=R, W=(R - L), y0=y0, y1=y1, x0=x0, x1=x1)


def warp_to(src: np.ndarray, src_mask: np.ndarray, dst_mask: np.ndarray) -> np.ndarray:
    """`src` を `dst_mask` の輪郭へ押し込む（縦は外接矩形、横は 1 行ずつ）。"""
    H, W = dst_mask.shape[:2]
    sp = rows_profile(src_mask)
    dp = rows_profile(dst_mask)
    n = len(sp["L"])

    out = np.zeros((H, W, 3), np.uint8)
    out[:, :] = np.median(src[:6, :6].reshape(-1, 3), axis=0)   # 背景は生成の灰色で埋める

    for y in range(dp["y0"], dp["y1"]):
        t = (y - dp["y0"]) / max(1, dp["y1"] - dp["y0"])
        k = min(n - 1, int(t * n))
        dl, dr = dp["L"][k], dp["R"][k]
        if dr - dl < 2:
            continue
        sl, sr = sp["L"][k], sp["R"][k]
        if sr - sl < 2:
            continue
        sy = sp["y0"] + t * (sp["y1"] - sp["y0"])
        sy0 = int(np.clip(sy, 0, src.shape[0] - 1))
        xs_dst = np.arange(int(dl), int(dr))
        u = (xs_dst - dl) / (dr - dl)                    # 行の中での位置 0..1
        xs_src = np.clip(sl + u * (sr - sl), 0, src.shape[1] - 1).astype(np.int32)
        out[y, int(dl):int(dr)] = src[sy0, xs_src]
    return out


def head_signature(img, mask):
    p = rows_profile(mask)
    y0 = p["y0"]
    y1 = y0 + int((p["y1"] - y0) * 0.22)            # 頭のあたり
    band = (mask[y0:y1] > 127)
    if band.sum() < 50:
        return 0.0, 0.5
    g = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY).astype(np.float32)
    whole = g[mask > 127]
    # ⚠ **割合は絶対の基準で測る。** 最初は「頭のあたりの 78 パーセンタイルより明るい画素」に
    #    していたが、それでは割合が定義上いつも 0.22 になり**何も分けられなかった**
    #    （4 枚とも 0.204〜0.219）。人形全体の中央値からどれだけ明るいかで見る。
    base = float(np.median(whole))
    sd = float(np.std(whole)) + 1e-6
    head = g[y0:y1]
    light = band & (head > base + 0.55 * sd)
    frac = float(light.sum()) / float(band.sum())
    ys_, xs_ = np.nonzero(light)
    if len(xs_) < 20:
        return frac, 0.5
    cx = float(xs_.mean())
    L, R = p["x0"], p["x1"]
    return frac, float(np.clip((cx - L) / max(1.0, R - L), 0, 1))


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--install", action="store_true", help="tools/doll-model/ の作業用 jpg を置き換える")
    ap.add_argument("--backup", action="store_true", help="置き換える前に現物を tex/backup/ へ退避")
    a = ap.parse_args()

    os.makedirs(TEX, exist_ok=True)
    panels = sheet_panels(SHEET)
    print(f"4 面図を {len(panels)} 枚に切った: " +
          " / ".join(f"{p.shape[1]}x{p.shape[0]}" for p, _ in panels))
    if len(panels) != 4:
        print("⚠ 4 枚に切れていない。生成をやり直す")
        return 1

    # ---- どの板がどの向きか。**幅の形（縦の profile）が似ているもの同士**で決める ----
    ph = {}
    for v in VIEWS:
        m = cv2.imread(os.path.join(MODEL, f"mask_{v}.png"), cv2.IMREAD_GRAYSCALE)
        ph[v] = rows_profile(m)

    # 向きは `head_signature`（顔の明るさと左右位置）で決める。**幅の形では決まらない** — 説明は関数側。
    photo_sig, panel_sig = {}, {}
    for v in VIEWS:
        img = cv2.imread(os.path.join(MODEL, "photos", f"{v}.jpg"))
        msk = cv2.imread(os.path.join(MODEL, f"mask_{v}.png"), cv2.IMREAD_GRAYSCALE)
        photo_sig[v] = head_signature(img, msk)
    for i, (p, pm) in enumerate(panels):
        panel_sig[i] = head_signature(p, pm)
        print(f"  板 {i}: 明るい割合 {panel_sig[i][0]:.3f} / 位置 {panel_sig[i][1]:.2f}")

    def norm(w):
        w = np.asarray(w, np.float64)
        return (w - w.mean()) / (w.std() + 1e-9)

    used, assign = set(), {}
    for v in VIEWS:
        best, bi, detail = -9, -1, ""
        for i, (_p, pm) in enumerate(panels):
            if i in used:
                continue
            shape = float(np.corrcoef(norm(ph[v]["W"]), norm(rows_profile(pm)["W"]))[0, 1])
            face = -abs(photo_sig[v][0] - panel_sig[i][0]) * 3.0 \
                   - abs(photo_sig[v][1] - panel_sig[i][1]) * 2.0
            s = shape * 0.5 + face
            if s > best:
                best, bi = s, i
                detail = f"形 {shape:+.3f} / 顔 {face:+.3f}"
        used.add(bi)
        assign[v] = (bi, best)
        print(f"  {v:6s} ← 板 {bi}（{detail}）")

    for v in VIEWS:
        i, _c = assign[v]
        src, src_mask = panels[i]
        dst_mask = cv2.imread(os.path.join(MODEL, f"mask_{v}.png"), cv2.IMREAD_GRAYSCALE)
        # 台と机のぶんを落とした版へ合わせる（下の CUT_BOTTOM の説明）。
        y0, y1 = doll_only(dst_mask)
        cut = int((y1 - y0) * CUT_BOTTOM[v])
        target = dst_mask.copy()
        target[y1 - cut:] = 0
        out = warp_to(src, src_mask, target)
        cv2.imwrite(os.path.join(TEX, f"{v}.jpg"), out, [cv2.IMWRITE_JPEG_QUALITY, 95])
        # 重ね合わせ（実写のマスクの縁を緑で描く）— **ずれはここでしか見えない**
        chk = out.copy()
        edge = cv2.Canny((dst_mask > 127).astype(np.uint8) * 255, 50, 150)
        chk[edge > 0] = (0, 255, 0)
        cv2.imwrite(os.path.join(TEX, f"check_{v}.jpg"), chk, [cv2.IMWRITE_JPEG_QUALITY, 88])
        print(f"  {v}.jpg  {out.shape[1]}x{out.shape[0]}")

    if a.install:
        if a.backup:
            bdir = os.path.join(TEX, "backup")
            os.makedirs(bdir, exist_ok=True)
            for v in VIEWS:
                shutil.copy2(os.path.join(MODEL, f"{v}.jpg"), os.path.join(bdir, f"{v}.jpg"))
            for f in ("doll_albedo.png", "doll_normal.png"):
                p = os.path.join(MODEL, f)
                if os.path.exists(p):
                    shutil.copy2(p, os.path.join(bdir, f))
            print(f"  退避 → {bdir}")
        for v in VIEWS:
            shutil.copy2(os.path.join(TEX, f"{v}.jpg"), os.path.join(MODEL, f"{v}.jpg"))
        print("  tools/doll-model/ の作業用 jpg を置き換えた。次: py -3.11 texture.py")

    print(f"\n→ {TEX}\n⚠ `check_*.jpg` を開いて、緑の縁と絵がずれていないか見る。")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
