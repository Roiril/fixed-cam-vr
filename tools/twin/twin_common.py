"""デジタルツイン（Cycles）の共通部品。座標は Unity (x, y, z) → Blender (x, z, y)。"""
from __future__ import annotations

import json
import math
import os
from pathlib import Path

import bpy
from mathutils import Matrix, Vector

ROOT = Path(__file__).resolve().parents[2]
SHOW = ROOT / "Assets" / "StreamingAssets" / "show" / "show.json"
RIG = Path(__file__).resolve().parent / "rig.json"
RENDERS = Path(os.environ.get("TWIN_RENDERS", r"G:\マイドライブ\shubie\renders\twin-cycles"))
WORK = Path(os.environ.get("TWIN_WORK", str(ROOT / "Logs" / "twin-cycles")))

# 部屋の殻。奥の 2 面は実物の部屋の壁（rig.json の room）。Unity の VirtualRoom も同じ値を読む
_ROOM = json.loads(RIG.read_text(encoding="utf-8"))["room"]
ROOM_X = (_ROOM["xMin"], 4.6)
ROOM_Y = (-4.6, _ROOM["zMax"])
ROOM_H = 3.2
BULB = Vector((0.02, -0.02, 2.55))
GRID_HALF = 0.9

# 実物の枠の実測（tools/walk-guide/build_walk_guide.py。元は Joint.blend）
FRAME_TOP = 1.853  # 上桟の高さ
RAIL_Y = 0.948  # 手すりの高さ
RAIL_D = 0.08  # 枠の芯から手すりまで
RAIL_EXT = 0.045  # 手すりが支柱より先へ出る長さ（U の芯まで）


def u2b(x: float, y: float, z: float) -> Vector:
    return Vector((x, z, y))


def rail_loop(corner, end_x, end_z) -> list[Vector]:
    """手すりの輪（Blender の xy・閉路）。内側の 2 本は角で突き当たり、外側は角と 2 つの端を回る。"""
    cx, cy = corner
    ex = end_x[0] + RAIL_EXT
    sy = end_z[1] - RAIL_EXT
    d = RAIL_D
    pts = [Vector((cx + d, cy - d, 0)), Vector((cx + d, sy, 0))]

    def arc(ox, oy, start, sweep, n):
        for i in range(1, n + 1):
            a = start + sweep * i / n
            pts.append(Vector((ox + d * math.cos(a), oy + d * math.sin(a), 0)))

    arc(cx, sy, 0.0, -math.pi, 14)  # w2 の端
    pts.append(Vector((cx - d, cy, 0)))
    arc(cx, cy, math.pi, -0.5 * math.pi, 8)  # 外の角
    pts.append(Vector((ex, cy + d, 0)))
    arc(ex, cy, 0.5 * math.pi, -math.pi, 14)  # w1 の端
    return pts


def add_poly_tube(name, points, r, mat=None, coll="Room", cyclic=False) -> bpy.types.Object:
    """折れ線をそのままなぞる管。"""
    c = bpy.data.curves.new(name, "CURVE")
    c.dimensions = "3D"
    c.bevel_depth = r
    c.bevel_resolution = 4
    c.use_fill_caps = True
    s = c.splines.new("POLY")
    s.points.add(len(points) - 1)
    for sp, p in zip(s.points, points):
        sp.co = (p.x, p.y, p.z, 1.0)
    s.use_cyclic_u = cyclic
    ob = bpy.data.objects.new(name, c)
    collection(coll).objects.link(ob)
    if mat is not None:
        c.materials.append(mat)
    return ob


def load_show() -> dict:
    return json.loads(SHOW.read_text(encoding="utf-8-sig"))


def load_rig() -> dict:
    return json.loads(RIG.read_text(encoding="utf-8"))


def walls_from_show(show: dict) -> list[dict]:
    return [w for w in show["layout"]["room"]["walls"]]


def frame_from_show(show: dict):
    """L の枠の角・+x の端・-z の端（Blender の xy）。"""
    w1, w2 = walls_from_show(show)
    return (w1["x1"], w1["z1"]), (w1["x2"], w1["z2"]), (w2["x2"], w2["z2"])


def collection(name: str) -> bpy.types.Collection:
    c = bpy.data.collections.get(name)
    if c is None:
        c = bpy.data.collections.new(name)
        bpy.context.scene.collection.children.link(c)
    return c


