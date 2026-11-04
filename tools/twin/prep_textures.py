"""生成した素材写真を、つなぎ目の出ない形に整えて tools/twin/textures/ へ置く。
py -3.11 tools/twin/prep_textures.py <生成元フォルダ>（既定は Drive の renders/twin-cycles/textures-src）

壁は横だけ、それ以外は縦横ともに端を重ねて溶かす（裏で上下の向きを保つので、床際の湿りは床際に残る）。
"""
from __future__ import annotations

import sys
from pathlib import Path

import numpy as np
from PIL import Image

OUT = Path(__file__).resolve().parent / "textures"
SRC = Path(r"G:\マイドライブ\shubie\renders\twin-cycles\textures-src")
JOBS = {"wall_a": "x", "wall_b": "x", "floor": "xy", "ceiling": "xy", "cardboard": "xy", "cloth": "xy", "curtain": "xy"}


def fade(a: np.ndarray, axis: int, ratio: float = 0.14) -> np.ndarray:
    n = a.shape[axis]
    o = int(n * ratio)
    a = np.moveaxis(a, axis, 0)
    w = (0.5 - 0.5 * np.cos(np.linspace(0, np.pi, o)))[(...,) + (None,) * (a.ndim - 1)]
    body = a[:n - o].copy()
    body[:o] = a[n - o:] * (1 - w) + a[:o] * w
    return np.moveaxis(body, 0, axis)


def main() -> int:
    src = Path(sys.argv[1]) if len(sys.argv) > 1 else SRC
    OUT.mkdir(parents=True, exist_ok=True)
    for name, axes in JOBS.items():
        p = src / f"{name}.png"
        if not p.exists():
            raise SystemExit(f"[prep] 無い: {p}")
        a = np.asarray(Image.open(p).convert("RGB")).astype(np.float32)
        if "x" in axes:
            a = fade(a, 1)
        if "y" in axes:
            a = fade(a, 0)
        Image.fromarray(np.clip(a + 0.5, 0, 255).astype(np.uint8)).save(OUT / f"{name}.jpg", quality=93, subsampling=0)
        print(name, a.shape)
    return 0


if __name__ == "__main__":
    sys.exit(main())
