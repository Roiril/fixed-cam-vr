"""Create neutral, labelled reference crops for close texture comparison and ImageGen conditioning."""
from pathlib import Path
import hashlib
import json
import sys
from PIL import Image, ImageDraw, ImageFont

sys.stdout.reconfigure(encoding="utf-8")
root = Path("G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study")
source = root / "sources/Screenshot_20261101-004612.png"
out = root / "still-mockup/reference-detail"
out.mkdir(parents=True, exist_ok=True)
image = Image.open(source).convert("L")
image.crop((450, 0, 1890, 1080)).save(out / "reference-active-grayscale.png")
regions = [("window", (870, 40, 1000, 300)), ("monitor", (1220, 270, 1300, 445)),
           ("desk", (540, 470, 920, 640)), ("floor", (1110, 760, 1510, 1030))]
sheet = Image.new("RGB", (1700, 1240), (238, 238, 238))
draw = ImageDraw.Draw(sheet)
font = ImageFont.truetype("C:/Windows/Fonts/meiryo.ttc", 18)
for i, (name, box) in enumerate(regions):
    crop = image.crop(box)
    crop.save(out / (name + "-native.png"))
    enlarged = crop.resize((crop.width * 2, crop.height * 2), Image.Resampling.NEAREST)
    x, y = i % 2 * 850, i // 2 * 620
    draw.text((x + 12, y + 8), f"{i+1:02d} {name} | source {box} | 2x nearest display", font=font, fill=(25, 25, 25))
    sheet.paste(enlarged.convert("RGB"), (x + 12, y + 42))
sheet.save(out / "reference-texture-details.png")
(out / "manifest.json").write_text(json.dumps({"source": str(source),
    "source_sha256": hashlib.sha256(source.read_bytes()).hexdigest(),
    "preparation": "luminance only; original ROI pixels retained; enlarged labels are diagnostic, not a new target",
    "regions": [{"name": name, "ROI": list(box)} for name, box in regions]}, ensure_ascii=False, indent=2), encoding="utf-8")
print("Reference detail sheet saved")
