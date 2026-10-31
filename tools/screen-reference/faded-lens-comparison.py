"""Arrange unchanged Unity captures for review; never synthesize source pixels."""
import argparse
import hashlib
import json
from pathlib import Path
import runpy
import sys

from PIL import Image, ImageDraw, ImageFont

sys.stdout.reconfigure(encoding="utf-8")
parser = argparse.ArgumentParser()
parser.add_argument("--study", choices=("faded", "signal", "brightness", "stack", "runtime"), default="faded")
args = parser.parse_args()
output = Path("G:/マイドライブ/shubie/reports/2026-10-30_crt-reference") / {
    "faded": "processing-faded-lens-01", "signal": "processing-signal-01",
    "brightness": "processing-signal-02", "stack": "processing-human-stack-01",
    "runtime": "processing-runtime-01/render"
}[args.study]
evidence = runpy.run_path(str(Path(__file__).with_name("evidence-index.py")))["read_evidence"](output)
labels = {
    "faded": {"baseline03": "比較の基準（候補03）", "fadeOnly": "色あせだけ", "warpOnly": "周辺の歪みだけ", "both": "色あせ＋周辺の歪み"},
    "signal": {"baseline": "基準：色を保った歪み版", "gainOnly": "全体の明るさを落とす", "bandwidthOnly": "細かな解像感を落とす", "both": "明るさ＋解像感を落とす"},
    "brightness": {"gain090": "前回の右下：明るさ0.90倍", "gain085": "少し暗め：明るさ0.85倍", "gain080": "強めの比較：明るさ0.80倍"},
    "stack": {"base090": "前回の右下：明るさ0.90倍", "base085": "少し暗め：明るさ0.85倍", "stack090": "0.90倍＋人形化前の通常加工", "stack085": "0.85倍＋人形化前の通常加工"},
    "runtime": {"runtime_mono0.0": "右下を少し暗く：追加加工なし", "runtime_normal_Bfirst": "既存加工を追加：初周Bの強さ", "runtime_normal_Cfirst": "既存加工を追加：初周Cの強さ", "runtime_mono0.5": "既存加工を追加：通常時の最大"},
}[args.study]
fonts = sorted(Path("C:/Windows/Fonts").glob("meiryo.ttc"))
if not fonts:
    raise FileNotFoundError("Japanese review font is unavailable")
font = ImageFont.truetype(str(fonts[0]), 25)
small = ImageFont.truetype(str(fonts[0]), 15)
width, image_height, label_height, gutter = 800, 644, 62, 12
columns = 3 if len(labels) == 3 else 2
row_count = (len(labels) + columns - 1) // columns
for plate in "ABC":
    panel = Image.new("RGB", (width * columns + gutter * (columns - 1), (image_height + label_height) * row_count + gutter * (row_count - 1)), "#faf9f6")
    draw = ImageDraw.Draw(panel)
    rows = []
    for i, (condition, label) in enumerate(labels.items()):
        row = next(r for r in evidence["rows"] if r["plate"] == plate and r["metadata"]["condition"] == condition)
        image = Image.open(row["path"]).convert("RGB")
        assert image.size == (1280, 1030)
        x, y = (i % columns) * (width + gutter), (i // columns) * (image_height + label_height + gutter)
        draw.text((x + 14, y + 2), label, fill="#242424", font=font)
        draw.text((x + 14, y + 36), row["file"] + " / " + row["sha256"][:12], fill="#666666", font=small)
        panel.paste(image.resize((width, image_height), Image.Resampling.LANCZOS), (x, y + label_height))
        assert hashlib.sha256(Path(row["path"]).read_bytes()).hexdigest() == row["sha256"]
        rows.append({"condition": condition, "file": row["file"], "sha256": row["sha256"]})
    target = output / (plate + "_comparison.png")
    panel.save(target)
    (output / (plate + "_comparison.json")).write_text(json.dumps({"source_unchanged": True, "layout_only": True, "rows": rows}, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"file": str(target), "size": panel.size}, ensure_ascii=False))
