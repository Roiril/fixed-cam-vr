"""Replace generated audio with approved narration and ingest doctor videos."""

import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile


sys.stdout.reconfigure(encoding="utf-8")
ROOT = Path(__file__).resolve().parents[2]
ASSETS = ROOT / "tablet/app/src/main/assets/web/asset"
SOURCE_SPECS = {
    "introduction": ("54e3e135785c2ca4df9cc29f65783ab6dfa980bc36054f88c38930f0aae83e9a", 390, 15.600),
    "subject-a": ("db3fd690fcfeb9be2318e849a5768676e58dc4dc8373345fc95d7b454db0c409", 206, 8.240),
    "subject-b": ("c4a89706674f99d349b95c8b58066b642c1d3b376f4343638c2cff44fd4fd3bf", 311, 12.440),
    "report": ("28e9d8cb36adc8cbdcff2aa2aa40d4218327c30e87f05d5feb40f334a995759b", 492, 19.680),
    "wear": ("f3de5a85690e360c36731f742890f4f3000fefc1202ecefe2d0e9e1fc48e755b", 237, 9.480),
}
AUDIO_SPECS = {
    "introduction": ("introduction-ja-v2.mp3", "386cb43643b5a3366431f30cbc4890eee8a4f20a46a522940e0af1e9bf8832a7", 15.638),
    "subject": ("subject-ja-v2.mp3", "053c2ce2555f9bebcfb9e5f31e1d9bc58610e264c3d4ad6e8f1773372bea44cf", 20.735),
    "report": ("report-ja-v2.mp3", "fe5d26492d6766cdbe96f9fb43d2184b83d7c3ddee16b37af2bcc9015983cb5a", 19.710),
    "wear": ("wear-ja-v2.mp3", "7046eebb09ed560383ecee913c213b073032d65860601e2d47a18e856b0408c9", 9.484),
}
OUTPUT_SPECS = {
    "introduction": ("introduction-ja-v1.mp4", "introduction-doctor-v1.jpg", 391, 15.638),
    "subject": ("subject-ja-v1.mp4", "subject-doctor-v1.jpg", 519, 20.735),
    "report": ("report-ja-v1.mp4", "report-doctor-v1.jpg", 493, 19.710),
    "wear": ("wear-ja-v1.mp4", "wear-doctor-v1.jpg", 238, 9.484),
}


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def run(*args):
    return subprocess.run(args, check=True, capture_output=True, text=True, encoding="utf-8").stdout


def probe(path, count_frames=False):
    args = ["ffprobe", "-v", "error"]
    if count_frames:
        args.append("-count_frames")
    args.extend([
        "-show_entries",
        "format=duration:stream=codec_name,codec_type,width,height,pix_fmt,r_frame_rate,sample_rate,channels,nb_read_frames,duration",
        "-of", "json", str(path),
    ])
    return json.loads(run(*args))


def require_video_source(label, path):
    expected_hash, expected_frames, expected_duration = SOURCE_SPECS[label]
    actual_hash = sha256(path)
    if actual_hash != expected_hash:
        raise SystemExit(f"unexpected {label} source SHA-256: {actual_hash}")
    info = probe(path, count_frames=True)
    video = next((stream for stream in info["streams"] if stream["codec_type"] == "video"), None)
    audio = next((stream for stream in info["streams"] if stream["codec_type"] == "audio"), None)
    if not video or (video["codec_name"], video["width"], video["height"], video["r_frame_rate"],
                     int(video["nb_read_frames"])) != ("h264", 1280, 720, "25/1", expected_frames):
        raise SystemExit(f"unexpected {label} source video format")
    if not audio or (audio["codec_name"], audio["sample_rate"], audio["channels"]) != ("aac", "48000", 1):
        raise SystemExit(f"unexpected {label} source audio format")
    if abs(float(info["format"]["duration"]) - expected_duration) > 0.002:
        raise SystemExit(f"unexpected {label} source duration")


def require_audio(label):
    filename, expected_hash, expected_duration = AUDIO_SPECS[label]
    path = ASSETS / filename
    actual_hash = sha256(path)
    if actual_hash != expected_hash:
        raise SystemExit(f"unexpected {label} narration SHA-256: {actual_hash}")
    if abs(float(probe(path)["format"]["duration"]) - expected_duration) > 0.003:
        raise SystemExit(f"unexpected {label} narration duration")
    return path


def require_faststart(path):
    data = path.read_bytes()
    moov = data.find(b"moov")
    mdat = data.find(b"mdat")
    if moov < 0 or mdat < 0 or moov > mdat:
        raise SystemExit(f"faststart metadata is missing: {path.name}")


