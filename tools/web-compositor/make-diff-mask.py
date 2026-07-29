# 種フレームと生成画像の差分から、cue の差し替えマスク（R チャンネル・白=差し替え）を作る。
#
#   生成画像は「同じ構図のまま何かを足した」ものなので、種フレームとの差がそのまま
#   差し替えたい領域になる。素材工房（ブラウザ）の差分マスクと同じ考え方を CLI へ出したもの。
#   卓を開かずに素材を仕込めるようにするため（authoring/ から呼ぶ）。
#
#   使い方:
#     python make-diff-mask.py --base captures/seed.jpg --gen captures/gen.png \
#         --out masks/cue_x.png [--threshold 30] [--feather 6] [--dilate 3] [--min-area 400]
#
#   ⚠ マスクは**そのカメラの構図**に対して焼かれる。別カメラの素材には使えない。

import argparse
import os
import sys

import numpy as np
from PIL import Image, ImageFilter


def largest_blob(mask: np.ndarray, min_area: int) -> np.ndarray:
    """4 近傍の連結成分のうち面積最大のものだけ残す（生成のゆらぎで出る細かい斑を落とす）。"""
    h, w = mask.shape
    labels = np.zeros((h, w), dtype=np.int32)
    best_label, best_area = 0, 0
    cur = 0
    for sy in range(h):
        for sx in range(w):
            if not mask[sy, sx] or labels[sy, sx]:
                continue
            cur += 1
            area = 0
            stack = [(sy, sx)]
            labels[sy, sx] = cur
            while stack:
                y, x = stack.pop()
                area += 1
                for ny, nx in ((y - 1, x), (y + 1, x), (y, x - 1), (y, x + 1)):
                    if 0 <= ny < h and 0 <= nx < w and mask[ny, nx] and not labels[ny, nx]:
                        labels[ny, nx] = cur
                        stack.append((ny, nx))
            if area > best_area:
                best_area, best_label = area, cur
    if best_area < min_area:
        return mask
    return labels == best_label


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument('--base', required=True, help='種フレーム（生映像から撮ったもの）')
    ap.add_argument('--gen', required=True, help='生成画像（同じ構図に何かを足したもの）')
    ap.add_argument('--out', required=True, help='書き出すマスク PNG')
    ap.add_argument('--threshold', type=int, default=30, help='この差（0-255）を超えたら差し替え領域')
    ap.add_argument('--dilate', type=int, default=3, help='領域を広げる画素（縁の欠けを埋める）')
    ap.add_argument('--feather', type=int, default=6, help='縁のぼかし（px）。継ぎ目を隠す')
    ap.add_argument('--min-area', type=int, default=400, help='最大領域がこれ未満なら分離をやめる')
    ap.add_argument('--keep-all', action='store_true', help='最大領域だけに絞らない')
    ap.add_argument('--roi', default='', help='この矩形の中だけを差し替える（x0,y0,x1,y1 の比率 0..1）。'
                                             '生成のゆらぎで背景まで差し替わるのを防ぐ')
    a = ap.parse_args()

    base = Image.open(a.base).convert('RGB')
    gen = Image.open(a.gen).convert('RGB')
    if gen.size != base.size:
        gen = gen.resize(base.size, Image.LANCZOS)

    d = np.abs(np.asarray(base).astype(np.int16) - np.asarray(gen).astype(np.int16)).max(axis=2)
    mask = d > a.threshold
    if a.roi:
        try:
            x0, y0, x1, y1 = (float(v) for v in a.roi.split(','))
        except ValueError:
            print('--roi は x0,y0,x1,y1（比率）で指定します', file=sys.stderr)
            return 2
        h, w = mask.shape
        keep = np.zeros_like(mask)
        keep[int(y0 * h):int(y1 * h), int(x0 * w):int(x1 * w)] = True
        mask &= keep
    if not mask.any():
        print('差が見つかりません（threshold を下げてください）', file=sys.stderr)
        return 2
    if not a.keep_all:
        mask = largest_blob(mask, a.min_area)

    img = Image.fromarray((mask * 255).astype(np.uint8), mode='L')
    if a.dilate > 0:
        img = img.filter(ImageFilter.MaxFilter(a.dilate * 2 + 1))
    if a.feather > 0:
        img = img.filter(ImageFilter.GaussianBlur(a.feather))

    os.makedirs(os.path.dirname(os.path.abspath(a.out)), exist_ok=True)
    # 実機は R チャンネルだけを見る。グレースケールのまま RGB へ複製して保存する。
    Image.merge('RGB', (img, img, img)).save(a.out)
    cover = float((np.asarray(img) > 127).mean())
    print(f'ok out={a.out} size={img.size[0]}x{img.size[1]} coverage={cover * 100:.1f}%')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
