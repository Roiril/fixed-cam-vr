# -*- coding: utf-8 -*-
"""合成マスクの境目を跨いだ塊を、種の画素へ戻して消す（`canon/LEDGER.md` 0120 の後処理）。

    py -3.11 tools/gen-plate/despill.py --run logs/gen-plate/<走行> [--cut 320] [--dilate 6] [--feather 3]
      → logs/gen-plate/<走行>_despill/  （out.png を書き換えた走行。judge / deliver / spill / survive がそのまま使える）

なぜ後処理か: 跨ぎは place を狭めても（left-40 → left-35）減らなかった（R033・4 枚中 0 枚）。
群れは指定した範囲と同じ幅で描かれて外へ出る。**プロンプトと place では動かない側**なので、
描き直し以外の走行を捨てずに済むように、境目を跨いだ塊（右端の 1〜3 体）だけを丸ごと種に戻す。

⚠ 消すのは「塊」単位。跨いだ塊が群れ全体と繋がっている（面積が足した所の 25% 超）なら、
   消すと群れが消えるので止まる（`--force` で無視）。
⚠ 戻すのは種（`seed.png`＝暗くしたプレート）の画素。生成は背景を数階調だけ触るので、
   戻した所の縁は `--feather` でなじませる。届いた画で縁が見えないかは目で確かめる。
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import sys
from collections import deque

import numpy as np
from PIL import Image, ImageFilter

import metrics
import spill


def label(m: np.ndarray, min_area: int = metrics.MIN_PART) -> tuple[np.ndarray, list[dict]]:
    """4 近傍の連結成分。ラベル画像（0 = 無し）と塊の一覧を返す。"""
    h, w = m.shape
    lab = np.zeros(m.shape, dtype=np.int32)
    comps = []
    ys, xs = np.nonzero(m)
    nid = 0
    for sy, sx in zip(ys, xs):
        if lab[sy, sx]:
            continue
        nid += 1
        q = deque([(sy, sx)])
        lab[sy, sx] = nid
        px = []
        while q:
            y, x = q.popleft()
            px.append((y, x))
            for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                ny, nx = y + dy, x + dx
                if 0 <= ny < h and 0 <= nx < w and m[ny, nx] and not lab[ny, nx]:
                    lab[ny, nx] = nid
                    q.append((ny, nx))
        if len(px) < min_area:
            for y, x in px:
                lab[y, x] = 0
            continue
        a = np.array(px)
        comps.append(dict(id=nid, area=len(px), y0=int(a[:, 0].min()), y1=int(a[:, 0].max()) + 1,
                          x0=int(a[:, 1].min()), x1=int(a[:, 1].max()) + 1))
    return lab, comps


def dilate(m: np.ndarray, n: int) -> np.ndarray:
    for _ in range(n):
        m = metrics._shift_or(m)
    return m


def run(run_dir: str, cut: int, dil: int, feather: int, force: bool, out_dir: str | None) -> int:
    man = json.load(open(os.path.join(run_dir, "manifest.json"), encoding="utf-8"))
    seed = Image.open(man["seed"]).convert("RGB")
    gen_p = os.path.join(run_dir, "out_fit.png")
    if not os.path.exists(gen_p):
        gen_p = os.path.join(run_dir, "out.png")
    gen = Image.open(gen_p).convert("RGB")
    if gen.size != seed.size:
        gen = gen.resize(seed.size, Image.LANCZOS)

    s = np.asarray(seed, dtype=np.float64)
    g = np.asarray(gen, dtype=np.float64)
    d = np.abs(g - s).max(axis=2)
    added = metrics.clean(d > metrics.ADD_THR)
    lab, comps = label(added)
    total = int(added.sum())
    # 境目を跨ぐ塊と、境目の向こう側だけにある塊（どちらもマスクで捨てられるか切れる）
    bad = [c for c in comps if c["x1"] > cut]
    if not bad:
        print(f"{os.path.basename(run_dir)}: 境目 x={cut} を越えた塊は無い。何もしない")
        return 0
    bad_area = sum(c["area"] for c in bad)
    if not force and total and bad_area > 0.25 * total:
        print(f"{os.path.basename(run_dir)}: 越えた塊が足した所の {100 * bad_area / total:.0f}% "
              f"（{len(bad)} 個・{bad_area} 画素）。群れごと繋がっているので消さない（--force で無視）")
        return 2

    erase = np.zeros(added.shape, dtype=bool)
    for c in bad:
        erase |= (lab == c["id"])
    erase = dilate(erase, dil)
    a = Image.fromarray((erase * 255).astype(np.uint8))
    if feather > 0:
        a = a.filter(ImageFilter.GaussianBlur(feather))
    alpha = np.asarray(a, dtype=np.float64)[:, :, None] / 255.0
    out = g * (1.0 - alpha) + s * alpha
    out_img = Image.fromarray(np.clip(out + 0.5, 0, 255).astype(np.uint8))

    out_dir = out_dir or (run_dir.rstrip("/\\") + "_despill")
    os.makedirs(out_dir, exist_ok=True)
    for name in ("prompt.txt", "seed.png"):
        p = os.path.join(run_dir, name)
        if os.path.exists(p):
            shutil.copyfile(p, os.path.join(out_dir, name))
    out_img.save(os.path.join(out_dir, "out.png"))
    man2 = dict(man)
    man2["out"] = os.path.join(out_dir, "out.png")
    man2["despill"] = dict(source=run_dir, cut=cut, dilate=dil, feather=feather,
                           erased=[dict(area=c["area"], x0=c["x0"], x1=c["x1"], y0=c["y0"], y1=c["y1"])
                                   for c in bad],
                           erased_px=int(erase.sum()), added_px=total)
    with open(os.path.join(out_dir, "manifest.json"), "w", encoding="utf-8") as f:
        json.dump(man2, f, ensure_ascii=False, indent=2)

    after = spill.measure(out_dir, cut)
    print(f"{os.path.basename(run_dir)}: 越えた塊 {len(bad)} 個（{bad_area} 画素・足した所の "
          f"{100 * bad_area / max(1, total):.1f}%）を種へ戻した → {out_dir}")
    print(f"  despill 後: 跨ぐ塊 {after['straddle']} / 越えた画素 {after['right_px']} / 右端 x {after['max_x']}")
    return 0 if after["straddle"] == 0 else 1


def main() -> int:
    ap = argparse.ArgumentParser(description="境目を跨いだ塊を種の画素へ戻す")
    ap.add_argument("--run", action="append", required=True, help="走行フォルダ（複数可）")
    ap.add_argument("--cut", type=int, default=spill.CUT, help=f"境目の素材 x 座標（既定 {spill.CUT}）")
    ap.add_argument("--dilate", type=int, default=6, help="消す塊を広げる画素（影・縁のにじみを含める）")
    ap.add_argument("--feather", type=int, default=3, help="戻した所の縁のぼかし（px）")
    ap.add_argument("--force", action="store_true", help="越えた塊が大きくても消す")
    ap.add_argument("--out-dir", default=None, help="出力の走行フォルダ（既定 <走行>_despill）")
    a = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")
    rc = 0
    for r in a.run:
        rc = max(rc, run(r, a.cut, a.dilate, a.feather, a.force, a.out_dir if len(a.run) == 1 else None))
    return rc


if __name__ == "__main__":
    raise SystemExit(main())
