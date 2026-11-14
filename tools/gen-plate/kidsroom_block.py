"""2 周目 B の飛び先（過去の人形からの視点の子供部屋。LEDGER 0355 / 0359）の構図を決める下絵を Blender で焼く。

    blender.exe --background --python tools/gen-plate/kidsroom_block.py -- --out <フォルダ> [--shots front,corner,lap]

部屋と家具を箱で組み、人形の目の高さ（畳に座った高さ）から撮る。出力は 640x480 の PNG（本編の B の無人写真と同じ
ピンホール。魚眼は本編の側が掛ける）。これを画像生成の入力にして実写風にする（bake-kidsroom.py --block）。
寸法は m。+y が襖のある奥、+x が灰色のカーテンのある右。
"""
import math
import os
import random
import sys

import bpy
from mathutils import Vector

W, D, H = 2.73, 3.64, 2.30          # 六畳
FOCAL_PX, RES = 288.33, (640, 480)   # tools/twin/rig.json の B と同じ
EYE_Z = 0.27                         # 畳に座った人形の目の高さ


def mat(name, rgb, rough=0.9):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    b = next(n for n in m.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
    b.inputs["Base Color"].default_value = (*rgb, 1)
    b.inputs["Roughness"].default_value = rough
    return m


def box(name, lo, hi, m):
    lo, hi = Vector(lo), Vector(hi)
    bpy.ops.mesh.primitive_cube_add(size=1, location=(lo + hi) / 2)
    o = bpy.context.object
    o.name = name
    o.scale = hi - lo
    o.data.materials.append(m)
    return o


def curtain(x, y0, y1, z0, z1, m, seed=3):
    """襞のある幕。x の面に沿って y 方向へ張る。"""
    rnd = random.Random(seed)
    nu, nv = 220, 24
    ph = [rnd.random() * 6.28 for _ in range(4)]
    verts, faces = [], []
    for j in range(nv + 1):
        v = j / nv
        for i in range(nu + 1):
            u = i / nu
            y = y0 + (y1 - y0) * u
            fold = 0.035 * math.sin(u * 64 + ph[0]) + 0.018 * math.sin(u * 23 + ph[1]) + 0.01 * math.sin(u * 141 + ph[2])
            verts.append((x - 0.05 - fold * (0.6 + 0.4 * (1 - v)), y, z0 + (z1 - z0) * v))
    for j in range(nv):
        for i in range(nu):
            a = j * (nu + 1) + i
            faces.append((a, a + 1, a + nu + 2, a + nu + 1))
    me = bpy.data.meshes.new("curtain")
    me.from_pydata(verts, [], faces)
    o = bpy.data.objects.new("Curtain", me)
    bpy.context.collection.objects.link(o)
    for p in me.polygons:
        p.use_smooth = True
    me.materials.append(m)
    return o


def part(name, center, size, m, rot=(0, 0, 0), parent=None):
    """中心と寸法で置く箱（回転つき）。parent があればその局所座標。"""
    bpy.ops.mesh.primitive_cube_add(size=1, location=center)
    o = bpy.context.object
    o.name, o.scale, o.rotation_euler = name, size, rot
    o.data.materials.append(m)
    if parent:
        o.parent = parent
    return o


def ball(name, center, r, m, scale=(1, 1, 1), parent=None):
    bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=2, radius=r, location=center)
    o = bpy.context.object
    o.name, o.scale = name, scale
    o.data.materials.append(m)
    if parent:
        o.parent = parent
    return o


def group(name, loc, rot_z=0.0, tilt=(0.0, 0.0)):
    e = bpy.data.objects.new(name, None)
    bpy.context.collection.objects.link(e)
    e.location, e.rotation_euler = (0, 0, 0), (0, 0, 0)
    return e, (loc, (tilt[0], tilt[1], rot_z))


def place(g):
    e, (loc, rot) = g
    e.location, e.rotation_euler = loc, rot


