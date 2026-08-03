# 作った人形を多角度でレンダリングして目で確かめる（visual-verification.md §1）。
# 1 角度だけ見て「壊れている / できている」を判断しない。
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
sc.render.resolution_x, sc.render.resolution_y = 520, 900
sc.render.film_transparent = False
w = bpy.data.worlds.new("W")
w.use_nodes = True
nt = w.node_tree
bg = nt.nodes.get("Background") or nt.nodes.new("ShaderNodeBackground")
out_node = nt.nodes.get("World Output") or nt.nodes.new("ShaderNodeOutputWorld")
if not bg.outputs[0].links:
    nt.links.new(out_node.inputs["Surface"], bg.outputs[0])
bg.inputs[0].default_value = (0.06, 0.06, 0.07, 1)
bg.inputs[1].default_value = 0.6
sc.world = w

for name, ang, elev in [("front", 0), ("q45", 45), ("side", 90), ("back", 180)][:0] or \
        [("front", 0, 0), ("q45", 45, 0), ("side", 90, 0), ("back", 180, 0),
         ("q_up", 30, 22)]:
    a = math.radians(ang)
    e = math.radians(elev)
    d = size * 3.1
    cam_data = bpy.data.cameras.new(name)
    cam_data.lens = 50
    cam = bpy.data.objects.new(name, cam_data)
    bpy.context.collection.objects.link(cam)
    cam.location = ctr + Vector((math.sin(a) * d * math.cos(e),
                                 -math.cos(a) * d * math.cos(e),
                                 d * math.sin(e)))
    dir_v = ctr - cam.location
    cam.rotation_euler = dir_v.to_track_quat('-Z', 'Y').to_euler()
    sc.camera = cam

    key = bpy.data.objects.new(f"L{name}", bpy.data.lights.new(f"l{name}", 'AREA'))
    key.data.energy = 60
    key.data.size = size * 2
    bpy.context.collection.objects.link(key)
    key.location = cam.location + Vector((size, -size * 0.4, size * 1.2))
    key.rotation_euler = (ctr - key.location).to_track_quat('-Z', 'Y').to_euler()

    sc.render.filepath = os.path.join(OUT, f"render_{name}.png")
    bpy.ops.render.render(write_still=True)
    print(f"[render] {name}")
