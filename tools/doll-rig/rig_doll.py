"""生成した市松人形の GLB にリグを付けて FBX で出す（Blender 5.x・--background 用）。

    blender --background --python rig_doll.py -- <出力ディレクトリ> [--ratio 0.03] [--check]

やること:
  1. GLB を読む → 頂点を落とす（Decimate Collapse）
  2. ボーンを置く（Root / Chest / Head / HeadTop / 左右の Arm・ForeArm・Hand）
  3. 重みを**手で書く**（自動ウェイトは袖の下端まで腕へ持っていく）
  4. 検証ポーズを焼く（腕を上下に振って、袖の下端が動かないことを絵で見る）
  5. FBX を書き出す

## 決めごと（変えるときは理由を書き足す）

- **Blender の +X が人形の左**。FBX 変換（`axis_forward='-Z'` / `axis_up='Y'`）で Unity の -X ＝
  人形の左に来る。`ShowActorRig` は名前（`LeftHand`）でボーンを探すので、ここが逆だと腕が体を横切る
- **腕は上下にしか振らない**（2026-08-15 ユーザー指示）。だから肘の重み配分は粗くてよい
- **袖の下端は垂れたまま**（同指示）。重みは「袖口への近さ」×「腕の高さからの落差の指数減衰」で、
  下へ行くほど 0 へ落ちる。⚠ 一定値で腕へ付けると袖が丸ごと持ち上がって傘になる
- **脇は体に残す**（`along`）。袖は脇で体に縫い付けられているので、そこを腕へ付けると
  腕を上げたときに袖が体から剥がれて裂け目ができる（既存人形で実測済み・tools/doll-model/README.md）
"""
import bpy, sys, os, math, json
from mathutils import Vector, Quaternion
sys.stdout.reconfigure(encoding="utf-8")

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
OUT = argv[0] if argv else os.path.dirname(os.path.abspath(__file__))
RATIO = 0.03
CHECK = "--check" in argv
if "--ratio" in argv:
    RATIO = float(argv[argv.index("--ratio") + 1])

SRC = r"C:\Users\kouga\Downloads\doll 3d model.glb"
NAME = "Ichimatsu2"

# ---- 実測値（probe_arms.py の出力から）---------------------------------------
ARM_Z, ARM_Y = 0.649, 0.015      # 腕の軸（水平・前後の中心）
SHOULDER_X = 0.100               # 肩関節（胴の中）
ELBOW_X = 0.240
WRIST_X = 0.370                  # 袖口のすぐ外
HAND_X = 0.452                   # 手の先
MOUTH_X = 0.357                  # 袖口（ここより外は白磁の腕だけ）
ARM_R = 0.085                    # 腕・手の太さ。⚠ **袖の下端も |x| 0.37 まで張り出している**ので、
                                 #   x だけで腕を判定すると袂の外側が腕になる（実際になった）
# 袖が腕へ付いてくる距離。⚠ **指数（exp(-d/R)）ではなくガウシアン（exp(-(d/R)²)）**。
# 指数だと袖口（腕に密着している輪）でも 0.64 までしか上がらず、手（1.0）との段差で
# **腕を下げたときに袖口が裂ける**（実際に焼いて確認した）。ガウシアンなら
# 近く（d<0.06 ＝ 腕を包む筒）は 0.78 以上・遠く（d>0.2 ＝ 垂れた袂）は 0.06 以下に分かれる。
SLEEVE_REACH = 0.120
MOUTH_GRIP = 0.120               # 袖口から内側へ何 m を「腕に密着した輪」として扱うか
# 脇の遷移。⚠ **ここを胴の半幅（0.145）まで引き伸ばすと、腕を包んでいる筒の部分まで
#   重みが薄まり、腕を下げたときに手が布を突き抜ける**（実際に焼いて確認した）。
# 体に残すのは「脇そのもの」だけでよく、そこから外は腕の側に付ける。
ARMPIT_IN, ARMPIT_OUT = 0.100, 0.170
HEAD_BASE = 0.690                # 首（ここから上が頭）
HEAD_TOP = 0.950