def validate_outputs(label, video_path, image_path):
    _, _, expected_frames, expected_duration = OUTPUT_SPECS[label]
    info = probe(video_path, count_frames=True)
    video = next(stream for stream in info["streams"] if stream["codec_type"] == "video")
    audio = next(stream for stream in info["streams"] if stream["codec_type"] == "audio")
    if (video["codec_name"], video["width"], video["height"], video["pix_fmt"], video["r_frame_rate"],
            int(video["nb_read_frames"])) != ("h264", 1280, 720, "yuv420p", "25/1", expected_frames):
        raise SystemExit(f"unexpected {label} output video format")
    if (audio["codec_name"], audio["sample_rate"], audio["channels"]) != ("aac", "48000", 1):
        raise SystemExit(f"unexpected {label} output audio format")
    video_duration = expected_frames / 25
    if abs(float(video["duration"]) - video_duration) > 0.002:
        raise SystemExit(f"unexpected {label} output video duration")
    if abs(float(audio["duration"]) - expected_duration) > 0.003:
        raise SystemExit(f"unexpected {label} output audio duration")
    if abs(float(info["format"]["duration"]) - video_duration) > 0.002:
        raise SystemExit(f"unexpected {label} output container duration")
    image = probe(image_path)["streams"][0]
    if (image["codec_name"], image["width"], image["height"]) != ("mjpeg", 1280, 720):
        raise SystemExit(f"unexpected {label} output image format")
    require_faststart(video_path)


def temporary_path(suffix):
    with tempfile.NamedTemporaryFile(dir=ASSETS, suffix=suffix, delete=False) as temporary:
        return Path(temporary.name)


def render_scene(label, sources, audio, video_path, image_path):
    if label == "subject":
        filter_complex = (
            "[0:v:0]trim=end_frame=206,fps=25,settb=1/25,setpts=N/(25*TB)[v0];"
            "[1:v:0]trim=end_frame=311,fps=25,settb=1/25,setpts=N/(25*TB),"
            "tpad=stop_mode=clone:stop=2[v1];"
            "[v0][v1]concat=n=2:v=1:a=0,format=yuv420p[v]"
        )
        input_args = ["-i", str(sources[0]), "-i", str(sources[1]), "-i", str(audio)]
        mapping = ["-filter_complex", filter_complex, "-map", "[v]", "-map", "2:a:0"]
    else:
        input_args = ["-i", str(sources[0]), "-i", str(audio)]
        source_frames = SOURCE_SPECS[label][1]
        mapping = [
            "-map", "0:v:0", "-map", "1:a:0", "-vf",
            f"trim=end_frame={source_frames},setpts=PTS-STARTPTS,"
            "tpad=stop_mode=clone:stop=1,fps=25,format=yuv420p",
        ]
    run(
        "ffmpeg", "-hide_banner", "-loglevel", "error", "-y", *input_args, *mapping,
        "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p",
        "-c:a", "aac", "-b:a", "128k", "-ar", "48000", "-ac", "1",
        "-movflags", "+faststart", "-f", "mp4", str(video_path),
    )
    run(
        "ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-i", str(video_path),
        "-map", "0:v:0", "-frames:v", "1", "-q:v", "2", "-f", "image2", str(image_path),
    )
    validate_outputs(label, video_path, image_path)


def ingest(scenes):
    temporary = {}
    try:
        for label, sources in scenes:
            video_path = temporary_path(".mp4")
            image_path = temporary_path(".jpg")
            temporary[label] = (video_path, image_path)
            render_scene(label, sources, require_audio(label), video_path, image_path)
        for label, _ in scenes:
            video_name, image_name, _, _ = OUTPUT_SPECS[label]
            video_path, image_path = temporary[label]
            os.replace(video_path, ASSETS / video_name)
            os.replace(image_path, ASSETS / image_name)
    finally:
        for video_path, image_path in temporary.values():
            video_path.unlink(missing_ok=True)
            image_path.unlink(missing_ok=True)

    result = {}
    for label, _ in scenes:
        video_name, image_name, frames, duration = OUTPUT_SPECS[label]
        video_path = ASSETS / video_name
        image_path = ASSETS / image_name
        result[label] = {
            "video": {"file": video_name, "bytes": video_path.stat().st_size, "frames": frames,
                      "audioDuration": duration, "sha256": sha256(video_path)},
            "image": {"file": image_name, "bytes": image_path.stat().st_size, "sha256": sha256(image_path)},
        }
    print(json.dumps(result, ensure_ascii=False, indent=2))


def main():
    if len(sys.argv) not in (2, 6):
        raise SystemExit(
            "usage: py -3.11 tools/visitor-ui/ingest-doctor-video.py <01-rip.mp4> "
            "[<02-rip.mp4> <03-rip.mp4> <04-rip.mp4> <05-rip.mp4>]"
        )
    sources = [Path(argument).resolve(strict=True) for argument in sys.argv[1:]]
    labels = ["introduction"] if len(sources) == 1 else ["introduction", "subject-a", "subject-b", "report", "wear"]
    for label, source in zip(labels, sources):
        require_video_source(label, source)
    if len(sources) == 1:
        require_audio("introduction")
        ingest([("introduction", [sources[0]])])
        return
    for label in AUDIO_SPECS:
        require_audio(label)
    ingest([
        ("subject", [sources[1], sources[2]]),
        ("report", [sources[3]]),
        ("wear", [sources[4]]),
    ])


if __name__ == "__main__":
    main()
