# shell.json（正面マスクの押し出し）に腕・ボーン・テクスチャを付けて FBX で出す。
#
# ボーンの命名は ShowActorRig の Generic 経路の規約に合わせる:
#   手 = "left"/"right" を含み "hand" を含む（指の名前は含めない）
#   その **親を 2 つ遡って** 肘・肩を取るので、捻りボーンを挟んではいけない
#   頭 = "head" を含み "top"/"end" を含まない
# 袖と身頃は Root に 1.0。ShowActorRig は上腕ボーンそのものを回すので、袖を肩へ付けると
# 腕を動かすたび袖が丸ごと回る（着物の袖は実物でも上腕に追従せず垂れたまま）。
import bpy, bmesh, json, math, os, sys
from mathutils import Vector

OUT = sys.argv[sys.argv.index("--") + 1]
D = json.load(open(os.path.join(OUT, "shell.json")))
H = D["height"]
ARM_Z = D["arm"]["z"]
TIP_X = D["arm"]["tip_x"]

SHOULDER_X = TIP_X * 0.17          # 胴の中
ELBOW_X = TIP_X * 0.58
WRIST_X = TIP_X * 0.90
HAND_X = TIP_X + 0.026             # 袖口から出る手の先
ARM_R = 0.0068
FACE_UV = (0.244, 0.854)           # 腕・手はテクスチャに写っていないので顔の白磁を引く
SEG = 12

bpy.ops.wm.read_factory_settings(use_empty=True)

# --- 殻（胴・頭・髪・袖）----------------------------------------------------
me = bpy.data.meshes.new("Shell")
me.from_pydata([tuple(v) for v in D["verts"]], [], [list(f) for f in D["faces"]])
me.validate()
shell = bpy.data.objects.new("Shell", me)
bpy.context.collection.objects.link(shell)

uv = me.uv_layers.new(name="UVMap")
src_uv = D["uvs"]
for p in me.polygons:
    for li in p.loop_indices:
        uv.data[li].uv = src_uv[me.loops[li].vertex_index]


# --- 腕（白磁）--------------------------------------------------------------
def arm_mesh(side):
    xs = [SHOULDER_X, ELBOW_X, WRIST_X, HAND_X - 0.012, HAND_X]
    rr = [ARM_R * 1.5, ARM_R, ARM_R * 0.9, ARM_R * 1.25, ARM_R * 0.45]
    verts, faces = [], []
    for x, r in zip(xs, rr):
        for i in range(SEG):
            a = 2 * math.pi * i / SEG
            verts.append((side * x, r * math.sin(a), ARM_Z + r * math.cos(a)))
    for k in range(len(xs) - 1):
        a0, b0 = k * SEG, (k + 1) * SEG
        for i in range(SEG):
            j = (i + 1) % SEG
            q = [a0 + i, a0 + j, b0 + j, b0 + i]
            faces.append(q if side > 0 else list(reversed(q)))
    faces.append(list(range(SEG - 1, -1, -1)) if side > 0 else list(range(SEG)))
    base = (len(xs) - 1) * SEG
    tip = list(range(base, base + SEG))
    faces.append(tip if side > 0 else list(reversed(tip)))
    m2 = bpy.data.meshes.new(f"Arm{side}")
    m2.from_pydata(verts, [], faces)
    m2.validate()
    u2 = m2.uv_layers.new(name="UVMap")
    for li in range(len(m2.loops)):
        u2.data[li].uv = FACE_UV
    ob = bpy.data.objects.new(f"Arm{side}", m2)
    bpy.context.collection.objects.link(ob)
    # 腕の頂点に印を付ける。join した後は座標だけでは袖と区別できない
    # （袖は前後に薄いので、腕の高さの袖の頂点が「腕の筒の中」の判定に入る。
    #  実測: 腕を上げると袖の上端が山形に引っ張られた）。
    vg = ob.vertex_groups.new(name="ARMMARK")
    vg.add(list(range(len(m2.vertices))), 1.0, 'REPLACE')
    return ob


arms = [arm_mesh(+1), arm_mesh(-1)]
for ob in [shell] + arms:
    ob.select_set(True)
bpy.context.view_layer.objects.active = shell
bpy.ops.object.join()
doll = bpy.context.object
doll.name = "Ichimatsu"
me = doll.data

bpy.ops.object.mode_set(mode='EDIT')
bpy.ops.mesh.select_all(action='SELECT')
bpy.ops.mesh.remove_doubles(threshold=1e-6)
bpy.ops.mesh.normals_make_consistent(inside=False)
bpy.ops.object.mode_set(mode='OBJECT')
bpy.ops.object.shade_smooth()

