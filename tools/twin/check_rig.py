"""機材の配置と歩く道を数で検査する（Blender 不要・数秒）。py -3.11 tools/twin/check_rig.py

1. rig.json の姿勢で枠を投影し、保存した無人写真から読んだ点と線（plate_obs.json）との差を見る
2. 歩く道が手すり沿いにあるか（枠の芯から 0.24m）。区画の外へ出ないか
3. 画面の担当カメラが、区画の割り当てどおりに替わるか
4. 歩く道と機材・部屋の壁・棚の離隔
5. 区間ごとに、担当カメラの画に人が入っている割合
失敗があれば exit 1。結果は tools/twin-game/data/rig_check.json にも書く。
"""
from __future__ import annotations

import csv
import json
import math
import sys
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
RIG = json.loads((HERE / "rig.json").read_text(encoding="utf-8"))
OBS = json.loads((HERE / "plate_obs.json").read_text(encoding="utf-8"))
SHOW = json.loads((HERE.parents[1] / "Assets" / "StreamingAssets" / "show" / "show.json").read_text(encoding="utf-8-sig"))
ROUTES = HERE / "routes"
OUT = HERE.parent / "twin-game" / "data" / "rig_check.json"
HEIGHT = 1.65
BODY_R = 0.2  # 肩の半幅
WALK_D = 0.24  # 枠の芯から歩く道まで（tools/walk-guide）
RMS_LIMIT = 6.0  # 写真との差の許容（px・640 幅）
RING_LIMITS = {"tripod": 0.3, "floor": 0.14}  # 三脚の脚の広がり・床置きの台の半幅


def cam_axes(cam):
    """右・上・前。TwinRig / look_matrix と同じく、ワールド上向きの基準に roll を掛ける。"""
    o = np.array(cam["pos"], float)
    f = np.array(cam["lookAt"], float) - o
    f /= np.linalg.norm(f)
    r = np.array([f[2], 0.0, -f[0]])
    r /= np.linalg.norm(r)
    u = np.cross(f, r)
    if u[1] < 0:
        u = -u
    a = math.radians(cam["rollDeg"])
    r2 = r * math.cos(a) + u * math.sin(a)
    u2 = u * math.cos(a) - r * math.sin(a)
    return o, r2, u2, f


def project(cam, p, w=640.0, h=480.0):
    o, r, u, f = cam_axes(cam)
    d = np.array(p, float) - o
    z = float(d @ f)
    if z < 1e-3:
        return None
    fpx = (w / 2) / math.tan(math.radians(cam["hfovDeg"]) / 2)
    return np.array([w / 2 + fpx * float(d @ r) / z, h / 2 - fpx * float(d @ u) / z])


def in_frame(cam, p):
    q = project(cam, p)
    return q is not None and 0 <= q[0] <= 640 and 0 <= q[1] <= 480


def plate_rms(cam):
    res = []
    for o in OBS["points"].get(cam["id"], []):
        q = project(cam, o["p"])
        res.extend(q - np.array(o["uv"], float))
    for o in OBS["lines"].get(cam["id"], []):
        a, b = project(cam, o["a"]), project(cam, o["b"])
        d = b - a
        n = np.array([-d[1], d[0]]) / max(np.linalg.norm(d), 1e-6)
        res.extend(float(n @ (np.array(q, float) - a)) for q in o["uv"])
    res = np.array(res)
    return math.sqrt(float(res @ res) / len(res))


def frame_distance(x, z):
    """枠の芯までの距離。端は手すりの U の芯（支柱の RAIL_EXT 先）まで伸ばして測る。"""
    w1, w2 = SHOW["layout"]["room"]["walls"]
    ext = OBS["frame"]["railExt"]
    best = 9.0
    for w in (w1, w2):
        a, b = np.array([w["x1"], w["z1"]]), np.array([w["x2"], w["z2"]])
        b = b + (b - a) / np.linalg.norm(b - a) * ext
        t = np.clip(((np.array([x, z]) - a) @ (b - a)) / ((b - a) @ (b - a)), 0, 1)
        best = min(best, float(np.linalg.norm(np.array([x, z]) - (a + t * (b - a)))))
    return best


