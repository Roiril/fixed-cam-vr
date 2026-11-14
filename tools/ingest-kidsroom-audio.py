"""Extract the adopted kids-room video sound (canon/LEDGER.md 0374) without changing its mix or timing.

The 2-B "other place" cut (cue `backrooms_B_in`) plays the video with its audio muted
(`ScreenOverlayController` sets `audioOutputMode = None`). The user approved the sound heard in Flow,
so the same audio is decoded here and played by `TakeRunner` as `SoundCue.KidsRoom`.
"""

from __future__ import annotations

import hashlib
import subprocess
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "tools/web-compositor/captures/kidsroomB_noeyes_20261114.mp4"
SOURCE_SHA256 = "408AF41BC73CBBAED8950C7C3788A77EE88D928058EE9F22BCEB33656D998FBD"
TARGET = ROOT / "Assets/Resources/Sound/sfx_kidsroom.wav"


def main() -> None:
    if not SOURCE.exists():
        raise RuntimeError(f"Source not found: {SOURCE}")
    if hashlib.sha256(SOURCE.read_bytes()).hexdigest().upper() != SOURCE_SHA256:
        raise RuntimeError(f"Source changed: {SOURCE.name}")
    subprocess.run(
        ["ffmpeg", "-v", "error", "-y", "-i", str(SOURCE), "-map", "0:a:0",
         "-vn", "-ar", "48000", "-ac", "2", "-c:a", "pcm_s16le", str(TARGET)],
        check=True,
    )
    print(f"{TARGET.name}: {TARGET.stat().st_size} bytes")


if __name__ == "__main__":
    main()
