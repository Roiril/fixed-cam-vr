"""Typeset independent warning parts from real OFL fonts, never from generated lettering."""
import argparse
import json
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

sys.stdout.reconfigure(encoding="utf-8")


def typeset(font_path, text, size, tracking, out_path):
    font = ImageFont.truetype(str(font_path), size)
    boxes = [font.getbbox(char) for char in text]
    top = min(b[1] for b in boxes)
    bottom = max(b[3] for b in boxes)
    advances = [font.getlength(char) for char in text]
    width = round(sum(advances) + tracking * (len(text) - 1))
    padding = 8
    image = Image.new("RGBA", (width + padding * 2, bottom - top + padding * 2))
    draw = ImageDraw.Draw(image)
    x = padding
    for char, advance in zip(text, advances):
        draw.text((round(x), padding - top), char, font=font, fill=(255, 255, 255, 255))
        x += advance + tracking
    temporary = out_path.with_suffix(".tmp.png")
    image.save(temporary)
    temporary.replace(out_path)
    return {"text": text, "font": font_path.name, "pixels": list(image.size)}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--fonts", type=Path, default=Path("Assets/Art/Fonts/UnauthorizedAccess"))
    parser.add_argument("--out", type=Path, default=Path("Assets/Art/Textures/UnauthorizedAccess"))
    args = parser.parse_args()
    latin = args.fonts / "DSEG14Modern-Bold.ttf"
    japanese = args.fonts / "DotGothic16-Regular.ttf"
    if not latin.is_file() or not japanese.is_file():
        raise SystemExit("Both source font files must exist before typesetting.")
    args.out.mkdir(parents=True, exist_ok=True)
    results = [
        typeset(latin, "WARNING", 240, 3, args.out / "wordmark-v3.png"),
        typeset(japanese, "不正アクセス検出", 96, 3, args.out / "subtitle-v3.png"),
    ]
    print(json.dumps(results, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