# --- マテリアル -------------------------------------------------------------
mat = bpy.data.materials.new("Ichimatsu")
mat.use_nodes = True
nt = mat.node_tree
bsdf = nt.nodes["Principled BSDF"]
tex_path = os.path.join(OUT, "doll_albedo.png")
img_node = nt.nodes.new("ShaderNodeTexImage")
img_node.image = bpy.data.images.load(tex_path)
nt.links.new(bsdf.inputs["Base Color"], img_node.outputs["Color"])
bsdf.inputs["Roughness"].default_value = 0.9
if "Specular IOR Level" in bsdf.inputs:
    bsdf.inputs["Specular IOR Level"].default_value = 0.15
me.materials.append(mat)

# --- ボーン -----------------------------------------------------------------
bpy.ops.object.armature_add(location=(0, 0, 0))
arm = bpy.context.object
arm.name = "DollRig"
bpy.ops.object.mode_set(mode='EDIT')
eb = arm.data.edit_bones
eb.remove(eb[0])


def bone(name, head, tail, parent=None):
    b = eb.new(name)
    b.head, b.tail = Vector(head), Vector(tail)
    if parent:
        b.parent, b.use_connect = parent, False
    return b


HEAD_BASE = ARM_Z + H * 0.02        # 首の付け根（腕の高さのすぐ上）
root = bone("Root", (0, 0, H * 0.38), (0, 0, H * 0.60))
bone("Head", (0, 0, HEAD_BASE), (0, 0, H * 0.97), root)
for side, tag in ((+1, "Left"), (-1, "Right")):
    up = bone(f"{tag}Arm", (side * SHOULDER_X, 0, ARM_Z), (side * ELBOW_X, 0, ARM_Z), root)
    lo = bone(f"{tag}ForeArm", (side * ELBOW_X, 0, ARM_Z), (side * WRIST_X, 0, ARM_Z), up)
    bone(f"{tag}Hand", (side * WRIST_X, 0, ARM_Z), (side * HAND_X, 0, ARM_Z), lo)
bpy.ops.object.mode_set(mode='OBJECT')

# --- ウェイト ---------------------------------------------------------------
names = ["Root", "Head", "LeftArm", "LeftForeArm", "LeftHand",
         "RightArm", "RightForeArm", "RightHand"]
g = {n: doll.vertex_groups.new(name=n) for n in names}
mark = doll.vertex_groups.get("ARMMARK")
mark_idx = mark.index if mark else -1
n_arm = 0
for v in me.vertices:
    co = v.co
    ax = abs(co.x)
    # 腕メッシュの頂点だけを拾う（印で判定する。座標だと袖を巻き込む）
    on_arm = False
    for gel in v.groups:
        if gel.group == mark_idx and gel.weight > 0.5:
            on_arm = True
            break
    if on_arm:
        n_arm += 1
        tag = "Left" if co.x > 0 else "Right"
        if ax <= ELBOW_X:
            w = (ax - SHOULDER_X) / max(1e-6, ELBOW_X - SHOULDER_X)
            g[f"{tag}Arm"].add([v.index], 1.0 - max(0.0, min(1.0, w)), 'REPLACE')
            g[f"{tag}ForeArm"].add([v.index], max(0.0, min(1.0, w)), 'REPLACE')
        elif ax <= WRIST_X:
            w = (ax - ELBOW_X) / max(1e-6, WRIST_X - ELBOW_X)
            g[f"{tag}ForeArm"].add([v.index], 1.0 - max(0.0, min(1.0, w)), 'REPLACE')
            g[f"{tag}Hand"].add([v.index], max(0.0, min(1.0, w)), 'REPLACE')
        else:
            g[f"{tag}Hand"].add([v.index], 1.0, 'REPLACE')
    elif co.z > HEAD_BASE and ax < H * 0.16:
        g["Head"].add([v.index], 1.0, 'REPLACE')
    else:
        g["Root"].add([v.index], 1.0, 'REPLACE')

if mark:
    doll.vertex_groups.remove(mark)   # 印は書き出さない

doll.parent = arm
doll.modifiers.new("Armature", 'ARMATURE').object = arm

out = os.path.join(OUT, "Ichimatsu.fbx")
bpy.ops.object.select_all(action='SELECT')
bpy.ops.export_scene.fbx(
    filepath=out, use_selection=True, global_scale=1.0,
    axis_forward='-Z', axis_up='Y', object_types={'ARMATURE', 'MESH'},
    add_leaf_bones=False, bake_anim=False,
    path_mode='COPY', embed_textures=False, mesh_smooth_type='FACE')
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(OUT, "doll.blend"))
print(f"[doll] verts={len(me.vertices)} polys={len(me.polygons)} armverts={n_arm}")
print(f"[doll] armZ={ARM_Z:.3f} tip={TIP_X:.3f} hand={HAND_X:.3f} exported")
