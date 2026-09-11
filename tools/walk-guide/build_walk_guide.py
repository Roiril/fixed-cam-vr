# -*- coding: utf-8 -*-
"""周回の歩き方を伝える動画を焼く（Blender ヘッドレス）。

    "C:\\Program Files\\Blender Foundation\\Blender 5.1\\blender.exe" --background ^
        --python tools\\walk-guide\\build_walk_guide.py
    …… 形だけ静止画で見るときは末尾に  -- --preview

骨組み（L 字の枠 + 手すり）は Joint.blend をそのまま取り込む。作り直さない。
床とカメラ 3 台は show.json から起こす。順路は手すりの外側 0.16m。

⚠ show.json の壁は 2026-08-05 〜 2026-09-11 のあいだ腕が 0.59m と 1.22m だったが、
  実物（Joint.blend）は左右とも 0.955m の等長。動画は実物に合わせてある。
  show.json の `room.walls` は 2026-09-11 に 0.955 / 0.955 へ直した（LEDGER 0183）。
  `layout.wall`（旧形式・位置合わせの既定点）は 1.0 / 1.0 のまま。

伝えるのは 3 つ:
  1 どちら回りか        — 道に矢印を敷き、人が同じ向きに 3 周する
  2 どれくらいゆっくりか — 0.25 m/s（1 周 およそ 22 秒・1 歩 1.35 秒）
  3 手すりを持つこと     — 右手を手すりの上に固定（IK）。3 周のあいだ離れない

⚠ 素材の歩きは 1.60 m/s の速い歩き。これをそのまま遅回しにすると
  「大股のスローモーション」になって、真似できる速さに見えない。
  歩幅を縮めてから（振りを平均へ寄せる）足が滑らない速さで歩かせる。
  縮めると足が浮くので、毎コマ低い方の足を床へ着け直す。
"""
import bpy
import bmesh
import io
import json
import math
import os
import sys

import mathutils
from mathutils import Vector, Euler, Quaternion

# ---------------------------------------------------------------- 設定 -----
REPO = r"C:\Users\kouga\Projects\Unity\fixed-cam-vr"
SHOW_JSON = os.path.join(REPO, "tools", "web-compositor", "show.json")
FBX = r"C:\Users\kouga\Downloads\Ch33_nonPBR@Walking.fbx"
OUT_DIR = os.path.join(REPO, "docs", "onsite")
CHECK_DIR = os.path.join(REPO, "logs", "walk-guide")   # 確認用の静止画（git 管理外）
OUT_NAME = "walk-guide"

JOINT = r"C:\Users\kouga\Projects\Blender\Joint.blend"   # 骨組みの正本

# ⚠ 骨組みの寸法は Joint.blend を実測した値。実物は左右とも 0.955m の等長。
#   show.json の壁は 2026-09-11 まで 0.59m / 1.22m と食い違っていた（いまは同じ値）。
WALL_ARM = 0.955          # L の腕 1 本の長さ [m]（門型の支柱の芯どうし）
WALL_H = 1.853            # 骨組みの上桟の高さ [m]
RAIL_H = 0.948            # 手すりの高さ [m]
D_RAIL = 0.08             # 壁の芯から手すりまで [m]（枠の奥行 0.16m の半分）
RAIL_EXT = 0.045          # 手すりが支柱より先へ出ている長さ [m]（U ベンドの芯まで）
D_WALK = 0.24             # 壁の芯から歩く道まで [m]（＝ 手すりから 0.16m）
PIPE_D = 0.028            # パイプ径 [m]
FLOOR_TOP = 0.012         # 床板の天端 [m]
JOINT_CORNER = (0.480, 0.0)    # Joint.blend の中の L の角
JOINT_FLOOR_Z = -0.886         # Joint.blend の中の足の裏
SPEED = 0.25              # 目標の速さ [m/s]（2026-08-14 に 0.5 の半分へ）
STEP_SEC = 1.35           # 1 歩にかける時間 [秒]
# ⚠ 速さを半分にするとき、歩数だけ半分にすると大股のスローモーションになる。
#   1 歩の時間を 1.05 → 1.35 秒に伸ばし、残りは歩幅を縮めて吸収する
CAPTIONS = False          # テロップ（2026-08-14 ユーザー指示で無し）
SHOW_GEAR = False         # 配信スマホと三脚（同上）
CAM_YAW = 28.0            # カメラを回す角 [度]（南東の斜め上から固定）
CURTAIN_HEM = 0.04        # 幕の裾を床から浮かせる高さ [m]
CURTAIN_T = 0.006         # 幕の厚み [m]
LAPS = 3
FPS = 30
RES = (1920, 1080)
# 暗めのオレンジ（2026-08-14 ユーザー指示）。図の正本 fig-room.svg の桃色から離れる
ACCENT = (0.40, 0.105, 0.012, 1.0)   # 線形。sRGB でおよそ #B05C18
ROUTE_W = 0.016           # 順路の線の太さ（半径 [m]）
ARROW_L = 0.125           # 矢印の長さ [m]

FONTS = [r"C:\Windows\Fonts\BIZ-UDGothicB.ttc",
         r"C:\Windows\Fonts\NotoSansJP-VF.ttf",
         r"C:\Windows\Fonts\msgothic.ttc"]

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
PREVIEW = "--preview" in argv          # 静止画だけ（速い。形の確認用）


def log(*a):
    print("[walk-guide]", *a)
    sys.stdout.flush()


def fcurves_of(obj):
    """Blender 5 のスロット付き action にも旧 action にも効く fcurve 一覧。"""
    ad = obj.animation_data
    if ad is None or ad.action is None:
        return []
    act = ad.action
    if hasattr(act, "fcurves"):
        return list(act.fcurves)
    out = []
    for layer in getattr(act, "layers", []):
        for strip in getattr(layer, "strips", []):
            for cb in getattr(strip, "channelbags", []):
                out.extend(cb.fcurves)
    return out