ARM_BONES = ("Arm", "ForeArm", "Hand")
# 各ボーンが受け持つ x の中心。最寄り 2 本で線形に配る。
BONE_CENTER = ((SHOULDER_X + ELBOW_X) / 2, (ELBOW_X + WRIST_X) / 2, (WRIST_X + HAND_X) / 2)


def log(*a):
    print("[rig]", *a)


# ---- 1. 読み込みと間引き -----------------------------------------------------
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=SRC)
ob = [o for o in bpy.data.objects if o.type == 'MESH'][0]
ob.name = NAME
ob.data.name = NAME
bpy.context.view_layer.objects.active = ob
ob.select_set(True)
bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
log("読み込み %d tri" % len(ob.data.polygons))

if RATIO < 1.0:
    m = ob.modifiers.new("dec", 'DECIMATE')
    m.decimate_type = 'COLLAPSE'
    m.ratio = RATIO
    m.use_collapse_triangulate = True
    bpy.ops.object.modifier_apply(modifier="dec")
bpy.ops.object.shade_smooth()
log("間引き後 %d tri / %d vert" % (len(ob.data.polygons), len(ob.data.vertices)))

verts = ob.data.vertices
H = max(v.co.z for v in verts)
log("全高 %.4f m" % H)

# ---- 2. ボーン ---------------------------------------------------------------
arm_data = bpy.data.armatures.new(NAME + "_arm")
arm = bpy.data.objects.new(NAME + "_arm", arm_data)
bpy.context.collection.objects.link(arm)
bpy.context.view_layer.objects.active = arm
bpy.ops.object.mode_set(mode='EDIT')
eb = arm_data.edit_bones


def bone(name, head, tail, parent=None):
    b = eb.new(name)
    b.head = Vector(head)
    b.tail = Vector(tail)
    b.parent = parent
    b.use_connect = False
    return b


root = bone("Root", (0, 0, 0.0), (0, 0, 0.45))
chest = bone("Chest", (0, 0, 0.45), (0, 0, HEAD_BASE), root)
head = bone("Head", (0, 0, HEAD_BASE), (0, 0, HEAD_TOP), chest)
# 身長の計測（ShowActorRig.MeasureHeight）がボーン最高点を見るので、頭頂まで骨を通す。
bone("HeadTop", (0, 0, HEAD_TOP), (0, 0, H), head)

for sign, tag in ((+1, "Left"), (-1, "Right")):     # Blender の +X が人形の左（→ Unity で -X）
    up = bone(f"{tag}Arm", (sign * SHOULDER_X, ARM_Y, ARM_Z), (sign * ELBOW_X, ARM_Y, ARM_Z), chest)
    lo = bone(f"{tag}ForeArm", (sign * ELBOW_X, ARM_Y, ARM_Z), (sign * WRIST_X, ARM_Y, ARM_Z), up)
    bone(f"{tag}Hand", (sign * WRIST_X, ARM_Y, ARM_Z), (sign * HAND_X, ARM_Y, ARM_Z), lo)

bpy.ops.object.mode_set(mode='OBJECT')
log("ボーン %d 本: %s" % (len(arm_data.bones), ", ".join(b.name for b in arm_data.bones)))

# ---- 3. 重み -----------------------------------------------------------------
groups = {}
for n in ["Root", "Chest", "Head"] + [f"{t}{b}" for t in ("Left", "Right") for b in ARM_BONES]:
    groups[n] = ob.vertex_groups.new(name=n)


def clamp01(x):
    return 0.0 if x < 0.0 else (1.0 if x > 1.0 else x)


def smoothstep(a, b, x):
    t = clamp01((x - a) / (b - a)) if b > a else 0.0
    return t * t * (3.0 - 2.0 * t)


