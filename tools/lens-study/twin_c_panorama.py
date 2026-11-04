"""Render the twin from camera C both as the calibrated pinhole and as a full sphere (0307 research).

Run in Blender:
    blender --background --factory-startup --python tools/lens-study/twin_c_panorama.py -- --out <dir>

The panorama is remapped later by lens_study.py with any lens. The pinhole render checks the axis
convention of that remap: resampled to the pinhole lens it must land on the pinhole render.
"""
import argparse
import json
import sys
from pathlib import Path

import bpy

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools" / "twin"))

import twin_render as tr  # noqa: E402


def render_to_npy(path):
    bpy.context.scene.render.filepath = str(path)
    bpy.ops.render.render(write_still=True)
    tr.save_npy(path)


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    ap.add_argument("--pano-w", type=int, default=4096)
    ap.add_argument("--spp", type=int, default=128)
    a = ap.parse_args(argv)
    out = Path(a.out)
    out.mkdir(parents=True, exist_ok=True)

    bpy.ops.wm.read_factory_settings(use_empty=True)
    feed_dir = tr.tc.WORK / "feeds"
    screens = {c: feed_dir / f"{c}.jpg" for c in "ABC" if (feed_dir / f"{c}.jpg").exists()}
    ctx = tr.ts.build_all(air_density=0.008, dust=1500, screens=screens)
    tr.setup_cycles(960, 720, a.spp)
    cam = tr.make_camera()
    rig = next(c for c in ctx["rig"]["cameras"] if c["id"] == "C")
    tr.aim(cam, (0, 0, 0), (0, 0, 0), hfov=rig["hfovDeg"], sensor=36.0, matrix=ctx["views"]["C"])
    bpy.context.scene.cycles.seed = 7
    render_to_npy(out / "C_pinhole.exr")

    d = cam.data
    # The modelled phone body and lens barrel wrap the camera beyond about 75 degrees off axis.
    # A real clip-on lens sits in front of them, so clip them away. The arm and shelf stay.
    d.clip_start = 0.04
    d.type = "PANO"
    d.panorama_type = "EQUIRECTANGULAR"
    d.latitude_min, d.latitude_max = -1.5707963, 1.5707963
    d.longitude_min, d.longitude_max = -3.1415927, 3.1415927
    s = bpy.context.scene
    s.render.resolution_x = a.pano_w
    s.render.resolution_y = a.pano_w // 2
    render_to_npy(out / "C_pano.exr")
    (out / "twin_meta.json").write_text(json.dumps({"hfovDeg": rig["hfovDeg"], "panoW": a.pano_w, "spp": a.spp}), encoding="utf-8")
    print("[lens-study] done", out)


if __name__ == "__main__":
    main()
