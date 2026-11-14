"""部屋・カーテンの壁・棚・固定カメラの機材を組む（Blender）。寸法の正は show.json と rig.json。"""
from __future__ import annotations

import math
import os
import random

import bmesh
import bpy
from mathutils import Matrix, Vector

import twin_common as tc
import twin_materials as tm
from twin_common import add_box, add_cyl, add_lathe, add_mesh, add_tube, u2b


def build_shell(mats):
    x0, x1 = tc.ROOM_X
    y0, y1 = tc.ROOM_Y
    h = tc.ROOM_H
    # 細かく割った面（光が面ごとに補間されない。Cycles には不要だが、壁のうねりを本物の形にする）
    def grid(name, origin, u, v, nu, nv, mat, wobble=0.0, seed=0):
        rnd = random.Random(seed)
        verts, faces = [], []
        n = u.cross(v).normalized()
        phase = [rnd.random() * 6.28 for _ in range(6)]
        for j in range(nv + 1):
            for i in range(nu + 1):
                a, b = i / nu, j / nv
                w = 0.0
                if wobble:
                    su, sv = a * u.length, b * v.length
                    w = wobble * (math.sin(su * 1.7 + phase[0]) * math.sin(sv * 1.3 + phase[1])
                                  + 0.5 * math.sin(su * 4.3 + phase[2]) * math.sin(sv * 3.1 + phase[3])
                                  + 0.2 * math.sin(su * 11 + phase[4]) * math.sin(sv * 9 + phase[5]))
                verts.append(origin + u * a + v * b + n * w)
        for j in range(nv):
            for i in range(nu):
                k = j * (nu + 1) + i
                faces.append((k, k + 1, k + nu + 2, k + nu + 1))
        ob = add_mesh(name, verts, faces, mat, "Room", smooth=True)
        uvl = ob.data.uv_layers.new(name="UVMap")
        for poly in ob.data.polygons:
            for li_ in poly.loop_indices:
                vi = ob.data.loops[li_].vertex_index
                ii, jj = vi % (nu + 1), vi // (nu + 1)
                uvl.data[li_].uv = (ii / nu * u.length / 4.8 + seed * 0.29, jj / nv * v.length / tc.ROOM_H)
        return ob

    def dice(ob, px=3.0):
        """材質の高さで面を実際に動かす。画面の細かさに合わせて割るので、影が段差に落ちる。"""
        m = ob.modifiers.new("dice", "SUBSURF")
        m.subdivision_type = "SIMPLE"
        m.use_adaptive_subdivision = True
        m.adaptive_space = "PIXEL"
        m.adaptive_pixel_size = px

    W, D = x1 - x0, y1 - y0
    grid("Floor", Vector((x0, y0, 0)), Vector((W, 0, 0)), Vector((0, D, 0)), 40, 40, mats["floor"], 0.0015, 1)
    grid("Ceiling", Vector((x0, y1, h)), Vector((W, 0, 0)), Vector((0, -D, 0)), 40, 40, mats["ceiling"], 0.006, 2)
    dice(grid("Wall +y", Vector((x1, y1, 0)), Vector((-W, 0, 0)), Vector((0, 0, h)), 90, 45, mats["wall"], 0.006, 3))
    dice(grid("Wall -y", Vector((x0, y0, 0)), Vector((W, 0, 0)), Vector((0, 0, h)), 90, 45, mats["wall"], 0.006, 4))
    dice(grid("Wall -x", Vector((x0, y1, 0)), Vector((0, -D, 0)), Vector((0, 0, h)), 90, 45, mats["wall"], 0.006, 5))
    dice(grid("Wall +x", Vector((x1, y0, 0)), Vector((0, D, 0)), Vector((0, 0, h)), 90, 45, mats["wall"], 0.006, 6))
    # 入隅の塗り溜まり（面と面が刃物の線で交わらないように）
    r = 0.017
    for a0, a1 in [((x0, y1, 0), (x0, y1, h)), ((x1, y1, 0), (x1, y1, h)), ((x0, y0, 0), (x0, y0, h)), ((x1, y0, 0), (x1, y0, h)),
                   ((x0, y0, h), (x0, y1, h)), ((x1, y0, h), (x1, y1, h)), ((x0, y0, h), (x1, y0, h)), ((x0, y1, h), (x1, y1, h))]:
        add_cyl("Corner bead", a0, a1, r, mats["corner"], "Room", seg=16)
    # 幅木（欠けた木）
    b = mats["baseboard"]
    for name, a0, a1 in [("Base +y", (x0, y1 - 0.016, 0), (x1, y1, 0.09)), ("Base -x", (x0, y0, 0), (x0 + 0.016, y1, 0.09)),
                         ("Base -y", (x0, y0, 0), (x1, y0 + 0.016, 0.09)), ("Base +x", (x1 - 0.016, y0, 0), (x1, y1, 0.09))]:
        add_box(name, a0, a1, b, bevel=0.003)


def build_conduit(mats):
    """壁を這う電線管・留め金・分電箱・スイッチ（奥の -x 壁と +y 壁）。"""
    x0, _ = tc.ROOM_X
    _, y1 = tc.ROOM_Y
    h = tc.ROOM_H
    px, py = x0 + 0.042, -0.9
    rust = mats["rust"]
    add_cyl("Conduit v", (px, py, 0.14), (px, py, h), 0.0165, rust)
    for z in [0.48, 1.06, 1.68, 2.3, 2.9]:
        add_box(f"Clamp {z}", (x0 + 0.002, py - 0.03, z - 0.012), (px + 0.02, py + 0.03, z + 0.012), rust, bevel=0.002)
    # 分電箱
    add_box("Junction", (x0 + 0.003, py + 0.06, 1.18), (x0 + 0.07, py + 0.2, 1.38), mats["panel"], bevel=0.004)
    add_box("Junction lid", (x0 + 0.07, py + 0.054, 1.174), (x0 + 0.077, py + 0.206, 1.386), mats["panel"], bevel=0.002)
    for sy, sz in [(0.068, 1.186), (0.192, 1.186), (0.068, 1.374), (0.192, 1.374)]:
        add_cyl("Junction screw", (x0 + 0.077, py + sy, sz), (x0 + 0.0795, py + sy, sz), 0.0042, rust, seg=12)
    add_cyl("Junction gland", (x0 + 0.042, py + 0.13, 1.372), (x0 + 0.042, py + 0.13, 1.405), 0.0165, rust, seg=16)
    add_cyl("Junction gland 2", (x0 + 0.042, py + 0.0, 1.27), (x0 + 0.042, py + 0.06, 1.27), 0.013, rust, seg=16)
    add_cyl("Conduit h", (x0 + 0.042, py + 0.13, 1.38), (x0 + 0.042, py + 0.13, 2.74), 0.011, rust)
    add_cyl("Conduit t", (x0 + 0.042, py + 0.13, 2.74), (x0 + 0.042, y1 - 0.04, 2.74), 0.011, rust)
    add_cyl("Conduit t2", (x0 + 0.042, y1 - 0.04, 2.74), (0.6, y1 - 0.04, 2.74), 0.011, rust)
    add_box("Junction2", (x0 + 0.02, y1 - 0.12, 2.68), (x0 + 0.1, y1 - 0.004, 2.8), mats["panel"], bevel=0.004)
    # 天井へ上がる電線と碍子
    add_cyl("Conduit c", (x0 + 0.042, py, 3.18), (tc.BULB.x, py, 3.18), 0.011, rust)
    add_cyl("Conduit c2", (tc.BULB.x, py, 3.18), (tc.BULB.x, tc.BULB.y, 3.18), 0.011, rust)
    # 壁スイッチ
    add_box("Switch", (x0 + 0.002, -0.28, 1.2), (x0 + 0.01, -0.2, 1.32), mats["switch"], bevel=0.002)
    add_box("Switch toggle", (x0 + 0.01, -0.25, 1.245), (x0 + 0.018, -0.23, 1.272), mats["porcelain"], bevel=0.0015)
    for z in (1.212, 1.308):
        add_cyl("Switch screw", (x0 + 0.01, -0.24, z), (x0 + 0.0125, -0.24, z), 0.003, mats["brass_old"], seg=10)
    # 古い扉（+x 壁）
    door = mats["door"]
    x1 = tc.ROOM_X[1]
    y_c = -2.1
    add_box("Door", (x1 - 0.03, y_c - 0.44, 0.012), (x1 - 0.002, y_c + 0.44, 2.04), door, bevel=0.003)
    for s, zz in [(-1, 0), (1, 0)]:
        add_box(f"Door jamb {s}", (x1 - 0.05, y_c + s * 0.5 - 0.05, 0), (x1, y_c + s * 0.5 + 0.05, 2.12), door, bevel=0.003)
    add_box("Door head", (x1 - 0.05, y_c - 0.55, 2.04), (x1, y_c + 0.55, 2.12), door, bevel=0.003)
    add_cyl("Door knob", (x1 - 0.03, y_c + 0.36, 1.0), (x1 - 0.085, y_c + 0.36, 1.0), 0.022, mats["brass"])


