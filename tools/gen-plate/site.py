# -*- coding: utf-8 -*-
"""場所（環境依存）の下書きと確認。**当日ここだけを触る。**

    py -3.11 tools/gen-plate/site.py draft --plate <プレート.jpg> --id A_20260817
    py -3.11 tools/gen-plate/site.py show  --id A_20260817

`draft` はプレートを測って面（床・布・壁）の箱を**下書き**し、
箱を描いた `sites/<id>_surfaces.png` を出す。**その 1 枚を見て箱を直すのが人の仕事。**
下書きが外れていても構わない（外れているのが 1 枚で分かる形にしてある）。
"""
from __future__ import annotations

import argparse
import json
import os
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFont

import spec

COLORS = dict(floor=(120, 220, 160), cloth=(230, 180, 120), wall=(150, 190, 240),
              furniture=(220, 140, 200), ceiling=(200, 200, 140))


def _grad(g: np.ndarray) -> np.ndarray:
    gx = np.zeros_like(g); gy = np.zeros_like(g)
    gx[:, 1:-1] = g[:, 2:] - g[:, :-2]
    gy[1:-1, :] = g[2:, :] - g[:-2, :]
    return np.hypot(gx, gy)


def _largest_box(mask: np.ndarray) -> list[int] | None:
    """最大の連結成分の外接箱（4 近傍・BFS）。"""
    from collections import deque
    h, w = mask.shape
    seen = np.zeros_like(mask, dtype=bool)
    best = None
    best_n = 0
    for y0 in range(0, h, 4):
        for x0 in range(0, w, 4):
            if not mask[y0, x0] or seen[y0, x0]:
                continue
            q = deque([(y0, x0)])
            seen[y0, x0] = True
            n = 0
            x1 = x2 = x0
            y1 = y2 = y0
            while q:
                y, x = q.popleft()
                n += 1
                x1, x2 = min(x1, x), max(x2, x)
                y1, y2 = min(y1, y), max(y2, y)
                for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                    ny, nx = y + dy, x + dx
                    if 0 <= ny < h and 0 <= nx < w and mask[ny, nx] and not seen[ny, nx]:
                        seen[ny, nx] = True
                        q.append((ny, nx))
            if n > best_n:
                best_n, best = n, [x1, y1, x2 + 1, y2 + 1]
    return best if best_n > mask.size * 0.03 else None


def draft(plate_path: str, site_id: str, cam: str | None, note: str) -> dict:
    im = Image.open(plate_path).convert("RGB")
    w, h = im.size
    g = np.asarray(im.convert("L"), dtype=np.float64)
    grad = _grad(g)

    # 床: 下側の帯。細かい模様（カーペット・目地）が続く所を下から探す
    rows = np.median(grad, axis=1)
    thr = float(np.percentile(rows, 60))
    top = int(h * 0.75)
    for y in range(h - 1, int(h * 0.35), -1):
        if rows[y] < thr:
            top = y
            break
        top = y
    floor = [0, min(top, int(h * 0.75)), w, h]

    # 布: 平らで色の薄い大きな面（襞は縦に走るので横方向の勾配だけ見る）
    rgb = np.asarray(im, dtype=np.float64)
    sat = rgb.max(axis=2) - rgb.min(axis=2)
    smooth = (grad < np.percentile(grad, 45)) & (sat < 40)
    smooth[int(h * 0.85):, :] = False
    cloth = _largest_box(smooth[::2, ::2])
    if cloth:
        cloth = [cloth[0] * 2, cloth[1] * 2, cloth[2] * 2, cloth[3] * 2]

    surfaces = {"floor": {"box": floor, "say": "手前の床"}}
    if cloth:
        surfaces["cloth"] = {"box": cloth, "say": "垂れている布の面"}
    surfaces["wall"] = {"box": [0, 0, w, floor[1]], "say": "奥の壁"}

    site = dict(id=site_id, cam=cam or site_id.split("_")[0][:1],
                plate=os.path.relpath(plate_path, spec.REPO).replace("\\", "/"),
                note=note or "（下書き。overlay を見て直すこと）", surfaces=surfaces)
    os.makedirs(spec.SITE_DIR, exist_ok=True)
    with open(os.path.join(spec.SITE_DIR, f"{site_id}.json"), "w", encoding="utf-8") as f:
        json.dump(site, f, ensure_ascii=False, indent=2)
    return site


def overlay(site: dict) -> str:
    im = Image.open(site["plate_abs"]).convert("RGB")
    d = ImageDraw.Draw(im)
    try:
        font = ImageFont.truetype("C:/Windows/Fonts/meiryo.ttc", 13)
    except OSError:
        font = ImageFont.load_default()
    for name, s in (site.get("surfaces") or {}).items():
        b = [int(v) for v in s["box"]]
        c = COLORS.get(name, (255, 255, 255))
        d.rectangle(b, outline=c, width=2)
        d.text((b[0] + 4, b[1] + 3), f"{name}: {s.get('say', '')}", fill=c, font=font)
    out_dir = os.path.join(spec.REPO, "logs", "gen-plate", "sites")
    os.makedirs(out_dir, exist_ok=True)
    out = os.path.join(out_dir, f"{site['id']}_surfaces.png")
    im.save(out)
    return out


def main() -> int:
    ap = argparse.ArgumentParser(description="場所（面の箱）の下書きと確認")
    ap.add_argument("mode", choices=["draft", "show", "list"])
    ap.add_argument("--plate")
    ap.add_argument("--id")
    ap.add_argument("--cam")
    ap.add_argument("--note", default="")
    args = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")

    if args.mode == "list":
        for s in spec.list_sites():
            print(s)
        return 0
    if args.mode == "draft":
        site = draft(args.plate, args.id, args.cam, args.note)
        site = spec.load_site(args.id)
        print(json.dumps(site.get("surfaces"), ensure_ascii=False))
    else:
        site = spec.load_site(args.id)
    print("overlay:", overlay(site))
    print("⚠ 下書きは当てにしない。overlay を開いて箱を直す（直す先は sites/<id>.json）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