def grid_cell(x, z):
    g = SHOW["layout"]["grid"]
    half = g["tileM"] * g["cols"] / 2
    j, i = int((half - z) // g["tileM"]), int((x + half) // g["tileM"])
    if 0 <= j < g["rows"] and 0 <= i < g["cols"]:
        return int(g["cells"][j][i])
    return -1


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    fail = []
    report = {"cameras": {}, "routes": {}, "hfovDeg": RIG["cameras"][0]["hfovDeg"]}
    for c in RIG["cameras"]:
        rms = plate_rms(c)
        report["cameras"][c["id"]] = {"mount": c["mount"], "text": c["text"], "plateRmsPx": round(rms, 2)}
        print(f"{c['id']} {c['mount']:6s} 写真との差 {rms:.2f}px  位置 {c['pos']}  画角 {c['hfovDeg']}")
        if rms > RMS_LIMIT:
            fail.append(f"{c['id']}: 写真との差 {rms:.2f}px（許容 {RMS_LIMIT}px）")
    room = RIG["room"]
    shelf = RIG["shelf"]
    for path in sorted(ROUTES.glob("*.tsv")):
        if path.name.endswith(".events.tsv"):
            continue
        rows = list(csv.DictReader(path.open(encoding="utf-8"), delimiter="\t"))
        walking = [r for r in rows if float(r["speed"]) > 0.02]
        res = {}
        # 2. 手すり沿いか・区画の中か
        dist = [frame_distance(float(r["x"]), float(r["z"])) for r in walking]
        res["frameDistM"] = [round(min(dist), 3), round(max(dist), 3)]
        if min(dist) < WALK_D - 0.03 or max(dist) > WALK_D + 0.03:
            fail.append(f"{path.stem}: 枠からの距離 {min(dist):.3f}〜{max(dist):.3f}m（手すり沿いは {WALK_D}m）")
        outside = sum(1 for r in walking if grid_cell(float(r["x"]), float(r["z"])) < 0)
        res["outsideGrid"] = outside
        if outside:
            fail.append(f"{path.stem}: 区画の外を {outside} コマ歩いた")
        # 3. 担当が区画どおりか（切替の遅れは暗転とヒステリシスの分だけ許す）
        wrong = sum(1 for r in walking if grid_cell(float(r["x"]), float(r["z"])) != int(float(r["active"])))
        res["activeMatchesGrid"] = round(1 - wrong / len(walking), 3)
        if wrong / len(walking) > 0.12:
            fail.append(f"{path.stem}: 担当カメラが区画と違うコマが {wrong / len(walking):.0%}")
        secs = {c: 0.0 for c in "ABC"}
        for a, b in zip(rows, rows[1:]):
            secs["ABC"[int(float(a["active"]))]] += float(b["t"]) - float(a["t"])
        res["activeSec"] = {k: round(v, 1) for k, v in secs.items()}
        # 4. 離隔
        clear = {}
        for c in RIG["cameras"]:
            if c["mount"] == "arm":
                continue
            d = min(math.hypot(float(r["x"]) - c["pos"][0], float(r["z"]) - c["pos"][2]) for r in rows)
            clear[c["id"]] = round(d, 3)
            need = RING_LIMITS[c["mount"]] + BODY_R
            if d < need:
                fail.append(f"{path.stem}: {c['id']} への離隔 {d:.3f}m（要 {need:.2f}m）")
        wall = min(float(r["x"]) for r in rows) - room["xMin"]
        north = room["zMax"] - max(float(r["z"]) for r in rows)
        shelf_d = shelf["min"][2] - max(float(r["z"]) for r in rows)
        clear.update(westWall=round(wall, 3), northWall=round(north, 3), shelf=round(shelf_d, 3))
        res["clearM"] = clear
        if wall < BODY_R:
            fail.append(f"{path.stem}: 西の壁との距離 {wall:.3f}m")
        if shelf_d < BODY_R:
            fail.append(f"{path.stem}: 棚との距離 {shelf_d:.3f}m")
        # 5. 担当カメラの画に入っている割合（足元・腰・頭）
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
            v[1] += 1 if (head or body or feet) else 0
            v[2] += 1 if (head and feet) else 0
            v[3] += 1 if head else 0
        res["inFrame"] = {k: {"n": v[0], "any": round(v[1] / max(v[0], 1), 3), "full": round(v[2] / max(v[0], 1), 3), "head": round(v[3] / max(v[0], 1), 3)} for k, v in vis.items()}
        for k, v in res["inFrame"].items():
            # 姿勢は写真から決まるので、割合は結果として見る。C は区間の後半で真下を通るので 6 割前後になる
            if v["n"] and v["any"] < 0.5:
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
