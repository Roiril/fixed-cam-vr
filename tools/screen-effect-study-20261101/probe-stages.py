"""Apply unchanged filters to existing first-lap/degraded current renderer outputs."""
from pathlib import Path
import importlib.util
import json
import shutil
import sys

sys.dont_write_bytecode = True
sys.stdout.reconfigure(encoding="utf-8")
HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE / "still"))
spec = importlib.util.spec_from_file_location("study_round10", HERE / "still/offline-round10.py")
r10 = importlib.util.module_from_spec(spec)
spec.loader.exec_module(r10)
r9 = r10.r9
np, Image = r10.np, r10.Image
sha = r10.sha
OUTPUT = Path("G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/still/stage-probe")
SOURCE = Path("G:/マイドライブ/shubie/reports/2026-11-01_screen-adoption/gpu-complete")
STAGES = ("0.000", "0.167", "0.333")


def main():
    OUTPUT.mkdir(parents=True, exist_ok=True)
    lookup = json.loads((OUTPUT.parent / "round-04-measured-lookup.json").read_text(encoding="utf-8"))
    manifest = {"purpose": "same filters, existing current runtime stage values; no runtime settings changed", "camera": "A", "source_manifest_sha256": sha(SOURCE / "manifest.json"), "inputs": {}, "outputs": {}}
    for stage in STAGES:
        original = SOURCE / f"A_progress_{stage}.png"
        expected = sha(original)
        base = OUTPUT / f"base-A-{stage}.png"
        if base.exists():
            assert sha(base) == expected
        else:
            shutil.copyfile(original, base)
        src = np.array(Image.open(base).convert("RGBA"))
        visible = src[:, :, 3] > 0
        luminance = src[:, :, :3].astype(float) @ r9.WEIGHTS / 255
        optical = r9.optical(luminance, "A")
        clean, pre, std, energy, weight = r10.prepare(optical)
        for name in ("09A", "10B"):
            if name == "09A":
                target = r9.signal(optical, "A", lookup)
            else:
                target, _ = r10.faults(pre, energy, lookup, r10.PARAMS["B"], visible)
            gain = np.divide(target, luminance, out=np.zeros_like(luminance), where=luminance > .00001)
            result = src.copy()
            result[:, :, :3] = r9.gray8(src[:, :, :3] / 255 * gain[:, :, None])
            result[~visible] = src[~visible]
            assert np.array_equal(result[:, :, 3], src[:, :, 3])
            assert np.array_equal(result[~visible], src[~visible])
            file = OUTPUT / f"A-{stage}-{name}.png"
            picture = Image.fromarray(result)
            picture.save(file)
            background = Image.new("RGBA", picture.size, (18, 18, 18, 255))
            background.alpha_composite(picture)
            background.convert("RGB").save(OUTPUT / f"A-{stage}-{name}-on-black.png")
            manifest["outputs"][file.name] = {"sha256": sha(file), "alpha_exact": True, "transparent_exact": True}
        manifest["inputs"][stage] = {"original": str(original), "sha256": expected, "copy": base.name}
        assert sha(original) == expected
    (OUTPUT / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"inputs": len(manifest["inputs"]), "outputs": len(manifest["outputs"]), "same_parameters": True, "unchanged_sources": True}))


if __name__ == "__main__":
    main()