def _curtain(name, a: Vector, b: Vector, height: float, seed: int, mat):
    """手で留めた厚手のカーテン。襞の幅は 7〜15cm で揃えない。断面は丸く（細い山にすると縁に白い線が出る）、下へ行くほど開く。"""
    rnd = random.Random(seed)
    ln = (b - a).length
    t = (b - a).normalized()
    n = Vector((-t.y, t.x, 0))
    nu, nv = int(ln / 0.007), int(height / 0.016)
    # 襞の中心を不揃いに
    cuts = [0.0]
    while cuts[-1] < ln:
        cuts.append(cuts[-1] + rnd.uniform(0.1, 0.21))
    cuts[-1] = ln
    amps = [rnd.uniform(0.6, 1.25) for _ in cuts]
    ph = [rnd.random() * 6.28 for _ in range(12)]
    drift = [rnd.uniform(-1.0, 1.0) for _ in cuts]
    verts, faces = [], []
    for j in range(nv + 1):
        v = j / nv
        z = height * v
        down = 1.0 - v
        for i in range(nu + 1):
            u = i / nu * ln
            # 下へ行くと襞は横へ流れる
            uu = u + 0.02 * down * math.sin(u * 7.1 + ph[0]) + 0.012 * down * math.sin(u * 3.3 + ph[1] + down * 2.0)
            k = 0
            while k < len(cuts) - 2 and uu > cuts[k + 1]:
                k += 1
            w = cuts[k + 1] - cuts[k]
            f = max(0.0, min(1.0, (uu - cuts[k]) / w))
            # 丸い断面（端で滑らかに次の襞へ）
            prof = math.cos(2 * math.pi * f)
            # 境目（f=0,1）で必ず同じ高さになるよう、形の差は山の中だけで足す。振幅も隣へ滑らかに渡す
            # （ここが不連続だと、段差の立ち上がりが上から下まで白い線になる）
            prof = prof + 0.1 * math.sin(2 * math.pi * f) ** 2 * math.cos(drift[k] * down * 3)
            fs = f * f * (3 - 2 * f)
            amp = amps[k] + (amps[k + 1] - amps[k]) * fs
            # 隣の襞の幅へ渡す補間は f=0,1 で傾き 0（直線だと山の頂点で折れ、襞に白い縦線が出る疑いがある）
            wid = w if k == 0 else (cuts[k] - cuts[k - 1]) + (w - (cuts[k] - cuts[k - 1])) * fs
            gather = 0.01 + 0.02 * (1 - math.exp(-down * 5.0))
            off = prof * gather * amp * (wid / 0.11) * (1 + 0.55 * down)
            # 大きなうねり。上は竿に揃い、下は手すりと床で乱れる
            off += 0.03 * down * math.sin(u * 4.2 + ph[2] + down * 2.3)
            off += 0.016 * down * math.sin(u * 9.3 + ph[3] - down * 3.1)
            off += 0.006 * math.sin(v * 13 + u * 6 + ph[4]) * down
            off += 0.002 * math.sin(v * 37 + u * 21 + ph[5])
            # 手すりの高さで押されて段がつく
            off += 0.008 * math.exp(-((z - tc.RAIL_Y) / 0.09) ** 2) * math.sin(u * 8 + ph[6])
            hem = math.exp(-z / 0.09)
            off += hem * 0.012 * math.sin(u * 14 + ph[7])
            p = a + t * u + n * off + Vector((0, 0, z))
            verts.append(p)
    for j in range(nv):
        for i in range(nu):
            k = j * (nu + 1) + i
            faces.append((k, k + 1, k + nu + 2, k + nu + 1))
    ob = add_mesh(name, verts, faces, mat, "Booth", smooth=True)
    # 布シミュレーションは使わない。襞の山が細い折れ線に潰れ、光を受けて上から下まで白い線になる
    sub = ob.modifiers.new("smooth", "SUBSURF")
    sub.levels = sub.render_levels = 2
    sol = ob.modifiers.new("thick", "SOLIDIFY")
    sol.thickness = 0.0026
    sol.use_rim = False
    pleats = [(cuts[i] + cuts[i + 1]) * 0.5 for i in range(len(cuts) - 1)]
    return ob, pleats


def build_booth(show, mats):
    walls = tc.walls_from_show(show)
    pipe = mats["pipe"]
    corner = None
    ends = []
    for w in walls:
        ends.append((w["x1"], w["z1"]))
        ends.append((w["x2"], w["z2"]))
    for e in ends:
        if sum(1 for q in ends if abs(q[0] - e[0]) + abs(q[1] - e[1]) < 0.01) >= 2:
            corner = e
    posts = []
    # 実物の枠の寸法。竿（上桟）を実測の高さに置くよう、布とクリップの基準をその少し下にする
    H = tc.FRAME_TOP - 0.015
    for i, w in enumerate(walls):
        a = u2b(w["x1"], 0, w["z1"])
        b = u2b(w["x2"], 0, w["z2"])
        t = (b - a).normalized()
        cloth_h = H - 0.015
        # 演出の試作用: 角の合わせ目を開く（TWIN_SEAM_GAP = 片側の引き幅 m。既定 0 で従来どおり）
        gap = float(os.environ.get("TWIN_SEAM_GAP", "0") or 0)
        ga = gap if corner and abs(w["x1"] - corner[0]) + abs(w["z1"] - corner[1]) < 0.01 else 0.0
        gb = gap if corner and abs(w["x2"] - corner[0]) + abs(w["z2"] - corner[1]) < 0.01 else 0.0
        # 枠の竿よりカーテンは内寄り。外側の手すりを避けるため少し中へ
        ob, pleats = _curtain(f"Curtain {w['id']}", a + t * (0.012 + ga), b - t * (0.004 + gb), cloth_h, 17 + i * 31, mats["curtain"])
        # 竿: プレート A/C のとおり
        add_cyl(f"Rod {w['id']}", a + Vector((0, 0, H + 0.015)) - t * 0.02, b + Vector((0, 0, H + 0.015)) + t * 0.02, 0.0135, pipe)
        # クリップ（プレートのカーテンクリップ）
        for k, along in enumerate(pleats):
            rk = random.Random(k * 7 + i)
            if rk.random() < 0.12:
                continue
            q = a + t * along + t * rk.uniform(-0.016, 0.016)
            add_box(f"Clip {w['id']} {k}", q + Vector((-0.006, -0.006, H - 0.04)) + t * 0, q + Vector((0.006, 0.006, H + 0.0)), mats["clip"])
            add_cyl(f"Hook {w['id']} {k}", q + Vector((0, 0, H - 0.003)), q + Vector((0, 0, H + 0.03)), 0.0016, mats["clip"], seg=8)
            add_cyl(f"Ring {w['id']} {k}", q + Vector((0, 0, H + 0.015)) - t * 0.003, q + Vector((0, 0, H + 0.015)) + t * 0.003, 0.0172, mats["clip"], seg=16)
        for e in (a, b):
            if not any((e - q).length < 0.01 for q in posts):
                posts.append(e)
    _build_rail(show, pipe)
    for e in posts:
        add_cyl("Post", e, e + Vector((0, 0, H + 0.03)), 0.0145, pipe)
        add_cyl("Post top", e + Vector((0, 0, H - 0.02)), e + Vector((0, 0, H + 0.035)), 0.0195, pipe)
        add_cyl("Post mid", e + Vector((0, 0, tc.RAIL_Y - 0.03)), e + Vector((0, 0, tc.RAIL_Y + 0.03)), 0.0195, pipe)
        add_lathe("Post base", [(0.0, 0.021), (0.1, 0.021), (0.104, 0.018), (0.106, 0.004), (0.104, 0.0), (0, 0)], mats["base"], "Booth", center=(e.x, e.y, 0))
        add_lathe("Post boss", [(0, 0.075), (0.016, 0.075), (0.024, 0.062), (0.03, 0.03), (0.04, 0.021)], pipe, "Booth", center=(e.x, e.y, 0))
    # 演出の試作用: 開いた合わせ目の奥に暖かい灯り（TWIN_SEAM_GLOW=1 のときだけ。既定は何も足さない）
    if os.environ.get("TWIN_SEAM_GLOW", "") == "1" and corner is not None:
        _build_seam_glow(walls, corner, H)
    return posts


