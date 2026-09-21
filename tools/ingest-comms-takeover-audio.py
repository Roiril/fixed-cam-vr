# -*- coding: utf-8 -*-
"""2026-09-21 に受け取った動画 2 本の音だけを、乗っ取りの時計に切って取り込む。

生成音の声色を変えない。切り出し、モノ化、音量、端の短いフェードだけ。
動画は Downloads に保持し、出力の WAV はリポジトリで保存する。
"""

from __future__ import annotations

import hashlib
import subprocess
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
DOWNLOADS = Path.home() / "Downloads"
OUT = ROOT / "Assets/Resources/Sound"
SOURCES = {
    "terminal": ("*20260921175231.mp4", "4D81A7AB0EFDC85723BE135D0E6F7A3CFD9D2E04EFA2BAA69360E9B1B4A79289"),
    "agent": ("*20260921175339.mp4", "515C4F97C96FDEBD83E9F084773518E153E35F7BE7C756ECB7575B840DE06467"),
}


def source(key: str) -> Path:
    pattern, expected_hash = SOURCES[key]
    matches = list(DOWNLOADS.glob(pattern))
    if len(matches) != 1:
        raise RuntimeError(f"{pattern}: expected one source, found {len(matches)}")
    path = matches[0]
    digest = hashlib.sha256(path.read_bytes()).hexdigest().upper()
    if digest != expected_hash:
        raise RuntimeError(f"Source changed: {path.name}")
    return path


def extract(path: Path, name: str, at: float, duration: float, gain_db: int) -> None:
    # Three pieces preserve source-relative timing. The second terminal piece is
    # started at the runtime fillStartSec; long translations delay both sounds.
    fade_out = max(0, duration - 0.04)
    filters = ("pan=mono|c0=0.5*c0+0.5*c1,"
               f"volume={gain_db}dB,afade=t=in:st=0:d=0.005,"
               f"afade=t=out:st={fade_out:.3f}:d=0.04")
    target = OUT / f"{name}.wav"
    subprocess.run(["ffmpeg", "-v", "error", "-y", "-ss", str(at), "-i", str(path),
                    "-t", str(duration), "-vn", "-af", filters, "-ar", "48000",
                    "-c:a", "pcm_s16le", str(target)], check=True)
    print(f"{target.name}: {duration:.2f}s, {gain_db:+d}dB, {target.stat().st_size} bytes")


if __name__ == "__main__":
    OUT.mkdir(parents=True, exist_ok=True)
    terminal = source("terminal")
    agent = source("agent")
    extract(terminal, "sfx_comms_alert", 0, 4.17, 10)
    extract(terminal, "sfx_comms_block", 4.17, 2.23, 10)
    extract(agent, "sfx_comms_sweep", 4.17, 1.50, 12)