def doll(loc, s, rot_z, ms, seated=True, tilt=(0.0, 0.0), seed=0):
    """ローポリの人形。着物の胴（円錐）・胡粉の顔・黒髪・座りなら前へ出た脚。原点は尻の下。"""
    kimono, skin, hair = ms[0][seed % len(ms[0])], ms[1], ms[2]
    g = group("doll", loc, rot_z, tilt)
    e = g[0]
    bpy.ops.mesh.primitive_cone_add(vertices=8, radius1=0.075 * s, radius2=0.04 * s, depth=0.2 * s, location=(0, 0, 0.1 * s))
    body = bpy.context.object
    body.data.materials.append(kimono)
    body.parent = e
    ball("head", (0, 0, 0.245 * s), 0.048 * s, skin, parent=e)
    ball("hair", (-0.012 * s, 0, 0.26 * s), 0.053 * s, hair, scale=(0.9, 1.05, 1.0), parent=e)
    part("sleeveL", (0.02 * s, 0.075 * s, 0.13 * s), (0.05 * s, 0.035 * s, 0.1 * s), kimono, parent=e)
    part("sleeveR", (0.02 * s, -0.075 * s, 0.13 * s), (0.05 * s, 0.035 * s, 0.1 * s), kimono, parent=e)
    if seated:
        part("legs", (0.07 * s, 0, 0.02 * s), (0.12 * s, 0.1 * s, 0.04 * s), kimono, parent=e)
    place(g)
    return e


def rocking_horse(loc, rot_z, m, mane):
    """古い木馬。弧の脚（揺り板）2 本・斜めの脚 4 本・胴・首・頭・たてがみ・尾。前は +x。"""
    g = group("horse", loc, rot_z)
    e = g[0]
    R, n = 0.95, 9
    for side in (-0.14, 0.14):
        for i in range(n):
            t = -0.42 + 0.84 * (i + 0.5) / n
            part("rocker", (R * math.sin(t), side, 0.02 + R * (1 - math.cos(t))), (0.105, 0.025, 0.03), m, rot=(0, -t, 0), parent=e)
    for lx, ly in ((0.2, 0.12), (0.2, -0.12), (-0.2, 0.12), (-0.2, -0.12)):
        part("leg", (lx * 1.2, ly, 0.2), (0.035, 0.035, 0.32), m, rot=(-ly * 1.2, lx * 0.9, 0), parent=e)
    part("body", (0, 0, 0.38), (0.5, 0.15, 0.1), m, parent=e)
    part("neck", (0.27, 0, 0.5), (0.08, 0.07, 0.26), m, rot=(0, 0.45, 0), parent=e)
    part("head", (0.36, 0, 0.62), (0.2, 0.07, 0.09), m, rot=(0, 0.25, 0), parent=e)
    part("mane", (0.23, 0, 0.6), (0.06, 0.03, 0.2), mane, rot=(0, 0.45, 0), parent=e)
    part("tail", (-0.28, 0, 0.36), (0.05, 0.03, 0.18), mane, rot=(0, -0.6, 0), parent=e)
    part("handle", (0.33, 0, 0.6), (0.02, 0.24, 0.02), m, parent=e)
    place(g)
    return e


def populate(horse=(1.55, 1.75, 2.4), rnd_seed=5):
    """0362 / 0364: 人形の多い部屋と、空いた床の古い木馬。人形は左の壁に掛け、奥の欄間の棚に並べ、机と床に座らせる。"""
    rnd = random.Random(rnd_seed)
    kimonos = [mat(f"kimono{i}", c) for i, c in enumerate(((0.3, 0.08, 0.07), (0.2, 0.18, 0.15), (0.12, 0.13, 0.18), (0.32, 0.26, 0.2)))]
    ms = (kimonos, mat("gofun", (0.62, 0.6, 0.55)), mat("hair", (0.02, 0.02, 0.02)))
    k = 0
    # 左の壁に掛かった人形（壁から 6cm。少し傾ける）
    for y, z in ((0.9, 1.55), (1.35, 1.8), (1.7, 1.35), (2.1, 1.75), (2.55, 1.45), (3.0, 1.85), (3.3, 1.3), (1.1, 1.1)):
        doll((0.07, y, z), rnd.uniform(0.85, 1.25), rnd.uniform(-0.3, 0.3), ms, seated=False,
             tilt=(rnd.uniform(-0.2, 0.2), rnd.uniform(-0.15, 0.1)), seed=k); k += 1
    # 奥の壁、襖の上の棚（欄間の前）に並ぶ人形
    box("shelf", (0.35, 3.44, 1.86), (2.15, 3.64, 1.89), mat("shelfwood", (0.1, 0.065, 0.035), 0.7))
    for x in (0.55, 0.85, 1.2, 1.6, 1.95):
        doll((x, 3.55, 1.89), rnd.uniform(0.8, 1.0), -math.pi / 2 + rnd.uniform(-0.4, 0.4), ms, seed=k); k += 1
    # 机の上と椅子と床に座る人形
    for loc, rz in (((0.25, 2.25, 0.45), -0.2), ((0.3, 2.65, 0.45), 0.4), ((0.8, 2.48, 0.285), -2.6),
                    ((0.25, 1.75, 0.0), 0.3), ((0.55, 3.25, 0.0), -0.8), ((0.2, 3.0, 0.0), 0.1)):
        doll(loc, rnd.uniform(1.0, 1.3), rz, ms, seed=k); k += 1
    # 倒れた人形 1 体（床の中ほど）
    doll((1.0, 2.75, 0.04), 1.1, 1.2, ms, seated=False, tilt=(math.pi / 2 - 0.05, 0), seed=k)
    rocking_horse((horse[0], horse[1], 0.0), horse[2], mat("horsepaint", (0.5, 0.48, 0.43), 0.8), mat("mane", (0.35, 0.32, 0.27)))


