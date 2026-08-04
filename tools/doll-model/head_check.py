# 頭だけを**フル解像度**で多角度に撮り、実物写真を隣へ並べる。
#
# visual-verification.md §10 の 4 点（フル解像度 / 実物と並置 / 何に見えるかを言う /
# 中間生成物を開く）のうち、機械で用意できる前 2 つをここで固定する。
# 前セッションは頭を横 3 つ並べた縮小画像で品質を判定して壊れているものを見落とした。
# **並置は場所を決めるためだけに使い、判定は 1 枚ずつのフル解像度で行うこと。**
#
#   blender --background --python head_check.py -- <dir>
#   → head_front.png / head_side.png / head_q45.png / head_back.png / head_top.png
#     head_vs_side.png（実物の側面写真と横並び）
import bpy, math, os, sys
from mathutils import Vector

OUT = sys.argv[sys.argv.index("--") + 1]
bpy.ops.wm.open_mainfile(filepath=os.path.join(OUT, "doll.blend"))

doll = bpy.data.objects["Ichimatsu"]
bb = [doll.matrix_world @ Vector(c) for c in doll.bound_box]
lo = Vector((min(p.x for p in bb), min(p.y for p in bb), min(p.z for p in bb)))
hi = Vector((max(p.x for p in bb), max(p.y for p in bb), max(p.z for p in bb)))
top = hi.z
H = hi.z - lo.z
# 頭は全高のおよそ上 22%。その中心へ寄せる
ctr = Vector(((lo.x + hi.x) / 2, (lo.y + hi.y) / 2, top - H * 0.105))
size = H * 0.23

sc = bpy.context.scene
sc.render.engine = 'BLENDER_EEVEE'
sc.render.resolution_x, sc.render.resolution_y = 1000, 1100
sc.render.film_transparent = False
try:
    sc.view_settings.view_transform = 'Filmic'
except Exception:
    pass

w = bpy.data.worlds.new("W")
w.use_nodes = True
nt = w.node_tree
bg = nt.nodes.get("Background") or nt.nodes.new("ShaderNodeBackground")
out_node = nt.nodes.get("World Output") or nt.nodes.new("ShaderNodeOutputWorld")
if not bg.outputs[0].links:
    nt.links.new(out_node.inputs["Surface"], bg.outputs[0])
bg.inputs[0].default_value = (0.05, 0.055, 0.07, 1)
bg.inputs[1].default_value = 0.55
sc.world = w


def add_light(name, loc, energy, sz):
    lt = bpy.data.lights.new(name, 'AREA')
    lt.energy, lt.size = energy, sz
    ob = bpy.data.objects.new(name, lt)
    bpy.context.collection.objects.link(ob)
    ob.location = loc
    ob.rotation_euler = (ctr - Vector(loc)).to_track_quat('-Z', 'Y').to_euler()
    return ob


# ⚠ ライトは人形の周りに固定してカメラだけ回す（render_check.py と同じ流儀）。
#    カメラに追従させると、どの角度でも同じ陰影になって「角度で破綻しないか」が見えない。
# ⚠ **強さは距離の 2 乗で効く。** render_check.py の値をそのまま持ってくると、頭に寄せた分
#    （size が全高の 0.23 倍）だけ約 19 倍明るくなり、**暗い髪が真っ白に飛んで何も判定できない**
#    （最初にこれをやった）。同じ見えにするため energy を size 比の 2 乗で割ってある。
_K = (0.23 ** 2)
add_light("key", ctr + Vector((size * 1.5, -size * 1.9, size * 1.7)), 26 * _K, size * 1.5)
add_light("fill", ctr + Vector((-size * 2.1, -size * 1.3, size * 0.4)), 8 * _K, size * 2.2)
add_light("rim", ctr + Vector((-size * 0.5, size * 2.3, size * 1.6)), 14 * _K, size * 1.4)

VIEWS = [("front", 0, 0), ("q45", 45, 5), ("side", 90, 0), ("back", 180, 0), ("top", 20, 55)]

for name, ang, elev in VIEWS:
    a, e = math.radians(ang), math.radians(elev)
    d = size * 3.4
    cam_data = bpy.data.cameras.new(name)
    cam_data.lens = 85
    cam = bpy.data.objects.new(name, cam_data)
    bpy.context.collection.objects.link(cam)
    cam.location = ctr + Vector((math.sin(a) * d * math.cos(e),
                                 -math.cos(a) * d * math.cos(e),
                                 d * math.sin(e)))
    cam.rotation_euler = (ctr - cam.location).to_track_quat('-Z', 'Y').to_euler()
    sc.camera = cam
    sc.render.filepath = os.path.join(OUT, f"head_{name}.png")
    bpy.ops.render.render(write_still=True)
    print(f"[head] {name}")
print("[head] done")
