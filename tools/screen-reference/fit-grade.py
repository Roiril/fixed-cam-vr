"""Derive a monotone luminance candidate from the directly generated image guide.

This creates a numeric lookup texture, not an edited comparison screenshot.
The guide is never used as implementation or completion evidence.
"""
from pathlib import Path
import argparse
import hashlib
import json
import sys
import numpy as np
from PIL import Image

sys.stdout.reconfigure(encoding="utf-8")
ROOT = Path(__file__).resolve().parents[2]
OUT = Path("G:/マイドライブ/shubie/reports/2026-10-30_crt-reference")
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--source", type=Path)
parser.add_argument("--guide", type=Path)
parser.add_argument("--replay", type=Path, help="Restore an already saved numeric curve without refitting")
parser.add_argument("--output", type=Path, default=ROOT / "Assets/Resources/CrtReference/Grade.png")
args = parser.parse_args()
if args.replay:
    if args.source or args.guide:
        parser.error("--replay cannot be combined with --source/--guide")
elif args.source is None or args.guide is None:
    parser.error("Fitting requires explicit --source and --guide; no implicit reference substitution")
source, guide = args.source, args.guide
size = (1280, 1030)
def luma(path):
    rgb = np.asarray(Image.open(path).convert("RGB").resize(size), dtype=np.float64) / 255
    # A fixed interior window keeps both casing and aperture uncertainty out of the fit.
    return (rgb[170:850, 180:1110] @ np.array([0.299, 0.587, 0.114])).ravel()

if args.replay:
    record = json.loads(args.replay.read_text(encoding="utf-8-sig"))
    values = np.asarray(record["lookup"], dtype=np.float64)
else:
    quantiles = np.linspace(0.01, 0.99, 99)
    sx, ty = np.quantile(luma(source), quantiles), np.quantile(luma(guide), quantiles)
    x = np.concatenate(([0.0], sx, [1.0]))
    y = np.concatenate(([0.0], ty, [1.0]))
    # Repeated input values have one well-defined monotone target.
    ux, first = np.unique(x, return_index=True)
    uy = np.maximum.accumulate(y[first])
    values = np.interp(np.linspace(0, 1, 256), ux, uy)
    # Smooth the estimated response; a LUT must not magnify JPEG/noise steps.
    for _ in range(12):
        values[1:-1] = (values[:-2] + 2 * values[1:-1] + values[2:]) / 4
    values = np.maximum.accumulate(values)
    record = {"source": str(source), "source_sha256": hashlib.sha256(source.read_bytes()).hexdigest(),
              "guide": str(guide), "guide_sha256": hashlib.sha256(guide.read_bytes()).hexdigest(),
              "method": "monotone 1D display-luma distribution fit to an explicit image guide; no spatial transfer",
              "source_quantiles": sx.tolist(), "target_quantiles": ty.tolist(),
              "lookup": values.tolist(), "completion_evidence": False}
assert values.shape == (256,) and np.all(np.isfinite(values))
assert np.all(np.diff(values) >= 0) and values[0] == 0 and values[-1] == 1
data = np.rint(values * 255).astype(np.uint8)
texture = np.repeat(data[np.newaxis, :, np.newaxis], 3, axis=2)
asset = args.output
asset.parent.mkdir(parents=True, exist_ok=True)
asset_temp = asset.with_name(asset.stem + ".tmp.png")
Image.fromarray(texture).save(asset_temp)
asset_temp.replace(asset)
if not args.replay:
    tmp = OUT / "grade-fit.json.tmp"
    tmp.write_text(json.dumps(record, ensure_ascii=False, indent=2), encoding="utf-8")
    tmp.replace(OUT / "grade-fit.json")
print(json.dumps({"monotone": True, "endpoints": [int(data[0]), int(data[-1])], "samples": data[::32].tolist()}))
