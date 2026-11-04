"""体験者の代役。企画書の図 1 と同じ黒い長袖・黒いカーゴ・白い Quest 3。歩きは Unity の仮想体験の記録に従う。

- 骨は Remy（Assets/ThirdParty/Mixamo/Remy.fbx）、動きは Walking.fbx を骨ごとの世界回転の差で移す
- 歩幅は実際の体験者の遅さ（0.25m/s。tools/walk-guide）に合わせて縮める（素材 1.6m/s をゆっくり再生すると大股のスローモーションになる）
- 右手は手すりの近くでは手すりをたどる（企画書 2 節「右手で壁の手すりをたどりながら歩く」）
"""
from __future__ import annotations

import csv
import math
import random
from pathlib import Path

import bmesh
import bpy
from mathutils import Euler, Matrix, Quaternion, Vector

import twin_common as tc
import twin_materials as tm
from twin_common import add_box, add_cyl, add_mesh

ASSETS = tc.ROOT / "Assets"
REMY = ASSETS / "ThirdParty" / "Mixamo" / "Remy.fbx"
WALK = ASSETS / "Editor" / "VirtualWalkAssets" / "Walking.fbx"
ROUTES = Path(__file__).resolve().parent / "routes"
HEIGHT = 1.65
CYCLE_M = 0.86  # 実際の遅い歩き（2 歩で 0.86m）
STRIDE_K = 0.56


def _bone_prefix(arm):
    return arm.data.bones[0].name.split("Hips")[0]


def _load_route(route: str):
    rows = []
    with open(ROUTES / f"{route}.tsv", encoding="utf-8") as f:
        for r in csv.DictReader(f, delimiter="\t"):
            rows.append({k: float(v) for k, v in r.items()})
    dist = 0.0
    for i, r in enumerate(rows):
        if i:
            dist += math.hypot(r["x"] - rows[i - 1]["x"], r["z"] - rows[i - 1]["z"])
        r["dist"] = dist
    return rows


def cloth_material(name, base, seed=0.0, sheen=0.14, rough=0.86, wrinkle=0.0016):
    """UV で固定した布（歩いても模様が泳がない）。皺は 5〜15cm の大きな折れだけ。細かい凹凸を足すと樹皮に見える。"""
    g = tm.G(name)
    uv = g.tc.outputs["UV"]
    pu = g.mapping(uv, scale=(1, 1, 1), loc=(seed, seed * 0.7, 0))
    large = g.noise(g.mapping(pu, scale=(1.0, 2.6, 1)), 7.0, 2.0, 0.42).outputs["Fac"]
    fold = g.noise(g.mapping(pu, scale=(2.4, 1.0, 1)), 12.0, 2.0, 0.4).outputs["Fac"]
    weave = g.noise(pu, 1400.0, 1.0, 0.4).outputs["Fac"]
    tone = g.noise(pu, 46.0, 3.0, 0.5).outputs["Fac"]
    c = g.mix(g.smooth(tone, 0.34, 0.66), tm.rgb(*[v * 0.8 for v in base]), tm.rgb(*[v * 1.3 for v in base]))
    h = g.add(g.mul(g.smooth(large, 0.3, 0.7), wrinkle), g.mul(g.smooth(fold, 0.32, 0.68), wrinkle * 0.7))
    h = g.add(h, g.mul(weave, 0.00003))
    n = g.bump(h, 1.0, 1.0)
    return g.finish(g.principled(c, rough, 0.28, normal=n, sheen=sheen))


def tm_cloth(name, base):
    """厚手の黒い綿。皺は素材写真の明暗を高さに使う（手続きの折れは粘土や岩に見えた）。座標はポーズで動かない。"""
    g = tm.G(name)
    gen = g.tc.outputs["Generated"]
    im = g.image("cloth", g.mapping(gen, scale=(1.6, 2.6, 1.6)), box=True)
    l = g.lum(im.outputs[0])
    tone = g.noise(gen, 12.0, 4.0, 0.5).outputs["Fac"]
    c = g.mix(g.smooth(tone, 0.32, 0.68), tm.rgb(*[v * 0.82 for v in base]), tm.rgb(*[v * 1.25 for v in base]))
    c = g.mix(g.smooth(l, 0.05, 0.3), tm.rgb(*[v * 0.55 for v in base]), c)
    # 綾織の目（8cm 四方）。皺の形は _shirt_folds が刻むので、ここは糸の細かさを足す
    wv = g.mapping(g.tc.outputs["Object"], scale=(12.5, 12.5, 12.5), loc=(0.2, 0.1, 0.3))
    wh, _, wd = g.split(g.image("cloth_weave_s", wv, srgb=False, box=True).outputs[0])
    c = g.mix(1.0, c, g.gray(g.add(0.55, g.mul(wd, 0.9))), "MULTIPLY")
    n = g.bump(g.add(g.mul(l, 0.016), g.mul(wh, 0.0009)), 1.0, 1.0)
    return g.finish(g.principled(c, g.add(0.74, g.mul(wd, 0.14)), 0.2, normal=n, sheen=0.06))


def skin_material(img_diffuse, normal_img=None):
    g = tm.G("skin")
    uv = g.tc.outputs["UV"]
    t = g.t.nodes.new("ShaderNodeTexImage")
    t.image = img_diffuse
    g.link(uv, t.inputs[0])
    pores = g.noise(uv, 1800.0, 3.0, 0.6).outputs["Fac"]
    mott = g.noise(uv, 140.0, 5.0, 0.6).outputs["Fac"]
    desat = g.t.nodes.new("ShaderNodeHueSaturation")
    desat.inputs["Saturation"].default_value = 0.66
    desat.inputs["Value"].default_value = 0.88
    g.link(t.outputs[0], desat.inputs["Color"])
    col = g.mix(g.mul(g.smooth(mott, 0.35, 0.7), 0.1), desat.outputs[0], tm.rgb(0.42, 0.22, 0.17), "MULTIPLY")
    nm = None
    if normal_img is not None:
        tn = g.t.nodes.new("ShaderNodeTexImage")
        tn.image = normal_img
        tn.image.colorspace_settings.name = "Non-Color"
        g.link(uv, tn.inputs[0])
        nn = g.t.nodes.new("ShaderNodeNormalMap")
        g.link(tn.outputs[0], nn.inputs["Color"])
        nn.inputs["Strength"].default_value = 0.9
        nm = nn.outputs[0]
    nb = g.bump(g.mul(pores, 0.0004), 1.0, 1.0, nm)
    sh = g.principled(col, g.add(0.42, g.mul(mott, 0.22)), 0.46, normal=nb, sss=0.6, sss_r=(0.8, 0.34, 0.2), sss_s=0.004)
    return g.finish(sh)