SEAM_GLOW_SRGB = (255, 196, 90)   # 2 周目 B で飛ぶ「別の場所」と同じ色味（束の映像で見える値）
# 上の色を、束の仕上げ（twin_post の phone・露出 7）とスマホの階調（feed_tone の A）を通して出すための発光（線形）。
# 仕上げのトーンは明るい所の色を白へ寄せるので、発光そのものはかなり赤く置く（2026-11-14 に逆算。出口は 252,196,89）
SEAM_GLOW_EMIT = (0.69, 0.029, 0.0016)
SEAM_GLOW_BACK = 0.15             # 角から外へ、灯りの面までの距離（m）。外の手すり（角から 0.08）より奥
SEAM_GLOW_HALF = 0.32             # 面の半幅。角の左右の幕の裏に収まり、画の上では幕の外へ出ない
SEAM_GLOW_SPILL_W = 8.0           # 縁と床へ漏れる灯りの強さ（W）
SEAM_GLOW_SPILL_Z = 0.45          # 漏れる灯りの高さ（m）。低いほど隙間の足元の床に溜まる
SEAM_GLOW_SPILL_TILT = 0.4        # 漏れる灯りの下向き（水平 1 に対する下がり）
SEAM_GLOW_SPILL_DEG = 100.0       # 漏れる灯りの円錐の開き。L の外の床（角から横へ 66 度より外）には当てない


def _build_seam_glow(walls, corner, top):
    """合わせ目の奥に、現実の部屋の代わりに見える暖色の発光面と、縁と床へ漏れる弱い灯りを置く。

    面は角の外側（L の外）に、角の二等分線へ垂直に立てる。高さは幕より低く、幅は幕の裏に収まる大きさ。
    面はカメラにだけ見え、何も照らさず、影も落とさない（照らすと L の外の床と、幕の上から見える奥の壁が
    暖色に染まり、合わせ目から離れた所の画が変わる）。照らすのは隙間の真後ろのスポット 1 つで、
    光のリンクで幕・床・支柱だけを受け手にする。幕は光を通さないので、届くのは隙間を抜けた分だけ。
    """
    c = u2b(corner[0], 0, corner[1])
    dirs = []
    for w in walls:
        a = u2b(w["x1"], 0, w["z1"])
        b = u2b(w["x2"], 0, w["z2"])
        dirs.append((b - a).normalized() if (a - c).length < 0.01 else (a - b).normalized())
    out = -(dirs[0] + dirs[1]).normalized()
    side = Vector((-out.y, out.x, 0.0))
    center = c + out * SEAM_GLOW_BACK
    h = top - 0.06
    p = [center - side * SEAM_GLOW_HALF, center + side * SEAM_GLOW_HALF]
    verts = [p[0], p[1], p[1] + Vector((0, 0, h)), p[0] + Vector((0, 0, h))]

    m = bpy.data.materials.new("seam_glow")
    m.use_nodes = True
    nt = m.node_tree
    nt.nodes.clear()
    em = nt.nodes.new("ShaderNodeEmission")
    em.inputs["Color"].default_value = SEAM_GLOW_EMIT + (1.0,)
    # 下ほど少し明るい（奥の部屋の床に灯りが溜まっている見え方）。面の上下は生成座標の z
    tco = nt.nodes.new("ShaderNodeTexCoord")
    sep = nt.nodes.new("ShaderNodeSeparateXYZ")
    nt.links.new(tco.outputs["Generated"], sep.inputs[0])
    rmp = nt.nodes.new("ShaderNodeMapRange")
    rmp.inputs["To Min"].default_value = 1.15
    rmp.inputs["To Max"].default_value = 0.85
    nt.links.new(sep.outputs["Z"], rmp.inputs["Value"])
    nt.links.new(rmp.outputs["Result"], em.inputs["Strength"])
    o = nt.nodes.new("ShaderNodeOutputMaterial")
    nt.links.new(em.outputs[0], o.inputs["Surface"])
    plane = add_mesh("Seam glow", verts, [(0, 1, 2, 3)], m, "Booth")
    for attr in ("visible_diffuse", "visible_glossy", "visible_transmission", "visible_volume_scatter", "visible_shadow"):
        setattr(plane, attr, False)

    # 縁の襞と、隙間の手前の床へ漏れる灯り（隙間の真後ろの低い所から、L の内へ少し下向きに）
    li = bpy.data.lights.new("Seam glow spill", "SPOT")
    li.energy = SEAM_GLOW_SPILL_W
    k = max(SEAM_GLOW_EMIT)
    li.color = tuple(v / k for v in SEAM_GLOW_EMIT)
    li.shadow_soft_size = 0.03
    li.spot_size = math.radians(SEAM_GLOW_SPILL_DEG)
    li.spot_blend = 0.5
    lo = bpy.data.objects.new("Seam glow spill", li)
    lo.location = c + out * 0.10 + Vector((0, 0, SEAM_GLOW_SPILL_Z))
    # スポットは局所の -Z を向く。L の内（-out）へ、少し下へ
    aim = (-out + Vector((0, 0, -SEAM_GLOW_SPILL_TILT))).normalized()
    lo.rotation_euler = aim.to_track_quat("-Z", "Z").to_euler()
    tc.collection("Booth").objects.link(lo)

    recv = bpy.data.collections.new("Seam glow receivers")
    for ob in bpy.data.objects:
        if ob.type == "MESH" and ob.name.startswith(("Curtain ", "Post", "Floor")):
            recv.objects.link(ob)
    lo.light_linking.receiver_collection = recv


