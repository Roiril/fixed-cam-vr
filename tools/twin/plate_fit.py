"""保存した無人写真に L の枠を当て、固定カメラの位置・向き・画角を解く（Blender 不要）。

    py -3.11 tools/twin/plate_fit.py              # 解いて表示
    py -3.11 tools/twin/plate_fit.py --out <dir>  # 枠を重ねた画も書く

観測は plate_obs.json（画素を読んだ点と線）。投影は tools/web-compositor/calib.js の projectPoint と同じ式。
画角は 3 台で共通（同じ機種の同じレンズ）。比較のため 1 台ごとに解いた画角も出す。
"""
from __future__ import annotations

import argparse
import json
import math
import sys
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
OBS = json.loads((HERE / "plate_obs.json").read_text(encoding="utf-8"))
W, H = 640, 480
CX, CY = W / 2, H / 2
CAMS = "ABC"


def rot(pitch, yaw, roll):
    x, y, z = map(math.radians, (-pitch, yaw, roll))
    cx, sx, cy, sy, cz, sz = math.cos(x), math.sin(x), math.cos(y), math.sin(y), math.cos(z), math.sin(z)
    ry = np.array([[cy, 0, sy], [0, 1, 0], [-sy, 0, cy]])
    rx = np.array([[1, 0, 0], [0, cx, -sx], [0, sx, cx]])
    rz = np.array([[cz, -sz, 0], [sz, cz, 0], [0, 0, 1]])
    return ry @ rx @ rz


def project(v, f, p):
    """v = [x, y, z, yaw, pitch, roll]"""
    r = rot(v[4], v[3], v[5])
    q = r.T @ (np.asarray(p, float) - v[:3])
    z = max(q[2], 1e-3)
    return np.array([CX + f * q[0] / z, CY - f * q[1] / z])


def line_dist(a, b, q):
    d = b - a
    n = np.array([-d[1], d[0]]) / max(np.linalg.norm(d), 1e-6)
    return float(n @ (np.asarray(q, float) - a))


def residuals_cam(cid, v, f):
    out = []
    for o in OBS["points"].get(cid, []):
        out.extend(project(v, f, o["p"]) - np.array(o["uv"], float))
    for o in OBS["lines"].get(cid, []):
        a, b = project(v, f, o["a"]), project(v, f, o["b"])
        out.extend(line_dist(a, b, q) for q in o["uv"])
    prior = OBS.get("priors", {}).get(cid, {})
    for key, axis in (("x", 0), ("y", 1), ("z", 2)):
        if key in prior:
            out.append((v[axis] - prior[key][0]) * prior[key][1])
    return out


def lm(fun, x0, steps, iters=200):
    x = np.array(x0, float)
    r = np.array(fun(x))
    cost = float(r @ r)
    lam = 1e-3
    for _ in range(iters):
        jac = np.zeros((len(r), len(x)))
        for k in range(len(x)):
            xx = x.copy()
            xx[k] += steps[k]
            jac[:, k] = (np.array(fun(xx)) - r) / steps[k]
        a = jac.T @ jac
        g = jac.T @ r
        better = False
        for _ in range(8):
            try:
                dx = np.linalg.solve(a + lam * np.diag(np.diag(a) + 1e-9), -g)
            except np.linalg.LinAlgError:
                lam *= 10
                continue
            nr = np.array(fun(x + dx))
            nc = float(nr @ nr)
            if nc < cost:
                x, r = x + dx, nr
                done = cost - nc < 1e-7
                cost = nc
                lam = max(lam / 3, 1e-9)
                better = True
                break
            lam *= 10
        if not better or done:
            break
    return x, r


CAM_STEPS = [2e-3, 2e-3, 2e-3, 2e-2, 2e-2, 2e-2]


def init_vec(cid):
    i = OBS["init"][cid]
    return [i["x"], i["y"], i["z"], i["yawDeg"], i["pitchDeg"], i["rollDeg"]]


def solve_joint():
    x0 = sum((init_vec(c) for c in CAMS), []) + [300.0]

    def fun(x):
        out = []
        for i, c in enumerate(CAMS):
            out.extend(residuals_cam(c, x[i * 6:i * 6 + 6], x[-1]))
        return out

    return lm(fun, x0, CAM_STEPS * 3 + [0.4])


def solve_single(cid, f0):
    def fun(x):
        return residuals_cam(cid, x[:6], x[6])

    return lm(fun, init_vec(cid) + [f0], CAM_STEPS + [0.4])


def hfov(f):
    return 2 * math.degrees(math.atan(CX / f))


