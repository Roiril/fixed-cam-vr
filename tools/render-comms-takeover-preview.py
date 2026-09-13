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
    overlap = [r for r in rows if r['phase'] == 'Pursuit'
               and int(r['missing']) > 0 and int(r['drawn']) > 0]
    seized = [r for r in rows if r['phase'] == 'Seized']
    assert overlap and seized
    assert len(overlap) / FPS >= .20, 'Erasure and output did not coexist'
    growing = [r for r in overlap if int(r['hit']) > 0]
    assert len(growing) >= 2, 'The tail did not keep producing ink during erasure'
    assert len({r['text'] for r in rows}) == 1, 'A second sentence replaced the source'
    assert all(int(r['generated']) < int(r['total']) for r in rows), 'The source finished'
    cut_frame = int(seized[0]['frame'])
    after_cut = rows[cut_frame:]
    assert all(int(r['hit']) == 0 and int(r['drawn']) == 0 for r in after_cut)
    assert all(float(r['face']) == 0 for r in seized[3:]), 'The seized face returned'
    expected = int(rows[0]['expected'])
    assert sum(int(r['hit']) for r in rows) == expected
    # The source keeps one layout; only ink is deformed.
    assert max(float(r['x']) for r in rows) - min(float(r['x']) for r in rows) < .01
    assert max(float(r['y']) for r in rows) - min(float(r['y']) for r in rows) < .01
    a = np.asarray(Image.open(folder / f'f{int(seized[3]["frame"]):04d}.png')).astype(np.int16)
    b = np.asarray(Image.open(folder / f'f{int(seized[-1]["frame"]):04d}.png')).astype(np.int16)
    still_delta = float(np.abs(a - b).mean())
    assert still_delta == 0, f'The empty display should remain still: {still_delta}'
    a = np.asarray(Image.open(folder / 'output.png')).astype(np.int16)
    changed = float(np.abs(a - b).mean())
    assert changed > .01, 'The output was not removed in pixels'

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
    keystrokes = np.zeros_like(out)
    for i, r in enumerate(row for row in rows if int(row['hit'])):
        clip = clips[i % len(clips)][0]
        start = int((int(r['frame']) / FPS + .035) * sr)
        count = min(len(clip), samples - start)
        if count > 0:
            keystrokes[start:start + count] += clip[:count]
    # Runtime StopAll cuts both currently sounding and DSP-scheduled keystrokes.
    keystrokes[int(cut_frame / FPS * sr):] = 0
    out += keystrokes
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
        typed=sum(int(r['hit']) for r in rows), expected=expected,
        generated=max(int(r['generated']) for r in rows), total=int(rows[0]['total']),
        overlap_seconds=len(overlap) / FPS, new_keystrokes_during_erasure=len(growing),
        cut_seconds=cut_frame / FPS, empty_hold_seconds=len(seized) / FPS,
        empty_static_pixel_delta=still_delta, output_to_empty_pixel_delta=changed,
        video=str(video), audio_note='Existing beds. Recorded keystrokes plus 35 ms DSP delay, cut at capture. No spatial audio.')
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