def _build_rail(show, pipe):
    """手すりは L を 1 周する 1 本の輪（プレート A・B・C）。端で U に折り返し、外の角は丸く回る。脚は 2 本ずつ床まで。"""
    corner, end_x, end_z = tc.frame_from_show(show)
    cx, cy = corner
    d = tc.RAIL_D
    y = tc.RAIL_Y
    lift = Vector((0, 0, y))
    # 手すりの影は襞の上でジグザグの棘になり、布の模様に見えるので落とさない
    loop = tc.add_poly_tube("Rail loop", [p + lift for p in tc.rail_loop(corner, end_x, end_z)], 0.0135, pipe, cyclic=True)
    loop.visible_shadow = False
    legs = [
        (end_z[0] - d, end_z[1]), (end_z[0] + d, end_z[1]),  # w2 の端
        (end_x[0], end_x[1] + d), (end_x[0], end_x[1] - d),  # w1 の端
        (cx - d, cy), (cx, cy + d),  # 外の角
        (cx + d, cy), (cx, cy - d),  # 内の角
    ]
    # 内の角は手すりが支柱の手前まで伸びて脚で受ける（プレート A の角に脚が 2 本見える）
    for sx, sy in ((cx + d, cy), (cx, cy - d)):
        stub = add_cyl("Rail stub", Vector((cx + d, cy - d, y)), Vector((sx, sy, y)), 0.0135, pipe)
        stub.visible_shadow = False
    for x, yy in legs:
        add_cyl("Rail leg", (x, yy, 0.0), (x, yy, y), 0.0105, pipe)
        add_cyl("Rail foot", (x, yy, 0.0), (x, yy, 0.075), 0.017, pipe)
    # 外側の床を這う棒（プレート C）
    for a, b in (((cx, cy + d), (end_x[0], end_x[1] + d)), ((cx - d, cy), (end_z[0] - d, end_z[1]))):
        add_cyl("Floor bar", (a[0], a[1], 0.014), (b[0], b[1], 0.014), 0.009, pipe)


def _phone(name, m: Matrix, mats, screen_mat=None):
    """Pixel 7a 相当（152×73×9mm）。行列の -Z がレンズの向き、局所 X が長辺（横向きに構える）。"""
    w, h, d = 0.1525, 0.0729, 0.009
    ob = add_box(name, (-w / 2, -h / 2, -d / 2), (w / 2, h / 2, d / 2), mats["phone_body"], "Rig", bevel=0.006)
    ob.matrix_world = m
    # カメラバー（背面 = -Z 側。レンズの中心を m の原点に合わせる）
    cam_x = -w / 2 + 0.03
    bar = add_box(name + " bar", (-w / 2 + 0.012, -h / 2 + 0.005, -d / 2 - 0.0022), (-w / 2 + 0.05, h / 2 - 0.005, -d / 2), mats["bar"], "Rig", bevel=0.0018)
    bar.matrix_world = m
    for ly in (0.014, -0.012):
        lens = add_cyl(name + " lens", (cam_x, ly, -d / 2 - 0.0022), (cam_x, ly, -d / 2 - 0.0034), 0.0072, mats["lens"], "Rig", seg=32)
        lens.matrix_world = m
    # 画面（+Z 側）
    sw, sh = w - 0.006, h - 0.005
    scr = add_mesh(name + " screen", [(-sw / 2, -sh / 2, d / 2 + 0.0002), (sw / 2, -sh / 2, d / 2 + 0.0002), (sw / 2, sh / 2, d / 2 + 0.0002), (-sw / 2, sh / 2, d / 2 + 0.0002)],
                   [(0, 1, 2, 3)], screen_mat or mats["screen"], "Rig")
    uv = scr.data.uv_layers.new(name="UVMap")
    for lo, co in zip(scr.data.loops, [(0, 0), (1, 0), (1, 1), (0, 1)]):
        uv.data[lo.index].uv = co
    scr.matrix_world = m
    return ob


def _clamp_holder(name, m: Matrix, mats):
    """スマホを上下で挟むクランプ（図 1 の三脚の頭）。"""
    w, h, d = 0.1525, 0.0729, 0.009
    jaw = mats["mount"]
    for s, sgn in (("t", 1), ("b", -1)):
        ob = add_box(name + " jaw " + s, (-0.024, sgn * (h / 2) - 0.004 if sgn > 0 else -h / 2 - 0.009, -d / 2 - 0.006),
                     (0.024, h / 2 + 0.009 if sgn > 0 else -h / 2 + 0.004, d / 2 + 0.006), jaw, "Rig", bevel=0.002)
        ob.matrix_world = m
    back = add_box(name + " back", (-0.012, -h / 2 - 0.004, -d / 2 - 0.016), (0.012, h / 2 + 0.004, -d / 2 - 0.006), jaw, "Rig", bevel=0.002)
    back.matrix_world = m
    knob = add_cyl(name + " knob", (0, 0, -d / 2 - 0.016), (0, 0, -d / 2 - 0.03), 0.0095, jaw, "Rig", seg=12)
    knob.matrix_world = m
    for sgn in (1, -1):
        pad = add_box(name + " pad", (-0.02, sgn * (h / 2 + 0.0003) - 0.0012, -d / 2 - 0.001), (0.02, sgn * (h / 2 + 0.0003) + 0.0012, d / 2 + 0.001), mats["rubber"], "Rig")
        pad.matrix_world = m


def build_tripod(cam, mats, m: Matrix, phone_center: Vector):
    """A: 三脚（図 1）。センターポール + 3 本脚。"""
    legm, mount = mats["pipe"], mats["mount"]
    base = Vector((phone_center.x, phone_center.y, 0))
    head_z = phone_center.z - 0.06
    hub_z = head_z - 0.3
    spread = 0.26
    add_cyl("Tripod pole", (base.x, base.y, hub_z - 0.1), (base.x, base.y, head_z), 0.0085, legm, "Rig")
    add_cyl("Tripod pole2", (base.x, base.y, hub_z - 0.04), (base.x, base.y, hub_z + 0.14), 0.0115, legm, "Rig")
    add_cyl("Tripod hub", (base.x, base.y, hub_z - 0.025), (base.x, base.y, hub_z + 0.03), 0.021, mount, "Rig")
    rnd = random.Random(5)
    for k in range(3):
        a = math.radians(k * 120 + 40 + rnd.random() * 10)
        foot = Vector((base.x + math.cos(a) * spread, base.y + math.sin(a) * spread, 0.016))
        top = Vector((base.x + math.cos(a) * 0.02, base.y + math.sin(a) * 0.02, hub_z))
        mid = top.lerp(foot, 0.52)
        add_cyl(f"Tripod leg {k}", top, mid, 0.0088, legm, "Rig")
        add_cyl(f"Tripod leg2 {k}", mid - (foot - top).normalized() * 0.02, foot, 0.0065, legm, "Rig")
        add_cyl(f"Tripod lock {k}", mid - (foot - top).normalized() * 0.012, mid + (foot - top).normalized() * 0.018, 0.0115, mount, "Rig")
        add_lathe(f"Tripod foot {k}", [(0.0, 0.03), (0.009, 0.03), (0.012, 0.012), (0.014, 0.0), (0, 0)], mats["rubber"], "Rig", center=(foot.x, foot.y, 0))
        # 脚のステー
        add_cyl(f"Tripod stay {k}", (base.x, base.y, hub_z - 0.1), top.lerp(foot, 0.38), 0.0035, mount, "Rig", seg=10)
    # 雲台
    add_lathe("Tripod ball", [(0, 0.05), (0.012, 0.05), (0.015, 0.038), (0.015, 0.012), (0.019, 0.0), (0, 0)], mount, "Rig", center=(base.x, base.y, head_z))
    add_cyl("Tripod knob", (base.x + 0.015, base.y, head_z + 0.03), (base.x + 0.04, base.y, head_z + 0.03), 0.007, mount, "Rig", seg=16)
    add_cyl("Tripod neck", (base.x, base.y, head_z + 0.05), phone_center - Vector((0, 0, 0.03)), 0.006, mount, "Rig", seg=16)


