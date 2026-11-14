"""Cut one doll laugh (2-A rebuild, step `dollLaugh`) from the solo laugh loop without changing its level.

The 2-A cut that carries `dollLaugh: true` plays this once from `TakeRunner.BeginStep`
as `SoundCue.DollLaughOnce` (2D, no music ducking). The source is the 3rd-lap solo laugh
`bed_doll_one.wav` (48 kHz mono, 31 s loop baked by `tools/ingest-sounds.py`).

What is done (and nothing else, rules/sound-design.md §4.5):
  - keep 0.0 .. 2.4 s (one laugh phrase; the second phrase starts at 2.4 s)
  - fade the last 0.5 s with the perceptual-linear curve (`SoundFade.Curve.Perceptual`:
    gain = (1 - t) ** (1 / 0.6)), so the tail does not cut off
  - no gain, no EQ, no resampling

    py -3.11 tools/ingest-doll-laugh.py
    .\\tools\\unity.ps1 menu sound-import
"""

from __future__ import annotations

import array
import hashlib
import sys
import wave
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
# 3 周目 A・B の一人の笑い（31 秒の輪）。ingest-sounds.py が焼き直すとここが変わるので、SHA-256 で照合する。
SOURCE = ROOT / "Assets/Resources/Sound/bed_doll_one.wav"
SOURCE_SHA256 = "C2E477CAF384A86A31D3F8895A530D0FCE8D96620F8DD93DB0EE18E6C3653BB5"
TARGET = ROOT / "Assets/Resources/Sound/sfx_doll_laugh_once.wav"

RATE = 48000
CUT_SEC = 2.4   # 2.4 秒から 2 回目の笑いが始まる（0.1 秒刻みの RMS で -75dB の谷）。「一回笑う」（0376）
FADE_SEC = 0.5
# 聴感の高さと振幅の関係の指数（C# の SoundFade.LoudnessExponent と対）。
LOUDNESS_EXPONENT = 0.6


def fade_gain(t: float) -> float:
    """0..1 の進み → 退きの振幅（1 → 0）。SoundFade.Gain(1 - t, Perceptual) と同じ式。"""
    t = min(1.0, max(0.0, t))
    return (1.0 - t) ** (1.0 / LOUDNESS_EXPONENT)


def cut(samples: array.array) -> array.array:
    """頭 CUT_SEC 秒を残し、尻 FADE_SEC 秒を聴感直線で 0 へ退かせる。音量は変えない。"""
    n = int(round(CUT_SEC * RATE))
    if len(samples) < n:
        raise RuntimeError(f"source is shorter than {CUT_SEC}s: {len(samples) / RATE:.3f}s")
    out = array.array("h", samples[:n])
    fade_n = int(round(FADE_SEC * RATE))
    start = n - fade_n
    for i in range(fade_n):
        # 最後の標本で進みが 1 ＝ 振幅 0 になるように (fade_n - 1) で割る。
        g = fade_gain(i / (fade_n - 1)) if fade_n > 1 else 0.0
        v = int(round(out[start + i] * g))
        out[start + i] = max(-32768, min(32767, v))
    return out


def main() -> None:
    sys.stdout.reconfigure(encoding="utf-8")
    if not SOURCE.exists():
        raise RuntimeError(f"Source not found: {SOURCE}")
    if hashlib.sha256(SOURCE.read_bytes()).hexdigest().upper() != SOURCE_SHA256:
        raise RuntimeError(f"Source changed: {SOURCE.name} (re-measure the cut before updating SOURCE_SHA256)")
    with wave.open(str(SOURCE), "rb") as w:
        if (w.getnchannels(), w.getframerate(), w.getsampwidth()) != (1, RATE, 2):
            raise RuntimeError(f"Unexpected format: ch={w.getnchannels()} rate={w.getframerate()} "
                               f"width={w.getsampwidth()} (want mono / {RATE} Hz / 16 bit)")
        samples = array.array("h", w.readframes(w.getnframes()))
    if sys.byteorder != "little":
        samples.byteswap()
    out = cut(samples)
    if sys.byteorder != "little":
        out.byteswap()
    tmp = TARGET.with_suffix(".wav.tmp")
    with wave.open(str(tmp), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(out.tobytes())
    tmp.replace(TARGET)
    sha = hashlib.sha256(TARGET.read_bytes()).hexdigest().upper()
    print(f"{TARGET.name}: {len(out) / RATE:.3f}s / {TARGET.stat().st_size} bytes / sha256 {sha}")


if __name__ == "__main__":
    main()
