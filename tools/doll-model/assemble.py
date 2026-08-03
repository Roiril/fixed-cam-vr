# shell.json（正面マスクの押し出し）に腕・ボーン・テクスチャを付けて FBX で出す。
#
# ボーンの命名は ShowActorRig の Generic 経路の規約に合わせる:
#   手 = "left"/"right" を含み "hand" を含む（指の名前は含めない）
#   その **親を 2 つ遡って** 肘・肩を取るので、捻りボーンを挟んではいけない
#   頭 = "head" を含み "top"/"end" を含まない
#
# 袖のウェイト（2026-08-03 改訂）:
#   最初は袖を Root に 1.0 で固定していた（＝腕を振っても袖は不動）。
#   実物の袖は上腕の回転に剛体で付いてくるわけではないが、**付け根は腕に持ち上げられ、
#   下端は垂れたまま**になる。そこで「腕の高さからどれだけ下か」で腕→Root へ配分し、
#   横方向は肩・肘・手首へ配分する。布シミュレーションの一次近似。
import bpy, bmesh, json, math, os, sys
from mathutils import Vector

OUT = sys.argv[sys.argv.index("--") + 1]
D = json.load(open(os.path.join(OUT, "shell.json")))
H = D["height"]
# ⚠ 袖の上端と同じ高さに置くと、腕の上半分が袖からはみ出して「棒が刺さっている」ように見える。
#    実物は袖に包まれていて、袖口から先だけが出ている。少し沈める。
ARM_Z = D["arm"]["z"] - 0.013
TIP_X = D["arm"]["tip_x"]
BODY_HALF = D.get("body_half", 0.054)

SHOULDER_X = TIP_X * 0.17          # 胴の中
ELBOW_X = TIP_X * 0.58
WRIST_X = TIP_X * 0.90
HAND_X = TIP_X + 0.026             # 袖口から出る手の先
ARM_R = 0.0060
SLEEVE_FALL = 0.105                # 袖が腕に追従しなくなるまでの落差 (m)
ARM_V0 = D.get("arm_v0", 0.0)      # アトラス下段（手のストリップ）の v 上限
SEG = 16

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
    """肩から手先までの筒。**手だけ断面を平たくする**（前後に薄く上下に広い）。
    円柱のままだと棒に見え、写真の「袖から出た手のひら」に見えない。

    UV はアトラス下段の「手のストリップ」を引く（旧: 顔の白磁の 1 点に固定＝完全な単色で、
    ライトを当てると滑らかな円柱の陰影だけが出て、いかにも CG の棒に見えた）。
      u = 腕の長さ方向（手先がストリップの外側）
      v = 周方向を上下に写す（上面が画像の上・下面が画像の下）
    """
    # ⚠ 肩から作らない。袖は前後に薄いので、袖の中を通る腕が布を貫通して見える。
    #    実物で見えるのは袖口から先だけ。メッシュは袖の中程から始める（ボーンは肩から）。
    x0 = TIP_X * 0.62
    xs = [x0, (x0 + WRIST_X) * 0.5, WRIST_X, HAND_X - 0.014, HAND_X]
    #     (前後 ry, 上下 rz)
    rr = [(ARM_R * 1.5, ARM_R * 1.5), (ARM_R, ARM_R), (ARM_R * 0.9, ARM_R * 0.9),
          (ARM_R * 0.62, ARM_R * 1.75), (ARM_R * 0.42, ARM_R * 1.05)]
    verts, faces, vuv = [], [], []
    for x, (ry, rz) in zip(xs, rr):
        prog = (x - x0) / max(1e-6, HAND_X - x0)                     # 0=袖の中 1=手先
        for i in range(SEG):
            a = 2 * math.pi * i / SEG
            verts.append((side * x, ry * math.sin(a), ARM_Z + rz * math.cos(a)))
            # ストリップは 左腕 = u[0,0.5] / 右腕 = u[0.5,1]。切り出しの外側が手先。
            uu = (0.5 * (1.0 - prog)) if side > 0 else (0.5 + 0.5 * prog)
            # ストリップの**下半分だけ**使う（上半分は切り出しに混じった背景）
            vv = ARM_V0 * (0.06 + 0.46 * (0.5 + 0.5 * math.cos(a)))
            vuv.append((uu, vv))
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
    for li, loop in enumerate(m2.loops):
        u2.data[li].uv = vuv[loop.vertex_index]
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
img_node = nt.nodes.new("ShaderNodeTexImage")
img_node.image = bpy.data.images.load(os.path.join(OUT, "doll_albedo.png"))
nt.links.new(bsdf.inputs["Base Color"], img_node.outputs["Color"])
bsdf.inputs["Roughness"].default_value = 0.78
if "Specular IOR Level" in bsdf.inputs:
    bsdf.inputs["Specular IOR Level"].default_value = 0.28

