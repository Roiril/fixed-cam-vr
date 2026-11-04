"""デジタルツインを Cycles で撮る。

blender --background --python tools/twin/twin_render.py -- still --tag x --shots hero,wide,A --w 1920 --spp 512
EXR は Logs/twin-cycles/ に置き、仕上げ（twin_post.py）を別プロセスで掛けて Drive へ書く。
"""
from __future__ import annotations

import argparse
import csv
import json
import math
import subprocess
import sys
import time
from datetime import datetime
from pathlib import Path

import bpy
from mathutils import Vector

sys.path.insert(0, str(Path(__file__).resolve().parent))
import twin_common as tc  # noqa: E402
import twin_scene as ts  # noqa: E402

try:
    import twin_person as tp  # noqa: E402
except ImportError:
    tp = None


def setup_cycles(w, h, spp, volume=True):
    s = bpy.context.scene
    s.render.engine = "CYCLES"
    prefs = bpy.context.preferences.addons["cycles"].preferences
    prefs.compute_device_type = "OPTIX"
    prefs.refresh_devices()
    used = []
    for d in prefs.devices:
        d.use = d.type == "OPTIX"
        if d.use:
            used.append(d.name)
    s.cycles.device = "GPU"
    print("[twin] OptiX:", used)
    c = s.cycles
    c.samples = spp
    c.use_adaptive_sampling = True
    c.adaptive_threshold = 0.012
    c.use_denoising = True
    c.denoiser = "OPTIX"
    c.denoising_input_passes = "RGB_ALBEDO_NORMAL"
    c.max_bounces = 10
    c.diffuse_bounces = 6
    c.glossy_bounces = 4
    c.transmission_bounces = 6
    c.volume_bounces = 1
    c.sample_clamp_indirect = 8.0
    c.sample_clamp_direct = 0.0
    c.caustics_reflective = False
    c.caustics_refractive = False
    if hasattr(c, "volume_step_rate"):
        c.volume_step_rate = 2.0
    s.render.resolution_x = w
    s.render.resolution_y = h
    s.render.resolution_percentage = 100
    s.render.film_transparent = False
    s.render.image_settings.media_type = "IMAGE"
    s.render.image_settings.file_format = "OPEN_EXR"
    s.render.image_settings.color_mode = "RGB"
    s.render.image_settings.color_depth = "16"
    s.render.image_settings.exr_codec = "ZIP"
    s.view_settings.view_transform = "Raw"
    s.view_settings.look = "None"
    w_ = bpy.data.worlds.new("black")
    w_.use_nodes = True
    w_.node_tree.nodes["Background"].inputs[1].default_value = 0.0
    s.world = w_


def make_camera(name="Cam"):
    d = bpy.data.cameras.new(name)
    o = bpy.data.objects.new(name, d)
    bpy.context.scene.collection.objects.link(o)
    bpy.context.scene.camera = o
    return o


def aim(cam, pos, target, lens=None, hfov=None, sensor=36.0, fstop=None, focus=None, roll=0.0, matrix=None):
    d = cam.data
    d.sensor_width = sensor
    if hfov is not None:
        d.lens = 0.5 * sensor / math.tan(math.radians(hfov) / 2)
    elif lens is not None:
        d.lens = lens
    d.clip_start = 0.003  # B のレンズは壁から 2.5cm。手前を切ると壁に穴が開く
    d.clip_end = 40
    cam.matrix_world = matrix if matrix is not None else tc.look_matrix(Vector(pos), Vector(target), roll)
    d.dof.use_dof = fstop is not None
    if fstop is not None:
        d.dof.aperture_fstop = fstop
        d.dof.focus_distance = focus if focus else (Vector(target) - Vector(pos)).length
        d.dof.aperture_blades = 7


def route_mid_times(route="forward"):
    """担当カメラごとに、いちばん長く担当している区間の真ん中の時刻。"""
    rows = list(csv.DictReader((Path(__file__).resolve().parent / "routes" / f"{route}.tsv").open(encoding="utf-8"), delimiter="\t"))
    best = {}
    start = 0
    for i in range(1, len(rows) + 1):
        if i == len(rows) or rows[i]["active"] != rows[start]["active"]:
            cam = "ABC"[int(float(rows[start]["active"]))]
            t0, t1 = float(rows[start]["t"]), float(rows[i - 1]["t"])
            if cam not in best or t1 - t0 > best[cam][1] - best[cam][0]:
                best[cam] = (t0, t1)
            start = i
    return {c: (a + b) / 2 for c, (a, b) in best.items()}