def build_cradle(cam, mats, m: Matrix, phone_center: Vector, cable_to: Vector):
    """B: 床に立てかけたスマホ（プレート B）。レンズは床から 3cm で、手前に写るものは無い。支えは背中側だけ。"""
    black, mount = mats["mount"], mats["pipe"]
    fx = m.to_3x3() @ Vector((1, 0, 0))
    fy = Vector((fx.y * -1, fx.x, 0)).normalized()
    fx2 = Vector((fx.x, fx.y, 0)).normalized()
    o = Vector((phone_center.x, phone_center.y, 0))
    rot = Matrix((fx2, fy, (0, 0, 1))).transposed().to_4x4()
    rot.translation = o
    plate = add_box("Cradle foot", (-0.068, -0.075, 0.0), (0.09, -0.012, 0.006), mount, "Rig", bevel=0.0015)
    plate.matrix_world = rot
    # 局所 +Y がレンズの向き。背板はスマホの画面側だけ（前に置くと床が隠れる）
    back = add_box("Cradle back", (-0.066, -0.062, 0.006), (0.08, -0.024, 0.1), black, "Rig", bevel=0.003)
    back.matrix_world = rot
    # スマホの短辺の USB 端子へ差した L 字のプラグから床へ
    port = phone_center + (m.to_3x3() @ Vector((0.0772, 0, 0)))
    add_cyl("Cradle plug", port, port + fx2 * 0.022, 0.0046, mats["mount"], "Rig", seg=14)
    add_tube("Cradle cable", [port + fx2 * 0.02, port + fx2 * 0.05 - Vector((0, 0, 0.035)), o + fx2 * 0.16 + Vector((0, 0, 0.012)), o + fx2 * 0.32 + fy * 0.05 + Vector((0, 0, 0.003)), cable_to], 0.0019, mats["cable"], "Rig")


def _slotted_flange(name, origin, along, z1, mat):
    """穴あきアングルの片面。幅 36mm、中央に 14×32mm の長穴が 60mm おき。厚みは Solidify。"""
    cols = [0.0, 0.011, 0.025, 0.036]
    rows = [0.0]
    z = 0.024
    while z + 0.032 < z1 - 0.01:
        rows += [z, z + 0.032]
        z += 0.06
    rows.append(z1)
    index, verts, faces = {}, [], []

    def vid(i, j):
        k = (i, j)
        if k not in index:
            index[k] = len(verts)
            verts.append(origin + along * cols[i] + Vector((0, 0, rows[j])))
        return index[k]

    for j in range(len(rows) - 1):
        hole = j % 2 == 1
        for i in range(3):
            if i == 1 and hole:
                continue
            faces.append((vid(i, j), vid(i + 1, j), vid(i + 1, j + 1), vid(i, j + 1)))
    ob = add_mesh(name, verts, faces, mat, "Rig")
    sol = ob.modifiers.new("thick", "SOLIDIFY")
    sol.thickness = 0.0018
    return ob


def _paint_can(idx, cx, cy, z, r, hh, tilt, rot, mats):
    """ペンキ缶。胴・巻き締めのふち・つる。紙ラベルは少し浮いて端がめくれる。"""
    ob = add_lathe(f"Can {idx}", [(0, 0), (r, 0), (r + 0.002, 0.004), (r + 0.002, 0.016), (r, 0.018), (r, hh - 0.016), (r + 0.003, hh - 0.012), (r + 0.003, hh - 0.002), (r - 0.006, hh), (r - 0.008, hh - 0.006), (0, hh - 0.006)],
                   mats["can"], "Rig", center=(0, 0, 0))
    ob.location = (cx, cy, z + (r * math.sin(tilt)))
    ob.rotation_euler = (tilt, 0.0, rot)
    for v in ob.data.vertices:
        t = math.atan2(v.co.y, v.co.x)
        dent = 0.0018 * math.sin(t * 3 + idx) * math.sin(v.co.z * 31 + idx)
        v.co.x += math.cos(t) * dent
        v.co.y += math.sin(t) * dent
    # 紙ラベル
    a0 = -2.4 + idx * 0.5
    sweep = [4.4, 3.3, 3.9][idx]
    nu, nv = 48, 14
    verts, faces, uvs = [], [], []
    for j in range(nv + 1):
        for i in range(nu + 1):
            u, v = i / nu, j / nv
            peel = max(0.0, u - 0.8) / 0.2 * max(0.0, v - 0.35) / 0.65
            rr = r + 0.0024 + peel * 0.016
            ang = a0 + u * sweep
            verts.append((math.cos(ang) * rr, math.sin(ang) * rr, hh * (0.2 + 0.56 * v) + peel * 0.003))
            uvs.append((u * min(1.0, sweep / 4.4), v))
    for j in range(nv):
        for i in range(nu):
            k = j * (nu + 1) + i
            faces.append((k, k + 1, k + nu + 2, k + nu + 1))
    lab = add_mesh(f"Can label {idx}", verts, faces, mats[f"label_{idx}"], "Rig", smooth=True)
    uvl = lab.data.uv_layers.new(name="UVMap")
    for lo in lab.data.loops:
        uvl.data[lo.index].uv = uvs[lo.vertex_index]
    lab.location, lab.rotation_euler = ob.location, ob.rotation_euler
    # つる（針金）。寝かせて缶の肩に掛かる
    side = 0.72 + idx * 0.7
    e0 = Vector((math.cos(side) * (r + 0.002), math.sin(side) * (r + 0.002), hh - 0.03))
    e1 = Vector((-e0.x, -e0.y, hh - 0.03))
    mid_dir = Vector((-e0.y, e0.x, 0)).normalized()
    pts = [e0, e0 * 1.03 + Vector((0, 0, 0.016)), mid_dir * (r * 1.06) + Vector((0, 0, hh - 0.012)), e1 * 1.03 + Vector((0, 0, 0.016)), e1]
    bail = add_tube(f"Can bail {idx}", pts, 0.0012, mats["bolt"], "Rig", res=16)
    bail.location, bail.rotation_euler = ob.location, ob.rotation_euler
    for e, nm in ((e0, "a"), (e1, "b")):
        n = e.normalized()
        ear = add_cyl(f"Can ear {idx}{nm}", e, e + n * 0.003, 0.0065, mats["bolt"], "Rig", seg=12)
        ear.location, ear.rotation_euler = ob.location, ob.rotation_euler
    return ob