def build(lap, sparse=False, dolls=False, horse=(1.55, 1.75, 2.4)):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    sc = bpy.context.scene
    tatami = mat("tatami", (0.36, 0.31, 0.17))
    heri = mat("heri", (0.03, 0.035, 0.05))
    wall = mat("wall", (0.33, 0.29, 0.21))
    wood = mat("wood", (0.12, 0.075, 0.04), 0.7)
    paper = mat("paper", (0.5, 0.45, 0.33))
    dark = mat("dark", (0.0, 0.0, 0.0), 1.0)
    cloth = mat("cloth", (0.2, 0.2, 0.21))
    ceil = mat("ceil", (0.07, 0.045, 0.03))

    box("floor", (0, 0, -0.05), (W, D, 0), tatami)
    for y in (D / 4, D / 2, D * 3 / 4):      # 畳の縁
        box("heri", (0, y - 0.015, 0), (W, y + 0.015, 0.002), heri)
    box("heri", (W / 2 - 0.015, 0, 0), (W / 2 + 0.015, D / 4, 0.002), heri)
    box("heri", (W / 2 - 0.015, D * 3 / 4, 0), (W / 2 + 0.015, D, 0.002), heri)
    box("wallL", (-0.05, 0, 0), (0, D, H), wall)
    box("wallR", (W, 0, 0), (W + 0.05, D, H), wall)
    box("wallBack", (0, -0.05, 0), (W, 0, H), wall)
    box("ceil", (0, 0, H), (W, D, H + 0.05), ceil)
    for y in [0.3 + 0.45 * i for i in range(8)]:   # 竿縁
        box("sao", (0, y - 0.015, H - 0.03), (W, y + 0.015, H), wood)

    # 奥の壁。襖の開口は幅 1.8m・高さ 1.76m。右の 1 枚が 16cm 開いて、奥は黒い箱
    fx0, fx1, fh = 0.35, 2.15, 1.76
    box("farL", (0, D, 0), (fx0, D + 0.05, H), wall)
    box("farR", (fx1, D, 0), (W, D + 0.05, H), wall)
    box("farTop", (fx0, D, fh), (fx1, D + 0.05, H), wall)
    box("kamoi", (fx0 - 0.05, D - 0.03, fh), (fx1 + 0.05, D + 0.05, fh + 0.07), wood)
    box("shikii", (fx0, D - 0.03, 0), (fx1, D + 0.05, 0.015), wood)
    box("hashiraL", (fx0 - 0.09, D - 0.03, 0), (fx0, D + 0.05, H), wood)
    box("hashiraR", (fx1, D - 0.03, 0), (fx1 + 0.09, D + 0.05, H), wood)
    mid = (fx0 + fx1) / 2
    gap = 0.16
    box("fusumaL", (fx0, D, 0.015), (mid + 0.02, D + 0.02, fh), paper)
    box("fusumaR", (mid + 0.02 + gap, D + 0.025, 0.015), (fx1 + 0.18, D + 0.045, fh), paper)
    for x in (mid - 0.08, mid + gap + 0.1):     # 引手
        bpy.ops.mesh.primitive_cylinder_add(radius=0.03, depth=0.01, location=(x, D - 0.005, 0.8), rotation=(math.pi / 2, 0, 0))
        bpy.context.object.data.materials.append(heri)
    box("oshiire", (fx0, D + 0.05, 0), (fx1, D + 0.9, fh), dark)

    # 右の壁いっぱいの灰色の幕。床まで届く
    box("rail", (W - 0.06, 0.2, 2.0), (W - 0.03, D - 0.2, 2.03), wood)
    curtain(W, 0.25, D - 0.25, 0.0, 2.0, cloth)

    # 左の壁際に低い机と椅子
    dx, dy = 0.04, 2.05
    box("deskTop", (dx, dy, 0.42), (dx + 0.5, dy + 0.85, 0.45), wood)
    for ax, ay in ((dx + 0.02, dy + 0.02), (dx + 0.44, dy + 0.02), (dx + 0.02, dy + 0.79), (dx + 0.44, dy + 0.79)):
        box("leg", (ax, ay, 0), (ax + 0.04, ay + 0.04, 0.42), wood)
    box("drawer", (dx + 0.04, dy + 0.45, 0.3), (dx + 0.48, dy + 0.8, 0.42), wood)
    cx, cy = dx + 0.62, dy + 0.3
    box("seat", (cx, cy, 0.26), (cx + 0.3, cy + 0.3, 0.285), wood)
    for ax, ay in ((cx, cy), (cx + 0.27, cy), (cx, cy + 0.27), (cx + 0.27, cy + 0.27)):
        box("cleg", (ax, ay, 0), (ax + 0.03, ay + 0.03, 0.26), wood)
    box("backL", (cx + 0.27, cy, 0.26), (cx + 0.3, cy + 0.03, 0.58), wood)
    box("backR", (cx + 0.27, cy + 0.27, 0.26), (cx + 0.3, cy + 0.3, 0.58), wood)
    box("backT", (cx + 0.27, cy, 0.5), (cx + 0.3, cy + 0.3, 0.58), wood)

    # 電灯（点いていない）
    box("cord", (W / 2 - 0.004, 1.9 - 0.004, 1.75), (W / 2 + 0.004, 1.9 + 0.004, H), heri)
    bpy.ops.mesh.primitive_cone_add(radius1=0.2, radius2=0.05, depth=0.1, location=(W / 2, 1.9, 1.72))
    bpy.context.object.data.materials.append(mat("shade", (0.45, 0.43, 0.38)))

    # 子供のもの（数えられるだけ）。sparse（0361）はお手玉 1 つと積み木 1 つだけにし、剥がれた壁紙と破片を床に散らす
    red = mat("red", (0.35, 0.05, 0.04))
    toys = ((1.75, 1.75),) if sparse else ((1.05, 1.35), (1.17, 1.42), (1.75, 1.75))
    for i, (x, y) in enumerate(toys):
        bpy.ops.mesh.primitive_uv_sphere_add(radius=0.045, location=(x, y, 0.028))
        bpy.context.object.scale.z = 0.62
        bpy.context.object.data.materials.append(red if i != 1 else paper)
    box("zori", (1.42, 3.2, 0), (1.5, 3.36, 0.02), red)
    blk = mat("block", (0.42, 0.33, 0.2))
    for x, y in (((2.3, 2.2),) if sparse else ((2.3, 2.2), (2.42, 2.34), (2.22, 2.5), (2.4, 2.66))):
        box("block", (x, y, 0), (x + 0.06, y + 0.06, 0.06), blk)
    if sparse:
        rnd = random.Random(11)
        scrap = mat("scrap", (0.4, 0.37, 0.3))
        for _ in range(14):   # 剥がれた壁紙の切れ端（平たい板を少し傾ける）
            x, y = rnd.uniform(0.15, 2.3), rnd.uniform(0.8, 3.3)
            w, l = rnd.uniform(0.05, 0.2), rnd.uniform(0.04, 0.14)
            box("scrap", (x, y, 0), (x + w, y + l, 0.004), scrap)
            bpy.context.object.rotation_euler = (rnd.uniform(-0.15, 0.15), rnd.uniform(-0.15, 0.15), rnd.uniform(0, 3.14))
        for _ in range(10):   # 砂壁の破片（小さな塊。左の壁際に寄せる）
            x, y = rnd.uniform(0.05, 0.6), rnd.uniform(0.8, 3.2)
            r = rnd.uniform(0.008, 0.025)
            box("chip", (x, y, 0), (x + r * 1.6, y + r, r), wall)

    if dolls:
        populate(horse)

    if lap:   # 画の下の縁に、座った人形の膝と手
        kimono = mat("kimono", (0.3, 0.03, 0.03))
        skin = mat("gofun", (0.75, 0.72, 0.66))
        box("lap", (W / 2 - 0.13, 0.56, 0), (W / 2 + 0.13, 0.84, 0.09), kimono)
        for sx in (-0.05, 0.05):
            bpy.ops.mesh.primitive_uv_sphere_add(radius=0.022, location=(W / 2 + sx, 0.78, 0.105))
            bpy.context.object.scale = (0.8, 1.3, 0.5)
            bpy.context.object.data.materials.append(skin)

    # 光は差し込まない。天井あたりの弱い面光だけ（下絵として形が読める明るさ）
    bpy.ops.object.light_add(type="AREA", location=(W / 2, 1.6, H - 0.1))
    li = bpy.context.object
    li.data.energy, li.data.size = 22, 2.2
    world = bpy.data.worlds.new("w")
    world.use_nodes = True
    world.node_tree.nodes["Background"].inputs[0].default_value = (0.02, 0.02, 0.022, 1)
    sc.world = world

    sc.render.engine = "CYCLES"
    sc.cycles.samples = 96
    sc.cycles.use_denoising = True
    sc.render.resolution_x, sc.render.resolution_y = RES
    sc.render.image_settings.media_type = "IMAGE"
    sc.render.image_settings.file_format = "PNG"
    sc.view_settings.view_transform = "Standard"
    return sc


