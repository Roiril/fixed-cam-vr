# -*- coding: utf-8 -*-
"""連絡の面のプレビュー（コマ）に重ねる**打鍵の音**を作る。

```
.\\tools\\unity.ps1 menu comms-preview                      # コマ ＋ type.tsv
py -3.11 tools/comms-preview-audio.py                       # → logs/preview/comms_type.wav
py -3.11 tools/make-preview-video.py Assets/Screenshots/comms-preview comms ^
        --audio logs/preview/comms_type.wav
```

⚠⚠ **鳴るコマを Python 側で数え直さない。** Unity が焼いた `type.tsv`（コマ番号・出ている字数・
その コマで打鍵が鳴ったか）をそのまま読む。数え直すと**実機と違う所で鳴る動画**ができて、
「一文字出るのに合わせて鳴っているか」という判断そのものが狂う
（`menu glitch` の `frames.tsv` と同じ流儀）。

⚠ 音は**実機と同じ選び方**で並べる — 8 種から直前と同じものを引かず、音程 ±4% / 音量 ±2dB を散らす
（`TypeAudioCue` の値と対。向こうを変えたらここも直す）。
"""
from __future__ import annotations

import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import soundkit as sk  # noqa: E402

sys.stdout.reconfigure(encoding="utf-8")

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
FRAMES = os.path.join(ROOT, "Assets", "Screenshots", "comms-preview")
SOUND = os.path.join(ROOT, "Assets", "Resources", "Sound")
OUT = os.path.join(ROOT, "logs", "preview", "comms_type.wav")

FPS = 30                # `CommsPreview.Fps` と対
VARIANTS = 8            # `TypeAudioCue.VariantCount` と対
PITCH = 0.04            # `TypeAudioCue.PitchSpread`
GAIN_DB = 2.0           # `TypeAudioCue.GainSpreadDb`
# 本編の敷く音（`rules/sound-design.md` §4 の表）。**単体で聴くと必ず大きく感じる**ので、
# 判断はこの上でしてもらう。
BED_ROOM, BED_DEVICE = 0.34, 1.0


def load(name: str) -> np.ndarray:
    y, _ = sk.read_wav(os.path.join(SOUND, f"{name}.wav"))
    return sk.to_stereo(y)


def tile(y: np.ndarray, n: int) -> np.ndarray:
    return np.tile(y, (n // len(y) + 1, 1))[:n]


def main() -> int:
    tsv = os.path.join(FRAMES, "type.tsv")
    if not os.path.exists(tsv):
        print(f"⚠ {tsv} が無い。先に `.\\tools\\unity.ps1 menu comms-preview`")
        return 1

    hits = []
    with open(tsv, encoding="utf-8") as f:
        next(f)
        for line in f:
            frame, _chars, hit = line.split("\t")
            if int(hit) == 1:
                hits.append(int(frame))
    total_frames = sum(1 for _ in open(tsv, encoding="utf-8")) - 1
    if not hits:
        print("⚠ 打鍵が 1 発も記録されていない（type.tsv の hit が全部 0）")
        return 1

    clips = [load(f"sfx_type_{i + 1}") for i in range(VARIANTS)]
    n = int((total_frames / FPS + 0.5) * sk.SR)
    out = tile(load("bed_room"), n) * BED_ROOM + tile(load("bed_device"), n) * BED_DEVICE

    rng = np.random.default_rng(20260816)
    last = -1
    for frame in hits:
        v = int(rng.integers(0, VARIANTS - 1))
        if v >= last:
            v += 1
        last = v
        c = clips[v]
        p = 1.0 + float(rng.uniform(-PITCH, PITCH))
        m = int(len(c) / p)
        c = np.stack([np.interp(np.linspace(0, len(c) - 1, m), np.arange(len(c)), c[:, ch])
                      for ch in (0, 1)], axis=1)
        g = 10 ** (float(rng.uniform(-GAIN_DB, GAIN_DB)) / 20.0)
        a = int(frame / FPS * sk.SR)
        b = min(n, a + m)
        out[a:b] += c[:b - a] * g

    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    sk.write_wav(OUT, out, peak_db=-1.0)
    print(f"  {len(hits)} 発 / {total_frames} コマ（{total_frames / FPS:.1f} 秒）→ "
          f"{os.path.relpath(OUT, ROOT)}")
    print(f"  最初の打鍵 {hits[0] / FPS:.2f}s / 最後 {hits[-1] / FPS:.2f}s / "
          f"間隔 {(hits[-1] - hits[0]) / max(len(hits) - 1, 1) / FPS * 1000:.0f}ms")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