def arm_share(ax):
    """腕方向の位置 ax（絶対値）を Arm / ForeArm / Hand の 3 本へ配る。"""
    c = BONE_CENTER
    if ax <= c[0]:
        return (1.0, 0.0, 0.0)
    if ax >= c[2]:
        return (0.0, 0.0, 1.0)
    if ax <= c[1]:
        t = (ax - c[0]) / (c[1] - c[0])
        return (1 - t, t, 0.0)
    t = (ax - c[1]) / (c[2] - c[1])
    return (0.0, 1 - t, t)


stat = {"limb": 0, "sleeve": 0, "body": 0, "head": 0, "sleeve_w": 0.0}
assign = {n: ([], []) for n in groups}     # name -> (indices, weights) はまとめて add する
wlist = [0.0] * len(verts)                 # 可視化用（絵にしないと配分の誤りに気づけない）

for v in verts:
    co = v.co
    ax = abs(co.x)
    tag = "Left" if co.x > 0 else "Right"
    d = math.hypot(co.y - ARM_Y, co.z - ARM_Z)      # 腕の軸からの距離

    if ax >= MOUTH_X and d < ARM_R:
        w = 1.0                                      # 袖から出た白磁の腕・手
        stat["limb"] += 1
    else:
        # 袖。**脇から外に出ていて / 腕の軸へ近いほど**腕に付いてくる。
        # ⚠ 落差（z だけ）で測ると、腕の前後にある布が同じ重みになり、腕を下げたときに
        #    手が布を突き抜ける（実際に焼いて確認した）。距離は前後も入れて 3D で測る。
        along = clamp01((ax - ARMPIT_IN) / (ARMPIT_OUT - ARMPIT_IN))
        k = d / SLEEVE_REACH
        w = along * math.exp(-k * k)
        # 袖口の輪は腕に密着しているので、手（1.0）と同じだけ付いてこないと段差で裂ける。
        # 0.895 のままだと 45° 回したとき 2cm ずれ、腕の太さ（3cm）に対して布から出る。
        km = d / ARM_R
        w += (1.0 - w) * smoothstep(MOUTH_X - MOUTH_GRIP, MOUTH_X, ax) * math.exp(-km * km)
        if w > 1e-3:
            stat["sleeve"] += 1
            stat["sleeve_w"] += w

    wlist[v.index] = w
    if w > 1e-3:
        sh = arm_share(ax)
        for i, b in enumerate(ARM_BONES):
            if sh[i] > 1e-4:
                assign[f"{tag}{b}"][0].append(v.index)
                assign[f"{tag}{b}"][1].append(w * sh[i])

    rest = 1.0 - w
    if rest > 1e-3:
        # 動かない側。頭は Head、それ以外は Chest（人形は腕しか動かさない）。
        host = "Head" if (co.z > HEAD_BASE and ax < 0.16) else "Chest"
        if host == "Head":
            stat["head"] += 1
        elif w <= 1e-3:
            stat["body"] += 1
        assign[host][0].append(v.index)
        assign[host][1].append(rest)

for name, (idx, ws) in assign.items():
    g = groups[name]
    for i, w in zip(idx, ws):
        g.add([i], w, 'REPLACE')

log("腕・手 %d / 袖 %d（平均 %.3f）/ 頭 %d / 胴ほか %d"
    % (stat["limb"], stat["sleeve"],
       stat["sleeve_w"] / max(1, stat["sleeve"]), stat["head"], stat["body"]))

mod = ob.modifiers.new("Armature", 'ARMATURE')
mod.object = arm
ob.parent = arm

