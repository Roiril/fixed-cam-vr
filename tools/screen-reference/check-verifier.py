"""Calibrate the image checker on a real draw, magenta failure and black failure."""
import json
from PIL import Image
from verify import OUT, assert_rendered

results = []
valid = Image.open(OUT / "round-20/02.png").convert("RGB")
assert_rendered(valid)
results.append({"input": "round-20 real draw", "accepted": True})
for label, color in [("shader error magenta", (255, 0, 255)), ("black output", (0, 0, 0))]:
    try:
        assert_rendered(Image.new("RGB", (16, 16), color))
    except AssertionError:
        results.append({"input": label, "rejected": True})
    else:
        raise AssertionError("checker accepted " + label)
temp = OUT / "checker-calibration.json.tmp"
temp.write_text(json.dumps(results, indent=2), encoding="utf-8")
temp.replace(OUT / "checker-calibration.json")
print(json.dumps(results))
