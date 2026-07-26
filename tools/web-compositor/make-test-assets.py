#!/usr/bin/env python3
"""動作確認用のダミー素材（testassets/）を生成する。

「演出が本当に差し替わったか」だけを見るための素材。中身は文字と時計だけで、
実素材（captures/ recordings/）と一目で区別できる。実機検証で
「素材が古い / 何が出ているか分からない」を潰すために置く。

    python tools/web-compositor/make-test-assets.py

生成物（640x480 = 配信と同じ 4:3）:
    test_still_A/B/C.png   … 「演出 A」等の文字だけの静止画（カメラ別配色）
    test_still_full.png    … 全面差し替え確認用（白地に大きく「全面差し替え」）
    test_clip_A/B/C.mp4    … 上と同配色の 6 秒動画。経過秒カウント + 進捗バー付き
                             （静止画と見分けがつき、止まっていれば一目で分かる）

動画は H.264 / yuv420p / 音声なし（Quest の VideoPlayer が最も確実に再生できる形）。
ffmpeg は imageio-ffmpeg 同梱のものを使う（別途インストール不要）。
"""

import os
import subprocess
import sys

from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(ROOT, 'testassets')
W, H = 640, 480
FPS = 30
DUR_SEC = 6

# (サフィックス, 見出し, 背景色, 文字色) — 配色は卓の CAM_COLORS 系に寄せる
VARIANTS = [
    ('A', '演出 A', (26, 46, 38), (122, 233, 176)),
    ('B', '演出 B', (24, 38, 56), (122, 186, 255)),
    ('C', '演出 C', (54, 38, 22), (255, 190, 120)),
]

FONT_CANDIDATES = [
    os.path.join(ROOT, '..', '..', 'Assets', 'Art', 'Fonts', 'SourceHanSansJP-Normal.otf'),
    r'C:\Windows\Fonts\meiryo.ttc',
    r'C:\Windows\Fonts\YuGothM.ttc',
    r'C:\Windows\Fonts\msgothic.ttc',
]


def font(size):
    for p in FONT_CANDIDATES:
        if os.path.isfile(p):
            try:
                return ImageFont.truetype(p, size)
            except OSError:
                continue
    return ImageFont.load_default()


def centered(draw, y, text, f, fill):
    box = draw.textbbox((0, 0), text, font=f)
    draw.text(((W - (box[2] - box[0])) / 2 - box[0], y), text, font=f, fill=fill)


def frame(title, bg, fg, sub=None, progress=None):
    im = Image.new('RGB', (W, H), bg)
    d = ImageDraw.Draw(im)
    d.rectangle([8, 8, W - 9, H - 9], outline=fg, width=3)
    centered(d, 150, title, font(96), fg)
    if sub:
        centered(d, 280, sub, font(36), fg)
    if progress is not None:
        x0, x1, y0, y1 = 60, W - 60, H - 90, H - 66
        d.rectangle([x0, y0, x1, y1], outline=fg, width=2)
        d.rectangle([x0 + 3, y0 + 3, x0 + 3 + (x1 - x0 - 6) * progress, y1 - 3], fill=fg)
    centered(d, H - 46, 'テスト素材（testassets）', font(22), fg)
    return im


def write_video(path, title, bg, fg):
    from imageio_ffmpeg import get_ffmpeg_exe
    n = FPS * DUR_SEC
    cmd = [get_ffmpeg_exe(), '-y', '-loglevel', 'error',
           '-f', 'rawvideo', '-pix_fmt', 'rgb24', '-s', f'{W}x{H}', '-r', str(FPS), '-i', '-',
           '-an', '-c:v', 'libx264', '-preset', 'veryfast', '-crf', '23',
           '-pix_fmt', 'yuv420p', '-movflags', '+faststart', path]
    p = subprocess.Popen(cmd, stdin=subprocess.PIPE)
    for i in range(n):
        t = i / FPS
        im = frame(title, bg, fg, sub=f'{t:4.1f} 秒 / {DUR_SEC} 秒', progress=i / (n - 1))
        p.stdin.write(im.tobytes())
    p.stdin.close()
    if p.wait() != 0:
        raise RuntimeError(f'ffmpeg 失敗: {path}')


def main():
    os.makedirs(OUT, exist_ok=True)
    made = []
    for suffix, title, bg, fg in VARIANTS:
        sp = os.path.join(OUT, f'test_still_{suffix}.png')
        frame(title, bg, fg, sub='静止画').save(sp)
        made.append(sp)
        vp = os.path.join(OUT, f'test_clip_{suffix}.mp4')
        write_video(vp, title, bg, fg)
        made.append(vp)
    fp = os.path.join(OUT, 'test_still_full.png')
    frame('全面差し替え', (245, 245, 245), (20, 20, 20), sub='マスク無し = 画面全部が変わる').save(fp)
    made.append(fp)
    for p in made:
        print(f'  {os.path.basename(p):24s} {os.path.getsize(p) / 1024:8.1f} KB')
    print(f'{len(made)} 個を {OUT} に生成しました。')


if __name__ == '__main__':
    sys.exit(main())
