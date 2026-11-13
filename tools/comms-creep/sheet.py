# -*- coding: utf-8 -*-
"""`menu comms-preview -Set recovery=1` のコマから、面の部分を拡大して縦に並べる。

  py -3.11 tools/comms-creep/sheet.py <出力 PNG> <コマ番号> [<コマ番号> ...] [--dir <recovery フォルダ>] [--scale 2]
"""
import argparse
import os
import sys

import cv2
import numpy as np

sys.stdout.reconfigure(encoding="utf-8")
ap = argparse.ArgumentParser()
ap.add_argument("out")
ap.add_argument("frames", nargs="+", type=int)
ap.add_argument("--dir", default="Assets/Screenshots/comms-preview/recovery")
ap.add_argument("--scale", type=float, default=2.0)
ap.add_argument("--box", default="285,195,880,385", help="x0,y0,x1,y1")
a = ap.parse_args()
x0, y0, x1, y1 = (int(v) for v in a.box.split(","))
rows = []
for n in a.frames:
    path = os.path.join(a.dir, f"f{n:04d}.png")
    im = cv2.imdecode(np.fromfile(path, dtype=np.uint8), cv2.IMREAD_COLOR)
    if im is None:
        raise SystemExit(f"コマが無い: {path}")
    c = im[y0:y1, x0:x1]
    if a.scale != 1.0:
        c = cv2.resize(c, None, fx=a.scale, fy=a.scale, interpolation=cv2.INTER_CUBIC)
    cv2.putText(c, f"f{n}", (6, 20), cv2.FONT_HERSHEY_SIMPLEX, 0.6, (200, 200, 200), 1)
    rows.append(c)
ok, buf = cv2.imencode(".png", np.concatenate(rows, 0))
with open(a.out, "wb") as f:
    f.write(buf.tobytes())
print("並べた:", a.out)
