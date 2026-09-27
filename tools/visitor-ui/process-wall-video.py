"""Prepare the one-lap wall footage for the tablet's looping evidence view.

The visual treatment matches the doll-camera photograph used by the website:
roughly 200 source blocks across, monochrome night vision, high contrast,
sensor grain, and darkened edges. The footage's audio is intentionally removed.
"""

import hashlib
import os
from pathlib import Path
import subprocess
import sys
import tempfile


sys.stdout.reconfigure(encoding="utf-8")
ROOT = Path(__file__).resolve().parents[2]
TARGET = ROOT / "Assets/Resources/Visitor/kabe-one-lap-doll-v1.mp4.bytes"
SOURCE_SHA256 = "9131a796ceff4c21a82684b42882093f6286224e68e151c17fa17560b3723e3e"
FILTERS = ",".join((
    "crop=1836:1032:37:0",  # Remove only the source's 37 px black side bars.
    "scale=200:112:flags=area",
    "scale=960:540:flags=bilinear",
    "hue=s=0",
    "eq=contrast=1.85:brightness=0.07:gamma=1.08",
    "noise=alls=55:allf=t+u",
    "format=gray",  # Keep grain monochrome after FFmpeg adds per-channel noise.
    "vignette=PI/6",
))


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def main():
    if len(sys.argv) != 2:
        raise SystemExit("usage: py -3.11 tools/visitor-ui/process-wall-video.py <Kabe1shuu.mp4>")
    source = Path(sys.argv[1]).resolve(strict=True)
    actual_hash = sha256(source)
    if actual_hash != SOURCE_SHA256:
        raise SystemExit(f"unexpected source SHA-256: {actual_hash}")

    with tempfile.NamedTemporaryFile(suffix=".mp4", delete=False) as output:
        temporary = Path(output.name)
    try:
        subprocess.run([
            "ffmpeg", "-hide_banner", "-loglevel", "error", "-y",
            "-i", str(source), "-map", "0:v:0", "-vf", FILTERS,
            "-c:v", "libx264", "-preset", "medium", "-crf", "24",
            "-profile:v", "high", "-level:v", "4.0", "-pix_fmt", "yuv420p",
            "-g", "60", "-an", "-movflags", "+faststart", str(temporary),
        ], check=True)
        os.replace(temporary, TARGET)
    finally:
        temporary.unlink(missing_ok=True)
    print(f"{TARGET} | {TARGET.stat().st_size} bytes | SHA-256 {sha256(TARGET)}")


if __name__ == "__main__":
    main()