# 法線マップ（布の襞をライトに反応させる）。Unity 側は ShowActor.shader の _BumpMap。
nrm_path = os.path.join(OUT, "doll_normal.png")
if os.path.exists(nrm_path):
    nimg = nt.nodes.new("ShaderNodeTexImage")
    nimg.image = bpy.data.images.load(nrm_path)
    nimg.image.colorspace_settings.name = 'Non-Color'
    nmap = nt.nodes.new("ShaderNodeNormalMap")
    nmap.inputs["Strength"].default_value = 0.45
    nt.links.new(nmap.inputs["Color"], nimg.outputs["Color"])
    nt.links.new(bsdf.inputs["Normal"], nmap.outputs["Normal"])
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


def clamp01(v):
    return 0.0 if v < 0.0 else (1.0 if v > 1.0 else v)


def spread_along_arm(v, tag, ax, w):
    """腕方向の位置 ax を 肩→肘→手首 へ配分し、合計 w を配る。"""
    if ax <= ELBOW_X:
        s = clamp01((ax - SHOULDER_X) / max(1e-6, ELBOW_X - SHOULDER_X))
        g[tag + "Arm"].add([v], w * (1.0 - s), 'REPLACE')
        g[tag + "ForeArm"].add([v], w * s, 'REPLACE')
    elif ax <= WRIST_X:
        s = clamp01((ax - ELBOW_X) / max(1e-6, WRIST_X - ELBOW_X))
        g[tag + "ForeArm"].add([v], w * (1.0 - s), 'REPLACE')
        g[tag + "Hand"].add([v], w * s, 'REPLACE')
    else:
        g[tag + "Hand"].add([v], w, 'REPLACE')


n_arm = n_sleeve = 0
for v in me.vertices:
    co = v.co
    ax = abs(co.x)
    on_arm = False
    for gel in v.groups:
        if gel.group == mark_idx and gel.weight > 0.5:
            on_arm = True
            break

    if on_arm:                                   # 白磁の腕: 全部を腕ボーンへ
        n_arm += 1
        spread_along_arm(v.index, "Left" if co.x > 0 else "Right", ax, 1.0)
        continue

    if ax > BODY_HALF * 0.92:                    # 胴より外へ張り出している = 袖
        drop = max(0.0, ARM_Z - co.z)            # 腕の高さからどれだけ下か
        w = clamp01(1.0 - drop / SLEEVE_FALL)
        w = w * w * (3.0 - 2.0 * w)              # smoothstep（付け根から滑らかに減衰）
        if w > 0.02:
            n_sleeve += 1
            spread_along_arm(v.index, "Left" if co.x > 0 else "Right", ax, w)
            if w < 1.0:
                g["Root"].add([v.index], 1.0 - w, 'REPLACE')
            continue

    if co.z > HEAD_BASE and ax < H * 0.16:
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
print(f"[doll] verts={len(me.vertices)} polys={len(me.polygons)} arm={n_arm} sleeve={n_sleeve}")
print(f"[doll] armZ={ARM_Z:.3f} tip={TIP_X:.3f} bodyHalf={BODY_HALF:.3f} exported")