class Person:
    def __init__(self, ctx, route: str = "forward"):
        self.ctx = ctx
        self.route_name = route
        self.rows = _load_route(route)
        self._import()
        self._dress()
        self._hmd()
        self._prepare_ik()

    # ---------- 取り込み ----------
    def _import(self):
        before = set(bpy.data.objects)
        bpy.ops.import_scene.fbx(filepath=str(REMY), ignore_leaf_bones=False, automatic_bone_orientation=False)
        new = set(bpy.data.objects) - before
        self.arm = next(o for o in new if o.type == "ARMATURE")
        # FBX に付いてくる 2 コマのアクションを残すと、描くたびに自前の姿勢を T ポーズで上書きする
        self.arm.animation_data_clear()
        self.meshes = {o.name: o for o in new if o.type == "MESH"}
        before = set(bpy.data.objects)
        bpy.ops.import_scene.fbx(filepath=str(WALK), ignore_leaf_bones=False, automatic_bone_orientation=False)
        new = set(bpy.data.objects) - before
        self.src = next(o for o in new if o.type == "ARMATURE")
        for o in list(new):
            if o.type == "MESH":
                bpy.data.objects.remove(o)
        self.src.hide_render = True
        self.src.hide_viewport = False
        self.act = self.src.animation_data.action
        self.prefix = _bone_prefix(self.arm)
        self.sprefix = _bone_prefix(self.src)
        # 身長を実測で合わせる
        self.arm.rotation_mode = "QUATERNION"
        self.base_q = self.arm.rotation_quaternion.copy()
        bpy.context.view_layer.update()
        top = self.arm.matrix_world @ self.arm.data.bones[self.prefix + "HeadTop_End"].head_local
        toe = self.arm.matrix_world @ self.arm.data.bones[self.prefix + "RightToe_End"].head_local
        heel = self.arm.matrix_world @ self.arm.data.bones[self.prefix + "RightFoot"].head_local
        h = top.z - min(toe.z, heel.z - 0.08)
        k = HEIGHT / h * 0.985  # 頭頂ボーンは髪の下
        self.arm.scale = tuple(v * k for v in self.arm.scale)
        bpy.context.view_layer.update()
        self.scale_k = k
        # 歩きの素材と同じ大きさへ（骨の回転だけ移すので位置は使わない）
        order = []

        def walk(b):
            order.append(b)
            for c in b.children:
                walk(c)

        walk(self.arm.data.bones[self.prefix + "Hips"])
        self.order = order
        self.frames = int(self.act.frame_range[1] - self.act.frame_range[0])
        self.f0 = self.act.frame_range[0]
        self._bake_cycle()
        self._rest_to_mean_pose()
        # 正面（爪先の向き）
        fp = self.arm.matrix_world @ self.arm.data.bones[self.prefix + "RightFoot"].head_local
        tp = self.arm.matrix_world @ self.arm.data.bones[self.prefix + "RightToeBase"].head_local
        f = (tp - fp)
        f.z = 0
        self.rest_fwd = f.normalized()

    def _rest_to_mean_pose(self):
        """基準の姿勢を T ポーズから「歩きの平均（腕を下ろした形）」へ移す。
        服は基準の姿勢で作るので、T ポーズのままだと腕を下ろしたときに肩が風船のように膨らむ。"""
        self._apply_directions({b.name: self.mean[b.name] for b in self.order if b.name in self.mean})
        bpy.context.view_layer.update()
        dg = bpy.context.evaluated_depsgraph_get()
        for ob in self.meshes.values():
            ev = ob.evaluated_get(dg)
            me = ev.to_mesh()
            co = [0.0] * (len(me.vertices) * 3)
            me.vertices.foreach_get("co", co)
            ev.to_mesh_clear()
            ob.data.vertices.foreach_set("co", co)
            ob.data.update()
        view = bpy.context.view_layer
        view.objects.active = self.arm
        self.arm.select_set(True)
        with bpy.context.temp_override(active_object=self.arm, object=self.arm, selected_objects=[self.arm], selected_editable_objects=[self.arm]):
            bpy.ops.object.mode_set(mode="POSE")
            bpy.ops.pose.select_all(action="SELECT")
            bpy.ops.pose.armature_apply(selected=False)
            bpy.ops.object.mode_set(mode="OBJECT")
        for pb in self.arm.pose.bones:
            pb.rotation_mode = "QUATERNION"
            pb.rotation_quaternion = Quaternion()
            pb.location = (0, 0, 0)
        # 骨の順は data のものを取り直す（armature_apply の前の参照は無効）
        order = []

        def walk(b):
            order.append(b)
            for c in b.children:
                walk(c)

        walk(self.arm.data.bones[self.prefix + "Hips"])
        self.order = order
        self.children = {b.name: self._chain_child(b) for b in self.order}
        bpy.context.view_layer.update()

    def _apply_directions(self, wanted):
        """{骨の名前: 世界の向き} を満たすように親から順に回す。指定の無い骨は親に付いていく。"""
        mw3q = self.arm.matrix_world.to_quaternion()
        inv = mw3q.inverted()
        poses = {}
        for b in self.order:
            pb = self.arm.pose.bones[b.name]
            pb.rotation_mode = "QUATERNION"
            rest_q = b.matrix_local.to_quaternion()
            if b.name in wanted:
                child = self.children[b.name]
                rest_dir = (child.head_local - b.head_local).normalized()
                target_arm = rest_dir.rotation_difference(inv @ wanted[b.name]) @ rest_q
            else:
                parent_q = poses.get(b.parent.name) if b.parent else None
                target_arm = (parent_q @ b.parent.matrix_local.to_quaternion().inverted() @ rest_q) if parent_q else rest_q
            if b.parent and b.parent.name in poses:
                rest_rel = b.parent.matrix_local.to_quaternion().inverted() @ rest_q
                basis = (poses[b.parent.name] @ rest_rel).inverted() @ target_arm
            else:
                basis = rest_q.inverted() @ target_arm
            pb.rotation_quaternion = basis
            poses[b.name] = target_arm

    def _chain_child(self, bone):
        """骨の向きを決める子。指や腰のように子が複数あるものは体の幹の側を選ぶ。"""
        short = bone.name[len(self.prefix):]
        pref = {"Hips": "Spine", "Spine2": "Neck", "Neck": "Head", "Head": "HeadTop_End"}.get(short)
        if pref:
            c = self.arm.data.bones.get(self.prefix + pref)
            if c:
                return c
        if short.endswith("Hand"):
            c = self.arm.data.bones.get(self.prefix + short + "Middle1")
            if c:
                return c
        return bone.children[0] if len(bone.children) else None

    def _bake_cycle(self):
        """素材の 1 循環ぶん、骨の向き（世界）を取る。

        Walking.fbx の rest は T ポーズではなく歩きの 1 コマなので、回転の差は移せない（腕が T のまま残る）。
        向きのベクトルだけ移す（swing）。ねじれは Remy の rest のまま。
        """
        s = bpy.context.scene
        self.dirs = []
        self.hip_z = []
        self.children = {b.name: self._chain_child(b) for b in self.order}
        for fi in range(self.frames):
            s.frame_set(int(self.f0 + fi))
            d = {}
            for b in self.order:
                c = self.children[b.name]
                if c is None:
                    continue
                sa = self.src.pose.bones.get(self.sprefix + b.name[len(self.prefix):])
                sb = self.src.pose.bones.get(self.sprefix + c.name[len(self.prefix):])
                if sa is None or sb is None:
                    continue
                v = (self.src.matrix_world @ sb.head) - (self.src.matrix_world @ sa.head)
                if v.length > 1e-5:
                    d[b.name] = v.normalized()
            self.dirs.append(d)
            hip = self.src.pose.bones[self.sprefix + "Hips"]
            self.hip_z.append((self.src.matrix_world @ hip.head).z)
        self.mean = {}
        for b in self.order:
            if b.name not in self.dirs[0]:
                continue
            m = Vector((0, 0, 0))
            for fi in range(self.frames):
                m += self.dirs[fi][b.name]
            self.mean[b.name] = m.normalized()
        hip_mean = sum(self.hip_z) / len(self.hip_z)
        self.hip_dz = [(z - hip_mean) for z in self.hip_z]

    # ---------- 服 ----------
    def _dress(self):
        """Remy の服は半袖・短パンで Body は服の下が無い。袖と脚を足して 1 つの厚みのある形に作り直し、
        筋肉の起伏をならしてから布の折れを本物の形で刻む（面を重ねると段差と継ぎ目が出る）。"""
        body = self.meshes["Body"]
        vg = {g.index: g.name for g in body.vertex_groups}
        pre = self.prefix

        def dominant(v):
            best, bw = "", 0.0
            for g in v.groups:
                if g.weight > bw:
                    best, bw = vg[g.group], g.weight
            return best[len(pre):]

        names = [dominant(v) for v in body.data.vertices]
        neck_z = self._bone_p("Neck").z
        bwm0 = body.matrix_world
        keep_skin = {i for i, n in enumerate(names) if ((n in ("Head", "Neck", "HeadTop_End", "LeftEye", "RightEye") and (bwm0 @ body.data.vertices[i].co).z > neck_z + 0.035) or "Hand" in n)}
        tops = self.meshes["Tops"]
        bottoms = self.meshes["Bottoms"]
        twm = tops.matrix_world
        top_pts = [twm @ v.co for v in tops.data.vertices if abs((twm @ v.co).x) < 0.2 + abs((twm @ v.co).y)]
        hip = self._bone_p("Hips")
        neck = self._bone_p("Neck")
        head = self._bone_p("Head")
        # 肩の高さで胴を閉じて首を通す。裾は腰骨の下まで
        torso = self._torso_rings(top_pts, hip.z - 0.08, neck.z + 0.05, 0.012, 0.062)
        rarm = self._rings_limb(["RightArm", "RightForeArm", "RightHand"], [0.054, 0.044, 0.039])
        larm = self._rings_limb(["LeftArm", "LeftForeArm", "LeftHand"], [0.054, 0.044, 0.039])
        # 袖の付け根は肩へもぐり込ませる（腕を上げても胴が裂けない）
        for arm_rings in (rarm, larm):
            c0, ax0, rt0, r0x, r0y = arm_rings[0]
            arm_rings.insert(0, (c0 - ax0 * 0.05, ax0, rt0, r0x * 0.82, r0y * 0.82))
        is_arm = lambda nm: "Arm" in nm or "Hand" in nm
        cloth_top = tm_cloth("shirt", (0.014, 0.014, 0.015))
        shirt = self._garment("Shirt", [torso], [tops], cloth_top, self._shirt_folds, allow=lambda nm: not is_arm(nm))
        sleeve_r = self._garment("Sleeve R", [rarm], [body, tops], cloth_top, self._shirt_folds, smooth=6, allow=lambda nm: is_arm(nm) and "Right" in nm)
        sleeve_l = self._garment("Sleeve L", [larm], [body, tops], cloth_top, self._shirt_folds, smooth=6, allow=lambda nm: is_arm(nm) and "Left" in nm)
        bwm = bottoms.matrix_world
        rleg = self._rings_limb(["RightUpLeg", "RightLeg", "RightFoot"], [0.103, 0.072, 0.062])
        lleg = self._rings_limb(["LeftUpLeg", "LeftLeg", "LeftFoot"], [0.103, 0.072, 0.062])
        rt_axis = self.rest_fwd.cross(Vector((0, 0, 1))).normalized()
        pelvis = [(Vector((hip.x, hip.y, z)), Vector((0, 0, 1)), rt_axis, 0.162, 0.118) for z in (hip.z - 0.12, hip.z - 0.04, hip.z + 0.05, hip.z + 0.1)]
        # カーゴのポケット（腿の外の膨らみ）
        pockets = []
        for sgn, name_ in ((1, "RightUpLeg"), (-1, "LeftUpLeg")):
            top_ = self._bone_p(name_)
            knee = self._bone_p(name_.replace("UpLeg", "Leg"))
            mid = top_.lerp(knee, 0.58)
            side = (top_ - hip)
            side.z = 0
            side.normalize()
            cc = mid + side * 0.066
            pockets.append([(cc + Vector((0, 0, z)), Vector((0, 0, 1)), rt_axis, 0.05 * k, 0.06 * k) for z, k in ((-0.085, 0.7), (-0.06, 1.0), (0.06, 1.0), (0.085, 0.7))])
        pants = self._garment("Pants", [pelvis, rleg, lleg, *pockets], [bottoms, body], tm_cloth("pants", (0.012, 0.012, 0.013)), self._pants_folds, allow=lambda nm: not is_arm(nm) and "Spine" not in nm)
        tops.hide_render = True
        bottoms.hide_render = True
        self.garments = [shirt, sleeve_r, sleeve_l, pants]
        # 肌は頭・首・手だけ残す
        me = body.data
        bm = bmesh.new()
        bm.from_mesh(me)
        bm.verts.ensure_lookup_table()
        bmesh.ops.delete(bm, geom=[v for v in bm.verts if v.index not in keep_skin], context="VERTS")
        bm.to_mesh(me)
        bm.free()
        imgs = bpy.data.images
        me.materials.clear()
        me.materials.append(skin_material(imgs["Remy_Body_Diffuse"], imgs.get("Remy_Body_Normal")))
        sub = body.modifiers.new("smooth", "SUBSURF")
        sub.levels = sub.render_levels = 2
        # 靴
        sh = self.meshes["Shoes"]
        g = tm.G("boots")
        t = g.t.nodes.new("ShaderNodeTexImage")
        t.image = imgs["Remy_Shoes_Diffuse"]
        g.link(g.tc.outputs["UV"], t.inputs[0])
        c = g.mix(0.88, t.outputs[0], tm.rgb(0.018, 0.017, 0.016), "MULTIPLY")
        n = g.bump(g.noise(g.tc.outputs["UV"], 700.0, 4.0).outputs["Fac"], 0.0005)
        sh.data.materials.clear()
        sh.data.materials.append(g.finish(g.principled(c, 0.56, 0.4, normal=n)))
        s2 = sh.modifiers.new("smooth", "SUBSURF")
        s2.levels = s2.render_levels = 1
        for n2 in ("Eyelashes", "Eyes"):
            if n2 in self.meshes:
                self.meshes[n2].hide_render = True  # HMD の中で見えない
        self._hair()

    def _garment(self, name, parts, sources, mat, folds, voxel=0.0042, smooth=14, allow=None):
        """服を体の外形の「包み」として作る。parts は [(輪の列)]、輪は (中心, 軸, 右, 右半径, 前半径)（世界 m・rest）。
        体の面をふくらませない（筋肉がそのまま服に写る）。輪の列をチューブにしてボクセルで 1 つにする。"""
        import numpy as np
        from mathutils.kdtree import KDTree

        base = sources[0]
        mw = base.matrix_world.copy()
        inv = mw.inverted()
        scale = self.arm.scale[0] * base.scale[0]
        verts, faces = [], []
        seg = 28
        for rings in parts:
            start = len(verts)
            for (c, ax, rt, rx, ry) in rings:
                fw = ax.cross(rt).normalized()
                for k in range(seg):
                    t = 2 * math.pi * k / seg
                    p = c + rt * (math.cos(t) * rx) + fw * (math.sin(t) * ry)
                    verts.append(inv @ p)
            for r in range(len(rings) - 1):
                for k in range(seg):
                    n = (k + 1) % seg
                    a0 = start + r * seg + k
                    a1 = start + r * seg + n
                    b0 = start + (r + 1) * seg + k
                    b1 = start + (r + 1) * seg + n
                    faces.append((a0, a1, b1, b0))
            faces.append(tuple(start + k for k in reversed(range(seg))))
            faces.append(tuple(start + (len(rings) - 1) * seg + k for k in range(seg)))
        me = bpy.data.meshes.new(name)
        me.from_pydata([tuple(v) for v in verts], [], faces)
        me.update()
        ob = bpy.data.objects.new(name, me)
        tc.collection("Person").objects.link(ob)
        ob.parent = self.arm
        ob.matrix_parent_inverse = base.matrix_parent_inverse.copy()
        ob.matrix_basis = base.matrix_basis.copy()
        rem = ob.modifiers.new("r", "REMESH")
        rem.mode = "VOXEL"
        rem.voxel_size = voxel / scale
        rem.use_smooth_shade = True
        smo = ob.modifiers.new("s", "SMOOTH")
        smo.factor = 0.8
        smo.iterations = smooth
        dg = bpy.context.evaluated_depsgraph_get()
        ev = ob.evaluated_get(dg)
        new_me = bpy.data.meshes.new_from_object(ev)
        ob.modifiers.clear()
        ob.data = new_me
        bpy.data.meshes.remove(me)
        me = new_me
        # 骨の重みは体の面から写す
        pts, grp = [], []
        for src in sources:
            names = {g.index: g.name for g in src.vertex_groups}
            src_inv = inv @ src.matrix_world
            for v in src.data.vertices:
                weights = [(names[g.group], g.weight) for g in v.groups if allow is None or allow(names[g.group])]
                # 使える骨の重みが無い面は写す元にしない（近くの頂点が全部そうだと、その部分だけ rest に取り残される）
                if weights:
                    pts.append(src_inv @ v.co)
                    grp.append(weights)
        kd = KDTree(len(pts))
        for i, co in enumerate(pts):
            kd.insert(co, i)
        kd.balance()
        groups = {}
        for v in me.vertices:
            acc = {}
            tot = 0.0
            for co, idx, dist in kd.find_n(v.co, 8):
                w = 1.0 / (dist * scale + 0.01) ** 2
                tot += w
                for nm, wt in grp[idx]:
                    if allow is None or allow(nm):
                        acc[nm] = acc.get(nm, 0.0) + wt * w
            norm = sum(acc.values())
            if norm <= 0:
                continue
            for nm, wt in acc.items():
                groups.setdefault(nm, []).append((v.index, wt / norm))
        for nm, lst in groups.items():
            vg = ob.vertex_groups.new(name=nm)
            for idx, wt in lst:
                vg.add([idx], wt, "REPLACE")
        co = np.empty(len(me.vertices) * 3, dtype=np.float64)
        me.vertices.foreach_get("co", co)
        co = co.reshape(-1, 3) * scale
        nor = np.empty(len(me.vertices) * 3, dtype=np.float64)
        me.vertices.foreach_get("normal", nor)
        nor = nor.reshape(-1, 3)
        disp = folds(co)
        me.vertices.foreach_set("co", ((co + nor * disp[:, None]) / scale).reshape(-1))
        me.update()
        me.materials.append(mat)
        me.polygons.foreach_set("use_smooth", [True] * len(me.polygons))
        mod = ob.modifiers.new("arm", "ARMATURE")
        mod.object = self.arm
        sub = ob.modifiers.new("sub", "SUBSURF")
        sub.levels = 0
        sub.render_levels = 1
        print(f"[twin] 服 {name}: {len(me.vertices)} 頂点")
        return ob

    def _bone_p(self, name):
        return self.arm.matrix_world @ self.arm.data.bones[self.prefix + name].head_local

    def _rings_limb(self, chain, radii, side_axis=None, slices=26):
        """骨の列に沿った輪。radii は chain の各節の半径（m）。"""
        pts = [self._bone_p(n) for n in chain]
        total = sum((pts[i + 1] - pts[i]).length for i in range(len(pts) - 1))
        rings = []
        for s_ in range(slices + 1):
            d = total * s_ / slices
            i = 0
            acc = 0.0
            while i < len(pts) - 2 and acc + (pts[i + 1] - pts[i]).length < d:
                acc += (pts[i + 1] - pts[i]).length
                i += 1
            seglen = (pts[i + 1] - pts[i]).length
            u = (d - acc) / seglen
            c = pts[i].lerp(pts[i + 1], u)
            ax = (pts[i + 1] - pts[i]).normalized()
            r = radii[i] + (radii[i + 1] - radii[i]) * u
            # 輪の向きは前の輪から運ぶ。節ごとに決め直すと向きが裏返り、チューブがねじれて板のような膜が張る
            if rings:
                rt = rings[-1][2] - ax * rings[-1][2].dot(ax)
            else:
                rt = ax.cross(Vector((0, 0, 1)))
                if rt.length < 0.2:
                    rt = ax.cross(Vector((1, 0, 0)))
            rt.normalize()
            rings.append((c, ax, rt, r, r))
        return rings

    def _torso_rings(self, verts_world, z0, z1, slack, top_r, slices=22):
        """胴の輪。各高さの体の左右・前後の幅を取り、なめらかにして少しゆとりを足す。"""
        import numpy as np

        arr = np.array([tuple(v) for v in verts_world])
        zs = np.linspace(z0, z1, slices)
        hx, hy, cx, cy = [], [], [], []
        for z in zs:
            m = np.abs(arr[:, 2] - z) < (z1 - z0) / slices * 1.2
            if m.sum() < 8:
                hx.append(hx[-1] if hx else 0.16)
                hy.append(hy[-1] if hy else 0.11)
                cx.append(cx[-1] if cx else 0.0)
                cy.append(cy[-1] if cy else 0.0)
                continue
            q = arr[m]
            cx.append((np.percentile(q[:, 0], 98) + np.percentile(q[:, 0], 2)) / 2)
            cy.append((np.percentile(q[:, 1], 98) + np.percentile(q[:, 1], 2)) / 2)
            hx.append((np.percentile(q[:, 0], 98) - np.percentile(q[:, 0], 2)) / 2)
            hy.append((np.percentile(q[:, 1], 98) - np.percentile(q[:, 1], 2)) / 2)
        def smooth1(a):
            a = np.array(a)
            for _ in range(5):
                a[1:-1] = (a[:-2] + a[1:-1] * 2 + a[2:]) / 4
            return a
        hx, hy, cx, cy = map(smooth1, (hx, hy, cx, cy))
        rings = []
        rt = self.rest_fwd.cross(Vector((0, 0, 1))).normalized()
        fw = self.rest_fwd
        n = len(zs)
        for i, z in enumerate(zs):
            c = Vector((cx[i], cy[i], z))
            # rx/ry は世界の x/y 幅から、体の左右/前後の軸へ写す（Remy はほぼ軸に揃っている）
            rx = min(float(hx[i] * abs(rt.x) + hy[i] * abs(rt.y)) + slack, 0.172)
            ry = min(float(hx[i] * abs(fw.x) + hy[i] * abs(fw.y)) + slack, 0.122)
            # 下半分は肩から落ちる服の形（くびれを作らない）
            if i < n // 2:
                rx = max(rx, 0.176)
                ry = max(ry, 0.126)
            if i == n - 1:
                rx, ry = top_r, top_r
            elif i == n - 2:
                rx, ry = rx * 0.72 + top_r * 0.28, ry * 0.72 + top_r * 0.28
            rings.append((c, Vector((0, 0, 1)), rt, rx, ry))
        return rings

    @staticmethod
    def _ridges(co, rng, n, freq, amp, stretch):
        """方向を揃えない折れ。尾根が細く谷が広い形（布の張り）。"""
        import numpy as np

        out = np.zeros(len(co))
        for _ in range(n):
            d = rng.normal(0, 1, 3)
            d[2] *= stretch
            d /= np.linalg.norm(d)
            f = freq * rng.uniform(0.6, 1.5)
            warp = 0.012 * np.sin(co @ (rng.normal(0, 1, 3) * 16.0) + rng.uniform(0, 6.28))
            s = np.sin((co @ d + warp) * f + rng.uniform(0, 6.28))
            env = 0.5 + 0.5 * np.sin(co @ (rng.normal(0, 1, 3) * 7.0) + rng.uniform(0, 6.28))
            out += (1 - np.abs(s)) ** 2.2 * env * amp * rng.uniform(0.5, 1.2)
        return out

    def _photo_folds(self, co, amp, seed):
        """cloth.jpg の明暗を高さとして、面の向きで貼り分けて形に刻む（基準姿勢の座標なのでポーズで泳がない）。"""
        import numpy as np

        if not hasattr(self, "_cloth_h"):
            img = bpy.data.images.load(str(tm.TEX / "cloth.jpg"), check_existing=True)
            w, h = img.size
            px = np.empty(w * h * 4, dtype=np.float32)
            img.pixels.foreach_get(px)
            lum = px.reshape(h, w, 4)[..., :3].mean(-1)
            # 繊維の細かさを消して、皺だけ残す（縦横 2 回の移動平均）
            k = 31
            for ax in (0, 1):
                acc = np.cumsum(np.concatenate([np.take(lum, range(-k, 0), axis=ax), lum], axis=ax), axis=ax)
                lum = (np.take(acc, range(k, acc.shape[ax]), axis=ax) - np.take(acc, range(0, acc.shape[ax] - k), axis=ax)) / k
            lum = (lum - lum.mean()) / max(lum.std(), 1e-5)
            self._cloth_h = np.clip(lum, -2.5, 2.5)
        H = self._cloth_h
        hh, ww = H.shape
        rng = np.random.default_rng(seed)
        ox, oy = rng.random(2)
        size = 0.42  # 写真の幅が実寸 42cm に当たる
        def sample(u, v):
            x = ((u / size + ox) % 1.0) * (ww - 1)
            y = ((v / size * (ww / hh) + oy) % 1.0) * (hh - 1)
            return H[y.astype(int), x.astype(int)]
        # 3 面投影（法線が無いので、体の周りの角度で前後/左右を混ぜる）
        c = co - co.mean(0)
        wx = np.abs(c[:, 0]) / (np.abs(c[:, 0]) + np.abs(c[:, 1]) + 1e-5)
        a = sample(c[:, 1], c[:, 2])
        b = sample(c[:, 0], c[:, 2])
        return (a * wx + b * (1 - wx)) * amp

    def _shirt_folds(self, co):
        import numpy as np

        return self._photo_folds(co, 0.0017, 5)
        rng = np.random.default_rng(5)
        return self._ridges(co, rng, 10, 22.0, 0.0018, 3.0)

    def _pants_folds(self, co):
        import numpy as np

        return self._photo_folds(co, 0.002, 8)
        rng = np.random.default_rng(8)
        return self._ridges(co, rng, 10, 20.0, 0.0024, 3.2)

    def _hair(self):
        """髪を 1 本ずつの毛に（カードの板は光ると平面に見える）。頭の骨に固定して動かす。"""
        import numpy as np

        src = self.meshes["Hair"]
        src.hide_render = True  # カードの板は光ると平面に見える。植える場所としてだけ使う
        dg = bpy.context.evaluated_depsgraph_get()
        ev = src.evaluated_get(dg)
        me = ev.to_mesh()
        mw = src.matrix_world
        tris = []
        me.calc_loop_triangles()
        for tri in me.loop_triangles:
            a, b, c = [mw @ me.vertices[i].co for i in tri.vertices]
            area = (b - a).cross(c - a).length * 0.5
            if area > 1e-8:
                tris.append((a, b, c, area, (mw.to_3x3() @ tri.normal).normalized()))
        ev.to_mesh_clear()
        areas = np.array([t[3] for t in tris])
        rng = np.random.default_rng(21)
        count = 200000
        idx = rng.choice(len(tris), count, p=areas / areas.sum())
        top = self.arm.matrix_world @ self.arm.data.bones[self.prefix + "HeadTop_End"].head_local
        seg = 6
        pts = np.empty((count, seg, 3), dtype=np.float32)
        for i, ti in enumerate(idx):
            a, b, c, _, n = tris[ti]
            u, v = rng.random(2)
            if u + v > 1:
                u, v = 1 - u, 1 - v
            o = a + (b - a) * u + (c - a) * v
            # つむじから外へ流れ、重力で下へ
            away = (o - top)
            away.z = 0
            away = away.normalized() if away.length > 1e-4 else Vector((1, 0, 0))
            tang = (away - n * away.dot(n)).normalized() if (away - n * away.dot(n)).length > 1e-4 else away
            d = (n * 0.28 + tang * 0.8 + Vector((0, 0, -0.18))).normalized()
            ln = float(rng.uniform(0.018, 0.036))
            jitter = Vector(rng.normal(0, 0.05, 3).tolist())
            p = o.copy()
            for k in range(seg):
                pts[i, k] = p
                d = (d + Vector((0, 0, -0.1)) + jitter * 0.22 - n * 0.14).normalized()
                p = p + d * (ln / (seg - 1))
        cv = bpy.data.hair_curves.new("Hair strands")
        cv.add_curves([seg] * count)
        cv.attributes["position"].data.foreach_set("vector", pts.reshape(-1))
        radius = cv.attributes.new("radius", "FLOAT", "POINT")
        r = np.tile(np.linspace(0.00017, 0.00004, seg, dtype=np.float32), count)
        radius.data.foreach_set("value", r)
        ob = bpy.data.objects.new("Hair strands", cv)
        tc.collection("Person").objects.link(ob)
        hm = bpy.data.materials.new("hair_strand")
        hm.use_nodes = True
        tn = hm.node_tree
        tn.nodes.clear()
        out = tn.nodes.new("ShaderNodeOutputMaterial")
        h = tn.nodes.new("ShaderNodeBsdfHairPrincipled")
        h.parametrization = "MELANIN"
        h.inputs["Melanin"].default_value = 1.0
        h.inputs["Melanin Redness"].default_value = 0.15
        h.inputs["Roughness"].default_value = 0.55
        h.inputs["Radial Roughness"].default_value = 0.55
        h.inputs["Random Roughness"].default_value = 0.3
        tn.links.new(h.outputs[0], out.inputs["Surface"])
        cv.materials.append(hm)
        self.hair_ob = ob

    # ---------- HMD ----------
    def _hmd(self):
        white = tm.G("hmd_white")
        fine = white.noise(white.pos, 900.0, 2.0).outputs["Fac"]
        grime = white.noise(white.pos, 60.0, 5.0, 0.6).outputs["Fac"]
        col = white.mix(white.mul(white.smooth(grime, 0.56, 0.72), 0.25), tm.rgb(0.72, 0.72, 0.7), tm.rgb(0.38, 0.37, 0.34))
        wm = white.finish(white.principled(col, 0.42, 0.5, normal=white.bump(fine, 0.00012)))
        dark = tm.plain("hmd_dark", (0.012, 0.012, 0.013), 0.32, 0.5)
        strap = tm.G("hmd_strap")
        wv = strap.noise(strap.pos, 800.0, 2.0).outputs["Fac"]
        sm = strap.finish(strap.principled(tm.rgb(0.12, 0.12, 0.115), 0.86, 0.25, normal=strap.bump(wv, 0.0004)))
        head = self.arm.data.bones[self.prefix + "Head"]
        eye_l = self.arm.data.bones.get(self.prefix + "LeftEye")
        eye_r = self.arm.data.bones.get(self.prefix + "RightEye")
        mw = self.arm.matrix_world
        eye_mid = mw @ ((eye_l.head_local + eye_r.head_local) * 0.5)
        head_p = mw @ head.head_local
        fwd = self.rest_fwd
        up = Vector((0, 0, 1))
        right = fwd.cross(up).normalized()
        c = eye_mid + fwd * 0.058 + up * 0.002
        rows = []
        # 本体: 角の丸い箱を細分（184 × 88 × 66mm）
        ob = add_box("HMD body", (-0.092, -0.033, -0.044), (0.092, 0.033, 0.044), wm, "Person", bevel=0.0)
        bev = ob.modifiers.new("b", "BEVEL")
        bev.width = 0.024
        bev.segments = 6
        sub = ob.modifiers.new("s", "SUBSURF")
        sub.levels = sub.render_levels = 2
        for p in ob.data.polygons:
            p.use_smooth = True
        # 顔当て（黒）
        fc = add_box("HMD gasket", (-0.08, -0.03, -0.038), (0.08, 0.03, 0.038), dark, "Person", bevel=0.0)
        bevf = fc.modifiers.new("b", "BEVEL")
        bevf.width = 0.02
        bevf.segments = 5
        for p in fc.data.polygons:
            p.use_smooth = True
        # 前面の 3 つの縦長のセンサー窓（Quest 3 の特徴）
        pills = []
        for sx in (-0.042, 0.0, 0.042):
            pill = add_box("HMD pill", (sx - 0.0062, -0.0345, -0.017), (sx + 0.0062, -0.031, 0.017), dark, "Person", bevel=0.0)
            pb = pill.modifiers.new("b", "BEVEL")
            pb.width = 0.0058
            pb.segments = 6
            pills.append(pill)
        # 横のレンズ
        side = []
        for sx in (-0.068, 0.068):
            s = add_cyl("HMD cam", (sx, -0.0345, 0.024), (sx, -0.032, 0.024), 0.004, dark, "Person", seg=16)
            side.append(s)
        # 本体の合わせ目（前面の縁を一周する細い溝）
        # 右側面のボタン
        btn = add_box("HMD button", (0.0918, -0.012, 0.022), (0.0934, 0.004, 0.03), dark, "Person")
        side.append(btn)
        # ストラップ（頭を一周する帯）
        band_verts, band_faces = [], []
        n = 64
        w, d, hgt = 0.108, 0.118, 0.012
        for i in range(n):
            t = 2 * math.pi * i / n
            x, y = math.cos(t) * w, math.sin(t) * d + 0.09
            nz = 0.012 * math.sin(t)
            band_verts.append((x, y, -hgt + nz + 0.012))
            band_verts.append((x, y, hgt + nz + 0.012))
        for i in range(n):
            j = (i + 1) % n
            band_faces.append((2 * i, 2 * j, 2 * j + 1, 2 * i + 1))
        band = add_mesh("HMD band", band_verts, band_faces, sm, "Person", smooth=True)
        bs = band.modifiers.new("t", "SOLIDIFY")
        bs.thickness = 0.005
        top_verts, top_faces = [], []
        for i in range(25):
            t = math.pi * i / 24
            y = 0.09 + math.cos(t) * 0.1
            z = 0.012 + math.sin(t) * 0.125
            top_verts.append((-0.011, y, z))
            top_verts.append((0.011, y, z))
        for i in range(24):
            top_faces.append((2 * i, 2 * i + 2, 2 * i + 3, 2 * i + 1))
        top = add_mesh("HMD top", top_verts, top_faces, sm, "Person", smooth=True)
        ts = top.modifiers.new("t", "SOLIDIFY")
        ts.thickness = 0.004
        parts = [ob, fc, *pills, *side, band, top]
        # 頭の子にする（局所: -Y が前、+Z が上、X が右）
        rest = Matrix((right, -fwd, up)).transposed().to_4x4()
        rest.translation = c
        self.hmd_rest = rest
        self.head_bone_name = head.name
        offs = {ob: Vector((0, 0, 0)), fc: Vector((0, 0.03, 0))}
        self.hmd_parts = []
        for p in parts:
            local = Matrix.Translation(offs.get(p, Vector((0, 0, 0))))
            p.matrix_world = rest @ local
            self.hmd_parts.append((p, rest @ local))
        # 頭の骨の子へ
        pose_head = self.arm.pose.bones[head.name]
        hrest = mw @ pose_head.bone.matrix_local
        self.hmd_offsets = []
        for p, mat in self.hmd_parts:
            self.hmd_offsets.append((p, hrest.inverted() @ mat))
        # 髪は世界座標で作ったので、rest の頭に対する差で動かす
        self.hmd_offsets.append((self.hair_ob, hrest.inverted()))

    # ---------- 右手 ----------
    def _prepare_ik(self):
        self.ik_target = bpy.data.objects.new("IK target", None)
        tc.collection("Person").objects.link(self.ik_target)
        pb = self.arm.pose.bones[self.prefix + "RightForeArm"]
        self.ik = pb.constraints.new("IK")
        self.ik.target = self.ik_target
        self.ik.chain_count = 2
        self.ik.use_tail = True
        self.ik.influence = 0.0
        # 手すりの輪（_build_rail と同じ形）を線分で持つ
        loop = [p + Vector((0, 0, tc.RAIL_Y)) for p in tc.rail_loop(*tc.frame_from_show(self.ctx["show"]))]
        self.rails = [(loop[i], loop[(i + 1) % len(loop)]) for i in range(len(loop))]

    def _nearest_rail(self, p: Vector):
        best, bd = None, 9.0
        for a, b in self.rails:
            ab = b - a
            t = max(0.0, min(1.0, (p - a).dot(ab) / ab.length_squared))
            q = a + ab * t
            d = (Vector((q.x, q.y, 0)) - Vector((p.x, p.y, 0))).length
            if d < bd:
                bd, best = d, q
        return best, bd

    # ---------- 姿勢 ----------
    def row_at(self, t: float):
        rows = self.rows
        lo, hi = 0, len(rows) - 1
        while hi - lo > 1:
            m = (lo + hi) // 2
            if rows[m]["t"] <= t:
                lo = m
            else:
                hi = m
        a, b = rows[lo], rows[hi]
        u = 0.0 if b["t"] == a["t"] else max(0.0, min(1.0, (t - a["t"]) / (b["t"] - a["t"])))
        out = {k: a[k] + (b[k] - a[k]) * u for k in a if k not in ("bodyYaw", "screenYaw")}
        for k in ("bodyYaw", "screenYaw"):
            d = ((b[k] - a[k] + 180) % 360) - 180
            out[k] = a[k] + d * u
        return out

    def visible_meshes(self):
        return [self.meshes[n] for n in ("Body", "Shoes")] + list(self.garments) + [self.hair_ob]

    def location_xy(self):
        return (self.arm.location.x, self.arm.location.y)

    def pose_at(self, t: float, route: str | None = None):
        r = self.row_at(t)
        speed = r["speed"]
        k = STRIDE_K * max(0.0, min(1.0, speed / 0.28))
        phase = (r["dist"] / CYCLE_M) % 1.0
        fi = phase * self.frames
        i0 = int(fi) % self.frames
        i1 = (i0 + 1) % self.frames
        u = fi - int(fi)
        # 骨の向きを armature の正面が 0 度のまま作る
        self.arm.rotation_quaternion = self.base_q
        bpy.context.view_layer.update()
        wanted = {}
        for b in self.order:
            if b.name in self.mean:
                d = self.dirs[i0][b.name].lerp(self.dirs[i1][b.name], u).normalized()
                wanted[b.name] = self.mean[b.name].lerp(d, k).normalized()
        self._apply_directions(wanted)
        hips = self.arm.pose.bones[self.prefix + "Hips"]
        hips.location = (0, 0, 0)
        # 姿勢: 少し前屈み、頭を探るように動かす
        spine = self.arm.pose.bones[self.prefix + "Spine1"]
        spine.rotation_quaternion = spine.rotation_quaternion @ Quaternion((1, 0, 0), math.radians(3.5))
        # 頭の向き（Unity の記録のスクリーン方位が体からどれだけずれたか + わずかな揺れ）
        head_off = ((r["screenYaw"] - r["bodyYaw"] + 180) % 360) - 180
        head_off = max(-35.0, min(35.0, head_off)) + 12.0 * math.sin(t * 0.73 + 0.5) + 5.0 * math.sin(t * 1.9)
        neck = self.arm.pose.bones[self.prefix + "Head"]
        neck.rotation_quaternion = neck.rotation_quaternion @ Quaternion((0, 1, 0), math.radians(-head_off * 0.7)) @ Quaternion((1, 0, 0), math.radians(4 + 3 * math.sin(t * 0.51)))
        # 全身の位置と向き（Unity の yaw は上から見て時計回り、+z 基準）
        yaw = math.radians(r["bodyYaw"])
        fwd = Vector((math.sin(yaw), math.cos(yaw), 0))
        ang = self.rest_fwd.to_2d().angle_signed(fwd.to_2d())
        q = Quaternion((0, 0, 1), -ang)
        self.arm.rotation_quaternion = q @ self.base_q
        pos = Vector((r["x"], r["z"], 0))
        self.arm.location = pos
        # 手すり
        rail_q, d = self._nearest_rail(pos)
        reach = max(0.0, min(1.0, (0.5 - d) / 0.12))
        if reach > 0 and rail_q is not None:
            side = (rail_q - pos)
            side.z = 0
            right = fwd.cross(Vector((0, 0, 1)))
            if side.normalized().dot(right) < -0.2:
                reach = 0.0  # 左側の手すりには右手を伸ばさない
        hand_goal = rail_q + Vector((0, 0, 0.018)) if rail_q is not None else pos
        self.ik_target.location = hand_goal
        self.ik.influence = reach
        bpy.context.view_layer.update()
        # 足を床へ
        toes = []
        for n in ("RightToe_End", "LeftToe_End", "RightFoot", "LeftFoot"):
            pb = self.arm.pose.bones[self.prefix + n]
            toes.append((self.arm.matrix_world @ pb.head).z - (0.075 if n.endswith("Foot") else 0.012))
        self.arm.location.z = -min(toes)
        bpy.context.view_layer.update()
        # HMD を頭へ
        pb = self.arm.pose.bones[self.prefix + "Head"]
        hm = self.arm.matrix_world @ pb.matrix
        for p, off in self.hmd_offsets:
            p.matrix_world = hm @ off
        return r
