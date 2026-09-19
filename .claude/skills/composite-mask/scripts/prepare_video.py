"""Recover an unmasked video from its original. Never bake a composite mask here."""
from pathlib import Path
import argparse
import hashlib
import json
import os
import subprocess
import sys

import cv2
import imageio_ffmpeg
from PIL import Image


def sha(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b''):
            h.update(chunk)
    return h.hexdigest()


def main():
    sys.stdout.reconfigure(encoding='utf-8')
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--input', required=True)
    ap.add_argument('--crop', required=True, help='x,y,width,height of content inside original')
    ap.add_argument('--size', required=True, help='width,height after crop')
    ap.add_argument('--out', required=True)
    ap.add_argument('--still', required=True, help='Actual last decoded frame, PNG')
    a = ap.parse_args()
    src, dst, still = map(lambda p: Path(p).resolve(), (a.input, a.out, a.still))
    crop = tuple(map(int, a.crop.split(',')))
    size = tuple(map(int, a.size.split(',')))
    if len(crop) != 4 or len(size) != 2 or min(crop[:2]) < 0 or min(crop[2:] + size) <= 0:
        ap.error('Invalid crop/size')
    if dst.suffix.lower() != '.mp4' or still.suffix.lower() != '.png':
        ap.error('Output must be MP4 and still PNG')
    report_path = dst.with_suffix('.source.json')
    if len({src, dst, still, report_path}) != 4:
        ap.error('Input and output paths must be distinct')
    cap = cv2.VideoCapture(str(src))
    if not cap.isOpened():
        ap.error('Cannot open source')
    sw, sh = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH)), int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
    fps = cap.get(cv2.CAP_PROP_FPS)
    count = 0
    while cap.read()[0]:
        count += 1
    cap.release()
    if count == 0 or crop[0] + crop[2] > sw or crop[1] + crop[3] > sh:
        ap.error('Empty video or crop outside source')
    dst.parent.mkdir(parents=True, exist_ok=True)
    still.parent.mkdir(parents=True, exist_ok=True)
    tmp = dst.with_name(dst.stem + '.partial.mp4')
    if tmp == src:
        ap.error('Temporary output aliases source')
    vf = f'crop={crop[2]}:{crop[3]}:{crop[0]}:{crop[1]},scale={size[0]}:{size[1]}:flags=lanczos'
    command = [imageio_ffmpeg.get_ffmpeg_exe(), '-nostdin', '-y', '-i', str(src), '-an',
               '-vf', vf, '-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-crf', '16',
               '-movflags', '+faststart', str(tmp)]
    r = subprocess.run(command, capture_output=True, text=True, encoding='utf-8', errors='replace')
    if r.returncode:
        raise RuntimeError(r.stderr[-4000:])
    decoded = cv2.VideoCapture(str(tmp))
    last = None
    actual = 0
    while True:
        ok, frame = decoded.read()
        if not ok:
            break
        last = frame
        actual += 1
    decoded.release()
    if actual != count or last is None or last.shape[:2] != (size[1], size[0]):
        raise RuntimeError(f'Frame/dimension mismatch: input={count}, output={actual}')
    stmp = still.with_name(still.stem + '.partial.png')
    Image.fromarray(cv2.cvtColor(last, cv2.COLOR_BGR2RGB)).save(stmp)
    record = {'version': 1, 'input': str(src), 'input_sha256': sha(src), 'crop_xywh': crop,
              'size': size, 'fps': fps, 'frame_count': actual, 'mask_baked': False,
              'filter': vf, 'video_sha256': sha(tmp), 'still_sha256': sha(stmp)}
    rtmp = report_path.with_suffix('.partial.json')
    rtmp.write_text(json.dumps(record, ensure_ascii=False, indent=2), encoding='utf-8', newline='\n')
    os.replace(tmp, dst)
    os.replace(stmp, still)
    os.replace(rtmp, report_path)
    print(json.dumps(record, ensure_ascii=False))


if __name__ == '__main__':
    main()
