"""Verify actual Unity capture provenance and the reflectance map's visible effect."""
from __future__ import annotations
import hashlib
import json
from pathlib import Path
import sys
from PIL import Image, ImageChops, ImageStat, ImageFilter

sys.stdout.reconfigure(encoding="utf-8")
ROOT = Path(__file__).resolve().parents[2]
OUT = Path("G:/マイドライブ/shubie/reports/2026-10-30_crt-reference")


def sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def assert_rendered(im: Image.Image) -> None:
    channels = ImageStat.Stat(im.convert("RGB")).mean
    assert not (channels[0] > 240 and channels[1] < 10 and channels[2] > 240), "shader error magenta"
    assert ImageStat.Stat(im.convert("L")).mean[0] > 5, "black output"


def verify() -> dict:
    m = json.loads((OUT / "manifest.json").read_text(encoding="utf-8-sig"))
    assert m["shaderHash"] == sha(ROOT / "Assets/Art/Shaders/Streaming/ScreenComposite.shader"), "stale shader capture"
    frame_path = ROOT / "Assets/Resources/CrtReference/Frame.png"
    reflection_path = ROOT / "Assets/Resources/CrtReference/Reflectance.png"
    case_color_path = ROOT / "Assets/Resources/CrtReference/CaseColor.jpg"
    assert m["caseColorHash"] == sha(case_color_path)
    assert m["glassHash"] == sha(ROOT / "Assets/Resources/CrtReference/Glass.png")
    assert m["normalHash"] == sha(ROOT / "Assets/Resources/CrtReference/Normal.png")
    assert m["gradeHash"] == sha(ROOT / "Assets/Resources/CrtReference/Grade.png")
    grade = Image.open(ROOT / "Assets/Resources/CrtReference/Grade.png").convert("RGB")
    assert grade.size == (256, 1)
    curve = [grade.getpixel((x, 0))[0] for x in range(256)]
    assert curve[0] == 0 and curve[-1] == 255
    assert all(a <= b for a, b in zip(curve, curve[1:])), "grade inverts display luminance"
    assert m["frameHash"] == sha(frame_path)
    assert m["reflectanceHash"] == sha(reflection_path)
    if "runtimeProfileHash" in m:
        assert m["runtimeProfileHash"] == sha(ROOT / "Assets/Scripts/Streaming/CrtReferenceScreen.cs")
    frame = Image.open(frame_path).convert("RGBA")
    reflection = Image.open(reflection_path)
    assert frame.size == reflection.size == (1398, 1125)
    assert frame.getpixel((699, 562))[3] == 0
    assert len(m["captures"]) == 6
    images = {}
    for c in m["captures"]:
        p = OUT / c["file"]
        assert sha(p) == c["sha256"]
        assert sha(Path(c["source"])) == c["sourceHash"]
        im = Image.open(p).convert("RGB")
        assert im.size == (1280, 1030), (p, im.size)
        assert abs(c["physicalAspect"] - 1280 / 1030) < 0.0001
        assert abs(c["logicalAspect"] - 16 / 9) < 0.0001
        assert_rendered(im)
        images[c["file"]] = im
    normal, moved = images["A_front.png"], images["A_reflection.png"]
    diff = ImageChops.difference(normal, moved).convert("L")
    alpha = frame.getchannel("A").resize(normal.size, Image.Resampling.BILINEAR)
    case = alpha.point(lambda v: 255 if v >= 240 else 0)
    glass = alpha.point(lambda v: 255 if v == 0 else 0)
    case_delta = ImageStat.Stat(diff, case).mean[0]
    glass_delta = ImageStat.Stat(diff, glass).mean[0]
    assert case_delta > 0.005, "reflectance map has no visible output"
    glass_detail_delta = ImageStat.Stat(ImageChops.difference(diff, diff.filter(ImageFilter.GaussianBlur(4))), glass).mean[0]
    assert glass_delta > 0.01, "glass reflectance has no visible output"
    assert glass_delta < 2.5 and glass_detail_delta < 0.25, "reflection shifts or strongly distorts room detail"
    mono_delta = ImageStat.Stat(ImageChops.difference(normal, images["A_mono1.0.png"]), glass).mean
    assert sum(mono_delta) > 10, "night mode is not visible"
    result = {
        "capture_count": 6, "dimensions": [1280, 1030], "source_hashes_match": True,
        "shader_and_texture_hashes_match": True, "physical_aspect": 1280 / 1030,
        "grade_is_monotone": True,
        "logical_aspect": 16 / 9, "reflection_case_pixel_difference": case_delta,
        "reflection_glass_pixel_difference": glass_delta, "night_mode_rgb_difference": mono_delta,
        "reflection_glass_detail_difference": glass_detail_delta,
    }
    tmp = OUT / "verification.json.tmp"
    tmp.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    tmp.replace(OUT / "verification.json")
    return result


if __name__ == "__main__":
    print(json.dumps(verify(), ensure_ascii=False))