# ------------------------------------------------------------ 道の形 -------
# ⚠ 中心にそろえた正方形の輪では会場に合わない。壁の西の腕は南へ y=-0.72 まで
#   伸びていて、正方形だとその端に体がぶつかる（最初に組んだ形は 0.02m まで寄っていた）。
#   順路は「L 字の壁を距離 d でなぞり、2 つの自由端を半円で閉じた輪」にする。
#   手すりは同じ形の d 違いなので、手と手すりの間は角でも自動でそろう。
#   壁はつねに右手側にある。だから持つのは右手。
WALL_B = (-0.5, 0.5)                          # L の角（show.json の壁と同じ位置）
WALL_A = (WALL_B[0], WALL_B[1] - WALL_ARM)    # 西の腕の南端（自由端）
WALL_C = (WALL_B[0] + WALL_ARM, WALL_B[1])    # 北の腕の東端（自由端）


def make_path(d):
    """壁の芯から距離 d の閉じた輪。(区間の並び, 全長) を返す。

    区間は 7 本で、d が違っても並びは同じ。だから「何区間目の何割か」で
    歩く道と手すりを対応づけられる（角でも間隔がずれない）。
    """
    # 手すりは支柱より RAIL_EXT だけ先で折り返す。輪はその位置で閉じる
    ax, ay = WALL_A[0], WALL_A[1] - RAIL_EXT
    bx, by = WALL_B
    cx, cy = WALL_C[0] + RAIL_EXT, WALL_C[1]
    raw = [
        ("line", (ax - d, ay), (0.0, 1.0), by - ay),            # 壁の西面・北へ
        ("arc", (bx, by), math.pi, -0.5 * math.pi),             # L の外角を回る
        ("line", (bx, by + d), (1.0, 0.0), cx - bx),            # 壁の北面・東へ
        ("arc", (cx, cy), 0.5 * math.pi, -math.pi),             # 北の腕の端を回る
        ("line", (cx, cy - d), (-1.0, 0.0), cx - bx - d),       # 壁の南面・西へ
        ("line", (bx + d, by - d), (0.0, -1.0), by - d - ay),   # 壁の東面・南へ
        ("arc", (ax, ay), 0.0, -math.pi),                       # 西の腕の端を回る
    ]
    segs, total = [], 0.0
    for r in raw:
        if r[0] == "line":
            segs.append(("line", r[1], r[2], r[3], d))
            total += r[3]
        else:
            ln = d * abs(r[3])
            segs.append(("arc", r[1], r[2], r[3], d, ln))
            total += ln
    return segs, total


WALK_SEGS, LAP_LEN = make_path(D_WALK)
RAIL_SEGS, RAIL_LEN = make_path(D_RAIL)


def _eval(segs, i, u):
    """i 番目の区間の割合 u（0〜1）における (点, 進行方向)。"""
    s = segs[i]
    if s[0] == "line":
        (x0, y0), (dx, dy), ln = s[1], s[2], s[3]
        return Vector((x0 + dx * ln * u, y0 + dy * ln * u)), Vector((dx, dy))
    (cx, cy), a0, da, d = s[1], s[2], s[3], s[4]
    a = a0 + da * u
    return (Vector((cx + d * math.cos(a), cy + d * math.sin(a))),
            Vector((-math.sin(a), math.cos(a))) * (1.0 if da > 0 else -1.0))


def _locate(segs, total, s):
    s = s % total
    for i, seg in enumerate(segs):
        ln = seg[3] if seg[0] == "line" else seg[5]
        if s <= ln or i == len(segs) - 1:
            return i, (s / ln if ln > 1e-9 else 0.0)
        s -= ln
    return 0, 0.0


def walk_at(s):
    """歩く道の弧長 s [m] における (点, 進行方向)。どちらも xy 平面。"""
    i, u = _locate(WALK_SEGS, LAP_LEN, s)
    return _eval(WALK_SEGS, i, u)


def rail_at(s):
    """s に対応する手すりの点。区間と割合で対応づけるので角でもずれない。"""
    i, u = _locate(WALK_SEGS, LAP_LEN, s)
    return _eval(RAIL_SEGS, i, u)


def right_of(t):
    """進行方向 t の右手側（＝壁と手すりのある側）。"""
    return Vector((t.y, -t.x))



# -------------------------------------------------------------- 素材 -------
def mat(name, rgba, rough=0.55, metal=0.0, emit=None, alpha=None):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    b = m.node_tree.nodes["Principled BSDF"]
    b.inputs["Base Color"].default_value = rgba
    b.inputs["Roughness"].default_value = rough
    b.inputs["Metallic"].default_value = metal
    if emit is not None:
        b.inputs["Emission Color"].default_value = emit
        b.inputs["Emission Strength"].default_value = 1.0
    if alpha is not None:
        b.inputs["Alpha"].default_value = alpha
        # 透けた面の影は大きな染みになる。素材の側でも切っておく
        if hasattr(m, "shadow_method"):
            m.shadow_method = 'NONE'
        if hasattr(m, "use_transparent_shadow"):
            m.use_transparent_shadow = True
        for attr, val in (("blend_method", 'BLEND'),
                          ("surface_render_method", 'BLENDED'),
                          ("show_transparent_back", False)):
            if hasattr(m, attr):
                setattr(m, attr, val)
    return m


def put(obj, material):
    obj.data.materials.append(material)
    bpy.context.scene.collection.objects.link(obj)
    return obj


def new_mesh(name):
    return bpy.data.objects.new(name, bpy.data.meshes.new(name))


def box(name, size, loc, rot_z=0.0):
    o = new_mesh(name)
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    bmesh.ops.scale(bm, vec=Vector(size), verts=bm.verts)
    bm.to_mesh(o.data)
    bm.free()
    o.location = loc
    o.rotation_euler = Euler((0, 0, rot_z))
    return o


def cyl(name, r, h, loc):
    o = new_mesh(name)
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, cap_tris=False, segments=20,
                          radius1=r, radius2=r, depth=h)
    bm.to_mesh(o.data)
    bm.free()
    o.location = loc
    return o


