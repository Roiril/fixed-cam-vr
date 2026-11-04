"""Retain byte-identical reference copies without altering the user's originals."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import sys

sys.stdout.reconfigure(encoding="utf-8")
destination = Path("G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/sources")
destination.mkdir(parents=True, exist_ok=True)
records = []
for original in (Path("C:/Users/kouga/Downloads/Screenshot_20261101-004612.png"),
                 Path("C:/Users/kouga/Downloads/screen-20261101-004528.mp4")):
    target = destination / original.name
    expected = hashlib.sha256(original.read_bytes()).hexdigest()
    if target.exists():
        if hashlib.sha256(target.read_bytes()).hexdigest() != expected:
            raise ValueError(f"Frozen reference differs: {target}")
    else:
        temporary = target.with_name(target.name + ".tmp")
        shutil.copyfile(original, temporary)
        if hashlib.sha256(temporary.read_bytes()).hexdigest() != expected:
            raise ValueError(f"Copy verification failed: {original}")
        os.replace(temporary, target)
    records.append({"original": str(original), "copy": str(target), "sha256": expected, "bytes": target.stat().st_size})
temporary = destination / "manifest.json.tmp"
temporary.write_text(json.dumps(records, ensure_ascii=False, indent=2), encoding="utf-8")
os.replace(temporary, destination / "manifest.json")
print(json.dumps(records, ensure_ascii=False))
