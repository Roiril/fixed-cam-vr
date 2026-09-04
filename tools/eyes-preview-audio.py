#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""目の異変のプレビュー（`menu eyes` の連番）に重ねる **音のトラック**を作る。

    py -3.11 tools/eyes-preview-audio.py                 # Assets/Screenshots/eyes-preview/frames.tsv から
    py -3.11 tools/eyes-preview-audio.py --tsv <frames.tsv> --out logs/preview/eyes_sound.wav

`make-preview-video.py <folder> eyes --audio logs/preview/eyes_sound.wav` で絵と重ねる。

**実機と同じ規則で置く**（`AnomalyEyes.DriveEyeSfx` の写し）:
  - 大きい目の音（`sfx_eye_big`）は兆しの頭のフレームで撃ち、`SfxPlayer.ScheduleLeadSec`（0.035 秒）だけ遅れて鳴る
  - 開いた目の数が増えたフレームで一撃（`sfx_eye_1..6` を直前と同じものを引かずに）。
    ただし **0.09 秒**（`EyeSfxMinIntervalSec`）に 1 発へ間引く。音程 ±5% / 音量 ±2dB
  - 劇伴と敷く音は入れない（判定したいのは**目の動きと音の一致**なので、素の上で聴く）

⚠ 早回し（`-Set trim=`）で焼いた連番には使えない。音は詰められないので、絵とずれる。
⚠ 音は録画にも実機にも残らない。「絵と合っているか」を人が判定できる形はこれしか無い。
"""
from __future__ import annotations

import argparse
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import soundkit as sk  # noqa: E402

sys.stdout.reconfigure(encoding="utf-8")

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SND = os.path.join(ROOT, "Assets", "Resources", "Sound")
FPS = 30
LEAD_SEC = 0.035           # SfxPlayer.ScheduleLeadSec
MIN_INTERVAL = 0.09        # AnomalyEyes.EyeSfxMinIntervalSec
PITCH_SPREAD = 0.05        # AnomalyEyes.EyeSfxPitchSpread
GAIN_SPREAD_DB = 2.0       # AnomalyEyes.EyeSfxGainSpreadDb


def load(name: str) -> np.ndarray:
    y, _ = sk.read_wav(os.path.join(SND, f"{name}.wav"))
    return sk.to_stereo(y)


def shot(rng, clip: np.ndarray, pitch: float, db: float) -> np.ndarray:
    r = 1.0 + float(rng.uniform(-pitch, pitch))
    m = max(8, int(len(clip) / r))
    x = np.linspace(0, len(clip) - 1, m)
    c = np.stack([np.interp(x, np.arange(len(clip)), clip[:, ch]) for ch in (0, 1)], axis=1)
    return c * 10 ** (float(rng.uniform(-db, db)) / 20.0)


def lay(dst: np.ndarray, src: np.ndarray, at_sec: float) -> None:
    i = int(at_sec * sk.SR)
    n = min(len(src), len(dst) - i)
    if n > 0:
        dst[i:i + n] += src[:n]


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--tsv", default=os.path.join(ROOT, "Assets", "Screenshots", "eyes-preview", "frames.tsv"))
    ap.add_argument("--out", default=os.path.join(ROOT, "logs", "preview", "eyes_sound.wav"))
    a = ap.parse_args()

    rows = []
    with open(a.tsv, encoding="utf-8") as f:
        head = f.readline().rstrip("\n").split("\t")
        for line in f:
            cols = line.rstrip("\n").split("\t")
            if len(cols) < len(head):
                continue
            rows.append(dict(zip(head, cols)))
    if not rows:
        print(f"連番の台帳が空: {a.tsv}")
        return 1

    total = (len(rows) + 1) / FPS + 7.0     # 大きい目の音（6.2 秒）が最後まで入る余白
    out = np.zeros((int(total * sk.SR), 2))
    rng = np.random.default_rng(20260904)
    big = load("sfx_eye_big")
    hits = [load(f"sfx_eye_{i}") for i in range(1, 7)]

    # 兆しの頭 ＝ 台帳で最初に Hint になったフレーム。その時刻 ＋ 先読みで大きい目の音。
    hint_frame = next((r for r in rows if r["stage"] == "Hint"), rows[0])
    t_hint = float(hint_frame["sec"]) - 1.0 / FPS          # フレームの頭
    lay(out, big, max(0.0, t_hint) + LEAD_SEC)

    # 開いた目が増えたフレームで一撃（0.09 秒に 1 発へ間引く）。
    last_open = 0
    cool = 0.0
    last_pick = -1
    n_hits = 0
    prev_t = t_hint
    for r in rows:
        t = float(r["sec"])
        dt = t - prev_t
        prev_t = t
        if cool > 0:
            cool -= dt
        open_n = int(float(r["open"]))
        if open_n > last_open and cool <= 0 and r["stage"] in ("Swarm", "Hold"):
            v = int(rng.integers(0, 5))
            if v >= last_pick:
                v += 1
            last_pick = v
            lay(out, shot(rng, hits[v], PITCH_SPREAD, GAIN_SPREAD_DB), t - 1.0 / FPS + LEAD_SEC)
            cool = MIN_INTERVAL
            n_hits += 1
        if open_n != last_open:
            last_open = open_n

    os.makedirs(os.path.dirname(a.out), exist_ok=True)
    peak = float(np.max(np.abs(out)))
    if peak > 0.9:
        out = out * (0.9 / peak)
    sk.write_wav(a.out, out, peak_db=-1.0)
    print(f"{os.path.relpath(a.out, ROOT)}  大きい目 {max(0.0, t_hint) + LEAD_SEC:.3f}s / 一撃 {n_hits} 発 / {total:.1f}s")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