def poly_curve(name, pts, depth, closed=True):
    cu = bpy.data.curves.new(name, 'CURVE')
    cu.dimensions = '3D'
    cu.bevel_depth = depth
    cu.bevel_resolution = 6
    cu.use_fill_caps = True
    sp = cu.splines.new('POLY')
    sp.points.add(len(pts) - 1)
    for i, p in enumerate(pts):
        sp.points[i].co = (p[0], p[1], p[2], 1.0)
    sp.use_cyclic_u = closed
    return bpy.data.objects.new(name, cu)


# ------------------------------------------------------------ 掃除 ---------
for o in list(bpy.data.objects):
    bpy.data.objects.remove(o, do_unlink=True)
for c in list(bpy.data.collections):
    bpy.data.collections.remove(c)

scene = bpy.context.scene
scene.render.fps = FPS
scene.unit_settings.system = 'METRIC'
scene.unit_settings.scale_length = 1.0

show = json.load(io.open(SHOW_JSON, encoding="utf-8"))
LAY = show["layout"]
FW, FD = LAY["floor"]["w"], LAY["floor"]["d"]
WALLS = LAY["room"]["walls"]
CAMS = [c["pose"] for c in show["cameras"] if c.get("pose")]

log("床 %.2f x %.2f m ／ L の腕 %.3f m ×2 ／ カメラ %d 台"
    % (FW, FD, WALL_ARM, len(CAMS)))
log("1 周 %.3f m ／ 手すり %.3f m" % (LAP_LEN, RAIL_LEN))


def _seg_dist(p, a, b):
    ab = Vector((b[0] - a[0], b[1] - a[1]))
    t = max(0.0, min(1.0, (p - Vector(a)).dot(ab) / max(ab.length_squared, 1e-9)))
    return (p - (Vector(a) + ab * t)).length


# 順路が壁と床にどれだけ余裕を持っているかを数で出す（目で見ても分からない）
_near, _out = 9.9, 0.0
for _i in range(600):
    _p, _ = walk_at(LAP_LEN * _i / 600)
    _near = min(_near, _seg_dist(_p, WALL_A, WALL_B), _seg_dist(_p, WALL_B, WALL_C))
    _out = max(_out, abs(_p.x) - FW / 2, abs(_p.y) - FD / 2)
log("順路と壁のいちばん近いところ %.3f m ／ 床からのはみ出し %.3f m" % (_near, _out))
if _near < 0.18:
    log("⚠ 壁に寄りすぎ。D_WALK を大きくする")

# 暗い部屋にポイントライト 1 灯。床だけライトグレーで、他は落とす
M_GROUND = mat("ground", (0.09, 0.09, 0.10, 1), rough=0.95)
M_FLOOR = mat("floor", (0.72, 0.71, 0.69, 1), rough=0.85)
M_WALL = mat("wall", (0.20, 0.21, 0.23, 1), rough=0.6, alpha=0.32)
M_EDGE = mat("edge", (0.12, 0.12, 0.13, 1), rough=0.5)
M_PIPE = mat("pipe", (0.030, 0.030, 0.034, 1), rough=0.34, metal=0.35)
M_FIT = mat("fitting", (0.035, 0.035, 0.038, 1), rough=0.62)   # 継手も黒（粗さで形を残す）
M_CURTAIN = mat("curtain", (0.035, 0.035, 0.038, 1), rough=0.94)  # 黒に近いグレーの幕
M_ROUTE = mat("route", ACCENT, rough=0.85,
              emit=(ACCENT[0] * .25, ACCENT[1] * .25, ACCENT[2] * .25, 1))
M_GEAR = mat("gear", (0.30, 0.30, 0.32, 1), rough=0.5)
M_TEXT = mat("text", (1, 1, 1, 1), emit=(1, 1, 1, 1))
M_PLATE = mat("plate", (0.04, 0.04, 0.05, 1), alpha=0.66)

# ------------------------------------------------------------ 会場 ---------
put(box("ground", (24, 24, 0.02), (0, 0, -0.011)), M_GROUND)
put(box("floor", (FW, FD, 0.012), (0, 0, 0.006)), M_FLOOR)
put(poly_curve("floor_edge",
               [(-FW / 2, -FD / 2, 0.013), (FW / 2, -FD / 2, 0.013),
                (FW / 2, FD / 2, 0.013), (-FW / 2, FD / 2, 0.013)], 0.006), M_EDGE)

# ⚠ 骨組みと手すりは自分で作り直さない。組む本人が引いた Joint.blend をそのまま持ってくる。
#   作り直すと、今回のように寸法が食い違ったまま気づかない
with bpy.data.libraries.load(JOINT, link=False) as (_src, _dst):
    _dst.objects = [nm for nm in _src.objects
                    if not nm.startswith(("Cube", "Cylinder", "Plane"))]   # 下絵は除く
FRAME = [o for o in _dst.objects if o is not None]
# スマホのマウントは出さない（2026-08-14 ユーザー指示）。材質名で拾う
_drop = [o for o in FRAME
         if any(sl.material is not None
                and any(k in sl.material.name.lower() for k in ("phone", "mount"))
                for sl in getattr(o, "material_slots", []))]
for o in _drop:
    FRAME.remove(o)
    bpy.data.objects.remove(o, do_unlink=True)
if _drop:
    log("スマホのマウントを外した: %d 個" % len(_drop))

# Joint.blend の L は「西と南へ伸びる」向き。会場は「東と南」なので Z 回りに 90 度回す
_rot = mathutils.Matrix.Rotation(math.pi / 2, 4, 'Z')
_rc = _rot @ Vector((JOINT_CORNER[0], JOINT_CORNER[1], 0.0))
XF = mathutils.Matrix.Translation(
    (WALL_B[0] - _rc.x, WALL_B[1] - _rc.y, FLOOR_TOP - JOINT_FLOOR_Z)) @ _rot
