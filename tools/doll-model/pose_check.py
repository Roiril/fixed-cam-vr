# リグの動作確認。腕のボーンを回して「腕だけが動き、袖と胴は動かない」ことを見る。
# （袖を Root に固定したのは、ShowActorRig が上腕ボーンそのものを回すため。
#   肩に付けると腕を振るたび袖が丸ごと回ってしまう）
import bpy, math, os, sys
from mathutils import Vector

OUT = sys.argv[sys.argv.index("--") + 1]
bpy.ops.wm.open_mainfile(filepath=os.path.join(OUT, "doll.blend"))

doll = bpy.data.objects["Ichimatsu"]
rig = bpy.data.objects["DollRig"]
bb = [doll.matrix_world @ Vector(c) for c in doll.bound_box]
lo = Vector((min(p.x for p in bb), min(p.y for p in bb), min(p.z for p in bb)))
hi = Vector((max(p.x for p in bb), max(p.y for p in bb), max(p.z for p in bb)))
ctr, size = (lo + hi) / 2, max(hi.x - lo.x, hi.z - lo.z)

sc = bpy.context.scene
sc.render.engine = 'BLENDER_EEVEE'
sc.render.resolution_x, sc.render.resolution_y = 620, 900
w = bpy.data.worlds.new("W"); w.use_nodes = True
nt = w.node_tree
bg = nt.nodes.get("Background") or nt.nodes.new("ShaderNodeBackground")
bg.inputs[0].default_value = (0.06, 0.06, 0.07, 1)
bg.inputs[1].default_value = 0.7
sc.world = w

cam_d = bpy.data.cameras.new("c"); cam_d.lens = 50
cam = bpy.data.objects.new("c", cam_d); bpy.context.collection.objects.link(cam)
cam.location = ctr + Vector((0, -size * 3.0, 0))
cam.rotation_euler = (ctr - cam.location).to_track_quat('-Z', 'Y').to_euler()
sc.camera = cam
key = bpy.data.objects.new("L", bpy.data.lights.new("l", 'AREA'))
key.data.energy, key.data.size = 70, size * 2
bpy.context.collection.objects.link(key)
key.location = cam.location + Vector((size, 0, size))
key.rotation_euler = (ctr - key.location).to_track_quat('-Z', 'Y').to_euler()

bpy.context.view_layer.objects.active = rig
bpy.ops.object.mode_set(mode='POSE')
for b in rig.pose.bones:
    b.rotation_mode = 'XYZ'


def shot(name, poses):
    for b in rig.pose.bones:
        b.rotation_euler = (0, 0, 0)
    for bone, rot in poses.items():
        rig.pose.bones[bone].rotation_euler = tuple(math.radians(a) for a in rot)
    bpy.context.view_layer.update()
    sc.render.filepath = os.path.join(OUT, f"pose_{name}.png")
    bpy.ops.render.render(write_still=True)
    print(f"[pose] {name}")


shot("rest", {})
shot("armsup", {"LeftArm": (0, 0, -55), "RightArm": (0, 0, 55)})
shot("armsdown", {"LeftArm": (0, 0, 70), "RightArm": (0, 0, -70)})
shot("bend", {"LeftArm": (0, 0, -30), "LeftForeArm": (0, 0, -55),
              "RightArm": (0, 0, 30), "RightForeArm": (0, 0, 55)})
