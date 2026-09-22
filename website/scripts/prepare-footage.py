"""Cut the website's three silent Quest recordings from the original captures."""

from pathlib import Path
import argparse
import shutil
import subprocess

from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parents[2]
ASSETS = ROOT / "website" / "assets"
CLIPS = {
    "fracture": {
        "source": ROOT / "Logs" / "capture" / "20260921_115112_eye.mp4",
        "start": 40.0,
        "duration": 18.0,
        "crop": None,
        "poster": 11.0,
        "contact": [0, 3, 6, 9, 9.5, 10, 10.5, 11, 11.5, 12, 17.9],
    },
    "error": {
        "source": ROOT / "Logs" / "capture" / "20260921_161248_raw.mp4",
        "start": 99.0,
        "duration": 11.0,
        "crop": "crop=iw/2:ih:iw/2:0",
        "poster": 8.5,
    },
    "eye": {
        "source": ROOT / "Logs" / "capture" / "20260919_192053_raw.mp4",
        "start": 108.0,
        "duration": 11.0,
        "crop": "crop=iw/2:ih:0:0",
        "poster": 6.0,
    },
}


def run(*args):
    subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", *map(str, args)], check=True)


def make_contact(name, video, qa_dir, duration, contact_times=None):
    frames = []
    if contact_times is None:
        contact_times = [min(duration * index / 6, duration - 0.1) for index in range(7)]
    for index, second in enumerate(contact_times):
        frame = qa_dir / f"{name}-{index:02d}.png"
        run("-ss", f"{second:.3f}", "-i", video, "-frames:v", 1, frame)
        with Image.open(frame) as image:
            thumb = image.convert("RGB")
            thumb.thumbnail((300, 330))
            frames.append((second, thumb.copy()))

    cell_w, cell_h = 320, 365
    sheet = Image.new("RGB", (cell_w * 4, cell_h * ((len(frames) + 3) // 4)), "#171717")
    draw = ImageDraw.Draw(sheet)
    for index, (second, frame) in enumerate(frames):
        x, y = (index % 4) * cell_w, (index // 4) * cell_h
        sheet.paste(frame, (x, y + 25))
        draw.text((x + 6, y + 6), f"{name} +{second:.1f}s", fill="white")
    sheet.save(qa_dir / f"{name}-contact.jpg", quality=90)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("clips", nargs="*", choices=CLIPS, help="clip names; defaults to all three")
    parser.add_argument("--qa-dir", type=Path, default=ROOT / ".codex-tmp" / "website-footage-20260922")
    args = parser.parse_args()
    selected = args.clips or list(CLIPS)
    if shutil.which("ffmpeg") is None:
        parser.error("ffmpeg is required")
    if any(not CLIPS[name]["source"].is_file() for name in selected):
        parser.error("an original Quest recording is missing")

    ASSETS.mkdir(parents=True, exist_ok=True)
    args.qa_dir.mkdir(parents=True, exist_ok=True)
    for name in selected:
        spec = CLIPS[name]
        video = ASSETS / f"footage-{name}.mp4"
        poster = ASSETS / f"footage-{name}.webp"
        filters = [spec["crop"]] if spec["crop"] else []
        filters.extend(["fps=30", "scale=-2:960:flags=lanczos", "format=yuv420p"])
        run(
            "-ss", spec["start"], "-i", spec["source"], "-t", spec["duration"],
            "-map", "0:v:0", "-an", "-vf", ",".join(filters),
            "-c:v", "libx264", "-preset", "medium", "-crf", 21,
            "-movflags", "+faststart", video,
        )
        run("-ss", spec["poster"], "-i", video, "-frames:v", 1,
            "-c:v", "libwebp", "-quality", 83, poster)
        make_contact(name, video, args.qa_dir, spec["duration"], spec.get("contact"))
        print(f"{video}\n{poster}\n{args.qa_dir / f'{name}-contact.jpg'}")


if __name__ == "__main__":
    main()