_lo = Vector((1e9, 1e9, 1e9))
_hi = Vector((-1e9, -1e9, -1e9))
for o in FRAME:
    scene.collection.objects.link(o)
    # ⚠ 取り込んだ直後の matrix_world はまだ計算されていない（単位行列が返る）。
    #   これに掛けると全部が 1 点へ重なる。親が無いので matrix_basis を使う
    o.matrix_basis = XF @ o.matrix_basis
    o.hide_render = False          # 元ファイルで隠してあることがある
    o.hide_viewport = False
    if o.type != 'MESH':
        continue
    for c in o.bound_box:
        w = o.matrix_basis @ Vector(c)
        for i in range(3):
            _lo[i] = min(_lo[i], w[i])
            _hi[i] = max(_hi[i], w[i])
log("骨組みを取り込んだ: %d 個 ／ x[%.2f,%.2f] y[%.2f,%.2f] z[%.2f,%.2f]"
    % (len(FRAME), _lo.x, _hi.x, _lo.y, _hi.y, _lo.z, _hi.z))

# ⚠ 元ファイルの青と白は「ビューポート表示色」で、シェーダは既定の白のまま。
#   そのまま焼くとパイプも継手も真っ白になる。表示色を Base Color へ写す
_done = set()
for o in FRAME:
    for sl in getattr(o, "material_slots", []):
        m = sl.material
        if m is None or m.name in _done:
            continue
        _done.add(m.name)
        pipe = "pipe" in m.name.lower()
        col = (0.030, 0.030, 0.034, 1) if pipe else (0.035, 0.035, 0.038, 1)
        if not m.use_nodes:
            m.use_nodes = True
        for nd in m.node_tree.nodes:
            if nd.type != 'BSDF_PRINCIPLED':
                continue
            nd.inputs["Base Color"].default_value = col
            nd.inputs["Roughness"].default_value = 0.34 if pipe else 0.62
            if "Metallic" in nd.inputs:
                nd.inputs["Metallic"].default_value = 0.35 if pipe else 0.0
log("骨組みの色を塗り直した（パイプも継手も黒）: %s" % ", ".join(sorted(_done)))

# 幕。手すりの内側（＝ 壁の芯）を通る高い方のパイプから下ろす。
# 芯を通る中桟のパイプは幕を貫通してよい（2026-08-14 ユーザー指示）。
for _nm, _p, _q in (("curtain_n", WALL_B, WALL_C), ("curtain_w", WALL_B, WALL_A)):
    _ln = math.hypot(_q[0] - _p[0], _q[1] - _p[1])
    _h = WALL_H - CURTAIN_HEM
    _c = put(box(_nm, (_ln, CURTAIN_T, _h),
                 ((_p[0] + _q[0]) / 2, (_p[1] + _q[1]) / 2,
                  FLOOR_TOP + CURTAIN_HEM + _h / 2),
                 rot_z=math.atan2(_q[1] - _p[1], _q[0] - _p[0])), M_CURTAIN)
log("幕: 上端 %.2fm → 裾 %.2fm ／ 長さ %.2fm ×2"
    % (WALL_H, CURTAIN_HEM, WALL_ARM))

N = 240

# ------------------------------------------------------ 順路の矢印 ---------
put(poly_curve("route", [(walk_at(LAP_LEN * i / N)[0].x,
                          walk_at(LAP_LEN * i / N)[0].y, FLOOR_TOP + 0.004)
                         for i in range(N)], ROUTE_W), M_ROUTE)

for i in range(16):
    p, t = walk_at(LAP_LEN * i / 16)
    r = right_of(t)
    a = new_mesh("arrow_%d" % i)
    bm = bmesh.new()
    tip = p + t * ARROW_L
    le = p - t * ARROW_L * 0.45 + r * ARROW_L * 0.62
    ri = p - t * ARROW_L * 0.45 - r * ARROW_L * 0.62
    _az = FLOOR_TOP + ROUTE_W + 0.006      # 線の上に乗せる（線に埋めると消える）
    bm.faces.new([bm.verts.new((tip.x, tip.y, _az)),
                  bm.verts.new((le.x, le.y, _az)),
                  bm.verts.new((ri.x, ri.y, _az))])
    bm.to_mesh(a.data)
    bm.free()
    put(a, M_ROUTE)

# ------------------------------------------------------ カメラ 3 台 -------
for i, c in enumerate(CAMS[:3] if SHOW_GEAR else []):
    x, y, h, yaw = c["x"], c["z"], c["y"], math.radians(c.get("yawDeg", 0))
    if h > 0.4:
        put(cyl("tripod_%d" % i, 0.011, h * 0.45, (x, y, h - h * 0.225)), M_GEAR)
        for k in range(3):
            a = k * 2 * math.pi / 3
            put(poly_curve("leg_%d_%d" % (i, k),
                           [(x, y, h * 0.55),
                            (x + 0.22 * math.cos(a), y + 0.22 * math.sin(a), 0.01)],
                           0.008, closed=False), M_GEAR)
    put(box("phone_%d" % i, (0.068, 0.010, 0.140), (x, y, h + 0.07), rot_z=yaw), M_GEAR)

# ------------------------------------------------------ 人 ----------------
before = set(bpy.data.objects)
bpy.ops.import_scene.fbx(filepath=FBX, automatic_bone_orientation=True)
PERSON = [o for o in bpy.data.objects if o not in before]
arm = next(o for o in PERSON if o.type == 'ARMATURE')
PFX = arm.data.bones[0].name.split(":")[0] + ":"
pb = arm.pose.bones
log("骨 %d 本（接頭辞 %s）" % (len(arm.data.bones), PFX))

act = arm.animation_data.action
F0, F1 = int(act.frame_range[0]), int(act.frame_range[1])
NSRC = F1 - F0                       # 1 循環ぶんの元コマ数（末尾は先頭と同じ）

# 前を向いている向きを実測する。
# ⚠ 肩の並びから外積で出すと符号を取り違える（実際に 180 度ずれ、体の反対側へ
#   腕が伸びたまま後ろ歩きしていた）。足首から爪先へのベクトルなら向きが一意に決まる。
FWD = Vector((0, 0, 0))
for i in range(NSRC):
    scene.frame_set(F0 + i)
    for side in ("Left", "Right"):
        v = (arm.matrix_world @ pb[PFX + side + "Toe_End"].head) - \
            (arm.matrix_world @ pb[PFX + side + "Foot"].head)
        v.z = 0
        FWD += v
