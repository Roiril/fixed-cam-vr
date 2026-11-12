# -*- coding: utf-8 -*-
"""目より上の乱れの材料を、舞台が読む形へ焼く。

gen/eyeglitch-{a,b,c}1.png（Codex に描かせた「壊れた画像データ」の絵）→ layers/eyeglitch-{a,b,c}.jpg

絵の形は変えない。変えるのは明るさの幅と色だけ:
- 生成された絵は明暗の幅が狭く（輝度の 2%点〜98%点が 0.26〜0.36）、焦げ茶一色で、博士の肌と同じ色に見える。
  肌と読めないように彩度を落として灰へ寄せ、明暗の幅を広げる
- 黒い面は作らない（下限を LO で止める）。光って見えないように上限を HI で止める

使い方: py -3.11 tools/visitor-ui/room-proto/make-eye-glitch.py
"""
import io
import os
import sys

import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
LO, HI = 0.08, 0.44          # 焼いた後の輝度の 2%点と 98%点
KEEP_CHROMA = 0.5            # 元の色みを残す割合
TINT = (0.97, 1.00, 0.98)    # ほぼ無彩の灰（肌の橙にも、発光する色にも寄せない）
ACCENT = 1.5                 # 差し色（青緑・赤紫）の粒は元の彩度を保つ側へ戻す


def bake(name):
    src = os.path.join(HERE, 'gen', f'eyeglitch-{name}1.png')
    a = np.asarray(Image.open(src).convert('RGB')).astype(np.float32) / 255.0
    lum = a @ np.array([0.299, 0.587, 0.114], dtype=np.float32)
    p2, p98 = np.percentile(lum, [2, 98])
    out_l = LO + (lum - p2) / max(1e-4, p98 - p2) * (HI - LO)
    out_l = np.clip(out_l, LO * 0.75, HI * 1.18)
    chroma = a - lum[..., None]
    # 地の焦げ茶（赤 > 緑 > 青）は落とし、青緑と赤紫の粒（青が緑か赤を上回る画素）は残す
    accent = np.clip((a[..., 2] - np.minimum(a[..., 0], a[..., 1])) * 12, 0, 1)[..., None]
    keep = KEEP_CHROMA + (ACCENT - KEEP_CHROMA) * accent
    gain = (out_l / np.maximum(lum, 1e-3))[..., None]
    rgb = out_l[..., None] * np.array(TINT, dtype=np.float32) * (1 - accent) + out_l[..., None] * accent + chroma * keep * gain
    rgb = np.clip(rgb, 0, 1)
    dst = os.path.join(HERE, 'layers', f'eyeglitch-{name}.jpg')
    tmp = dst + '.tmp'
    Image.fromarray((rgb * 255 + 0.5).astype(np.uint8)).save(tmp, format='JPEG', quality=90)
    os.replace(tmp, dst)
    l2 = rgb @ np.array([0.299, 0.587, 0.114], dtype=np.float32)
    q = np.percentile(l2, [2, 50, 98])
    print(f'{name}: {rgb.shape[1]}x{rgb.shape[0]} mean={rgb.reshape(-1, 3).mean(0).round(3).tolist()} lum p2/p50/p98={q.round(3).tolist()}')


if __name__ == '__main__':
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')
    for n in 'abc':
        bake(n)
