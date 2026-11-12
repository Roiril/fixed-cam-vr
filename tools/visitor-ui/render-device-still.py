"""博士の説明に出す「観測装置」の絵を、Quest 3 のモデルから撮る（LEDGER 0328 の続き）。

    blender --background --factory-startup --python tools/visitor-ui/render-device-still.py -- <meta quest 3.glb> <出力.png> [--flip 0|1] [--spp 256]

モデルはユーザーが渡した `meta-quest-3.zip`（`source/meta quest 3.glb`）。リポジトリには入れない。
撮った PNG（1280×960）の (150,140)-(1170,905) を切って 960×720 の JPEG（`asset/device-quest3-v1.jpg`）にする。

置き方: 本体は床に置く。左コントローラーは、いちばん平たくなる向きを探して本体の右手前に寝かせる
（`--flip 1` で裏返す。ボタンのある面が上を向く側を、撮った画を見て選ぶ）。
光はツインの部屋と同じく、上の裸電球 1 灯と弱い回り込みだけ。
"""
import math
import random
import sys

import bpy
from mathutils import Euler, Vector

argv = sys.argv[sys.argv.index("--") + 1:]
glb, out = argv[0], argv[1]
flip = int(argv[argv.index("--flip") + 1]) if "--flip" in argv else 0
spp = int(argv[argv.index("--spp") + 1]) if "--spp" in argv else 256

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=glb)
sc = bpy.context.scene
objs = {o.name: o for o in sc.objects}
head = [objs["LOD1_backClips_GEO001"], objs["LOD1_frontLens_01_GEO001"]]
ctrl = objs["controller_left"]
objs["m_shell002"].hide_render = True  # 右のコントローラーは体験者に渡さない


def world_verts(o):
    return [o.matrix_world @ v.co for v in o.data.vertices]


# 親の変換を焼いてから動かす
for o in head + [ctrl]:
    m = o.matrix_world.copy()
    o.parent = None
    o.matrix_world = m
bpy.context.view_layer.update()

floor_z = min(v.z for o in head for v in world_verts(o))

# コントローラーをいちばん平たく寝かせる向きを探す（頂点を間引いて高さを測る）
base = ctrl.matrix_world.copy()
pts = world_verts(ctrl)[::40]
center = sum(pts, Vector()) / len(pts)
rel = [p - center for p in pts]
random.seed(7)
best = None
for _ in range(4000):
    e = Euler((random.uniform(0, math.tau), random.uniform(0, math.tau), 0.0))
    m = e.to_matrix()
    zs = [(m @ r).z for r in rel]
    h = max(zs) - min(zs)
    if best is None or h < best[0]:
        best = (h, e)
rot = best[1].to_matrix().to_4x4()
if flip:
    rot = Euler((math.pi, 0, 0)).to_matrix().to_4x4() @ rot
from mathutils import Matrix
ctrl.matrix_world = Matrix.Translation(center) @ rot @ Matrix.Translation(-center) @ base
bpy.context.view_layer.update()
# 床の上で、握りが手前へ伸びる向きに回して本体の右手前へ
pts = world_verts(ctrl)
c2 = sum(pts, Vector()) / len(pts)
yaw = math.radians(float(argv[argv.index("--yaw") + 1])) if "--yaw" in argv else math.radians(35)
ctrl.matrix_world = Matrix.Translation(c2) @ Euler((0, 0, yaw)).to_matrix().to_4x4() @ Matrix.Translation(-c2) @ ctrl.matrix_world
bpy.context.view_layer.update()
pts = world_verts(ctrl)
c3 = sum(pts, Vector()) / len(pts)
target = Vector((0.21, -0.07, 0))
dz = floor_z - min(p.z for p in pts)
ctrl.matrix_world = Matrix.Translation(Vector((target.x - c3.x, target.y - c3.y, dz))) @ ctrl.matrix_world

# 床（黒い布のような面）
bpy.ops.mesh.primitive_plane_add(size=6, location=(0, 0, floor_z))
floor = bpy.context.object
mat = bpy.data.materials.new("cloth")
mat.use_nodes = True
nt = mat.node_tree
bsdf = next(n for n in nt.nodes if n.type == "BSDF_PRINCIPLED")
bsdf.inputs["Base Color"].default_value = (0.035, 0.03, 0.026, 1)
bsdf.inputs["Roughness"].default_value = 0.92
noise = nt.nodes.new("ShaderNodeTexNoise")
noise.inputs["Scale"].default_value = 260
noise.inputs["Detail"].default_value = 6
bump = nt.nodes.new("ShaderNodeBump")
bump.inputs["Strength"].default_value = 0.35
nt.links.new(noise.outputs["Fac"], bump.inputs["Height"])
nt.links.new(bump.outputs["Normal"], bsdf.inputs["Normal"])
floor.data.materials.append(mat)

# 光: 上の裸電球 1 灯（暖色）と、弱い冷たい回り込み
def light(kind, loc, energy, color, size=0.1):
    d = bpy.data.lights.new(kind, kind)
    d.energy = energy
    d.color = color
    if kind == "AREA":
        d.size = size
    else:
        d.shadow_soft_size = size
    o = bpy.data.objects.new(kind, d)
    o.location = loc
    sc.collection.objects.link(o)
    return o

light("POINT", (-0.35, -0.25, 0.85), 26, (1.0, 0.86, 0.66), 0.03)
fill = light("AREA", (0.9, -0.9, 0.5), 5, (0.75, 0.86, 1.0), 1.2)
fill.rotation_euler = (Vector((0, 0, 0)) - fill.location).to_track_quat("-Z", "Y").to_euler()
w = bpy.data.worlds.new("w")
sc.world = w
w.use_nodes = True
w.node_tree.nodes["Background"].inputs[0].default_value = (0.01, 0.012, 0.012, 1)

cam_d = bpy.data.cameras.new("cam")
cam = bpy.data.objects.new("cam", cam_d)
sc.collection.objects.link(cam)
cam.location = (0.36, -0.78, 0.36)
look = Vector((0.06, -0.03, floor_z + 0.06))
cam.rotation_euler = (look - cam.location).to_track_quat("-Z", "Y").to_euler()
cam_d.lens = 50
cam_d.dof.use_dof = True
cam_d.dof.focus_distance = (look - cam.location).length
cam_d.dof.aperture_fstop = 4.0
sc.camera = cam

sc.render.engine = "CYCLES"
sc.cycles.samples = spp
sc.cycles.use_denoising = True
prefs = bpy.context.preferences.addons["cycles"].preferences
for kind in ("OPTIX", "CUDA"):
    try:
        prefs.compute_device_type = kind
        prefs.get_devices()
        break
    except Exception:
        pass
for d in prefs.devices:
    d.use = d.type != "CPU"
sc.cycles.device = "GPU"
print("[device] gpu:", [d.name for d in prefs.devices if d.use])
sc.view_settings.view_transform = "Filmic" if "Filmic" in [i.identifier for i in bpy.types.ColorManagedViewSettings.bl_rna.properties["view_transform"].enum_items] else sc.view_settings.view_transform
sc.render.resolution_x = 1280
sc.render.resolution_y = 960
sc.render.filepath = out
bpy.ops.render.render(write_still=True)
print("[device] wrote", out)