FWD.normalize()
YAW0 = math.atan2(FWD.y, FWD.x)
# 肩から出した「右」と突き合わせて、右手側が本当に右手側か確かめる
scene.frame_set(F0)
_r = (arm.matrix_world @ pb[PFX + "RightArm"].head) - \
     (arm.matrix_world @ pb[PFX + "LeftArm"].head)
_r.z = 0
_chk = _r.normalized().dot(FWD.cross(Vector((0, 0, 1))))
log("素の向き yaw = %.1f 度 ／ 右手側の一致 %.2f（1 に近ければ正）" % (
    math.degrees(YAW0), _chk))
if _chk < 0.8:
    log("⚠ 前と右が合っていない。腕が体の反対側へ伸びる")

BONES = [b.name for b in arm.data.bones]
HIPS = PFX + "Hips"
FEET = [PFX + n for n in ("LeftToe_End", "RightToe_End", "LeftFoot", "RightFoot")]

for b in pb:
    b.rotation_mode = 'QUATERNION'

# --- 元の 1 循環を採る -----------------------------------------------------
samp = {n: [] for n in BONES}
hips_loc = []
for i in range(NSRC):
    scene.frame_set(F0 + i)
    for n in BONES:
        samp[n].append(pb[n].rotation_quaternion.copy())
    hips_loc.append(pb[HIPS].location.copy())


def mean_quat(qs):
    acc = Quaternion((0, 0, 0, 0))
    ref = qs[0]
    for q in qs:
        s = -1.0 if q.dot(ref) < 0 else 1.0
        for i in range(4):
            acc[i] += q[i] * s
    acc.normalize()
    return acc


MEAN = {n: mean_quat(samp[n]) for n in BONES}
MEAN_LOC = Vector((sum(v.x for v in hips_loc) / NSRC,
                   sum(v.y for v in hips_loc) / NSRC,
                   sum(v.z for v in hips_loc) / NSRC))


def apply_pose(i, k):
    """i 番目の元コマを「振り k 倍」にして当てる。"""
    for n in BONES:
        pb[n].rotation_quaternion = MEAN[n].slerp(samp[n][i], k)
    pb[HIPS].location = MEAN_LOC.lerp(hips_loc[i], k)
    bpy.context.view_layer.update()


def stride_of(k):
    """振り k 倍のときの歩幅 [m]（腰から見た足の前後の振れ幅）。"""
    best = 0.0
    for bone in (PFX + "LeftFoot", PFX + "RightFoot"):
        vals = []
        for i in range(NSRC):
            apply_pose(i, k)
            d = (arm.matrix_world @ pb[bone].head) - (arm.matrix_world @ pb[HIPS].head)
            vals.append(d.x * FWD.x + d.y * FWD.y)
        best = max(best, max(vals) - min(vals))
    return best


S_RAW = stride_of(1.0)
log("素の歩幅 %.3f m ⇒ 素の速さ %.2f m/s" % (S_RAW, S_RAW / (NSRC / FPS / 2)))

# 焼く長さを決める。3 周がちょうど整数循環になるように歩数を丸める
CYC_FRAMES = int(round(STEP_SEC * 2 * FPS))                 # 1 循環 = 2 歩
n_cyc = max(1, int(round(LAPS * LAP_LEN / SPEED * FPS / CYC_FRAMES)))
TOTAL = n_cyc * CYC_FRAMES
SPEED_EFF = LAPS * LAP_LEN / (TOTAL / FPS)
STEP_EFF = CYC_FRAMES / 2 / FPS
STRIDE_WANT = SPEED_EFF * STEP_EFF

# 歩幅がその値になる k を詰める（歩幅は k に比例しないので反復で合わせる）
k = 1.0
for _ in range(6):
    s = stride_of(k)
    if abs(s - STRIDE_WANT) < 0.002:
        break
    k = max(0.05, min(1.0, k * (STRIDE_WANT / s) ** 0.85))
S_NEW = stride_of(k)
log("振り %.3f 倍 ⇒ 歩幅 %.3f m（狙い %.3f）" % (k, S_NEW, STRIDE_WANT))
log("%d コマ = %.1f 秒 ／ %.4f m/s ／ 1 歩 %.2f 秒 ／ 1 周 %.1f 秒"
    % (TOTAL, TOTAL / FPS, SPEED_EFF, STEP_EFF, TOTAL / FPS / LAPS))

# --- 1 循環ぶんを焼き直す（足が床に着くように高さも測る）------------------
arm.animation_data.action = None
Z_FIX = []
for j in range(CYC_FRAMES):
    apply_pose(int(round(j / CYC_FRAMES * NSRC)) % NSRC, k)
    low = min((arm.matrix_world @ pb[n].head).z for n in FEET)
    Z_FIX.append(-low)
    for n in BONES:
        pb[n].keyframe_insert("rotation_quaternion", frame=j + 1)
    pb[HIPS].keyframe_insert("location", frame=j + 1)

for fc in fcurves_of(arm):
    for kp in fc.keyframe_points:
        kp.interpolation = 'LINEAR'
    m = fc.modifiers.new('CYCLES')
    m.mode_before = 'REPEAT'
    m.mode_after = 'REPEAT'
log("足の着き直し %.3f 〜 %.3f m" % (min(Z_FIX), max(Z_FIX)))

# ------------------------------------------------------ 右手を手すりへ ----
tgt = bpy.data.objects.new("rail_grip", None)
tgt.empty_display_type = 'SPHERE'
tgt.empty_display_size = 0.03
scene.collection.objects.link(tgt)

ik = pb[PFX + "RightForeArm"].constraints.new('IK')
ik.target = tgt
ik.chain_count = 2
ik.use_tail = True

# ------------------------------------------------------ 歩かせる ----------
scene.frame_start = 1
scene.frame_end = TOTAL
GRIP_Z = RAIL_H + PIPE_D / 2 + 0.038      # 手すりの上に手のひらが乗る高さ（手首の芯）

