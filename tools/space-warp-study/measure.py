"""試作と参照を同じ計器で測る。
校正: 剛体のカメラ揺れ（sway）は面ごとの射影で説明でき、画面の波（wave2d）は説明できないはず。

  py -3.11 tools/space-warp-study/measure.py --out <json> <ラベル=mp4 か 連番フォルダ> ...
"""
from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path

import cv2
import numpy as np

W, H, FPS, GAP = 720, 540, 15, 8
CENTER = np.array([W / 2, H / 2 - 30])
BINS = [(0, 90), (90, 170), (170, 250), (250, 330), (330, 460)]
REG = {"floor": lambda x, y: y > 345, "left": lambda x, y: (y <= 345) & (x < 175),
       "right": lambda x, y: (y <= 345) & (x > 560), "back": lambda x, y: (y <= 345) & (x >= 175) & (x <= 560)}
lsd = cv2.createLineSegmentDetector(cv2.LSD_REFINE_STD)


def load(src: str):
    p = Path(src)
    if p.is_dir():
        names = sorted(n for n in os.listdir(p) if n.endswith(".png"))
        if not names:
            raise SystemExit(f"コマが無い: {p}")
        return [cv2.resize(cv2.imread(str(p / n), cv2.IMREAD_GRAYSCALE), (W, H)) for n in names]
    if not p.exists():
        raise SystemExit(f"無い: {p}")
    c = cv2.VideoCapture(str(p))
    src_fps = c.get(cv2.CAP_PROP_FPS)
    out, i, nxt = [], 0, 0.0
    while True:
        ok, f = c.read()
        if not ok:
            break
        if i >= nxt:
            out.append(cv2.resize(cv2.cvtColor(f, cv2.COLOR_BGR2GRAY), (W, H), interpolation=cv2.INTER_AREA))
            nxt += src_fps / FPS
        i += 1
    if len(out) < GAP + 2:
        raise SystemExit(f"短すぎる: {p}")
    return out


def mask_for(top):
    m = np.full((H, W), 255, np.uint8)
    m[:max(top, 10)] = 0
    m[-10:] = 0
    m[:, :10] = 0
    m[:, -10:] = 0
    return m


def long_lines(img, top, min_len=70):
    d = lsd.detect(img[top:])[0]
    if d is None:
        return 0, 0.0
    d = d.reshape(-1, 4)
    n = np.hypot(d[:, 2] - d[:, 0], d[:, 3] - d[:, 1])
    return int((n > min_len).sum()), float(n[n > min_len].sum())


def measure(label, src, top):
    frames = load(src)
    mask = mask_for(top)
    single, per_plane, pts, scale_rate = [], [], [], []
    for i in range(0, len(frames) - GAP, 6):
        a, b = frames[i], frames[i + GAP]
        p0 = cv2.goodFeaturesToTrack(a, 1500, 0.01, 7, mask=mask)
        if p0 is None or len(p0) < 50:
            continue
        p1, st, _ = cv2.calcOpticalFlowPyrLK(a, b, p0, None, winSize=(25, 25), maxLevel=4)
        p2, st2, _ = cv2.calcOpticalFlowPyrLK(b, a, p1, None, winSize=(25, 25), maxLevel=4)
        good = (st.ravel() == 1) & (st2.ravel() == 1) & (np.linalg.norm((p0 - p2).reshape(-1, 2), axis=1) < 1.0)
        A, B = p0[good].reshape(-1, 2), p1[good].reshape(-1, 2)
        if len(A) < 40:
            continue
        _, inl = cv2.findHomography(A, B, cv2.RANSAC, 2.0)
        if inl is None:
            continue
        single.append(float(inl.mean()))
        n_in = n_tot = 0
        for fn in REG.values():
            m = fn(A[:, 0], A[:, 1])
            if m.sum() < 15:
                continue
            _, inn = cv2.findHomography(A[m], B[m], cv2.RANSAC, 2.0)
            if inn is not None:
                n_in += int(inn.sum())
                n_tot += int(m.sum())
        if n_tot:
            per_plane.append(n_in / n_tot)
        pts.append((A, B - A))
        # RANSAC は動かない中央の点だけを拾って参照で 7 分の 1 に出る。全点の最小二乗で測る
        scale_rate.append(abs(float(np.polyfit((A - A.mean(0)).ravel(), (B - B.mean(0)).ravel(), 1)[0]) - 1.0))
    if not pts:
        raise SystemExit(f"追跡点が取れない: {src}")
    P = np.vstack([a for a, _ in pts])
    D = np.vstack([d for _, d in pts])
    r = np.linalg.norm(P - CENTER, axis=1)
    mag = np.linalg.norm(D, axis=1)
    radial = {}
    for a, b in BINS:
        s = (r >= a) & (r < b)
        radial[f"{a}-{b}"] = round(float(np.median(mag[s])), 1) if s.sum() > 10 else None
    series = [long_lines(f, top) for f in frames[::10]]
    base = long_lines(frames[0], top)
    res = dict(label=label, frames=len(frames), single_h=round(float(np.mean(single)), 3), per_plane_h=round(float(np.mean(per_plane)), 3),
               scale_rate=round(float(np.mean(scale_rate)), 4), radial_px=radial, lines_first=base[0], lines_min=min(s[0] for s in series), lines_max=max(s[0] for s in series),
               line_len_ratio=round(float(np.mean([s[1] for s in series]) / max(base[1], 1)), 2))
    print(json.dumps(res, ensure_ascii=False), flush=True)
    return res


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    ap.add_argument("--top", type=int, default=50, help="焼いた名札を除く上端 px（720x540 基準）")
    ap.add_argument("items", nargs="+")
    a = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")
    res = []
    for it in a.items:
        label, src = it.split("=", 1)
        res.append(measure(label, src, 10 if label == "reference" else a.top))
    Path(a.out).write_text(json.dumps(res, ensure_ascii=False, indent=1), encoding="utf-8")


if __name__ == "__main__":
    main()
