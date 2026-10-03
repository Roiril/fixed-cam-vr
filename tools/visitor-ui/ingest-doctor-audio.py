"""Split the user-approved ElevenLabs take at measured silent gaps."""
import hashlib
import json
from pathlib import Path
import subprocess
import sys

sys.stdout.reconfigure(encoding="utf-8")
ROOT = Path(__file__).resolve().parents[2]
SOURCE = Path(__file__).with_name("doctor-ja-v2-original.mp3")
SOURCE_SHA256 = "16C072929E29CCA5B583B30BFD871CBB47639DD3C4A7D62453E571B85888D560"
ASSETS = ROOT / "tablet/app/src/main/assets/web/asset"

# Boundaries are midpoints of >=1 s quiet gaps measured with ffmpeg silencedetect
# at -35 dB. All times are milliseconds in the original 65.567 s recording.
SCENES = (
    ("introduction", 0, 15638),
    ("subject", 15638, 36373),
    ("report", 36373, 56083),
    ("wear", 56083, 65567),
)


def run(*args):
    result = subprocess.run(args, check=True, capture_output=True, text=True, encoding="utf-8")
    return result.stdout


def duration_ms(path):
    result = json.loads(run("ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "json", str(path)))
    return round(float(result["format"]["duration"]) * 1000)


def main():
    actual_hash = hashlib.sha256(SOURCE.read_bytes()).hexdigest().upper()
    if actual_hash != SOURCE_SHA256:
        raise ValueError(f"unexpected source hash: {actual_hash}")
    if abs(duration_ms(SOURCE) - SCENES[-1][2]) > 2:
        raise ValueError("source duration changed")
    outputs = {}
    for scene_id, start_ms, end_ms in SCENES:
        target = ASSETS / f"{scene_id}-ja-v2.mp3"
        temporary = target.with_name(target.name + ".tmp")
        filter_chain = f"atrim=start={start_ms / 1000:.3f}:end={end_ms / 1000:.3f},asetpts=PTS-STARTPTS"
        run("ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-i", str(SOURCE),
            "-af", filter_chain, "-ac", "1", "-ar", "48000", "-codec:a", "libmp3lame",
            "-b:a", "128k", "-f", "mp3", str(temporary))
        temporary.replace(target)
        measured_ms = duration_ms(target)
        if abs(measured_ms - (end_ms - start_ms)) > 50:
            raise ValueError(f"unexpected segment duration: {target.name}: {measured_ms}")
        outputs[scene_id] = {"file": target.name, "durationMs": measured_ms,
                             "sha256": hashlib.sha256(target.read_bytes()).hexdigest().upper()}
    print(json.dumps(outputs, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