# ⚠ 取り込んだ armature は X 90 度で立っている。その姿勢を捨てて yaw を書くと人が寝る。
#   世界の Z 回りの回転を「左から」掛けて、元の姿勢を保ったまま向きだけ変える。
BASE_Q = (arm.rotation_quaternion.copy() if arm.rotation_mode == 'QUATERNION'
          else arm.rotation_euler.to_quaternion())
arm.rotation_mode = 'QUATERNION'

# 出だしは「カメラの方へ歩いてくる」区間から始める（壁の東面＝ 6 区間目の頭）。
# 背中から始めると、いちばん見せたい「手すりに手を置いている」が見えない。
START_S = sum((s[3] if s[0] == "line" else s[5]) for s in WALK_SEGS[:5])

heading = None
for fr in range(1, TOTAL + 2):
    s = START_S + (fr - 1) / FPS * SPEED_EFF
    p, t = walk_at(s)
    a = math.atan2(t.y, t.x)
    if heading is None:                      # atan2 は ±π で折り返す。繋いで数える
        heading = a
    else:
        heading += (a - heading + math.pi) % (2 * math.pi) - math.pi
    arm.location = (p.x, p.y, Z_FIX[(fr - 1) % CYC_FRAMES] + FLOOR_TOP)
    arm.rotation_quaternion = Quaternion((0, 0, 1), heading - YAW0) @ BASE_Q
    arm.keyframe_insert("location", frame=fr)
    arm.keyframe_insert("rotation_quaternion", frame=fr)
    q, _ = rail_at(s)
    tgt.location = (q.x, q.y, GRIP_Z)
    tgt.keyframe_insert("location", frame=fr)

for o in (arm, tgt):
    for fc in fcurves_of(o):
        for kp in fc.keyframe_points:
            kp.interpolation = 'LINEAR'

# --- 手のひらを手すりの上へ寝かせる ---------------------------------------
# IK は手首の位置しか決めない。向きは歩きの振りのままなので、放っておくと
# 手すりの横で手が回り続ける。指を進行方向へ、手のひらを下へ固定する。
# 「どの軸が手のひらか」は骨のロールで変わるので、指の付け根から実測する。
scene.frame_set(1)
bpy.context.view_layer.update()
dg = bpy.context.evaluated_depsgraph_get()
ae = arm.evaluated_get(dg)
hm = ae.matrix_world @ ae.pose.bones[PFX + "RightHand"].matrix
wrist = hm.translation


def bone_head(name):
    return ae.matrix_world @ ae.pose.bones[PFX + name].head


# 右手では (人差し指 - 手首) × (小指 - 手首) が手のひらの側を向く（右手系の性質）。
# ⚠ 親指との内積で符号を決めるのは駄目。親指は横に張り出していて内積がほぼ 0 になり、
#   コマによって符号が反転する。
nrm = (bone_head("RightHandIndex1") - wrist).cross(bone_head("RightHandPinky1") - wrist)
nrm.normalize()
log("手のひらの向き（世界）= (%.2f, %.2f, %.2f)" % (nrm.x, nrm.y, nrm.z))

basis = hm.to_3x3().normalized()
y_loc = mathutils.Vector((0, 1, 0))              # 骨の長さ方向 ＝ 指の向き
n_loc = basis.transposed() @ nrm
n_loc -= y_loc * n_loc.dot(y_loc)
n_loc.normalize()


def frame_of(y, n):
    m = mathutils.Matrix.Identity(3)
    m.col[0], m.col[1], m.col[2] = y, n, y.cross(n)
    return m


# ⚠ 基準は「1 コマ目に体が向いている向き」。s=0 で取ると出だしの位置ずらし
#   （START_S）のぶんだけ食い違い、指が進む向きの真後ろを向く。
_, t1 = walk_at(START_S)
A = frame_of(y_loc, n_loc)
B = frame_of(mathutils.Vector((t1.x, t1.y, 0)).normalized(),
             mathutils.Vector((0, 0, -1)))       # 指は進む方へ・手のひらは下へ
R = B @ A.transposed()

hand_aim = bpy.data.objects.new("hand_aim", None)
hand_aim.empty_display_type = 'ARROWS'
hand_aim.empty_display_size = 0.12
scene.collection.objects.link(hand_aim)
hand_aim.parent = arm                            # 体と一緒に回るので 1 度決めれば足りる
hand_aim.rotation_mode = 'QUATERNION'
hand_aim.rotation_quaternion = (
    arm.matrix_world.to_3x3().normalized().inverted() @ R).to_quaternion()

cr = pb[PFX + "RightHand"].constraints.new('COPY_ROTATION')
cr.target = hand_aim
cr.target_space = 'WORLD'
cr.owner_space = 'WORLD'

# できあがりを測って確かめる（向きは目で見ても分かりにくい）
scene.frame_set(int(TOTAL * 0.4))
bpy.context.view_layer.update()
_ae = arm.evaluated_get(bpy.context.evaluated_depsgraph_get())
_hm = _ae.matrix_world @ _ae.pose.bones[PFX + "RightHand"].matrix
_fing = (_hm.to_3x3().normalized() @ mathutils.Vector((0, 1, 0)))
_palm = (_hm.to_3x3().normalized() @ n_loc)
_w = _ae.matrix_world @ _ae.pose.bones[PFX + "RightHand"].head
_s = START_S + (int(TOTAL * 0.4) - 1) / FPS * SPEED_EFF
_q, _t = rail_at(_s)
log("手 手のひらの下向き %.2f（1 が真下）／ 指と進む向きの一致 %.2f"
    % (-_palm.z, _fing.x * _t.x + _fing.y * _t.y))
log("手首 (%.3f,%.3f,%.3f) ／ 狙い (%.3f,%.3f,%.3f) ／ 離れ %.3f m" % (
    _w.x, _w.y, _w.z, _q.x, _q.y, GRIP_Z,
    (_w - mathutils.Vector((_q.x, _q.y, GRIP_Z))).length))
log("IK 影響 %.2f ／ 鎖 %d ／ 的 %s" % (ik.influence, ik.chain_count,
                                      ik.target.name if ik.target else "なし"))