def shots(ctx, person_at=None):
    """見た目の監査用のアングル。人の位置は Blender の xy。"""
    b = tc.BULB
    views = ctx["views"]
    head = Vector((person_at[0], person_at[1], 1.55)) if person_at else Vector((0.1, -0.1, 1.55))
    out = {
        # 正面から L の角を見る（図 1 の構図）
        "hero": dict(pos=(2.05, -2.45, 1.32), target=(-0.3, 0.3, 1.5), lens=28, fstop=2.8, focus=3.4),
        "wide": dict(pos=(3.2, -3.3, 1.75), target=(-0.2, 0.15, 0.95), lens=26, fstop=5.6, focus=4.8),
        "low": dict(pos=(1.6, -1.4, 0.16), target=(-0.5, 0.55, 1.25), lens=24, fstop=2.0, focus=2.3),
        "close": dict(pos=(head.x + 0.95, head.y - 1.0, 1.42), target=(head.x - 0.05, head.y + 0.02, 1.32), lens=50, fstop=1.8, focus=1.38),
        "rail": dict(pos=(-0.05, -0.28, 1.17), target=(-0.36, 0.4, 0.96), lens=35, fstop=2.0, focus=0.55),
        "bulb": dict(pos=(0.65, -0.5, 1.65), target=(b.x, b.y, b.z + 0.08), lens=45, fstop=2.0, focus=1.45),
        "wall": dict(pos=(-0.66, 0.2, 1.28), target=(-1.06, 0.75, 1.05), lens=28, fstop=4.0, focus=0.7),
        "floor": dict(pos=(-0.8, 0.8, 0.42), target=(-1.0, 1.2, 0.02), lens=35, fstop=2.8, focus=0.6),
    }
    mid = route_mid_times()
    for cid, m in views.items():
        cam = next(c for c in ctx["rig"]["cameras"] if c["id"] == cid)
        out[cid] = dict(matrix=m, hfov=cam["hfovDeg"], sensor=36.0, look="phone", aspect=4 / 3, t=mid[cid])
    # 機材の肩越し（図 1 の配置が読める画）
    a, bb, c = [Vector(tc.u2b(*x["pos"])) for x in ctx["rig"]["cameras"]]
    out["rigA"] = dict(pos=(a.x + 0.55, a.y - 0.75, 1.05), target=(a.x - 0.25, a.y + 0.3, 0.75), lens=35, fstop=2.2, focus=1.05)
    out["rigB"] = dict(pos=(bb.x + 0.62, bb.y - 0.25, 0.32), target=(bb.x, bb.y, 0.08), lens=35, fstop=2.0, focus=0.7, t=22.0)
    out["rigC"] = dict(pos=(c.x + 0.2, c.y - 0.95, 1.92), target=(c.x + 0.05, c.y + 0.3, 1.98), lens=30, fstop=2.2, focus=1.1)
    return out


def save_npy(exr: Path):
    """OpenCV の EXR 読みは環境で塞がれているので、Blender で読んで numpy にする（線形 RGB・上が 0 行目）。"""
    import numpy as np

    img = bpy.data.images.load(str(exr))
    img.colorspace_settings.name = "Non-Color"
    w, h = img.size
    px = np.empty(w * h * 4, dtype=np.float32)
    img.pixels.foreach_get(px)
    a = px.reshape(h, w, 4)[::-1, :, :3].astype(np.float16)
    np.save(str(exr.with_suffix(".npy")), a)
    bpy.data.images.remove(img)


