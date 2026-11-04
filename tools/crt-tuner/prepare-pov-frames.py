"""Extract native-size stills from the authored POV clips without color processing."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import struct
import subprocess
import sys
import uuid
from urllib.parse import unquote, urlsplit

import imageio_ffmpeg


ROOT = Path(__file__).resolve().parents[2]
DEFAULT_OUTPUT = Path("G:/マイドライブ/shubie/reports/2026-11-01_crt-pov-tuner/frames")
FRAME_SECONDS = 0.5
SOURCES = tuple(f"pov_{index}" for index in range(5))


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def png_size(path: Path) -> tuple[int, int]:
    with path.open("rb") as stream:
        header = stream.read(24)
    if len(header) != 24 or header[:8] != b"\x89PNG\r\n\x1a\n" or header[12:16] != b"IHDR":
        raise ValueError(f"Not a PNG: {path}")
    width, height = struct.unpack(">II", header[16:24])
    if width < 1 or height < 1:
        raise ValueError(f"Invalid PNG dimensions: {path}")
    return width, height


def clip_path(source_url: str, web_root: Path) -> Path:
    url = urlsplit(source_url)
    if url.scheme or url.netloc or url.query or url.fragment or not url.path.startswith("/"):
        raise ValueError(f"POV source must be a local asset URL: {source_url}")
    path = (web_root / unquote(url.path).lstrip("/")).resolve()
    if not path.is_relative_to(web_root.resolve()) or path.suffix.lower() != ".mp4" or not path.is_file():
        raise ValueError(f"Missing local POV mp4: {path}")
    return path


def prepare(show_path: Path, output: Path) -> None:
    show = json.loads(show_path.read_text(encoding="utf-8-sig"))
    cues = show.get("cues", [])
    clips = []
    for source in SOURCES:
        matches = [cue for cue in cues if cue.get("id") == source]
        if len(matches) != 1:
            raise ValueError(f"Expected one authored cue for {source}, found {len(matches)}")
        source_url = matches[0]["sourceUrl"]
        clips.append((source, source_url, clip_path(source_url, show_path.parent)))
    # Resolve every authored clip before creating outputs. Never substitute another image.
    output.mkdir(parents=True, exist_ok=True)
    manifest_path = output / "manifest.json"
    cached = {}
    if manifest_path.is_file():
        try:
            previous = json.loads(manifest_path.read_text(encoding="utf-8"))
            if previous.get("version") == 1:
                cached = {entry["id"]: entry for entry in previous.get("frames", [])}
        except (ValueError, KeyError, TypeError):
            cached = {}
    records = []
    ffmpeg = imageio_ffmpeg.get_ffmpeg_exe()
    for source, source_url, clip in clips:
        source_sha = sha256(clip)
        frame_path = output / f"{source}.png"
        old = cached.get(source, {})
        if (old.get("sourceSha256") == source_sha and old.get("sourceUrl") == source_url
                and old.get("timeSeconds") == FRAME_SECONDS and old.get("output") == frame_path.name
                and frame_path.is_file() and old.get("outputSha256") == sha256(frame_path)
                and list(png_size(frame_path)) == old.get("dimensions")
                and old.get("sourceDimensions") == old.get("dimensions")):
            records.append(old)
            print(f"{source}: cached {old['dimensions'][0]}x{old['dimensions'][1]}")
            continue
        reader = imageio_ffmpeg.read_frames(str(clip), input_params=["-ss", str(FRAME_SECONDS)])
        try:
            metadata = next(reader)
        finally:
            reader.close()
        native_size = tuple(metadata["source_size"])
        temporary = output / f".{source}-{uuid.uuid4().hex}.png"
        try:
            result = subprocess.run(
                [ffmpeg, "-hide_banner", "-loglevel", "error", "-ss", str(FRAME_SECONDS),
                 "-i", str(clip), "-map", "0:v:0", "-frames:v", "1", "-pix_fmt", "rgb24",
                 "-update", "1", "-y", str(temporary)],
                capture_output=True, text=True, encoding="utf-8", errors="replace", check=False)
            if result.returncode:
                raise RuntimeError(f"ffmpeg failed for {source}: {result.stderr.strip()}")
            size = png_size(temporary)
            if size != native_size:
                raise ValueError(f"Native dimensions changed for {source}: {native_size} -> {size}")
            record = {"id": source, "sourceUrl": source_url, "sourcePath": str(clip),
                      "sourceSha256": source_sha, "timeSeconds": FRAME_SECONDS,
                      "sourceDimensions": list(native_size), "output": frame_path.name,
                      "dimensions": list(size), "outputSha256": sha256(temporary)}
            os.replace(temporary, frame_path)
            records.append(record)
            print(f"{source}: extracted {size[0]}x{size[1]} at {FRAME_SECONDS}s")
        finally:
            temporary.unlink(missing_ok=True)
    manifest = {"version": 1, "showPath": str(show_path.resolve()),
                "description": "Native-size still frames; no color processing or resizing", "frames": records}
    temporary_manifest = output / f".manifest-{uuid.uuid4().hex}.json"
    try:
        temporary_manifest.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
        if json.loads(temporary_manifest.read_text(encoding="utf-8")) != manifest:
            raise ValueError("Manifest round-trip failed")
        os.replace(temporary_manifest, manifest_path)
    finally:
        temporary_manifest.unlink(missing_ok=True)
    print(f"Prepared {len(records)} POV stills: {manifest_path}")


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--show", type=Path, default=ROOT / "tools/web-compositor/show.json")
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    args = parser.parse_args()
    try:
        prepare(args.show.resolve(), args.output.resolve())
    except (OSError, ValueError, KeyError, RuntimeError) as error:
        print(f"POV frame preparation failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
