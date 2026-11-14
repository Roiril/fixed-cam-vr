"""4:3 の素材へ、本編の B と C と同じ魚眼（CameraLensLogic の版 3）を掛けた画を書く。候補を見るための道具。

    py -3.11 tools/gen-plate/fisheye_preview.py <in.png> <out.png> [--dim]

式は Assets/Scripts/Streaming/CameraLensLogic.cs の SourceFromScreen と同じ（座標はソースの幅を 1 とした中心原点）。
--dim は装置の暗さの目安（露出を落として無彩へ寄せるだけ）。実機の信号処理は通らない — 構図と曲がりを見る用。
"""
import math
import sys

import numpy as np
from PIL import Image

SOURCE_FOCAL = 288.33 / 640.0
SCREEN_FOCAL = 0.5 / (2.0 * math.sin(math.atan(0.5 / SOURCE_FOCAL) * 0.5))


def warp(src: np.ndarray) -> np.ndarray:
    h, w = src.shape[:2]
    ys, xs = np.mgrid[0:h, 0:w].astype(np.float64)
    sx = (xs + 0.5) / w - 0.5
    sy = ((ys + 0.5) - h / 2.0) / w
    r = np.sqrt(sx * sx + sy * sy)
    half = np.minimum(r / (2.0 * SCREEN_FOCAL), 0.7)
    theta = 2.0 * np.arcsin(half)
    k = np.where(r < 1e-5, SOURCE_FOCAL / SCREEN_FOCAL, SOURCE_FOCAL * np.tan(theta) / np.maximum(r, 1e-9))
    u = (sx * k + 0.5) * w - 0.5
    v = sy * k * w + h / 2.0 - 0.5
    inside = (u >= 0) & (u <= w - 1) & (v >= 0) & (v <= h - 1)
    u0 = np.clip(np.floor(u).astype(int), 0, w - 2)
    v0 = np.clip(np.floor(v).astype(int), 0, h - 2)
    fu = np.clip(u - u0, 0, 1)[..., None]
    fv = np.clip(v - v0, 0, 1)[..., None]
    s = src.astype(np.float64)
    out = (s[v0, u0] * (1 - fu) * (1 - fv) + s[v0, u0 + 1] * fu * (1 - fv)
           + s[v0 + 1, u0] * (1 - fu) * fv + s[v0 + 1, u0 + 1] * fu * fv)
    out[~inside] = 0
    return out


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    if len(args) != 2:
        sys.exit(__doc__)
    im = np.asarray(Image.open(args[0]).convert("RGB"))
    out = warp(im)
    if "--dim" in sys.argv:
        lin = (out / 255.0) ** 2.2
        y = lin @ np.array([0.2126, 0.7152, 0.0722])
        lin = (lin * 0.25 + y[..., None] * 0.75) * 0.55
        out = np.clip(lin, 0, 1) ** (1 / 2.2) * 255.0
    Image.fromarray(np.clip(out + 0.5, 0, 255).astype(np.uint8)).save(args[1])


if __name__ == "__main__":
    main()