# ---- 4. 検証ポーズ -----------------------------------------------------------
if CHECK:
    scene = bpy.context.scene
    for name in ('BLENDER_EEVEE', 'BLENDER_EEVEE_NEXT'):
        try:
            scene.render.engine = name
            break
        except TypeError:
            continue
    scene.render.image_settings.media_type = 'IMAGE'
    scene.render.image_settings.file_format = 'PNG'
    scene.render.film_transparent = False
    world = bpy.data.worlds.new("w")
    scene.world = world
    world.use_nodes = True
    world.node_tree.nodes["Background"].inputs[0].default_value = (0.30, 0.30, 0.32, 1)
    sun_d = bpy.data.lights.new("sun", 'SUN')
    sun_d.energy = 3.0
    sun = bpy.data.objects.new("sun", sun_d)
    bpy.context.collection.objects.link(sun)
    sun.rotation_euler = (math.radians(52), 0, math.radians(28))

    cam_d = bpy.data.cameras.new("cam")
    cam_d.type = 'ORTHO'
    cam_d.ortho_scale = 1.10
    cam = bpy.data.objects.new("cam", cam_d)
    bpy.context.collection.objects.link(cam)
    scene.camera = cam
    scene.render.resolution_x = 520
    scene.render.resolution_y = 560

    # 重みそのものを絵にする（赤 = 腕に付いてくる / 緑 = 動かない）。
    # ⚠ 「どこがどれだけ追従するか」は、ポーズを付けた絵からは読み取れない
    #    （動いた結果しか見えないので、原因が重みか回転かを切り分けられない）。
    col = ob.data.color_attributes.new(name="w", type='FLOAT_COLOR', domain='POINT')
    for i, w in enumerate(wlist):
        col.data[i].color = (w, 1.0 - w, 0.15, 1.0)
    keep_engine = scene.render.engine
    scene.render.engine = 'BLENDER_WORKBENCH'
    scene.display.shading.light = 'FLAT'
    scene.display.shading.color_type = 'VERTEX'
    for view, loc, rot in (("front", Vector((0, -3, 0.49)), (math.radians(90), 0, 0)),
                           ("side", Vector((3, 0, 0.49)), (math.radians(90), 0, math.radians(90)))):
        cam.location = loc
        cam.rotation_euler = rot
        scene.render.filepath = os.path.join(OUT, "weight_%s.png" % view)
        bpy.ops.render.render(write_still=True)
        log("焼いた", scene.render.filepath)
    scene.render.engine = keep_engine

    bpy.context.view_layer.objects.active = arm
    bpy.ops.object.mode_set(mode='POSE')

    def pose_arm(tag, deg):
        """腕を上下に振る。deg > 0 で上げる。

        ⚠ **ボーンのローカル Y 軸は腕の長さ方向**なので、そこを回してもねじりにしかならない
        （最初これで焼いて、腕が 60° 回っているのに袖が 1mm も動かなかった）。
        上下はワールドの Y 軸まわりなので、rest 行列で挟んでボーン空間へ落とす。
        """
        sign = -1 if tag == "Left" else +1        # 左腕は +X へ伸びているので符号が逆
        world = Quaternion((0, 1, 0), math.radians(deg * sign)).to_matrix()
        for b in ARM_BONES:
            name = f"{tag}{b}"
            pb = arm.pose.bones[name]
            pb.rotation_mode = 'QUATERNION'
            if b == "Arm":
                M = arm.data.bones[name].matrix_local.to_3x3()
                pb.rotation_quaternion = (M.inverted() @ world @ M).to_quaternion()
            else:
                pb.rotation_quaternion = Quaternion((1, 0, 0, 0))   # 肘から先は曲げない

    # ユーザー指定の 3 ポーズ（2026-08-15）。**左は人形の左手**＝正面図では画面の右。
    for label, left, right, views in (("1_tpose", 0, 0, ("front", "side", "zoom")),
                                      ("2_left_up", 45, 0, ("front", "side", "zoom")),
                                      ("3_left_down", -45, 0, ("front", "side", "zoom"))):
        pose_arm("Left", left)
        pose_arm("Right", right)
        bpy.context.view_layer.update()
        for view in views:
            # ⚠ 全身の絵では布の破綻が数画素にしかならない。腕の周りは必ず寄って見る
            #    （~/.claude/rules/work-style.md §2「分解して並べるまで見る」）。
            if view == "front":
                cam.location = Vector((0, -3, 0.49))
                cam.rotation_euler = (math.radians(90), 0, 0)
                cam_d.ortho_scale = 1.10
                scene.render.resolution_x, scene.render.resolution_y = 520, 560
            elif view == "side":
                cam.location = Vector((3, 0, 0.49))
                cam.rotation_euler = (math.radians(90), 0, math.radians(90))
                cam_d.ortho_scale = 1.10
                scene.render.resolution_x, scene.render.resolution_y = 520, 560
            else:                                    # zoom: 左腕（+X）の周り
                cam.location = Vector((0.30, -3, 0.60))
                cam.rotation_euler = (math.radians(90), 0, 0)
                cam_d.ortho_scale = 0.62
                scene.render.resolution_x, scene.render.resolution_y = 560, 560
            scene.render.filepath = os.path.join(OUT, "pose_%s_%s.png" % (label, view))
            bpy.ops.render.render(write_still=True)
            log("焼いた", scene.render.filepath)

    # 手首が実際に動いた量を数で出す（絵だけだと「回っているのにねじれonly」を見落とす）
    dg = bpy.context.evaluated_depsgraph_get()
    for deg in (0, 45, -45):
        pose_arm("Left", deg)
        bpy.context.view_layer.update()
        dg.update()
        wrist = arm.pose.bones["LeftHand"].head
        log("左手 %+3d° -> 手首 (%.3f, %.3f, %.3f)" % (deg, wrist.x, wrist.y, wrist.z))
    pose_arm("Left", 0)
    pose_arm("Right", 0)
    bpy.ops.object.mode_set(mode='OBJECT')

