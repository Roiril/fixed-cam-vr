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
    required = {'frame', 'phase', 'reveal', 'strain', 'resistance', 'collapse',
                'face', 'missing', 'deformed', 'bottom_y', 'panel_alpha', 'hit',
                'drawn', 'generated', 'total', 'expected', 'x', 'y', 'text'}
    assert required <= rows[0].keys(), f'Missing frame columns: {sorted(required - rows[0].keys())}'
    frames = [folder / f'f{i:04d}.png' for i in range(len(rows))]
    assert all(p.is_file() for p in frames)
    pursuit = [r for r in rows if r['phase'] == 'Pursuit']
    deforming = [r for r in pursuit if float(r['strain']) > 0
                 and int(r['deformed']) > 0 and int(r['drawn']) > 0]
    resistance = [r for r in pursuit if int(r['resistance'])]
    seized = [r for r in rows if r['phase'] == 'Seized']
    assert deforming and resistance and seized
    assert all(int(r['missing']) == int(r['deformed']) for r in pursuit), \
        'CorruptedChars and TakeoverDeformedChars disagree'
    growing = [r for r in deforming if int(r['hit']) > 0]
    assert sum(int(r['hit']) for r in growing) >= 2, \
        'The tail did not keep producing ink during deformation'
    assert len({r['text'] for r in rows}) == 1, 'A second sentence replaced the source'
    assert all(int(r['generated']) < int(r['total']) for r in rows), 'The source finished'
    assert all(int(r['hit']) == 0 for r in resistance), 'Typing continued during resistance'
    resistance_generated = {int(r['generated']) for r in resistance}
    assert len(resistance_generated) == 1, 'The source advanced during resistance'
    resistance_end = int(resistance[-1]['frame'])
    resumed = [r for r in pursuit if int(r['frame']) > resistance_end and int(r['hit']) > 0]
    assert resumed, 'No new character appeared after resistance'
    assert int(resumed[0]['generated']) > next(iter(resistance_generated)), \
        'The first resumed keystroke did not add a character'
    cut_frame = int(seized[0]['frame'])
    after_cut = rows[cut_frame:]
    assert all(int(r['hit']) == 0 and int(r['drawn']) == 0 for r in after_cut)
    assert all(float(r['face']) == 0 for r in seized[3:]), 'The seized face returned'
    expected = int(rows[0]['expected'])
    typed = sum(int(r['hit']) for r in rows)
    assert typed == expected
    # The source keeps one layout; its anchor does not move during deformation.
    assert max(float(r['x']) for r in rows) - min(float(r['x']) for r in rows) < .01
    assert max(float(r['y']) for r in rows) - min(float(r['y']) for r in rows) < .01
    base_bottom = min(float(r['bottom_y']) for r in rows
                      if r['phase'] == 'Output' and int(r['drawn']) > 0)
    deformed_bottom = min(float(r['bottom_y']) for r in deforming)
    body_drop = base_bottom - deformed_bottom
    assert body_drop > .001, f'The body did not stretch downward: {body_drop}'
    resistance_a = np.asarray(Image.open(folder / 'resistance-a.png')).astype(np.int16)
    resistance_b = np.asarray(Image.open(folder / 'resistance-b.png')).astype(np.int16)
    resistance_delta = float(np.abs(resistance_a - resistance_b).mean())
    assert resistance_delta == 0, f'Resistance changed pixels: {resistance_delta}'
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
    keystrokes = np.zeros((samples, 2), np.float32)
    audio_events = 0
    for r in rows:
        for _ in range(int(r['hit'])):
            clip = clips[audio_events % len(clips)][0]
            start = int((int(r['frame']) / FPS + .035) * sr)
            count = min(len(clip), samples - start)
            if count > 0:
                keystrokes[start:start + count] += clip[:count]
            audio_events += 1
    assert audio_events == typed, 'The audio omitted recorded keystrokes'
    # Runtime StopAll cuts both currently sounding and DSP-scheduled keystrokes.
    keystrokes[int(cut_frame / FPS * sr):] = 0
    out = keystrokes
    post_capture_peak = float(np.abs(out[int(cut_frame / FPS * sr):]).max())
    assert post_capture_peak == 0, 'Type audio continued after capture'
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
        typed=typed, audio_events=audio_events, expected=expected,
        multi_hit_frames=sum(int(r['hit']) > 1 for r in rows),
        generated=max(int(r['generated']) for r in rows), total=int(rows[0]['total']),
        deformation_seconds=len(deforming) / FPS,
        keystrokes_during_deformation=sum(int(r['hit']) for r in growing),
        resistance_seconds=len(resistance) / FPS,
        resistance_keystrokes=sum(int(r['hit']) for r in resistance),
        resistance_static_pixel_delta=resistance_delta,
        resumed_keystrokes=sum(int(r['hit']) for r in resumed),
        first_resumed_frame=int(resumed[0]['frame']), body_min_y_drop=body_drop,
        cut_seconds=cut_frame / FPS, empty_hold_seconds=len(seized) / FPS,
        post_capture_audio_peak=post_capture_peak,
        empty_static_pixel_delta=still_delta, output_to_empty_pixel_delta=changed,
        video=str(video),
        audio_note='Recorded keystrokes only, including every same-frame hit. 35 ms DSP delay; cut at capture. No beds or spatial audio.')
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
