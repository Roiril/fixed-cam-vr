"""代役の歩きを描かずに数で見る。関節の位置を 15fps で取り、コマごとの加速度（2 階差分）が大きい所を出す。

    blender --background --factory-startup --python tools/twin/probe_gait.py -- [--route forward] [--t0 0] [--t1 12] [--out Logs/twin-cycles/gait.tsv]

「ぴくつく」は、位置の 2 階差分が周りより飛び抜けて大きいコマとして出る。描くより先にここで確かめる。
"""
import sys
from pathlib import Path

import bpy

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import twin_common as tc  # noqa: E402
import twin_person as tp  # noqa: E402
import twin_scene as ts  # noqa: E402

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []


def opt(name, default):
    return argv[argv.index(name) + 1] if name in argv else default


route = opt("--route", "forward")
t0, t1 = float(opt("--t0", 0)), float(opt("--t1", 12))
fps = float(opt("--fps", 15))
out = Path(opt("--out", str(tc.WORK / "gait.tsv")))

bpy.ops.wm.read_factory_settings(use_empty=True)
ctx = ts.build_all(air_density=0.0, dust=0)
person = tp.Person(ctx, route)
names = ["Hips", "Head", "RightHand", "LeftHand", "RightFoot", "LeftFoot", "RightForeArm", "LeftForeArm"]
rows = []
n = int((t1 - t0) * fps) + 1
for i in range(n):
    t = t0 + i / fps
    r = person.pose_at(t)
    bpy.context.view_layer.update()
    mw = person.arm.matrix_world
    row = [t, r["speed"], person.ik.influence]
    for nm in names:
        p = mw @ person.arm.pose.bones[person.prefix + nm].head
        row += [p.x, p.y, p.z]
    rows.append(row)
out.parent.mkdir(parents=True, exist_ok=True)
head = ["t", "speed", "ik"] + [f"{nm}.{a}" for nm in names for a in "xyz"]
out.write_text("\n".join(["\t".join(head)] + ["\t".join(f"{v:.5f}" for v in row) for row in rows]), encoding="utf-8")
# 2 階差分の大きさ（m/コマ^2）を関節ごとに
print("[gait] t  ik  " + "  ".join(names))
for i in range(1, len(rows) - 1):
    acc = []
    for k in range(len(names)):
        a, b, c = (rows[i - 1][3 + 3 * k:6 + 3 * k], rows[i][3 + 3 * k:6 + 3 * k], rows[i + 1][3 + 3 * k:6 + 3 * k])
        acc.append(sum((a[j] - 2 * b[j] + c[j]) ** 2 for j in range(3)) ** 0.5)
    print(f"[gait] {rows[i][0]:5.2f} {rows[i][2]:.2f} " + " ".join(f"{v * 1000:5.1f}" for v in acc))
