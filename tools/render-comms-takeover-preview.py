#!/usr/bin/env python3
"""Package Unity-rendered takeover frames with the recorded keystrokes. No invented event timing."""
from __future__ import annotations

import csv
import json
from pathlib import Path
import subprocess
import sys
import wave

import imageio_ffmpeg
import numpy as np
from PIL import Image

sys.stdout.reconfigure(encoding='utf-8')
ROOT = Path(__file__).resolve().parent.parent
OUTPUT = ROOT / 'Logs/comms-takeover-20260914'
FPS = 30


def wav_read(path):
    with wave.open(str(path), 'rb') as f:
        assert f.getsampwidth() == 2, path
        sr, channels = f.getframerate(), f.getnchannels()
        a = np.frombuffer(f.readframes(f.getnframes()), dtype='<i2').astype(np.float32) / 32768
    a = a.reshape(-1, channels)
    if channels == 1:
        a = np.repeat(a, 2, axis=1)
    assert channels in (1, 2)
    return a, sr


def render_language(folder):
    with (folder / 'frames.tsv').open(encoding='utf-8-sig', newline='') as f:
        rows = list(csv.DictReader(f, delimiter='\t'))
    assert rows and [int(r['frame']) for r in rows] == list(range(len(rows)))
    frames = [folder / f'f{i:04d}.png' for i in range(len(rows))]
    assert all(p.is_file() for p in frames)
    truth = [r for r in rows if r['phase'] == 'Truth' and int(r['full']) > 0]
    hold = [r for r in rows if r['phase'] == 'LieHold']
    assert truth and hold
    truth_glyphs = max(int(r['full']) for r in truth)
    readable = [r for r in truth if int(r['full']) == truth_glyphs]
    assert len(readable) / FPS >= 1.7, 'Truth was not held long enough to read'
    assert len(hold) / FPS >= 2.1, 'Lie was not held long enough to read'
    assert all(int(r['missing']) == 0 and float(r['lie']) >= .999 for r in hold)
    assert all(int(r['full']) == int(r['drawn']) and int(r['full']) > 0 for r in hold)
    assert all(float(r['face']) >= .99 for r in hold), 'The doll did not remain'
    assert all(int(r['hit']) == 0 for r in rows if r['phase'] not in ('Off', 'Truth'))
    assert sum(int(r['hit']) for r in rows) == truth_glyphs
    # Same TMP position must survive the wording change.
    assert max(float(r['x']) for r in rows) - min(float(r['x']) for r in rows) < .01
    assert max(float(r['y']) for r in rows) - min(float(r['y']) for r in rows) < .01
    a = np.asarray(Image.open(folder / 'lie-readable.png')).astype(np.int16)
    b = np.asarray(Image.open(folder / 'lie-still.png')).astype(np.int16)
    still_delta = float(np.abs(a - b).mean())
    assert still_delta == 0, f'Lie should remain still: {still_delta}'
    a = np.asarray(Image.open(folder / 'truth-readable.png')).astype(np.int16)
    changed = float(np.abs(a - b).mean())
    assert changed > .01, 'Text content changed in state but not in pixels'

    sounds = ROOT / 'Assets/Resources/Sound'
    clips = [wav_read(sounds / f'sfx_type_{i}.wav') for i in range(1, 9)]
    sr = clips[0][1]
    assert all(rate == sr for _, rate in clips)
    samples = int(len(rows) / FPS * sr)
    out = np.zeros((samples, 2), np.float32)
    # Existing room and device beds. Only keystrokes stop during concealment.
    for name, gain in [('bed_room', .34), ('bed_device', 1.0)]:
        bed, rate = wav_read(sounds / f'{name}.wav')
        assert rate == sr
        out += np.tile(bed, (samples // len(bed) + 1, 1))[:samples] * gain
    for i, r in enumerate(row for row in rows if int(row['hit'])):
        clip = clips[i % len(clips)][0]
        start = int((int(r['frame']) / FPS + .035) * sr)
        count = min(len(clip), samples - start)
        if count > 0:
            out[start:start + count] += clip[:count]
    assert np.abs(out).max() <= 1, 'Preview audio would clip'
    audio = folder / 'type.wav'
    with wave.open(str(audio), 'wb') as f:
        f.setnchannels(2)
        f.setsampwidth(2)
        f.setframerate(sr)
        f.writeframes(np.round(out * 32767).astype('<i2').tobytes())
    video = folder / 'takeover.mp4'
    subprocess.run([imageio_ffmpeg.get_ffmpeg_exe(), '-y', '-v', 'error',
        '-framerate', str(FPS), '-i', str(folder / 'f%04d.png'), '-i', str(audio),
        '-c:v', 'libx264', '-crf', '18', '-pix_fmt', 'yuv420p', '-c:a', 'aac',
        '-b:a', '192k', '-shortest', '-movflags', '+faststart', str(video)], check=True)
    result = dict(language=folder.name, frames=len(rows), seconds=len(rows) / FPS,
        typed=sum(int(r['hit']) for r in rows), expected=truth_glyphs,
        truth_readable_seconds=len(readable) / FPS, lie_hold_seconds=len(hold) / FPS,
        lie_static_pixel_delta=still_delta, truth_to_lie_pixel_delta=changed,
        video=str(video), audio_note='Existing beds. Recorded keystrokes plus 35 ms DSP delay. No spatial audio.')
    (folder / 'evidence.json').write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    return result


def main():
    folder = Path((OUTPUT / 'latest-render.txt').read_text(encoding='utf-8').strip()).resolve()
    assert folder.is_relative_to(OUTPUT.resolve())
    results = [render_language(folder / lang) for lang in ('ja', 'en', 'fr')]
    (folder / 'evidence.json').write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps(results, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
