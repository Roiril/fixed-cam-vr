"""Convert existing camera photographs to web assets without changing originals."""
from pathlib import Path
import os
import sys
from PIL import Image

sys.stdout.reconfigure(encoding="utf-8")
root = Path(__file__).resolve().parents[2]
captures = root / "tools/web-compositor/captures"
assets = root / "website/assets"
photos = {
    "camera-01.webp": "plate_A_20260907_101013.jpg",
    "camera-02.webp": "plate_B_20260907_104115.jpg",
    "camera-03.webp": "plate_C_20260823_194236.jpg",
    "wall-evidence.webp": "plate_B_20260907_104115.jpg",
}
for original in photos.values():
    if not (captures / original).is_file():
        raise FileNotFoundError(captures / original)
for output, original in photos.items():
    target = assets / output
    temporary = target.with_suffix(".webp.tmp")
    with Image.open(captures / original) as photo:
        photo.convert("RGB").save(temporary, format="WEBP", quality=88, method=6)
    os.replace(temporary, target)
    print(f"{output}: {target.stat().st_size} bytes")