SHOTS = {
    # 部屋の手前寄りに座り、襖をほぼ正面に見る
    "front": dict(pos=(W / 2 - 0.1, 0.5, EYE_Z), target=(W / 2 + 0.25, D, 0.62), lap=False),
    # 左の壁際から。右の幕が大きく入り、襖は右寄りの奥
    "corner": dict(pos=(0.55, 0.45, EYE_Z), target=(W / 2 + 0.45, D, 0.6), lap=False),
    # 正面と同じ。下の縁に自分の膝と手が入る
    "lap": dict(pos=(W / 2, 0.5, EYE_Z + 0.03), target=(W / 2 + 0.2, D, 0.2), lap=True),
    # 0361: 正面と同じ構図。人形は映さない・おもちゃは少なく・床に壁紙の切れ端と破片
    "dark": dict(pos=(W / 2 - 0.1, 0.5, EYE_Z), target=(W / 2 + 0.25, D, 0.62), lap=False, sparse=True),
    # 0364: 手前左の隅の床から、対角に襖と幕を見る。人形と木馬を置く（人形は必ず入れる）
    "nook": dict(pos=(0.22, 0.22, EYE_Z), target=(1.75, D, 0.55), lap=False, sparse=True, dolls=True),
    # 0364: 手前右の隅（幕の端）から。左の壁の人形が正面寄りに並ぶ
    "nookR": dict(pos=(W - 0.3, 0.22, EYE_Z), target=(0.95, D, 0.55), lap=False, sparse=True, dolls=True,
                  horse=(1.0, 1.05, 3.56)),
}


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    # 相対パスは Blender が // 扱いで別の所へ書くので、起動したフォルダからの絶対パスに直す
    out = os.path.abspath(argv[argv.index("--out") + 1]).replace("\\", "/")
    shots = (argv[argv.index("--shots") + 1] if "--shots" in argv else "front,corner,lap").split(",")
    for name in shots:
        s = SHOTS[name]
        sc = build(s["lap"], s.get("sparse", False), s.get("dolls", False), s.get("horse", (1.55, 1.75, 2.4)))
        cd = bpy.data.cameras.new("cam")
        cd.sensor_width = 36.0
        cd.lens = 36.0 * FOCAL_PX / RES[0]
        cd.clip_start = 0.02
        cam = bpy.data.objects.new("cam", cd)
        sc.collection.objects.link(cam)
        cam.location = s["pos"]
        cam.rotation_euler = (Vector(s["target"]) - Vector(s["pos"])).to_track_quat("-Z", "Y").to_euler()
        sc.camera = cam
        sc.render.filepath = f"{out}/block_{name}.png"
        bpy.ops.render.render(write_still=True)
        print(f"[block] {name} -> {sc.render.filepath}")


if __name__ == "__main__":
    main()
