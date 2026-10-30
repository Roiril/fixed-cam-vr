"""Measure RGB edge spread directly from the reference JPEG (no image editing)."""
from pathlib import Path
import json
import sys
import numpy as np
from PIL import Image

sys.stdout.reconfigure(encoding="utf-8")
ROOT = Path(__file__).resolve().parents[2]
REF = ROOT / "Assets/Resources/CrtReference/CaseColor.jpg"
rgb = np.asarray(Image.open(REF).convert("RGB"), dtype=np.float64) / 255
profiles, positions = [], []
for row in range(410, 650, 4):
    line = rgb[row, 1010:1055]
    # The right white panel terminates against the dark vertical frame.
    g = np.convolve(line[:, 1], [0.25, 0.5, 0.25], "valid")
    edge = int(np.argmin(np.diff(g))) + 1011
    if 1022 <= edge <= 1048 and rgb[row, edge-8, 1] - rgb[row, edge+8, 1] > 0.40:
        positions.append(edge)
        profiles.append(rgb[row, edge-8:edge+9])
assert len(profiles) >= 20
profile = np.mean(profiles, axis=0)
measurements = []
for channel in range(3):
    signal = profile[:, channel]
    high, low = signal[:3].mean(), signal[-3:].mean()
    assert high > low + 0.20, "the selected line is not a white-to-dark edge"
    weights = np.maximum(-np.diff(signal), 0)
    x = np.arange(-7.5, 8)
    center = float(np.sum(x * weights) / weights.sum())
    sigma = float(np.sqrt(np.sum((x-center)**2 * weights) / weights.sum()))
    measurements.append({"channel": "RGB"[channel], "high": float(high), "low": float(low),
                         "derivative_center_px": center, "derivative_sigma_px": sigma})
result = {"source": str(REF), "edge_count": len(profiles), "edge_x_range": [min(positions), max(positions)],
          "channels": measurements, "mean_profile": profile.tolist(),
          "limitation": "A physical door edge is assumed locally straight. This is an estimate, not ground truth of the camera PSF."}
OUT = Path("G:/マイドライブ/shubie/reports/2026-10-30_crt-reference")
temp = OUT / "reference-edge.json.tmp"
temp.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
temp.replace(OUT / "reference-edge.json")
print(json.dumps({k: v for k, v in result.items() if k not in ("mean_profile", "source")}))