def add_mesh(name: str, verts, faces, mat=None, coll: str = "Room", smooth: bool = False, edges=()) -> bpy.types.Object:
    me = bpy.data.meshes.new(name)
    me.from_pydata([tuple(v) for v in verts], list(edges), [tuple(f) for f in faces])
    me.update()
    if smooth:
        me.polygons.foreach_set("use_smooth", [True] * len(me.polygons))
        me.update()
    ob = bpy.data.objects.new(name, me)
    collection(coll).objects.link(ob)
    if mat is not None:
        me.materials.append(mat)
    return ob


def box_verts(cmin, cmax):
    x0, y0, z0 = cmin
    x1, y1, z1 = cmax
    v = [(x0, y0, z0), (x1, y0, z0), (x1, y1, z0), (x0, y1, z0), (x0, y0, z1), (x1, y0, z1), (x1, y1, z1), (x0, y1, z1)]
    f = [(3, 2, 1, 0), (4, 5, 6, 7), (0, 1, 5, 4), (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)]
    return v, f


def add_box(name, cmin, cmax, mat=None, coll="Room", bevel=0.0) -> bpy.types.Object:
    v, f = box_verts(cmin, cmax)
    ob = add_mesh(name, v, f, mat, coll)
    if bevel > 0:
        m = ob.modifiers.new("bevel", "BEVEL")
        m.width = bevel
        m.segments = 3
        m.limit_method = "ANGLE"
        m.harden_normals = False
        for p in ob.data.polygons:
            p.use_smooth = True
    return ob


def add_cyl(name, a, b, r, mat=None, coll="Room", seg=24, cap=True) -> bpy.types.Object:
    a, b = Vector(a), Vector(b)
    d = b - a
    q = Vector((0, 0, 1)).rotation_difference(d.normalized())
    verts, faces = [], []
    for k in range(seg):
        t = 2 * math.pi * k / seg
        o = q @ Vector((math.cos(t) * r, math.sin(t) * r, 0))
        verts.append(a + o)
        verts.append(b + o)
    for k in range(seg):
        n = (k + 1) % seg
        faces.append((2 * k, 2 * n, 2 * n + 1, 2 * k + 1))
    if cap:
        faces.append(tuple(2 * k for k in reversed(range(seg))))
        faces.append(tuple(2 * k + 1 for k in range(seg)))
    ob = add_mesh(name, verts, faces, mat, coll)
    # 側面だけ滑らかに（端面は平らのまま）
    for p in ob.data.polygons:
        p.use_smooth = len(p.vertices) == 4
    return ob


def add_lathe(name, profile, mat=None, coll="Room", seg=48, center=(0, 0, 0)) -> bpy.types.Object:
    """profile: [(半径, 高さ), ...] を Z 軸まわりに回す。"""
    verts, faces = [], []
    n = len(profile)
    for k in range(seg):
        t = 2 * math.pi * k / seg
        for r, z in profile:
            verts.append((center[0] + r * math.cos(t), center[1] + r * math.sin(t), center[2] + z))
    for k in range(seg):
        m = (k + 1) % seg
        for i in range(n - 1):
            faces.append((k * n + i, m * n + i, m * n + i + 1, k * n + i + 1))
    ob = add_mesh(name, verts, faces, mat, coll, smooth=True)
    return ob


def add_tube(name, points, r, mat=None, coll="Room", res=32) -> bpy.types.Object:
    """点列を通る滑らかな管（ケーブル）。"""
    c = bpy.data.curves.new(name, "CURVE")
    c.dimensions = "3D"
    c.bevel_depth = r
    c.bevel_resolution = 3
    c.resolution_u = res
    s = c.splines.new("BEZIER")
    s.bezier_points.add(len(points) - 1)
    for bp, p in zip(s.bezier_points, points):
        bp.co = p
        bp.handle_left_type = bp.handle_right_type = "AUTO"
    ob = bpy.data.objects.new(name, c)
    collection(coll).objects.link(ob)
    if mat is not None:
        c.materials.append(mat)
    return ob


def look_matrix(pos: Vector, target: Vector, roll_deg: float = 0.0) -> Matrix:
    fwd = (target - pos).normalized()
    q = fwd.to_track_quat("-Z", "Y")
    m = q.to_matrix().to_4x4()
    if roll_deg:
        m = m @ Matrix.Rotation(math.radians(roll_deg), 4, "Z")
    m.translation = pos
    return m