def run_still(a):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    t0 = time.time()
    screens = {}
    feed_dir = Path(a.feeds) if a.feeds else tc.WORK / "feeds"
    if feed_dir:
        for cid in "ABC":
            p = feed_dir / f"{cid}.jpg"
            if p.exists():
                screens[cid] = p
    ctx = ts.build_all(air_density=a.air, dust=a.dust, screens=screens)
    person = None
    at = None
    if a.person and tp is not None:
        person = tp.Person(ctx)
        person.pose_at(a.time, a.route)
        at = person.location_xy()
    setup_cycles(a.w, a.w * 9 // 16, a.spp)
    cam = make_camera()
    table = shots(ctx, at)
    out_dir = tc.WORK / "exr" / a.stamp
    out_dir.mkdir(parents=True, exist_ok=True)
    names = [s for s in a.shots.split(",") if s]
    manifest = {}
    print(f"[twin] 組み立て {time.time() - t0:.1f}s")
    for name in names:
        sh = dict(table[name])
        look = sh.pop("look", "photo")
        shot_t = sh.pop("t", a.time)
        if person is not None:
            person.pose_at(shot_t)
        aspect = sh.pop("aspect", 16 / 9)
        bpy.context.scene.render.resolution_y = int(a.w / aspect)
        aim(cam, sh.pop("pos", (0, 0, 0)), sh.pop("target", (0, 0, 0)), **sh)
        bpy.context.scene.cycles.seed = 7
        p = out_dir / f"{name}.exr"
        bpy.context.scene.render.filepath = str(p)
        t1 = time.time()
        bpy.ops.render.render(write_still=True)
        print(f"[twin] {name} {time.time() - t1:.1f}s")
        save_npy(p)
        manifest[name] = look
    (out_dir / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
    print("[twin] EXR", out_dir)
    return out_dir, manifest


def person_points(person):
    """人の外形の代表点（世界）。切り抜きの範囲を決める。"""
    pts = []
    for pb in person.arm.pose.bones:
        pts.append(person.arm.matrix_world @ pb.head)
    pts.append(person.arm.matrix_world @ person.arm.pose.bones[person.prefix + "HeadTop_End"].head + Vector((0, 0, 0.12)))
    return pts


def project(cam, co, w, h):
    from bpy_extras.object_utils import world_to_camera_view

    v = world_to_camera_view(bpy.context.scene, cam, co)
    return v.x * w, v.y * h, v.z


def crop_box(cam, person, w, h):
    """人と、電球が床へ落とす影の範囲を画面へ投影した箱（0..1）。画に入らなければ None。"""
    bulb = tc.BULB + Vector((0, 0, -0.05))
    pts = person_points(person)
    world = list(pts)
    for co in pts:
        d = co - bulb
        if d.z < -1e-3:
            t = min(bulb.z / -d.z, 3.0)
            hit = bulb + d * t
            hit.x = max(tc.ROOM_X[0], min(tc.ROOM_X[1], hit.x))
            hit.y = max(tc.ROOM_Y[0], min(tc.ROOM_Y[1], hit.y))
            world.append(hit)
    xs, ys = [], []
    for co in world:
        x, y, z = project(cam, co, w, h)
        if z > 0.02:
            xs.append(x)
            ys.append(y)
    if not xs:
        return None
    pad = 0.09 * h
    x0, x1 = max(0, min(xs) - pad), min(w, max(xs) + pad)
    y0, y1 = max(0, min(ys) - pad), min(h, max(ys) + pad)
    if x1 <= x0 or y1 <= y0:
        return None
    return x0 / w, x1 / w, y0 / h, y1 / h


def run_seq(a):
    """固定カメラの動画のコマ。背景 1 枚 + 人の周りだけ毎コマ描き、境をぼかして重ねる。

    出力: Logs/twin-cycles/seq/<route>/<cam>/fNNNNN.npy と plate.npy（仕上げは seq_post.py）
    """
    import numpy as np

    bpy.ops.wm.read_factory_settings(use_empty=True)
    ctx = ts.build_all(air_density=a.air, dust=a.dust, screens={c: tc.WORK / "feeds" / f"{c}.jpg" for c in "ABC" if (tc.WORK / "feeds" / f"{c}.jpg").exists()})
    person = tp.Person(ctx, a.route)
    w, h = a.w, a.w * 3 // 4
    setup_cycles(w, h, a.spp)
    s = bpy.context.scene
    s.render.use_persistent_data = True
    cam = make_camera()
    root = tc.WORK / "seq" / a.route
    rig = {c["id"]: c for c in ctx["rig"]["cameras"]}
    last_t = person.rows[-1]["t"]
    nframes = int(last_t * a.fps) + 1
    frames = range(nframes)
    if a.frames:
        frames = [int(x) for x in a.frames.split(",")]
    tmp = root / "_tmp.exr"
    root.mkdir(parents=True, exist_ok=True)

    def shoot_full(path):
        s.render.filepath = str(tmp)
        bpy.ops.render.render(write_still=True)
        img = bpy.data.images.load(str(tmp))
        img.colorspace_settings.name = "Non-Color"
        px = np.empty(w * h * 4, dtype=np.float32)
        img.pixels.foreach_get(px)
        arr = px.reshape(h, w, 4)[::-1, :, :3]
        bpy.data.images.remove(img)
        return arr

    for cid in [c for c in a.cams.split(",") if c]:
        aim(cam, (0, 0, 0), (0, 0, 0), hfov=rig[cid]["hfovDeg"], sensor=36.0, matrix=ctx["views"][cid])
        out = root / cid
        out.mkdir(parents=True, exist_ok=True)
        plate_path = out / "plate.npy"
        # 背景は人を消して、高い品質で 1 回
        s.render.use_border = False
        for o in tc.collection("Person").objects:
            o.hide_render = True
        for m in person.visible_meshes():
            m.hide_render = True
        if plate_path.exists():
            plate = np.load(plate_path).astype(np.float32)
        else:
            s.cycles.samples = a.plate_spp
            s.cycles.seed = 3
            t0 = time.time()
            plate = shoot_full(plate_path)
            np.save(plate_path, plate.astype(np.float16))
            print(f"[twin] plate {cid} {time.time() - t0:.1f}s", flush=True)
        for o in tc.collection("Person").objects:
            o.hide_render = False
        for m in person.visible_meshes():
            m.hide_render = False
        s.cycles.samples = a.spp
        t_all = time.time()
        for f in frames:
            path = out / f"f{f:05d}.npy"
            if path.exists():
                continue
            t = f / a.fps
            person.pose_at(t)
            s.cycles.seed = 3
            box = crop_box(cam, person, w, h)
            if box is None:
                np.save(path, plate.astype(np.float16))
                continue
            x0, x1, y0, y1 = box
            area = (x1 - x0) * (y1 - y0)
            if area > 0.72:
                s.render.use_border = False
            else:
                s.render.use_border = True
                s.render.use_crop_to_border = False
                s.render.border_min_x, s.render.border_max_x = x0, x1
                s.render.border_min_y, s.render.border_max_y = y0, y1
            t0 = time.time()
            arr = shoot_full(path)
            # 境をぼかして背景へ
            if area <= 0.72:
                yy = (h - 1 - np.arange(h)) / h
                xx = (np.arange(w) + 0.5) / w
                feather = 0.03
                wx = np.clip((xx - x0) / feather, 0, 1) * np.clip((x1 - xx) / feather, 0, 1)
                wy = np.clip((yy - y0) / feather, 0, 1) * np.clip((y1 - yy) / feather, 0, 1)
                mask = (wy[:, None] * wx[None, :])[..., None]
                arr = plate * (1 - mask) + arr * mask
            np.save(path, arr.astype(np.float16))
            print(f"[twin] {cid} f{f} t={t:.2f} area={area:.2f} {time.time() - t0:.1f}s", flush=True)
        print(f"[twin] {cid} 全コマ {time.time() - t_all:.0f}s", flush=True)
    tmp.unlink(missing_ok=True)


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    ap = argparse.ArgumentParser()
    ap.add_argument("cmd", choices=["still", "seq"])
    ap.add_argument("--cams", default="A,B,C")
    ap.add_argument("--fps", type=float, default=15.0)
    ap.add_argument("--frames", default="")
    ap.add_argument("--plate-spp", dest="plate_spp", type=int, default=512)
    ap.add_argument("--tag", default="x")
    ap.add_argument("--shots", default="hero")
    ap.add_argument("--w", type=int, default=960)
    ap.add_argument("--spp", type=int, default=128)
    ap.add_argument("--air", type=float, default=0.016)
    ap.add_argument("--dust", type=int, default=1500)
    ap.add_argument("--person", type=int, default=0)
    ap.add_argument("--time", type=float, default=5.0)
    ap.add_argument("--route", default="forward")
    ap.add_argument("--feeds", default="")
    a = ap.parse_args(argv)
    a.stamp = datetime.now().strftime("%Y%m%d_%H%M%S") + "_" + a.tag
    if a.cmd == "seq":
        run_seq(a)
    else:
        run_still(a)


if __name__ == "__main__":
    main()
