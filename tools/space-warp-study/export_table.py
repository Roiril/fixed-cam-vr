"""本編の RoomWarpField へ写す表（カメラごとの平面・利得）と、C# の照合値を書き出す。

  py -3.11 tools/space-warp-study/export_table.py --depth <bake_depth の出力> --out <json>

平面の逆深度は画面座標の 1 次式になるので a,b,c の 3 つで足りる。
"""
from __future__ import annotations

import argparse
import json
import math
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import depth_modes as dm  # noqa: E402
import study as st  # noqa: E402

TARGET = 0.20  # 変位の 99 パーセンタイル（NDC）。頭打ちに触れる格子が 3% 以下になる強さ
GOLDEN = [(0.0, 0.0, 0.0), (-0.6, 0.5, 1.7), (0.7, -0.7, 3.3), (0.84, 0.84, 5.9)]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--depth", required=True)
    ap.add_argument("--out", required=True)
    a = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")
    table = {}
    for cid in "ABC":
        cam = st.Cam(cid, Path(a.depth))
        gz = dm.plane_depth(cam)
        dm.set_depth(cam, gz)
        A = np.stack([cam.gx.ravel(), cam.gy.ravel(), np.ones(cam.gx.size)], axis=1)
        coeff, *_ = np.linalg.lstsq(A, (1 / gz).ravel(), rcond=None)
        # 本編と同じく、1 次式の平面で格子を作り直してから利得と照合値を決める
        gz2 = 1 / np.maximum(A @ coeff, 0.05).reshape(cam.gx.shape)
        dm.set_depth(cam, gz2)
        times = [i / 5 for i in range(0, 64)]
        gain = dm.calibrate(cam, times, TARGET)
        golden = []
        for gx, gy, t in GOLDEN:
            cam.gx = np.array([[gx]])
            cam.gy = np.array([[gy]])
            z = 1 / max(float(coeff[0] * gx + coeff[1] * gy + coeff[2]), 0.05)
            cam.gworld = cam.to_world(cam.gx, cam.gy, np.array([[z]]))
            dx, dy = dm.offsets(cam, t, gain)
            golden.append({"x": gx, "y": gy, "t": t, "dx": float(dx[0, 0]), "dy": float(dy[0, 0])})
        table[cid] = {"origin": cam.origin.tolist(), "rot": cam.rot.ravel().tolist(), "tan": [cam.tanh, cam.tanv],
                      "plane": coeff.tolist(), "pivot": cam.pivot.tolist(), "gain": gain, "golden": golden}
        print(cid, "gain", round(gain, 4), "plane", [round(float(v), 4) for v in coeff], flush=True)
    if not all(math.isfinite(v) for c in table.values() for v in c["plane"] + [c["gain"]]):
        raise SystemExit("数値が壊れている")
    Path(a.out).write_text(json.dumps({"target": TARGET, "overscan": st.OVERSCAN, "cams": table}, indent=1), encoding="utf-8")


if __name__ == "__main__":
    main()
