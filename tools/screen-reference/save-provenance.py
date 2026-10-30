"""Save only image-generation calls, never conversation text or tool outputs."""
from pathlib import Path
import hashlib
import json
import sys

sys.stdout.reconfigure(encoding="utf-8")
ROOT = Path(__file__).resolve().parents[2]
OUT = Path("G:/マイドライブ/shubie/reports/2026-10-30_crt-reference")
calls = []
with Path(sys.argv[1]).open(encoding="utf-8") as stream:
    for line in stream:
        row = json.loads(line)
        p = row.get("payload", {})
        if not isinstance(p, dict) or p.get("type") not in ("function_call", "custom_tool_call"):
            continue
        args = p.get("arguments", p.get("input", ""))
        if p.get("name") == "exec" and "tools.image_gen__imagegen(" in args:
            calls.append({"timestamp": row.get("timestamp"), "exact_tool_call": args})
assert calls, "generation calls not found"
assets = []
for path in sorted((ROOT / "Assets/Resources/CrtReference").iterdir()):
    if path.suffix.lower() in (".png", ".jpg"):
        assets.append({"path": path.relative_to(ROOT).as_posix(), "sha256": hashlib.sha256(path.read_bytes()).hexdigest()})
result = {"tool": "built-in image_gen", "calls": calls, "assets": assets,
          "case_color": "Original reference JPEG, unchanged bytes; generated Frame supplies only alpha.",
          "ai_guide": "ai-calibration-target.png is not implementation evidence or completion evidence."}
temp = OUT / "generation-provenance.json.tmp"
temp.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
temp.replace(OUT / "generation-provenance.json")
print(json.dumps({"generation_call_count": len(calls), "assets": len(assets)}))