def overlay(cid, v, f, out_dir):
    from PIL import Image, ImageDraw

    fr = OBS["frame"]
    (cx, cz), (ax, az), (bx, bz) = fr["corner"], fr["endW1"], fr["endW2"]
    top, rail = fr["topY"], fr["railY"]
    im = Image.open(ROOT / "Assets" / "StreamingAssets" / "show" / "assets" / OBS["plates"][cid]).convert("RGB")
    d = ImageDraw.Draw(im)
    segs = []
    for ex, ez in ((ax, az), (bx, bz)):
        segs += [((cx, top, cz), (ex, top, ez), (255, 70, 70)), ((cx, 0, cz), (ex, 0, ez), (70, 170, 255)), ((ex, 0, ez), (ex, top, ez), (255, 240, 0))]
        segs.append(((cx, rail, cz), (ex, rail, ez), (70, 255, 90)))
    segs.append(((cx, 0, cz), (cx, top, cz), (255, 240, 0)))
    for a, b, col in segs:
        pts = []
        for t in np.linspace(0, 1, 30):
            p = np.array(a) + t * (np.array(b) - np.array(a))
            r = rot(v[4], v[3], v[5])
            if (r.T @ (p - v[:3]))[2] > 0.03:
                pts.append(tuple(project(v, f, p)))
        if len(pts) > 1:
            d.line(pts, fill=col, width=1)
    for o in OBS["points"].get(cid, []):
        u, w = o["uv"]
        d.ellipse([u - 3, w - 3, u + 3, w + 3], outline=(255, 255, 255))
    path = Path(out_dir) / f"fit_{cid}.png"
    im.save(path)
    return path


def write_rig(res) -> None:
    """解を rig.json のカメラへ移す。向きは pos + 視線 2m の lookAt と roll で持つ（TwinRig・look_matrix と同じ形）。"""
    path = HERE / "rig.json"
    rig = json.loads(path.read_text(encoding="utf-8"))
    rig["_fit"] = {"hfovDeg": res["hfovDeg"], "fPx": res["fPx"], "rmsPx": {c: res["cameras"][c]["rmsPx"] for c in CAMS}}
    for cam in rig["cameras"]:
        s = res["cameras"][cam["id"]]
        fwd = rot(s["pitchDeg"], s["yawDeg"], s["rollDeg"])[:, 2]
        pos = np.array([s["x"], s["y"], s["z"]])
        cam["pos"] = [float(v) for v in pos]
        cam["lookAt"] = [round(float(v), 4) for v in pos + fwd * 2.0]
        cam["rollDeg"] = s["rollDeg"]
        cam["hfovDeg"] = res["hfovDeg"]
    tmp = path.with_suffix(".json.tmp")
    tmp.write_text(json.dumps(rig, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
    tmp.replace(path)
    print("rig.json を書いた")


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser()
    ap.add_argument("--out")
    ap.add_argument("--json", help="解を JSON で書く")
    ap.add_argument("--write", action="store_true", help="解を rig.json へ書く（機材の種類・棚・部屋の寸法は rig.json のまま）")
    a = ap.parse_args()
    x, r = solve_joint()
    f = x[-1]
    print(f"共通の画角: 水平 {hfov(f):.1f} 度（f={f:.1f}px）  全体 rms {math.sqrt(float(r @ r) / len(r)):.2f}px")
    res = {"fPx": round(float(f), 2), "hfovDeg": round(hfov(f), 2), "cameras": {}}
    for i, c in enumerate(CAMS):
        v = x[i * 6:i * 6 + 6]
        rr = np.array(residuals_cam(c, v, f))
        rms = math.sqrt(float(rr @ rr) / len(rr))
        print(f"{c}: 位置 x={v[0]:+.3f} 高さ={v[1]:.3f} z={v[2]:+.3f}  yaw={v[3]:+.1f} pitch={v[4]:+.1f} roll={v[5]:+.1f}  rms {rms:.2f}px")
        res["cameras"][c] = {"x": round(float(v[0]), 3), "y": round(float(v[1]), 3), "z": round(float(v[2]), 3), "yawDeg": round(float(v[3]), 2), "pitchDeg": round(float(v[4]), 2), "rollDeg": round(float(v[5]), 2), "rmsPx": round(rms, 2)}
        if a.out:
            overlay(c, v, f, a.out)
    print("1 台ごとに解いた画角:")
    for c in CAMS:
        xs, rs = solve_single(c, float(f))
        print(f"  {c}: 水平 {hfov(xs[6]):.1f} 度  rms {math.sqrt(float(rs @ rs) / len(rs)):.2f}px  位置 {xs[0]:+.3f},{xs[1]:.3f},{xs[2]:+.3f}")
    if a.json:
        Path(a.json).write_text(json.dumps(res, ensure_ascii=False, indent=1), encoding="utf-8")
    if a.write:
        write_rig(res)
    return 0


if __name__ == "__main__":
    sys.exit(main())