def build_shelf(rig, mats):
    """C のアームを留める軽量ラック（実プレート C）。"""
    s = rig["shelf"]
    x0, z0, y0 = s["min"]
    x1, z1, y1 = s["max"]
    steel = mats["shelf_steel"]
    for (x, y) in [(x0, y0), (x1, y0), (x0, y1), (x1, y1)]:
        # L アングルの柱。長穴は形として開ける（縁に光と影が乗る）
        _slotted_flange("Rack post a", Vector((x - 0.018, y - 0.018, 0)), Vector((1, 0, 0)), z1, steel)
        _slotted_flange("Rack post b", Vector((x - 0.018, y - 0.018, 0)), Vector((0, 1, 0)), z1, steel)
    # 棚板の高さはプレート A から読んだ値（下段・0.8m・1.17m・最上段）
    levels = [0.14, 0.8, 1.17, s["max"][1] - 0.02]
    for z in levels:
        add_box(f"Rack shelf {z}", (x0 - 0.012, y0 - 0.012, z - 0.012), (x1 + 0.012, y1 + 0.012, z), steel, "Rig", bevel=0.0015)
        add_box(f"Rack beam f {z}", (x0, y0 - 0.02, z - 0.045), (x1, y0 - 0.012, z), steel, "Rig", bevel=0.001)
    rnd = random.Random(11)
    # 段ボール（潰れて傾いた箱）・ペンキ缶・布・ケーブルの束（プレート C の棚の中身に寄せる）
    def box(name, cx, cy, z, w, d, hh):
        import bmesh as bm_

        me = bpy.data.meshes.new(name)
        bm = bm_.new()
        bm_.ops.create_cube(bm, size=1.0)
        bm_.ops.subdivide_edges(bm, edges=bm.edges[:], cuts=14, use_grid_fill=True)
        ph = [rnd.random() * 6.28 for _ in range(6)]
        for v in bm.verts:
            x, y, zz = v.co.x * w, v.co.y * d, (v.co.z + 0.5) * hh
            # 面のへこみと角の丸み
            dent = 0.008 * math.sin(x * 11 + ph[0]) * math.sin(zz * 13 + ph[1]) + 0.004 * math.sin(y * 23 + ph[2]) * math.sin(zz * 29 + ph[3])
            nx, ny = v.co.x * 2, v.co.y * 2
            x += dent * nx
            y += dent * ny
            if v.co.z > 0.49:
                zz -= 0.012 * (1 - nx * nx) * (1 - ny * ny) + 0.004 * math.sin(x * 19 + ph[4])
            v.co = Vector((x, y, zz))
        bm.to_mesh(me)
        bm.free()
        # 正面（-y）だけに印刷。box_print は範囲の外が「インク無し」
        uvl = me.uv_layers.new(name="UVMap")
        for poly in me.polygons:
            for li_ in poly.loop_indices:
                co = me.vertices[me.loops[li_].vertex_index].co
                if poly.normal.y < -0.7:
                    uvl.data[li_].uv = (co.x / (w * 0.84) + 0.5, (co.z - hh * 0.1) / (min(w * 0.84 * 2 / 3, hh * 0.78)))
                else:
                    uvl.data[li_].uv = (-1.0, -1.0)
        ob = bpy.data.objects.new(name, me)
        tc.collection("Rig").objects.link(ob)
        ob.location = (cx, cy, z)
        ob.rotation_euler[2] = math.radians(rnd.uniform(-7, 7))
        me.materials.append(mats["cardboard"])
        me.polygons.foreach_set("use_smooth", [True] * len(me.polygons))
        bev = ob.modifiers.new("b", "BEVEL")
        bev.width = 0.002
        bev.limit_method = "ANGLE"
        # 蓋の合わせ目と、ガムテープの切れ端
        seam = add_box(name + " seam", (-0.0016, -d / 2 + 0.002, hh - 0.0155), (0.0016, d / 2 - 0.002, hh - 0.0115), mats["rubber"], "Rig")
        seam.location = (cx, cy, z)
        seam.rotation_euler[2] = ob.rotation_euler[2]
        if rnd.random() < 0.7:
            l = d * rnd.uniform(0.45, 1.0)
            tw = 0.048
            tape = add_box(name + " tape", (-tw / 2, -l / 2, hh - 0.012), (tw / 2, l / 2, hh - 0.008), mats["tape"], "Rig")
            tape.location = (cx + rnd.uniform(-0.03, 0.03), cy, z)
            tape.rotation_euler[2] = ob.rotation_euler[2]
        return ob

    box("Rack box 1", x0 + 0.3, y0 + 0.19, 0.805, 0.4, 0.3, 0.24)
    box("Rack box 2", x0 + 0.86, y0 + 0.18, 1.175, 0.34, 0.27, 0.2)
    box("Rack box 3", x0 + 0.29, y0 + 0.2, 0.145, 0.44, 0.31, 0.3)
    # ペンキ缶（大きさと潰れ方を変える。ラベルと垂れ）
    cans = [(x0 + 0.84, y0 + 0.14, 0.8, 0.082, 0.19, 0.0, 0.14), (x0 + 1.03, y0 + 0.2, 0.8, 0.058, 0.12, 0.0, -0.06), (x0 + 0.34, y0 + 0.22, 1.17, 0.1, 0.235, 0.0, 0.3)]
    for i, (cx, cy, z, r, hh, tilt, rot) in enumerate(cans):
        _paint_can(i, cx, cy, z, r, hh, tilt, rot, mats)
    # 束ねたケーブル（棚の段から垂れる）
    for k in range(3):
        pts = [Vector((x0 + 0.5 + k * 0.05, y0 + 0.02, 0.8 - 0.004))]
        for m in range(5):
            pts.append(Vector((x0 + 0.5 + k * 0.05 + rnd.uniform(-0.04, 0.04), y0 - 0.03 - rnd.uniform(0, 0.03), 0.8 - 0.1 * (m + 1))))
        add_tube(f"Rack cable {k}", pts, 0.0028, mats["cable"], "Rig")
    # 棚のボルト
    for (x, y) in [(x0, y0), (x1, y0)]:
        for z in levels:
            for dx in (-0.012, 0.012):
                add_cyl("Rack bolt", (x + dx, y - 0.0195, z - 0.03), (x + dx, y - 0.024, z - 0.03), 0.0045, mats["bolt"], "Rig", seg=6)
    return levels


def build_arm(cam, mats, m: Matrix, phone_center: Vector, rig):
    """C: ラック上段の前梁へクランプし、2 関節のアームで L の上へ差し出す（図 1）。"""
    s = rig["shelf"]
    x0, z0, y0 = s["min"]
    pipe, mount = mats["pipe"], mats["mount"]
    clamp = Vector((phone_center.x + 0.02, y0 - 0.012, s["max"][1] - 0.02))
    add_box("Arm clamp", clamp + Vector((-0.024, -0.03, -0.05)), clamp + Vector((0.024, 0.03, 0.012)), mount, "Rig", bevel=0.003)
    add_cyl("Arm clamp screw", clamp + Vector((0, -0.012, -0.07)), clamp + Vector((0, -0.012, -0.045)), 0.005, mount, "Rig", seg=12)
    p0 = clamp + Vector((0, -0.01, 0.012))
    p1 = p0 + Vector((-0.02, -0.05, 0.16))
    back = phone_center + (m.to_3x3() @ Vector((0, 0, 0.03)))
    p2 = back + Vector((0.01, 0.03, -0.06))
    for a, b, r in [(p0, p1, 0.0105), (p1, p2, 0.0085), (p2, back, 0.0065)]:
        add_cyl("Arm seg", a, b, r, pipe, "Rig", seg=18)
    for q in (p0, p1, p2):
        add_cyl("Arm joint", q + Vector((-0.016, 0, 0)), q + Vector((0.016, 0, 0)), 0.015, mount, "Rig", seg=20)
    # 上腕のバネ
    add_cyl("Arm spring", p0 + Vector((0.016, -0.012, 0.03)), p1 + Vector((0.012, 0.012, -0.03)), 0.0022, mats["clip"], "Rig", seg=8)
    # USB ケーブルは棚の裏へ垂れる
    port = phone_center + (m.to_3x3() @ Vector((0.0772, 0, 0)))
    side = (m.to_3x3() @ Vector((1, 0, 0)))
    add_cyl("Arm plug", port, port + side * 0.022, 0.0046, mats["mount"], "Rig", seg=14)
    add_tube("Arm cable", [port + side * 0.02, port + side * 0.05 + Vector((0.0, 0.015, -0.05)), port + side * 0.06 + Vector((0.01, 0.07, -0.13)), p1 + Vector((0.06, 0.03, -0.02)), clamp + Vector((0.07, 0.02, -0.08)), clamp + Vector((0.1, 0.1, -0.28)), clamp + Vector((0.13, 0.24, -0.5))], 0.0019, mats["cable"], "Rig")
    # クランプの締めねじとゴムのあて
    add_cyl("Arm knob", clamp + Vector((0, -0.012, -0.095)), clamp + Vector((0, -0.012, -0.07)), 0.011, mats["mount"], "Rig", seg=10)