log("的の実位置 (%.3f,%.3f,%.3f)" % tuple(tgt.matrix_world.translation))

# ------------------------------------------------------ ライト ------------
# 取り込んだ服は艶が強く、暗い部屋で光を 1 灯当てるとビニールに見える。艶を消す。
# ⚠ マテリアル名で絞らない。この人形の服は "Material" という名前で、
#   "Ch" で始まる名前だけ直していたときは上着だけ光ったままだった。
#   使っているオブジェクトから引く。
_skin = set()
for _o in PERSON:
    for _sl in getattr(_o, "material_slots", []):
        if _sl.material is not None:
            _skin.add(_sl.material.name)
MATTE = {"Roughness": 0.92, "Specular IOR Level": 0.0, "Specular": 0.0,
         "Coat Weight": 0.0, "Sheen Weight": 0.0, "Metallic": 0.0,
         "Transmission Weight": 0.0}
_kinds = set()
for _nm in sorted(_skin):
    m = bpy.data.materials[_nm]
    if not m.use_nodes:
        continue
    for n in list(m.node_tree.nodes):
        _kinds.add(n.type)
        if n.type != 'BSDF_PRINCIPLED':
            continue
        for key, val in MATTE.items():
            if key not in n.inputs:
                continue
            # ⚠ 値を入れるだけでは足りない。テクスチャが繋がっていると素通しされる。
            #   艶を確実に消すには繋がっている線を外す
            for lk in list(n.inputs[key].links):
                m.node_tree.links.remove(lk)
            n.inputs[key].default_value = val
log("人のマテリアルをマットに: %s ／ ノード %s"
    % (", ".join(sorted(_skin)), ",".join(sorted(_kinds))))

# 天井にぶら下がった裸電球 1 個ぶん。太陽も補助光も置かない
lamp = bpy.data.objects.new("lamp", bpy.data.lights.new("lamp", 'POINT'))
# ⚠ 骨組みの真上に置かない。幕の上端まで 0.45m しかなく、そこだけ白く飛ぶ。
#   斜め上から流し込むと幕は上から下へ落ちる自然な階調になる
lamp.data.energy = 260          # W
lamp.data.shadow_soft_size = 0.12
lamp.data.color = (1.0, 0.96, 0.90)
lamp.location = (1.15, -1.05, 2.70)
scene.collection.objects.link(lamp)

world = bpy.data.worlds.new("world")
world.use_nodes = True
world.node_tree.nodes["Background"].inputs[0].default_value = (0.16, 0.17, 0.20, 1)
world.node_tree.nodes["Background"].inputs[1].default_value = 0.025
scene.world = world

# ------------------------------------------------------ カメラ ------------
cam = bpy.data.objects.new("view", bpy.data.cameras.new("view"))
cam.data.lens = 35
scene.collection.objects.link(cam)
scene.camera = cam

pivot = bpy.data.objects.new("pivot", None)
scene.collection.objects.link(pivot)
# 水平から 55 度見下ろす。この角だと 1.75m の人が画の高さの 4 割ほどに収まり、
# 床 1.8m 角と順路が同時に読める（真横だと順路が潰れ、真上だと人が読めない）
SWING = math.radians(CAM_YAW)     # 固定。この角で収まるかを当たり判定に使う

# ⚠ 距離を手で決めると、人が奥の辺に来たコマだけ頭が切れる（実際に切れた）。
#   遠い所・高い所を全部並べて、どのコマでも収まる最短の距離を探す。
_pts = []
for _i in range(48):
    _p, _ = walk_at(LAP_LEN * _i / 48)
    _pts += [Vector((_p.x, _p.y, 0.0)), Vector((_p.x, _p.y, 1.80))]
for _sx in (-1, 1):
    for _sy in (-1, 1):
        _pts.append(Vector((_sx * FW / 2, _sy * FD / 2, 0.0)))
for _c3 in (WALL_A, WALL_B, WALL_C):           # 骨組みの上端
    _pts.append(Vector((_c3[0], _c3[1], WALL_H + FLOOR_TOP)))
for _c in (CAMS[:3] if SHOW_GEAR else []):    # 三脚も画に入る。切れると目立つ
    _pts.append(Vector((_c["x"], _c["z"], _c["y"] + 0.15)))
    _pts.append(Vector((_c["x"], _c["z"], 0.0)))

TGT = Vector((sum(p.x for p in _pts) / len(_pts),
              sum(p.y for p in _pts) / len(_pts), 0.50))
_tan_w = 0.5 * cam.data.sensor_width / cam.data.lens
_tan_h = _tan_w * RES[1] / RES[0]


def _fits(dist, elev, swing, margin=1.02):
    pos = TGT + (mathutils.Matrix.Rotation(swing, 3, 'Z')
                 @ Vector((0, -math.cos(elev), math.sin(elev))) * dist)
    fwd = (TGT - pos).normalized()
    rgt = fwd.cross(Vector((0, 0, 1))).normalized()
    up = rgt.cross(fwd)
    for p in _pts:
        v = p - pos
        z = v.dot(fwd)
        if z <= 0.1:
            return False
        if abs(v.dot(rgt)) / z > _tan_w / margin:
            return False
        if abs(v.dot(up)) / z > _tan_h / margin:
            return False
    return True


# 見下ろす角も探す。角によって画に必要な広さが変わり、いちばん寄れる角がある
DIST, ELEV = 9.0, math.radians(52)
for _e_deg in range(50, 62, 2):
    _e = math.radians(_e_deg)
    _d = 2.0
    while _d < 9.0:
        if _fits(_d, _e, SWING):
            break
        _d += 0.05
    if _d < DIST:
        DIST, ELEV = _d, _e
log("カメラ 距離 %.2f m ／ 見下ろし %.0f 度 ／ 見る先 (%.2f, %.2f)"
    % (DIST, math.degrees(ELEV), TGT.x, TGT.y))

cam.parent = pivot
cam.rotation_euler = Euler((math.pi / 2 - ELEV, 0, 0))

