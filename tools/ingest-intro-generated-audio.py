"""Extract the selected generated glass sound without changing its mix or timing."""

from __future__ import annotations

import hashlib
import subprocess
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SOURCE_PATTERN = "*20260920133909.mp4"
SOURCE_SHA256 = "4921CE7F5E2508660AC8D173D5DBBD7F2205C838F8A3D96C127A487A46816CB3"
TARGET = ROOT / "Assets/Resources/Sound/sfx_shatter.wav"


def main() -> None:
    matches = list((Path.home() / "Downloads").glob(SOURCE_PATTERN))
    if len(matches) != 1:
        raise RuntimeError(f"Expected one source matching {SOURCE_PATTERN}, found {len(matches)}")
    source = matches[0]
    if hashlib.sha256(source.read_bytes()).hexdigest().upper() != SOURCE_SHA256:
        raise RuntimeError(f"Source changed: {source.name}")
    subprocess.run(
        ["ffmpeg", "-v", "error", "-y", "-i", str(source), "-map", "0:a:0",
         "-vn", "-ar", "48000", "-ac", "2", "-c:a", "pcm_s16le", str(TARGET)],
        check=True,
    )
    print(f"{TARGET.name}: {TARGET.stat().st_size} bytes")


if __name__ == "__main__":
    main()