def build_room_parts(rig, mats):
    """実物の部屋にあって写真に写るもの。西の壁の奥の白い柱と、棚の南面に下がる黒い布（プレート B・C）。"""
    room = rig["room"]
    pil = room["pillar"]
    x0, y1 = tc.ROOM_X[0], tc.ROOM_Y[1]
    h = tc.ROOM_H
    faces = [
        ("Pillar south", Vector((x0, pil["zMin"], 0)), Vector((pil["xMax"] - x0, 0, 0))),
        ("Pillar east", Vector((pil["xMax"], pil["zMin"], 0)), Vector((0, y1 - pil["zMin"], 0))),
    ]
    for name, o, u in faces:
        ob = add_mesh(name, [o, o + u, o + u + Vector((0, 0, h)), o + Vector((0, 0, h))], [(0, 1, 2, 3)], mats["wall"], "Room")
        uv = ob.data.uv_layers.new(name="UVMap")
        for lo, co in zip(ob.data.loops, [(0, 0), (u.length / 4.8, 0), (u.length / 4.8, 1), (0, 1)]):
            uv.data[lo.index].uv = (co[0] + 0.37, co[1])
    c = room["cloth"]
    black = tm.plain("rack_cloth", (0.006, 0.006, 0.008), 0.92, 0.2)
    _curtain("Rack cloth", Vector((c["xMin"], c["z"], 0)), Vector((c["xMax"], c["z"], 0)), c["h"], 91, black)


def build_rig(rig, mats, screens=None):
    """A/B/C のスマホと機材を置き、{id: 行列（Blender カメラ姿勢）} を返す。"""
    views = {}
    for cam in rig["cameras"]:
        p = Vector(cam["pos"])
        pos = u2b(p.x, p.y, p.z)
        t = Vector(cam["lookAt"])
        target = u2b(t.x, t.y, t.z)
        m = tc.look_matrix(pos, target, cam.get("rollDeg", 0.0))
        views[cam["id"]] = m
        # スマホはカメラの向きに。レンズ（局所 -Z）がカメラ位置に来るよう本体を長辺方向へずらす
        center = pos + m.to_3x3() @ Vector((0.046, 0, 0.006))
        body = m.copy()
        body.translation = center
        scr = None
        if screens and cam["id"] in screens:
            scr = tm.phone_screen(screens[cam["id"]])
        _phone(f"Phone {cam['id']}", body, mats, scr)
        if cam["mount"] == "tripod":
            _clamp_holder("Tripod clamp", body, mats)
            build_tripod(cam, mats, body, center)
        elif cam["mount"] == "floor":
            build_cradle(cam, mats, body, center, Vector((tc.ROOM_X[0] + 0.1, -1.55, 0.002)))
        elif cam["mount"] == "arm":
            _clamp_holder("Arm clamp phone", body, mats)
            build_arm(cam, mats, body, center, rig)
    return views


def build_bulb(mats):
    b = tc.BULB
    h = tc.ROOM_H
    # 布巻きコード（わずかに曲がる）
    add_tube("Cord", [Vector((b.x + 0.012, b.y - 0.006, h)), Vector((b.x + 0.018, b.y - 0.004, h - 0.14)), Vector((b.x + 0.008, b.y + 0.006, (h + b.z) / 2)), Vector((b.x - 0.004, b.y + 0.002, b.z + 0.3)), Vector((b.x, b.y, b.z + 0.1))], 0.0032, mats["cord"], "Bulb")
    # 電線の継ぎ目の絶縁テープ
    add_cyl("Cord tape", (b.x - 0.0035, b.y + 0.002, b.z + 0.29), (b.x - 0.0043, b.y + 0.0026, b.z + 0.33), 0.0052, mats["rubber"], "Bulb", seg=14)
    add_cyl("Socket screw", (b.x + 0.021, b.y, b.z + 0.06), (b.x + 0.026, b.y, b.z + 0.06), 0.003, mats["brass"], "Bulb", seg=10)
    add_cyl("Socket ring", (b.x, b.y, b.z + 0.03), (b.x, b.y, b.z + 0.036), 0.0225, mats["brass"], "Bulb", seg=32)
    add_lathe("Ceiling rose", [(0, -0.018), (0.02, -0.018), (0.04, -0.01), (0.044, 0)], mats["porcelain"], "Bulb", center=(b.x, b.y, h))
    # ソケット
    add_lathe("Socket", [(0.0, 0.112), (0.008, 0.112), (0.014, 0.1), (0.021, 0.082), (0.021, 0.046), (0.018, 0.03), (0.0, 0.03)], mats["brass_old"], "Bulb", center=b)
    # 口金
    add_lathe("Screw", [(0, 0.03), (0.0135, 0.03), (0.0135, 0.018), (0.016, 0.012), (0, 0.012)], mats["brass_old"], "Bulb", center=b)
    # ガラス球（A60）: 見た目用の発光殻
    prof = []
    for i in range(30):
        t = i / 29
        z = 0.012 - t * 0.095
        # 洋梨形
        if t < 0.35:
            r = 0.012 + (t / 0.35) ** 1.3 * 0.012
        else:
            s = (t - 0.35) / 0.65
            r = 0.024 + 0.006 * s + math.sin(s * math.pi * 0.62) * 0.0 + 0.0
            r = 0.031 * math.sqrt(max(0.0, 1 - ((s - 0.38) / 0.62) ** 2)) if s > 0.38 else 0.024 + (s / 0.38) * 0.007
        prof.append((r, z))
    glass = add_lathe("Bulb glass", prof, mats["bulb_glass"], "Bulb", center=b)
    glass.visible_shadow = False
    glass.visible_diffuse = False
    glass.visible_glossy = True
    # 点光源（フィラメントの位置。カメラには直接映さない）
    li = bpy.data.lights.new("Bulb", "POINT")
    li.energy = 34.0
    li.color = (1.0, 0.72, 0.42)
    li.shadow_soft_size = 0.045
    ob = bpy.data.objects.new("Bulb light", li)
    tc.collection("Bulb").objects.link(ob)
    ob.location = b + Vector((0, 0, -0.05))
    ob.visible_camera = False
    return ob


