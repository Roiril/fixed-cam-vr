"""Freeze the real shader output before a fresh two-image comparison."""
from pathlib import Path
import json
import shutil
import sys
from verify import OUT, ROOT, verify, sha

sys.stdout.reconfigure(encoding="utf-8")
number = int(sys.argv[1])
target = OUT / f"round-{number:02d}"
assert not target.exists(), f"already frozen: {target}"
verify()
reference = ROOT / ".codex-remote-attachments/01a18d24-f13f-7a60-944d-465ecdf72b3c/121f1c28-ec82-42f2-a510-6cc7d9e9b5f4/1-1000011333.jpg"
assert reference.is_file()
target.mkdir()
for source, name in [(reference, "01.jpg"), (OUT / "A_front.png", "02.png"),
                     (OUT / "manifest.json", "manifest.json"),
                     (OUT / "verification.json", "verification.json")]:
    shutil.copyfile(source, target / name)
record = {"reference_sha256": sha(target / "01.jpg"), "render_sha256": sha(target / "02.png"),
          "blind_prompt_includes_only_two_images": True, "threshold_disclosed": False}
(target / "inputs.json").write_text(json.dumps(record, indent=2), encoding="utf-8")
print(target.as_posix())
