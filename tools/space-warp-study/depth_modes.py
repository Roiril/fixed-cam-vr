"""同じ変形で、奥行きの持ち方だけを変えて比べる。奥行きは本当に効いているのか。

  py -3.11 tools/space-warp-study/depth_modes.py --depth <bake_depth の出力> --out <出力> [--cams A,B,C] [--sec 6.4]

案:
  plane  平面 1 枚（奥行きを知らない）
  smooth いまの試作（ぼかした奥行き）
  shell  壁・床・天井・カーテンだけ。折れ目を残す
カメラごとに変位の 99 パーセンタイルを揃える（tanh の頭打ちに頼らない）。
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import cv2
import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import study as st  # noqa: E402

TARGET = 0.13  # 変位の 99 パーセンタイル（NDC）
SHELL = ("Wall", "Floor", "Ceiling", "Curtain")


def shell_depth(cam, depth_dir: Path, meta):
    z = np.load(depth_dir / f"depth_{cam.id}.npz")
    names = meta["cams"][cam.id]["names"]
    keep_ids = [i for n, i in names.items() if n.startswith(SHELL)]
    if not keep_ids:
        raise SystemExit(f"面が無い: {cam.id}")
    ids = z["ids"]
    d = np.minimum(z["depth"].astype(np.float32), cam.z_far)
    ok = np.isin(ids, keep_ids)
    # 面でない所は周りの面から埋める
    fill = d.copy()
    fill[~ok] = 0
    weight = ok.astype(np.float32)
    for sigma in (3, 7, 15, 31, 61):
        num = cv2.GaussianBlur(fill * weight, (0, 0), sigma)
        den = cv2.GaussianBlur(weight, (0, 0), sigma)
        hole = weight < 0.5
        if not hole.any():
            break
        got = den > 0.02
        fill[hole & got] = (num[hole & got] / den[hole & got])
        weight[hole & got] = 1
    if (weight < 0.5).any():
        raise SystemExit(f"埋まらない穴がある: {cam.id}")
    mapx = ((cam.gx + 1) / 2 * (fill.shape[1] - 1)).astype(np.float32)
    mapy = (((1 - cam.gy) / 2) * (fill.shape[0] - 1)).astype(np.float32)
    g = cv2.remap(fill, mapx, mapy, cv2.INTER_AREA)
    g = np.sqrt(g.astype(np.float64) ** 2 + 0.45 ** 2)
    iz = st.limit_gradient((1 / g).astype(np.float32), st.MAX_INV_DEPTH_STEP)
    return (1 / iz).astype(np.float64)


def plane_depth(cam):
    """いまの格子の点へ平面を当てはめ、その平面までの奥行きに置き換える。"""
    P = cam.gworld.reshape(-1, 3)
    use = np.ones(len(P), bool)
    for _ in range(4):
        c = P[use].mean(axis=0)
        _, _, vt = np.linalg.svd(P[use] - c, full_matrices=False)
        n = vt[-1]
        d = np.abs((P - c) @ n)
        use = d < max(np.percentile(d, 70), 1e-3)
    ray = np.stack([cam.gx * cam.tanh, cam.gy * cam.tanv, -np.ones_like(cam.gx)], axis=-1) @ cam.rot.T
    den = ray @ n
    den = np.where(np.abs(den) < 0.05, 0.05 * np.sign(den + 1e-9), den)
    z = ((c - cam.origin) @ n) / den
    return np.clip(z, 0.5, cam.z_far + 2)


def set_depth(cam, gz):
    cam.gworld = cam.to_world(cam.gx, cam.gy, gz)
    center = (np.abs(cam.gx) < 0.25) & (np.abs(cam.gy) < 0.25)
    cam.pivot = np.median(cam.gworld[center], axis=0)


def raw_offset(cam, t):
    u, v = cam.project(st.room_points(cam, t, 1.0, st.ROOM_WAVES))
    return u - cam.gx, v - cam.gy


def calibrate(cam, times, target):
    mags = [np.hypot(*raw_offset(cam, t)) for t in times]
    return min(1.0, target / float(np.percentile(np.stack(mags), 99)))


def offsets(cam, t, gain):
    dx, dy = raw_offset(cam, t)
    return st.limit_offset(dx * gain, dy * gain)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--depth", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--cams", default="A,B,C")
    ap.add_argument("--sec", type=float, default=6.4)
    ap.add_argument("--target", type=float, default=TARGET, help="変位の 99 パーセンタイル（NDC）")
    ap.add_argument("--modes", default="plane,smooth,shell")
    ap.add_argument("--video", help="写真の代わりに流す映像。{cid} をカメラ名に置く")
    ap.add_argument("--start", type=float, default=0.0)
    a = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")
    out = Path(a.out)
    out.mkdir(parents=True, exist_ok=True)
    depth_dir = Path(a.depth)
    meta = json.loads((depth_dir / "depth_meta.json").read_text(encoding="utf-8"))
    report = {}
    n = int(a.sec * st.FPS)
    times = [i / st.FPS for i in range(0, n, 6)]
    for cid in a.cams.split(","):
        if not st.PLATES[cid].exists():
            raise SystemExit(f"写真が無い: {st.PLATES[cid]}")
        plate = cv2.resize(cv2.imread(str(st.PLATES[cid])), (st.W, st.H), interpolation=cv2.INTER_CUBIC)
        cam = st.Cam(cid, depth_dir)
        video = None
        if a.video:
            vp = Path(a.video.replace("{cid}", cid))
            if not vp.exists():
                raise SystemExit(f"映像が無い: {vp}")
            video = cv2.VideoCapture(str(vp))
            video.set(cv2.CAP_PROP_POS_MSEC, a.start * 1000)
            vfps = video.get(cv2.CAP_PROP_FPS)
        gz_smooth = -((cam.gworld - cam.origin) @ cam.rot)[..., 2]
        depths = {"plane": plane_depth(cam), "smooth": gz_smooth, "shell": shell_depth(cam, depth_dir, meta)}
        depths = {k: depths[k] for k in a.modes.split(",")}
        titles = {"plane": ("平面 1 枚", "奥行きを知らない"), "smooth": ("ぼかした奥行き", "いまの試作"), "shell": ("壁と床だけ", "折れ目を残す")}
        report[cid] = {}
        fields = {}
        for mode, gz in depths.items():
            set_depth(cam, gz)
            gain = calibrate(cam, times, a.target)
            mags = []
            field = []
            for t in times:
                dx, dy = offsets(cam, t, gain)
                mags.append(np.hypot(dx, dy))
                field.append(np.stack([dx, dy], axis=-1))
            M = np.stack(mags)
            F = np.stack(field)
            fields[mode] = F
            center = (np.abs(cam.gx) < 0.2) & (np.abs(cam.gy) < 0.2)
            rim = (np.abs(cam.gx) > 0.6) | (np.abs(cam.gy) > 0.6)
            det = np.stack([st.fold_stats(cam, t, 1.0, st.ROOM_WAVES) for t in times])
            report[cid][mode] = {"gain": round(gain, 3), "saturated_cells": round(float((M > 0.8 * st.LIMIT).mean()), 4), "center_median": round(float(np.median(M[:, center])), 4), "rim_median": round(float(np.median(M[:, rim])), 4),
                                 "rim_over_center": round(float(np.median(M[:, rim]) / max(np.median(M[:, center]), 1e-4)), 1), "fold_min_ungained": round(float(det.min()), 3)}
        for mode in [m for m in ("smooth", "shell") if m in fields and "plane" in fields]:
            residual = fields[mode] - fields["plane"]
            report[cid][mode]["explained_by_plane"] = round(float(1 - (residual ** 2).sum() / (fields[mode] ** 2).sum()), 3)
        # 同じ時刻を並べる
        tw, th = st.W // 2, st.H // 2
        grid = st.Writer(out / f"{cid}_modes.mp4", tw * len(depths), th)
        singles = {m: st.Writer(out / f"{cid}_{m}.mp4", st.W, st.H) for m in depths}
        gains = {m: report[cid][m]["gain"] for m in depths}
        for i in range(n):
            t = i / st.FPS
            if video is not None:
                ok, fr = video.read()
                if not ok:
                    raise SystemExit("映像が足りない")
                plate = cv2.resize(fr, (st.W, st.H), interpolation=cv2.INTER_AREA)
                for _ in range(max(0, round(vfps / st.FPS) - 1)):
                    video.grab()
            tiles = []
            for mode, gz in depths.items():
                set_depth(cam, gz)
                dx, dy = offsets(cam, t, gains[mode])
                img = st.sample_raw(plate, cam.vx + st.upsample(dx), cam.vy + st.upsample(dy))
                img = st.burn(img, f"{cid} / {titles[mode][0]}", titles[mode][1])
                singles[mode].add(img)
                tiles.append(cv2.resize(img, (tw, th), interpolation=cv2.INTER_AREA))
            grid.add(np.hstack(tiles))
        grid.close()
        for w in singles.values():
            w.close()
        print(cid, json.dumps(report[cid], ensure_ascii=False), flush=True)
    (out / "modes.json").write_text(json.dumps(report, ensure_ascii=False, indent=1), encoding="utf-8")


if __name__ == "__main__":
    main()