# 固定。動かさない（2026-08-14 ユーザー指示）
pivot.location = TGT
pivot.rotation_euler = Euler((0, 0, SWING))
cam.location = (0, -math.cos(ELEV) * DIST, math.sin(ELEV) * DIST)
# ------------------------------------------------------ 文字 --------------
font = None
for path in (FONTS if CAPTIONS else []):
    if os.path.exists(path):
        try:
            font = bpy.data.fonts.load(path)
            log("書体:", os.path.basename(path))
            break
        except Exception as e:                                  # noqa: BLE001
            log("書体を開けない:", path, e)
if font is None:
    log("テロップ: 出さない" if not CAPTIONS else "⚠ 日本語の書体が無い — 文字は出さない")


def caption(text, f_in, f_out, y=-0.175, size=0.030, plate_w=None):
    if font is None or not CAPTIONS:
        return
    cu = bpy.data.curves.new("cap", 'FONT')
    cu.font = font
    cu.body = text
    cu.size = size
    cu.align_x = 'CENTER'
    cu.align_y = 'CENTER'
    o = bpy.data.objects.new("cap", cu)
    put(o, M_TEXT)
    o.parent = cam
    o.location = (0, y, -1.0)

    # ⚠ 帯の幅は文字数から見積もらない（全角と半角で 2 倍ずれ、端が白地に溶ける）。
    #   組んだ文字の実寸を測って囲む
    bpy.context.view_layer.update()
    dg = bpy.context.evaluated_depsgraph_get()
    text_w = o.evaluated_get(dg).dimensions.x

    # ⚠ 帯はカメラの面と平行に置く。立ててしまうと横倒しの線にしか映らない
    pw = plate_w if plate_w is not None else (text_w + size * 1.5)
    pl = box("plate", (pw, size * 2.2, 0.0002), (0, 0, 0))
    put(pl, M_PLATE)
    pl.parent = cam
    pl.location = (0, y, -1.004)
    pl.rotation_euler = Euler((0, 0, 0))

    for ob in (o, pl):
        for fr, hide in ((1, True), (f_in, False), (f_out, False), (f_out + 1, True)):
            ob.hide_render = hide
            ob.hide_viewport = hide
            ob.keyframe_insert("hide_render", frame=fr)
            ob.keyframe_insert("hide_viewport", frame=fr)
        for fc in fcurves_of(ob):
            for kp in fc.keyframe_points:
                kp.interpolation = 'CONSTANT'


lap = TOTAL / LAPS
caption("手すりに手をそえたまま歩きます", 10, int(lap * 0.78))
caption("同じ向きに 3 周まわります", int(lap * 0.98), int(lap * 1.70))
caption("1 周 およそ %d 秒。ふだんの半分くらいの速さ" % round(TOTAL / FPS / LAPS),
        int(lap * 1.90), int(lap * 2.60))
for i in range(LAPS):
    caption("%d 周目" % (i + 1), int(lap * i) + 1, int(lap * (i + 1)),
            y=0.235, size=0.026)

# ------------------------------------------------------ 焼く --------------
engines = [e.identifier for e in
           bpy.types.RenderSettings.bl_rna.properties['engine'].enum_items]
for want in ('BLENDER_EEVEE_NEXT', 'BLENDER_EEVEE', 'BLENDER_WORKBENCH'):
    if want in engines:
        scene.render.engine = want
        break
log("エンジン:", scene.render.engine)

ee = getattr(scene, "eevee", None)
if ee is not None:
    for attr, val in (("taa_render_samples", 64), ("use_shadows", True),
                      ("use_raytracing", False), ("use_gtao", True)):
        if hasattr(ee, attr):
            setattr(ee, attr, val)

scene.render.resolution_x, scene.render.resolution_y = RES
scene.render.resolution_percentage = 100
scene.render.film_transparent = False
scene.view_settings.view_transform = 'Standard'
scene.view_settings.exposure = 0.0        # 明るさはライト 1 灯だけで作る
os.makedirs(OUT_DIR, exist_ok=True)
os.makedirs(CHECK_DIR, exist_ok=True)

imset = scene.render.image_settings
if PREVIEW:
    # ⚠ Blender 5 は media_type を先に決める。旧来の file_format = 'FFMPEG' は
    #   静止画の一覧に無く TypeError で落ちる
    if hasattr(imset, "media_type"):
        imset.media_type = 'IMAGE'
    imset.file_format = 'PNG'
    for fr in (1, int(TOTAL * 0.13), int(TOTAL * 0.31), int(TOTAL * 0.56), int(TOTAL * 0.81)):
        scene.frame_set(max(1, fr))
        scene.render.filepath = os.path.join(CHECK_DIR, "check-%04d.png" % fr)
        bpy.ops.render.render(write_still=True)
        log("静止画:", scene.render.filepath)
else:
    if hasattr(imset, "media_type"):
        imset.media_type = 'VIDEO'          # これで file_format が FFMPEG になる
    else:
        imset.file_format = 'FFMPEG'
    scene.render.ffmpeg.format = 'MPEG4'
    scene.render.ffmpeg.codec = 'H264'
    scene.render.ffmpeg.constant_rate_factor = 'HIGH'
    scene.render.ffmpeg.ffmpeg_preset = 'GOOD'
    scene.render.ffmpeg.gopsize = 15
    scene.render.filepath = os.path.join(OUT_DIR, OUT_NAME)
    bpy.ops.render.render(animation=True)
    # ⚠ 動画のときは Blender がコマ番号を足した名前で書く（walk-guide0001-0882.mp4）。
    #   毎回名前が変わると貼り先のリンクが切れるので、決まった名前へ置き直す
    made = os.path.join(OUT_DIR, "%s%04d-%04d.mp4" % (OUT_NAME, 1, TOTAL))
    dst = os.path.join(OUT_DIR, OUT_NAME + ".mp4")
    if os.path.exists(made):
        if os.path.exists(dst):
            os.remove(dst)
        os.rename(made, dst)
    log("動画:", dst, "%.1f MB" % (os.path.getsize(dst) / 1e6))

log("おわり")