def build_debris(mats):
    """床の細片。大半は数 mm の屑で、塗膜の片がときどき混じる。角ばった多角形を平らに置かない（紙細工に見える）。"""
    rnd = random.Random(7)
    x0, x1 = tc.ROOM_X
    y0, y1 = tc.ROOM_Y
    verts, faces = [], []
    n = 0

    def place():
        side = rnd.randrange(4)
        d = rnd.expovariate(2.6)
        s = rnd.random()
        if side == 0:
            return x0 + d, y0 + (y1 - y0) * s
        if side == 1:
            return x1 - d, y0 + (y1 - y0) * s
        if side == 2:
            return x0 + (x1 - x0) * s, y1 - d
        return x0 + (x1 - x0) * s, y0 + d

    # 塗膜の片（薄く反った不定形）
    for _ in range(180):
        x, y = place()
        if not (x0 < x < x1 and y0 < y < y1):
            continue
        size = 0.0015 + rnd.random() ** 6 * 0.006
        seg = 14
        ph = [rnd.random() * 6.28 for _ in range(4)]
        curl = rnd.uniform(-0.25, 0.45)
        a = rnd.random() * 6.28
        z0 = 0.0012 + rnd.random() * 0.002
        tx, ty = rnd.uniform(-0.3, 0.3), rnd.uniform(-0.3, 0.3)
        base = n
        verts.append((x, y, z0 + size * 0.08))
        n += 1
        for j in range(seg):
            t = 6.283 * j / seg
            r = size * (0.62 + 0.25 * math.sin(t * 2 + ph[0]) + 0.16 * math.sin(t * 5 + ph[1]) + 0.1 * math.sin(t * 9 + ph[2]))
            px, py = math.cos(t + a) * r, math.sin(t + a) * r
            pz = z0 + px * tx + py * ty + curl * (px * px + py * py) / max(size, 1e-4)
            verts.append((x + px, y + py, max(pz, 0.0006)))
            n += 1
        for j in range(seg):
            faces.append((base, base + 1 + j, base + 1 + (j + 1) % seg))
    add_mesh("Debris flakes", verts, faces, mats["debris"], "Room", smooth=True)
    # 細かい屑（4 面体の砂粒）
    verts, faces = [], []
    n = 0
    for _ in range(38000):
        x, y = place()
        if rnd.random() < 0.35:
            x, y = rnd.uniform(x0, x1), rnd.uniform(y0, y1)
        if not (x0 < x < x1 and y0 < y < y1):
            continue
        r = 0.0004 + rnd.random() ** 7 * 0.0032
        b = n
        k = rnd.randint(4, 6)
        apex = b + k
        a0 = rnd.random() * 6.28
        for q in range(k):
            ang = a0 + q / k * 6.28 + rnd.uniform(-0.4, 0.4)
            rr = r * rnd.uniform(0.45, 1.25)
            verts.append((x + math.cos(ang) * rr, y + math.sin(ang) * rr * rnd.uniform(0.6, 1.0), rnd.uniform(0.0, r * 0.2)))
        verts.append((x + rnd.uniform(-r, r) * 0.4, y + rnd.uniform(-r, r) * 0.4, r * rnd.uniform(0.25, 0.9)))
        n += k + 1
        for q in range(k):
            faces.append((b + q, b + (q + 1) % k, apex))
    add_mesh("Debris grit", verts, faces, mats["grit"], "Room", smooth=True)


def build_dust(count=1500):
    """空気中の埃。実物大（0.2〜0.9mm）の粒。"""
    rnd = random.Random(3)
    verts, faces = [], []
    x0, x1 = -2.0, 2.2
    y0, y1 = -2.2, 1.7
    n = 0
    for _ in range(count):
        x, y, z = rnd.uniform(x0, x1), rnd.uniform(y0, y1), rnd.uniform(0.1, 3.0)
        r = 0.00025 + rnd.random() ** 4 * 0.0009
        base = n
        for (dx, dy, dz) in [(r, 0, 0), (-r * 0.5, r * 0.86, 0), (-r * 0.5, -r * 0.86, 0), (0, 0, r)]:
            verts.append((x + dx, y + dy, z + dz))
            n += 1
        faces += [(base, base + 1, base + 3), (base + 1, base + 2, base + 3), (base + 2, base, base + 3), (base, base + 2, base + 1)]
    g = tm.G("dust")
    sh = g.principled(tm.rgb(0.65, 0.62, 0.55), 0.9, 0.2)
    ob = add_mesh("Dust", verts, faces, g.finish(sh), "Air")
    ob.visible_shadow = False
    return ob


def build_air(density):
    if density <= 0:
        return None
    x0, x1 = tc.ROOM_X
    y0, y1 = tc.ROOM_Y
    m = tm.volume_material(density)
    # 壁の外まで広げる。箱の面が部屋の中にあると、材質の AO が面に当たって壁が斑に暗くなる
    return add_box("Air", (x0 - 0.1, y0 - 0.1, -0.1), (x1 + 0.1, y1 + 0.1, tc.ROOM_H + 0.1), m, "Air")


def _wall_streaks():
    """-x の壁の金具の下に垂れる錆の筋。位置は build_conduit と同じ。"""
    py = -0.9
    out = [(py, z - 0.012, 0.03, 0.34 + (z % 0.37)) for z in [0.48, 1.06, 1.68, 2.3, 2.9]]
    out.append((py + 0.13, 1.18, 0.07, 0.52))
    return out


def make_mats():
    return {
        "wall": tm.wall_material(_wall_streaks()),
        "corner": tm.corner_material(),
        "floor": tm.floor_material(),
        "ceiling": tm.ceiling_material(),
        "curtain": tm.curtain_material(),
        "rust": tm.rust_metal(),
        "pipe": tm.painted_metal((0.014, 0.014, 0.015), 0.34, dust=0.5),
        "base": tm.painted_metal((0.022, 0.022, 0.023), 0.5),
        "mount": tm.worn_plastic(),
        "rubber": tm.plain("rubber", (0.008, 0.008, 0.008), 0.72, 0.3),
        "clip": tm.plain("clip", (0.55, 0.55, 0.54), 0.3, 0.5, 1.0),
        "brass": tm.plain("brass", (0.42, 0.3, 0.13), 0.32, 0.5, 1.0, bumpk=0.0003),
        "brass_old": tm.plain("brass_old", (0.17, 0.125, 0.06), 0.46, 0.5, 1.0, bumpk=0.0003),
        "bulb_glass": tm.glass_emission(),
        "cord": tm.plain("cord", (0.018, 0.016, 0.014), 0.9, 0.2, bumpk=0.0008, scale=900.0),
        "porcelain": tm.plain("porcelain", (0.3, 0.29, 0.25), 0.34),
        "panel": tm.rust_metal(2.2),
        "switch": tm.plain("switch", (0.3, 0.27, 0.2), 0.45),
        "door": tm.painted_metal((0.045, 0.085, 0.078), 0.6, chip=1.2),
        "baseboard": tm.painted_metal((0.02, 0.034, 0.03), 0.62, chip=1.3),
        "debris": tm.debris(),
        "grit": tm.plain("grit", (0.09, 0.087, 0.08), 0.92, 0.25),
        "paper": tm.plain("paper", (0.28, 0.25, 0.2), 0.85, 0.3),
        "phone_body": tm.plain("phone_body", (0.02, 0.021, 0.023), 0.42, 0.5),
        "bar": tm.plain("bar", (0.12, 0.12, 0.12), 0.34, 0.5, 1.0),
        "lens": tm.plain("lens", (0.005, 0.005, 0.006), 0.06, 0.7),
        "screen": tm.phone_screen(),
        "cable": tm.plain("cable", (0.02, 0.02, 0.021), 0.48, 0.45),
        "shelf_steel": tm.shelf_steel(),
        "can": tm.tin_can(),
        "cardboard": tm.cardboard(),
        "tape": tm.packing_tape(),
        "bolt": tm.plain("bolt", (0.32, 0.32, 0.3), 0.48, 0.5, 1.0, bumpk=0.0002),
        "label_0": tm.paper_label("can_label_a"),
        "label_1": tm.paper_label("can_label_b"),
        "label_2": tm.paper_label("can_label_c"),
        "black_cloth": tm.plain("black_cloth", (0.012, 0.012, 0.014), 0.86, 0.3, bumpk=0.0014, scale=240.0),
    }


def build_all(air_density=0.016, dust=1500, screens=None):
    show = tc.load_show()
    rig = tc.load_rig()
    mats = make_mats()
    build_shell(mats)
    build_conduit(mats)
    build_booth(show, mats)
    build_shelf(rig, mats)
    build_room_parts(rig, mats)
    views = build_rig(rig, mats, screens)
    build_bulb(mats)
    build_debris(mats)
    if dust:
        build_dust(dust)
    build_air(air_density)
    return {"show": show, "rig": rig, "mats": mats, "views": views}
