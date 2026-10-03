"""Package the site's doctor image and approved narration for a 20-second avatar UI."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys
from zipfile import ZipFile, ZIP_DEFLATED

sys.stdout.reconfigure(encoding="utf-8")
ROOT = Path(__file__).resolve().parents[2]
HERE = Path(__file__).resolve().parent
ASSETS = ROOT / "Assets/Resources/Visitor"
DEFAULT_OUTPUT = ROOT / "output/visitor-lipsync-kit-20261003"
SOURCES = {
    "doctor.jpg.bytes": "9DF0EF6867195FD105183FC120D4E9F680DB147FC24EBDE1488D2B753E0EBA23",
    "introduction-ja-v2.mp3.bytes": "386CB43643B5A3366431F30CBC4890EEE8A4F20A46A522940E0AF1E9BF8832A7",
    "subject-ja-v2.mp3.bytes": "053C2CE2555F9BEBCFB9E5F31E1D9BC58610E264C3D4AD6E8F1773372BEA44CF",
    "report-ja-v2.mp3.bytes": "FE5D26492D6766CDBE96F9FB43D2184B83D7C3DDEE16B37AF2BCC9015983CB5A",
    "wear-ja-v2.mp3.bytes": "7046EEBB09ED560383ECEE913C213B073032D65860601E2D47A18E856B0408C9",
}
ORIGINAL_SHA256 = "16C072929E29CCA5B583B30BFD871CBB47639DD3C4A7D62453E571B85888D560"


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def duration_ms(path):
    result = subprocess.run(
        ["ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "json", str(path)],
        check=True, capture_output=True, text=True, encoding="utf-8",
    )
    return round(float(json.loads(result.stdout)["format"]["duration"]) * 1000)


def copy_verified(source, target, expected_hash):
    if sha256(source) != expected_hash:
        raise ValueError(f"source changed: {source}")
    shutil.copyfile(source, target)
    if sha256(target) != expected_hash:
        raise ValueError(f"copy changed: {target}")


def cut_original(source, target, start_ms, end_ms):
    if target.exists():
        raise FileExistsError(target)
    filt = f"atrim=start={start_ms / 1000:.3f}:end={end_ms / 1000:.3f},asetpts=PTS-STARTPTS"
    subprocess.run([
        "ffmpeg", "-hide_banner", "-loglevel", "error", "-i", str(source),
        "-af", filt, "-ac", "1", "-ar", "48000", "-codec:a", "libmp3lame",
        "-b:a", "128k", str(target),
    ], check=True)
    if abs(duration_ms(target) - (end_ms - start_ms)) > 50:
        raise ValueError(f"unexpected duration: {target}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    args = parser.parse_args()
    output = args.output if args.output.is_absolute() else ROOT / args.output
    original = HERE / "doctor-ja-v2-original.mp3"
    if sha256(original) != ORIGINAL_SHA256:
        raise ValueError("doctor audio changed")
    for filename, expected in SOURCES.items():
        if sha256(ASSETS / filename) != expected:
            raise ValueError(f"site asset changed: {filename}")
    if output.exists() and any(output.iterdir()):
        raise FileExistsError(f"preserving existing kit: {output}")
    output.mkdir(parents=True, exist_ok=True)
    copy_verified(ASSETS / "doctor.jpg.bytes", output / "doctor-site.jpg", SOURCES["doctor.jpg.bytes"])
    for source, target in (
        ("introduction-ja-v2.mp3.bytes", "01_introduction.mp3"),
        ("report-ja-v2.mp3.bytes", "04_report.mp3"),
        ("wear-ja-v2.mp3.bytes", "05_wear.mp3"),
    ):
        copy_verified(ASSETS / source, output / target, SOURCES[source])
    cut_original(original, output / "02_subject_a.mp3", 15638, 23897)
    cut_original(original, output / "03_subject_b.mp3", 23897, 36373)
    shutil.copyfile(HERE / "lipsync-kit.md", output / "README.md")
    files = sorted(path for path in output.iterdir() if path.is_file())
    archive = output / "doctor-lipsync-input.zip"
    with ZipFile(archive, "w", compression=ZIP_DEFLATED) as zip_file:
        for path in files:
            zip_file.write(path, arcname=path.name)
    for path in files:
        print(json.dumps({"name": path.name, "bytes": path.stat().st_size,
                          "durationMs": duration_ms(path) if path.suffix == ".mp3" else None,
                          "sha256": sha256(path)}, ensure_ascii=False))
    print(f"archive: {archive}")


if __name__ == "__main__":
    main()