# ---- 5. テクスチャと FBX -----------------------------------------------------
tex_dir = os.path.join(OUT, "tex")
os.makedirs(tex_dir, exist_ok=True)
saved = {}
for img in bpy.data.images:
    if img.size[0] == 0:
        continue
    low = img.name.lower()
    if "basecolor" in low:
        key = "albedo"
    elif "normal" in low:
        key = "normal"
    elif "_rm" in low or "roughness" in low or "metallic" in low:
        key = "rm"
    else:
        continue
    path = os.path.join(tex_dir, "ichimatsu2_%s.png" % key)
    img.filepath_raw = path
    img.file_format = 'PNG'
    img.save()
    saved[key] = path
    log("テクスチャ %s -> %s (%dx%d)" % (key, path, img.size[0], img.size[1]))

bpy.ops.object.select_all(action='DESELECT')
ob.select_set(True)
arm.select_set(True)
bpy.context.view_layer.objects.active = arm
fbx = os.path.join(OUT, NAME + ".fbx")
bpy.ops.export_scene.fbx(
    filepath=fbx, use_selection=True, global_scale=1.0,
    axis_forward='-Z', axis_up='Y', object_types={'ARMATURE', 'MESH'},
    add_leaf_bones=False, bake_anim=False,
    mesh_smooth_type='FACE', use_tspace=True, path_mode='STRIP',
)
log("書き出し", fbx, "%.1f MB" % (os.path.getsize(fbx) / 1e6))

json.dump({
    "name": NAME, "ratio": RATIO, "heightM": round(H, 4),
    "tris": len(ob.data.polygons), "verts": len(ob.data.vertices),
    "armZ": ARM_Z, "shoulderX": SHOULDER_X, "wristX": WRIST_X, "handX": HAND_X,
    "armLenM": round(HAND_X - SHOULDER_X, 4),
    "textures": saved,
}, open(os.path.join(OUT, "rig_report.json"), "w"), indent=2, ensure_ascii=False)
log("done")
