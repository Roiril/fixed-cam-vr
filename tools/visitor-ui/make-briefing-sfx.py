"""Build the short material cue used when the doctor's reference is presented.

No sampled recording or pure oscillator is used. A seeded band of noise makes
the soft contact and the short friction from the same reproducible waveform.
"""

import argparse
import hashlib
import io
import json
import os
from pathlib import Path
import sys
import tempfile
import wave

import numpy as np

sys.stdout.reconfigure(encoding="utf-8")
ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
import soundkit as sk

DESTINATION = ROOT / "tablet/app/src/main/assets/web/asset/briefing-reference-v1.wav"
RATE = 48000


def waveform():
    duration = 0.180
    t = np.arange(round(duration * RATE), dtype=np.float64) / RATE
    contact = sk.loop_noise(duration, 240, 2100, slope_db_oct=-3, seed=20261014)
    contact *= (1 - np.exp(-t / 0.0015)) * np.exp(-t / 0.014)
    friction = sk.loop_noise(duration, 500, 3200, slope_db_oct=-4, seed=20261015)
    friction *= (1 - np.exp(-t / 0.009)) * np.exp(-t / 0.042)
    body = sk.loop_noise(duration, 180, 1050, slope_db_oct=-3, seed=20261016)
    body *= (1 - np.exp(-t / 0.0025)) * np.exp(-t / 0.025)
    y = contact * 0.57 + friction * 0.24 + body * 0.19
    y *= np.minimum(1.0, (duration - t) / 0.012)
    y -= np.mean(y)
    y *= np.minimum(1.0, t / 0.001) * np.minimum(1.0, (duration - t) / 0.004)
    y *= 10 ** (-24 / 20) / max(np.max(np.abs(y)), 1e-12)
    y[0] = y[-1] = 0
    return y


def pcm_bytes(y):
    with io.BytesIO() as buffer:
        with wave.open(buffer, "wb") as output:
            output.setnchannels(1)
            output.setsampwidth(2)
            output.setframerate(RATE)
            output.writeframes(np.round(y * 32767).astype("<i2").tobytes())
        return buffer.getvalue()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="Compare the asset without writing it.")
    parser.add_argument("--report", type=Path)
    args = parser.parse_args()
    report_path = args.report.resolve() if args.report else None
    if report_path and not report_path.parent.is_dir():
        parser.error("The report directory must exist.")

    # Calibrate the tone detector before using its threshold for this asset.
    calibration_t = np.arange(RATE) / RATE
    tone = 0.1 * np.sin(2 * np.pi * 1000 * calibration_t)
    noise = sk.loop_noise(1.0, 200, 3500, seed=20261014)
    tone_value = sk.tonality_db(tone)
    noise_value = sk.tonality_db(noise)
    assert tone_value > 25 and noise_value < 25, (tone_value, noise_value)
    y = waveform()
    data = pcm_bytes(y)
    # Analyse the actual quantized samples that will be shipped.
    with wave.open(io.BytesIO(data), "rb") as source:
        quantized = np.frombuffer(source.readframes(source.getnframes()), dtype="<i2").astype(float) / 32768
    metrics = {
        "durationMs": len(quantized) / RATE * 1000,
        "sampleRate": RATE,
        "channels": 1,
        "truePeakDb": sk.true_peak_db(quantized),
        "rmsDbfs": float(20 * np.log10(np.sqrt(np.mean(quantized * quantized)))),
        "tonalityDb": sk.tonality_db(quantized),
        "tonePurity": sk.tone_purity(quantized),
        "sharpness": sk.sharpness(quantized),
        "roughness": sk.roughness(quantized),
        "dc": float(abs(np.mean(quantized))),
        "sha256": hashlib.sha256(data).hexdigest(),
        "calibration": {"toneDb": tone_value, "noiseDb": noise_value},
    }
    assert metrics["truePeakDb"] <= -20
    assert metrics["tonalityDb"] <= 25
    assert metrics["tonePurity"] <= 0.60
    assert metrics["sharpness"] <= 3.5
    assert metrics["roughness"] <= 1.0
    assert metrics["dc"] <= 0.002
    if args.check:
        assert DESTINATION.read_bytes() == data, "The bundled cue differs from the recipe."
    else:
        with tempfile.NamedTemporaryFile(dir=DESTINATION.parent, suffix=".tmp", delete=False) as temporary:
            temporary.write(data)
            temporary_path = Path(temporary.name)
        os.replace(temporary_path, DESTINATION)
    if report_path:
        report_path.write_text(json.dumps(metrics, ensure_ascii=False, indent=2), encoding="utf-8", newline="\n")
    print(json.dumps(metrics, ensure_ascii=False))


if __name__ == "__main__":
    main()
