"""2 周目 B の飛び先（過去の人形からの視点の子供部屋。LEDGER 0355 / 0359）の構図を決める下絵を Blender で焼く。

    blender.exe --background --python tools/gen-plate/kidsroom_block.py -- --out <フォルダ> [--shots front,corner,lap]

部屋と家具を箱で組み、人形の目の高さ（畳に座った高さ）から撮る。出力は 640x480 の PNG（本編の B の無人写真と同じ
ピンホール。魚眼は本編の側が掛ける）。これを画像生成の入力にして実写風にする（bake-kidsroom.py --block）。
寸法は m。+y が襖のある奥、+x が灰色のカーテンのある右。
"""
import math
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


def build(lap):
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

    # 子供のもの（数えられるだけ）
    red = mat("red", (0.35, 0.05, 0.04))
    for i, (x, y) in enumerate(((1.05, 1.35), (1.17, 1.42), (1.75, 1.75))):
        bpy.ops.mesh.primitive_uv_sphere_add(radius=0.045, location=(x, y, 0.028))
        bpy.context.object.scale.z = 0.62
        bpy.context.object.data.materials.append(red if i != 1 else paper)
    box("zori", (1.42, 3.2, 0), (1.5, 3.36, 0.02), red)
    blk = mat("block", (0.42, 0.33, 0.2))
    for x, y in ((2.3, 2.2), (2.42, 2.34), (2.22, 2.5), (2.4, 2.66)):
        box("block", (x, y, 0), (x + 0.06, y + 0.06, 0.06), blk)

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
}


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    out = argv[argv.index("--out") + 1]
    shots = (argv[argv.index("--shots") + 1] if "--shots" in argv else "front,corner,lap").split(",")
    for name in shots:
        s = SHOTS[name]
        sc = build(s["lap"])
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
