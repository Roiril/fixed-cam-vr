"""機材の配置を数で検査する（Blender 不要・1 秒）。py -3.11 tools/twin/check_rig.py

1. 企画書の図 4 からの実測値（rig.json の fig4）と置いた位置の差
2. 歩行の記録（routes/*.tsv）と機材の離隔。足が機材を踏まないか
3. 区間ごとに、担当カメラの画に人（足元〜頭）が入っている割合
失敗があれば exit 1。結果は tools/twin-game/data/rig_check.json にも書く。
"""
from __future__ import annotations

import csv
import json
import math
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
RIG = json.loads((HERE / "rig.json").read_text(encoding="utf-8"))
ROUTES = HERE / "routes"
OUT = HERE.parent / "twin-game" / "data" / "rig_check.json"
HEIGHT = 1.65
FOOT_HALF = 0.2  # 歩く足の横の広がり（体の中心から）
LIMITS = {"tripod": 0.3, "floor": 0.14, "arm": 0.0}


def cam_axes(cam):
    px, py, pz = cam["pos"]
    tx, ty, tz = cam["lookAt"]
    f = [tx - px, ty - py, tz - pz]
    n = math.sqrt(sum(v * v for v in f))
    f = [v / n for v in f]
    # Unity 座標（左手系）: right = up × forward
    r = [f[2], 0.0, -f[0]]
    n = math.sqrt(sum(v * v for v in r))
    r = [v / n for v in r]
    u = [f[1] * r[2] - f[2] * r[1], f[2] * r[0] - f[0] * r[2], f[0] * r[1] - f[1] * r[0]]
    if u[1] < 0:
        u = [-v for v in u]
    return (px, py, pz), r, u, f


def in_frame(cam, p):
    o, r, u, f = cam_axes(cam)
    d = [p[i] - o[i] for i in range(3)]
    z = sum(d[i] * f[i] for i in range(3))
    if z < 0.05:
        return False
    tan_h = math.tan(math.radians(cam["hfovDeg"]) / 2)
    tan_v = tan_h * 3 / 4
    x = sum(d[i] * r[i] for i in range(3)) / z
    y = sum(d[i] * u[i] for i in range(3)) / z
    return abs(x) <= tan_h and abs(y) <= tan_v


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    fail = []
    report = {"cameras": {}, "routes": {}}
    for c in RIG["cameras"]:
        dx = c["pos"][0] - c["fig4"][0]
        dz = c["pos"][2] - c["fig4"][1]
        off = math.hypot(dx, dz)
        report["cameras"][c["id"]] = {"mount": c["mount"], "text": c["text"], "fromFig4M": round(off, 3)}
        print(f"{c['id']} {c['mount']:6s} {c['text']}  図4 との差 {off:.3f}m")
    shelf = RIG["shelf"]
    for path in sorted(ROUTES.glob("*.tsv")):
        rows = list(csv.DictReader(path.open(encoding="utf-8"), delimiter="\t"))
        res = {}
        for c in RIG["cameras"]:
            d = min(math.hypot(float(r["x"]) - c["pos"][0], float(r["z"]) - c["pos"][2]) for r in rows)
            limit = LIMITS[c["mount"]]
            need = limit + FOOT_HALF if limit else 0.0
            ok = d >= need or c["mount"] == "arm"
            if c["mount"] == "arm":
                ok = c["pos"][1] - HEIGHT >= 0.3  # 頭の上を通る
            res[c["id"]] = {"minDistM": round(d, 3), "ok": ok}
            if not ok:
                fail.append(f"{path.stem}: {c['id']} への離隔 {d:.3f}m（要 {need:.2f}m）")
        # 棚
        xmin, _, zmin = shelf["min"]
        xmax, _, zmax = shelf["max"]
        dshelf = 9.0
        for r in rows:
            x, z = float(r["x"]), float(r["z"])
            dx = max(xmin - x, 0.0, x - xmax)
            dz = max(zmin - z, 0.0, z - zmax)
            dshelf = min(dshelf, math.hypot(dx, dz))
        res["shelf"] = {"minDistM": round(dshelf, 3), "ok": dshelf >= 0.39}
        if dshelf < 0.39:
            fail.append(f"{path.stem}: 棚への離隔 {dshelf:.3f}m（要 0.39m）")
        # 担当カメラの画に入っている割合
        cams = RIG["cameras"]
        vis = {"A": [0, 0, 0, 0], "B": [0, 0, 0, 0], "C": [0, 0, 0, 0]}
        for r in rows:
            cid = "ABC"[int(float(r["active"]))]
            cam = next(c for c in cams if c["id"] == cid)
            x, z = float(r["x"]), float(r["z"])
            head = in_frame(cam, (x, HEIGHT - 0.1, z))
            body = in_frame(cam, (x, 1.0, z))
            feet = in_frame(cam, (x, 0.05, z))
            v = vis[cid]
            v[0] += 1
            v[1] += 1 if (head or body) else 0
            v[2] += 1 if (head and feet) else 0
            v[3] += 1 if head else 0
        res["inFrame"] = {k: {"n": v[0], "any": round(v[1] / max(v[0], 1), 3), "full": round(v[2] / max(v[0], 1), 3), "head": round(v[3] / max(v[0], 1), 3)} for k, v in vis.items()}
        for k, v in res["inFrame"].items():
            if v["n"] and v["any"] < 0.75:
                fail.append(f"{path.stem}: {k} の担当区間で人が映る割合 {v['any']:.2f}")
        report["routes"][path.stem] = res
        print(path.stem, json.dumps(res, ensure_ascii=False))
    report["failures"] = fail
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(json.dumps(report, ensure_ascii=False, indent=1), encoding="utf-8")
    for f in fail:
        print("FAIL", f)
    print("OK" if not fail else f"FAIL {len(fail)}")
    return 1 if fail else 0


if __name__ == "__main__":
    sys.exit(main())
