"""Replace generated audio with the approved narration and ingest the doctor video."""

import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile


sys.stdout.reconfigure(encoding="utf-8")
ROOT = Path(__file__).resolve().parents[2]
ASSETS = ROOT / "Assets/Resources/Visitor"
AUDIO = ASSETS / "introduction-ja-v2.mp3.bytes"
VIDEO_TARGET = ASSETS / "introduction-ja-v1.mp4.bytes"
IMAGE_TARGET = ASSETS / "introduction-doctor-v1.jpg.bytes"
SOURCE_SHA256 = "54e3e135785c2ca4df9cc29f65783ab6dfa980bc36054f88c38930f0aae83e9a"
AUDIO_SHA256 = "386cb43643b5a3366431f30cbc4890eee8a4f20a46a522940e0af1e9bf8832a7"
TARGET_DURATION = 15.638


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def run(*args):
    return subprocess.run(args, check=True, capture_output=True, text=True, encoding="utf-8").stdout


def probe(path):
    return json.loads(run(
        "ffprobe", "-v", "error", "-show_entries",
        "format=duration:stream=codec_name,codec_type,width,height,pix_fmt,r_frame_rate,sample_rate,channels",
        "-of", "json", str(path),
    ))


def require_source(path):
    if sha256(path) != SOURCE_SHA256:
        raise SystemExit(f"unexpected source SHA-256: {sha256(path)}")
    info = probe(path)
    streams = info["streams"]
    video = next((stream for stream in streams if stream["codec_type"] == "video"), None)
    audio = next((stream for stream in streams if stream["codec_type"] == "audio"), None)
    if not video or (video["codec_name"], video["width"], video["height"], video["r_frame_rate"]) != (
            "h264", 1280, 720, "25/1"):
        raise SystemExit("unexpected source video format")
    if not audio or audio["codec_name"] != "aac":
        raise SystemExit("unexpected source audio format")
    if abs(float(info["format"]["duration"]) - 15.600) > 0.002:
        raise SystemExit("unexpected source duration")


def require_audio():
    if sha256(AUDIO) != AUDIO_SHA256:
        raise SystemExit(f"unexpected narration SHA-256: {sha256(AUDIO)}")
    if abs(float(probe(AUDIO)["format"]["duration"]) - 15.638021) > 0.002:
        raise SystemExit("unexpected narration duration")


def validate_outputs(video_path, image_path):
    info = probe(video_path)
    video = next(stream for stream in info["streams"] if stream["codec_type"] == "video")
    audio = next(stream for stream in info["streams"] if stream["codec_type"] == "audio")
    if (video["codec_name"], video["width"], video["height"], video["pix_fmt"], video["r_frame_rate"]) != (
            "h264", 1280, 720, "yuv420p", "25/1"):
        raise SystemExit("unexpected output video format")
    if (audio["codec_name"], audio["sample_rate"], audio["channels"]) != ("aac", "48000", 1):
        raise SystemExit("unexpected output audio format")
    if abs(float(info["format"]["duration"]) - 15.640) > 0.002:
        raise SystemExit("unexpected output duration")
    image = probe(image_path)["streams"][0]
    if (image["codec_name"], image["width"], image["height"]) != ("mjpeg", 1280, 720):
        raise SystemExit("unexpected output image format")


def main():
    if len(sys.argv) != 2:
        raise SystemExit("usage: py -3.11 tools/visitor-ui/ingest-doctor-video.py <09-ripsync.mp4>")
    source = Path(sys.argv[1]).resolve(strict=True)
    require_source(source)
    require_audio()

    with tempfile.NamedTemporaryFile(dir=ASSETS, suffix=".mp4", delete=False) as video_file:
        temporary_video = Path(video_file.name)
    with tempfile.NamedTemporaryFile(dir=ASSETS, suffix=".jpg", delete=False) as image_file:
        temporary_image = Path(image_file.name)
    try:
        run(
            "ffmpeg", "-hide_banner", "-loglevel", "error", "-y",
            "-i", str(source), "-i", str(AUDIO), "-map", "0:v:0", "-map", "1:a:0",
            "-vf", "tpad=stop_mode=clone:stop_duration=0.04,fps=25,format=yuv420p",
            "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "128k", "-ar", "48000", "-ac", "1",
            "-t", f"{TARGET_DURATION:.3f}", "-movflags", "+faststart", "-f", "mp4",
            str(temporary_video),
        )
        run(
            "ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-i", str(temporary_video),
            "-map", "0:v:0", "-frames:v", "1", "-q:v", "2", "-f", "image2", str(temporary_image),
        )
        validate_outputs(temporary_video, temporary_image)
        os.replace(temporary_video, VIDEO_TARGET)
        os.replace(temporary_image, IMAGE_TARGET)
    finally:
        temporary_video.unlink(missing_ok=True)
        temporary_image.unlink(missing_ok=True)

    result = {
        "video": {"file": VIDEO_TARGET.name, "bytes": VIDEO_TARGET.stat().st_size, "sha256": sha256(VIDEO_TARGET)},
        "image": {"file": IMAGE_TARGET.name, "bytes": IMAGE_TARGET.stat().st_size, "sha256": sha256(IMAGE_TARGET)},
    }
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
