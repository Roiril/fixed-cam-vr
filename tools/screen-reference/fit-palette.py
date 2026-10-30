"""Fit a bounded, luminance-conditioned color residual from two image distributions."""
from pathlib import Path
import hashlib
import json
import sys
import numpy as np
from PIL import Image

sys.stdout.reconfigure(encoding="utf-8")
ROOT = Path(__file__).resolve().parents[2]
OUT = Path("G:/マイドライブ/shubie/reports/2026-10-30_crt-reference")
source, target = OUT / "round-22/02.png", ROOT / "Assets/Resources/CrtReference/CaseColor.jpg"
weights = np.array([0.299, 0.587, 0.114])
def conditional(path):
    rgb = np.asarray(Image.open(path).convert("RGB"), dtype=np.float64)[150:900,160:1140] / 255
    rgb = rgb.reshape(-1, 3)
    y = rgb @ weights
    neutral = np.ptp(rgb, axis=1) < 0.25
    curve = np.full((256, 3), np.nan)
    for i in range(256):
        selected = neutral & (np.abs(y - i/255) < 0.018)
        if selected.sum() >= 64:
            curve[i] = np.median(rgb[selected] - y[selected, None], axis=0)
    for c in range(3):
        valid = np.isfinite(curve[:,c])
        assert valid.sum() > 100
        curve[:,c] = np.interp(np.arange(256), np.flatnonzero(valid), curve[valid,c])
    return curve

delta = np.clip(conditional(target) - conditional(source), -0.035, 0.035)
for _ in range(12):
    delta[1:-1] = (delta[:-2] + 2 * delta[1:-1] + delta[2:]) / 4
delta -= (delta @ weights)[:, None]
data = np.rint(np.clip(0.5 + delta * 4, 0, 1) * 255).astype(np.uint8)[None, :, :]
(OUT / "experimental-assets").mkdir(parents=True, exist_ok=True)
Image.fromarray(data).save(OUT / "experimental-assets/Palette.png")
record = {"source_sha256": hashlib.sha256(source.read_bytes()).hexdigest(),
          "target_sha256": hashlib.sha256(target.read_bytes()).hexdigest(),
          "method": "bounded conditional color residual; neutral regions; median; no spatial transfer",
          "delta_srgb": delta.tolist()}
temp = OUT / "palette-fit.json.tmp"
temp.write_text(json.dumps(record, ensure_ascii=False, indent=2), encoding="utf-8")
temp.replace(OUT / "palette-fit.json")
print(json.dumps({"delta_samples": delta[[13,51,128,204]].round(5).tolist()}))
