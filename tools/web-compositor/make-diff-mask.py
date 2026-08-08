# 種フレームと生成画像の差分から、cue の差し替えマスク（R チャンネル・白=差し替え）を作る。
#
#   生成画像は「同じ構図のまま何かを足した」ものなので、種フレームとの差がそのまま
#   差し替えたい領域になる。素材工房（ブラウザ）の差分マスクと同じ考え方を CLI へ出したもの。
#   卓を開かずに素材を仕込めるようにするため（authoring/ から呼ぶ）。
#
#   使い方:
#     py -3.11 make-diff-mask.py --base captures/seed.jpg --gen captures/gen.png \
#         --out masks/cue_x.png [--threshold 30] [--feather 6] [--dilate 3] [--min-area 400]
#
#   ⚠ マスクは**そのカメラの構図**に対して焼かれる。別カメラの素材には使えない。
#
#   ⚠ 領域の内側の穴は既定で埋める（`--no-fill-holes` で切れる）。生成画像の中に種フレームと
#   たまたま色が近い画素があると黒く抜け、合成でそこだけライブが透ける（人形の顔に穴が空く）。
#
#   ⚠ 生成のゆらぎで背景まで差が出ることがある。カバー率が対象の面積より明らかに大きければ
#   `--roi` で領域を絞る（2026-08-05 実測: 壁の前に人形を足した 1 枚が、カーテンの皺の描き直しで
#   42% を覆った。roi で絞ると 14%）。
#
#   ⚠ 出力は **スクリーン枠空間（16:9）**。差分は種フレームの画素座標（ふつう 4:3）で取れるので、
#   実機で素材が置かれる矩形（シェーダの `_OverlayScale` と同じ contain-fit）へ収めてから書き出す。
#   実機はマスクだけ contain-fit を通さず生 uv で読む（`_MaskScale` は無い）ため、ソース座標のまま
#   焼くと水平 1.33 倍・枠幅の最大 12.5% 外側にずれる。common.js の MW/MH と対で、片方だけ
#   変えると沈黙して食い違う。

import argparse
import os
import sys

import numpy as np
from PIL import Image, ImageFilter

# スクリーン枠空間。tools/web-compositor/common.js の MW / MH と同じ値でなければならない。
FRAME_W, FRAME_H = 640, 360


def contain_rect(sw: int, sh: int, fw: int, fh: int):
    """ソース aspect を枠 fw×fh の中央に contain-fit した矩形 (x, y, w, h)。"""
    fa = fw / fh
    a = (sw / sh) if (sw and sh) else fa
    if a > fa:
        w, h = fw, max(1, round(fw / a))
    else:
        w, h = max(1, round(fh * a)), fh
    return (fw - w) // 2, (fh - h) // 2, w, h


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


def fill_holes(mask: np.ndarray) -> np.ndarray:
    """領域の内側にできた穴を埋める（縁から到達できない黒を白に倒す）。

    ⚠ 生成画像の中に、種フレームとたまたま色が近い画素があると黒く抜ける。合成では
    そこだけライブが透けるので、**人形の顔に穴が空く**（2026-08-05 に実際に出た）。
    差し替えたいのは「輪郭の内側ぜんぶ」なので、穴は常に埋めてよい。
    """
    h, w = mask.shape
    outside = np.zeros_like(mask, dtype=bool)
    stack = []
    for x in range(w):
        for y in (0, h - 1):
            if not mask[y, x] and not outside[y, x]:
                outside[y, x] = True
                stack.append((y, x))
    for y in range(h):
        for x in (0, w - 1):
            if not mask[y, x] and not outside[y, x]:
                outside[y, x] = True
                stack.append((y, x))
    while stack:
        y, x = stack.pop()
        for ny, nx in ((y + 1, x), (y - 1, x), (y, x + 1), (y, x - 1)):
            if 0 <= ny < h and 0 <= nx < w and not mask[ny, nx] and not outside[ny, nx]:
                outside[ny, nx] = True
                stack.append((ny, nx))
    return mask | (~outside)


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
    ap.add_argument('--no-fill-holes', action='store_true',
                    help='領域の内側の穴を埋めない（既定は埋める。埋めないと合成でそこだけライブが透ける）')
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
    if not a.no_fill_holes:
        mask = fill_holes(mask)

    img = Image.fromarray((mask * 255).astype(np.uint8), mode='L')
    if a.dilate > 0:
        # 領域を広げるのはソース解像度で（px 指定が種フレームの画素基準になる）。
        img = img.filter(ImageFilter.MaxFilter(a.dilate * 2 + 1))

    # ソース座標 → スクリーン枠空間。ぼかしは縮小後（枠空間の px）に掛けるので、
    # 実機で見える滲み幅がそのまま feather の値になる。矩形の外へは滲まない
    # （滲むと「マスクは白いが素材が無い」領域ができ、live が隠れて黒が出る）。
    x, y, w, h = contain_rect(base.width, base.height, FRAME_W, FRAME_H)
    img = img.resize((w, h), Image.LANCZOS)
    if a.feather > 0:
        img = img.filter(ImageFilter.GaussianBlur(a.feather))
    canvas = Image.new('L', (FRAME_W, FRAME_H), 0)
    canvas.paste(img, (x, y))
    img = canvas

    os.makedirs(os.path.dirname(os.path.abspath(a.out)), exist_ok=True)
    # 実機は R チャンネルだけを見る。グレースケールのまま RGB へ複製して保存する。
    Image.merge('RGB', (img, img, img)).save(a.out)
    cover = float((np.asarray(img) > 127).mean())
    print(f'ok out={a.out} size={img.size[0]}x{img.size[1]} coverage={cover * 100:.1f}% '
          f'（枠空間 {FRAME_W}x{FRAME_H} / 素材の矩形 {w}x{h} at {x},{y}）')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
