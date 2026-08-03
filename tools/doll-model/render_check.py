# 作った人形を多角度でレンダリングして目で確かめる（visual-verification.md §1）。
# 1 角度だけ見て「壊れている / できている」を判断しない。
#
# ライティングは 3 灯（キー / フィル / リム）を**人形の周りに固定**し、カメラだけが回る。
# 「ライトを当てたときに破綻しないか」が主眼なので、暗い現場を模した弱い光ではなく
# ちゃんと当てて撮る。角度によって陰影が動くかどうかもここで見る。
import bpy, math, os, sys
from mathutils import Vector

OUT = sys.argv[sys.argv.index("--") + 1]
bpy.ops.wm.open_mainfile(filepath=os.path.join(OUT, "doll.blend"))

doll = bpy.data.objects["Ichimatsu"]
bb = [doll.matrix_world @ Vector(c) for c in doll.bound_box]
lo = Vector((min(p.x for p in bb), min(p.y for p in bb), min(p.z for p in bb)))
hi = Vector((max(p.x for p in bb), max(p.y for p in bb), max(p.z for p in bb)))
ctr = (lo + hi) / 2
size = max(hi.x - lo.x, hi.z - lo.z)
print(f"[render] bbox x={hi.x-lo.x:.3f} y={hi.y-lo.y:.3f} z={hi.z-lo.z:.3f}")

sc = bpy.context.scene
sc.render.engine = 'BLENDER_EEVEE'
sc.render.resolution_x, sc.render.resolution_y = 620, 1000
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


add_light("key", ctr + Vector((size * 1.5, -size * 1.9, size * 1.7)), 26, size * 1.5)
add_light("fill", ctr + Vector((-size * 2.1, -size * 1.3, size * 0.4)), 8, size * 2.2)
add_light("rim", ctr + Vector((-size * 0.5, size * 2.3, size * 1.6)), 14, size * 1.4)

VIEWS = [("front", 0, 0), ("q30", 30, 6), ("q60", 60, 3), ("side", 90, 0),
         ("q135", 135, 6), ("back", 180, 0), ("up", 25, 28), ("low", 20, -13)]

for name, ang, elev in VIEWS:
    a, e = math.radians(ang), math.radians(elev)
    d = size * 3.0
    cam_data = bpy.data.cameras.new(name)
    cam_data.lens = 62
    cam = bpy.data.objects.new(name, cam_data)
    bpy.context.collection.objects.link(cam)
    cam.location = ctr + Vector((math.sin(a) * d * math.cos(e),
                                 -math.cos(a) * d * math.cos(e),
                                 d * math.sin(e)))
    cam.rotation_euler = (ctr - cam.location).to_track_quat('-Z', 'Y').to_euler()
    sc.camera = cam
    sc.render.filepath = os.path.join(OUT, f"render_{name}.png")
    bpy.ops.render.render(write_still=True)
    print(f"[render] {name}")
