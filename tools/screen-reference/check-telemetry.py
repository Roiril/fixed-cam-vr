"""Calibrate the CRT telemetry gate using one valid state and three failures."""
from pathlib import Path
import importlib.util
import json
import sys

sys.stdout.reconfigure(encoding="utf-8")
ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
spec = importlib.util.spec_from_file_location("xp", ROOT / "tools/analyze-xp-log.py")
xp = importlib.util.module_from_spec(spec)
spec.loader.exec_module(xp)
show = json.loads((ROOT / "tools/web-compositor/show.json").read_text(encoding="utf-8-sig"))
expected = xp.expected_from_show(show)
base = {"t": "2", "ev": "sum", "phase": "Run", "crtRef": "1",
        "crtCase": str(1280 / 1030), "crtFrame": str(16 / 9), "_cams": []}
cases = [("ready", {}, "OK"), ("missing_texture", {"crtRef": "0"}, "FAIL"),
         ("wrong_physical_aspect", {"crtCase": str(16 / 9)}, "FAIL"),
         ("wrong_logical_aspect", {"crtFrame": "1.3333"}, "FAIL")]
result = []
for name, patch, wanted in cases:
    report, verdicts = xp.analyze([dict(base, **patch)], [], expected)
    found = [v for v in verdicts if "参照CRT" in str(v)]
    assert len(found) == 1, (name, found)
    assert found[0][0] == wanted, (name, found)
    result.append({"case": name, "verdict": found[0]})
out = Path("G:/マイドライブ/shubie/reports/2026-10-30_crt-reference/telemetry-check.json")
temp = out.with_suffix(".json.tmp")
temp.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
temp.replace(out)
print(json.dumps(result, ensure_ascii=False))
