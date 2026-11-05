"""ツインの部屋から固定カメラ A/B/C の深度・法線・物体 ID を焼く（Blender 内で実行）。

  blender --background --factory-startup --python tools/space-warp-study/bake_depth.py -- <出力フォルダ> [幅]
"""
import json, math, os, sys
from pathlib import Path

import bpy
import numpy as np
from mathutils import Vector

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools" / "twin"))
import twin_common as tc  # noqa: E402
import twin_scene as ts  # noqa: E402

argv = sys.argv[sys.argv.index("--") + 1:]
out = Path(argv[0])
W = int(argv[1]) if len(argv) > 1 else 320
H = W * 3 // 4
out.mkdir(parents=True, exist_ok=True)

bpy.ops.wm.read_factory_settings(use_empty=True)
ctx = ts.build_all(air_density=0.0, dust=0, screens={})
scene = bpy.context.scene
dg = bpy.context.evaluated_depsgraph_get()
dg.update()

OWN_MOUNT = ("Arm", "Phone", "Tripod", "Cradle", "Clamp", "Holder", "Lens", "Cable")
meta = {"w": W, "h": H, "cams": {}}
for cam in ctx["rig"]["cameras"]:
    cid = cam["id"]
    m = ctx["views"][cid]
    origin = m.translation.copy()
    r = m.to_3x3()
    tan_h = math.tan(math.radians(cam["hfovDeg"]) / 2)
    tan_v = tan_h * 3 / 4
    depth = np.zeros((H, W), np.float32)
    normal = np.zeros((H, W, 3), np.float32)
    ids = np.zeros((H, W), np.int16)
    names = {}
    for y in range(H):
        ny = 1 - (y + 0.5) / H * 2
        for x in range(W):
            nx = (x + 0.5) / W * 2 - 1
            d_cam = Vector((nx * tan_h, ny * tan_v, -1.0))
            d = (r @ d_cam).normalized()
            # レンズのすぐ前にある自分の機材（B は土台の内側、C はアームの中）は写真に写っていない
            start = origin + d * 0.003
            hit = False
            for _ in range(12):
                hit, loc, nor, _i, obj, _m = scene.ray_cast(dg, start, d, distance=40.0)
                if not hit:
                    break
                own = obj.name.startswith(OWN_MOUNT)
                if not own and (loc - origin).length > 0.03:
                    break
                start = loc + d * 0.002
                hit = False
            if not hit:
                depth[y, x] = 12.0
                continue
            # カメラの光軸方向の距離（1/z で投影が決まる）
            depth[y, x] = -(r.transposed() @ (loc - origin)).z
            normal[y, x] = (nor.x, nor.y, nor.z)
            ids[y, x] = names.setdefault(obj.name, len(names) + 1)
    np.savez_compressed(out / f"depth_{cid}.npz", depth=depth, normal=normal, ids=ids,
                        origin=np.array(origin), rot=np.array(r), tan=np.array([tan_h, tan_v]))
    meta["cams"][cid] = {"names": names, "hfov": cam["hfovDeg"], "depthMed": float(np.median(depth))}
    print("[bake]", cid, "median", float(np.median(depth)), "objs", len(names), flush=True)
(out / "depth_meta.json").write_text(json.dumps(meta, ensure_ascii=False, indent=1), encoding="utf-8")
