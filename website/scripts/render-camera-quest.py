"""Still-frame recreation of the Quest ScreenComposite path.

Values and operation order come from ScreenComposite.shader, CameraFeelFx,
CameraFeelLogic, ShowControlClient and tools/web-compositor/show.json.
This is a still preview, not a headset capture.
"""
from pathlib import Path
import sys

import numpy as np
from PIL import Image


ROOT = Path(__file__).resolve().parents[2]
CAMERA = sys.argv[1] if len(sys.argv) > 1 else "03"
if CAMERA not in ("01", "02", "03"):
    raise SystemExit("camera must be 01, 02 or 03")
SOURCE = ROOT / f"website/assets/camera-{CAMERA}.webp"
TARGET = Path(sys.argv[2]).resolve() if len(sys.argv) > 2 else ROOT / f"output/mawarimi-camera{CAMERA}-quest-full-preview.png"
DOLL = "--doll" in sys.argv[3:]
W, H = 1280, 720


def srgb_to_linear(c):
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def linear_to_srgb(c):
    c = np.clip(c, 0, 1)
    return np.where(c <= 0.0031308, 12.92 * c, 1.055 * c ** (1 / 2.4) - 0.055)


def frac(a):
    return a - np.floor(a)


def hash21(px, py):
    x = frac(px * 123.34)
    y = frac(py * 456.21)
    d = x * (x + 45.32) + y * (y + 45.32)
    x = x + d
    y = y + d
    return frac(x * y)


source = Image.open(SOURCE).convert("RGB")
sw, sh = source.size
source_srgb = np.asarray(source, dtype=np.float32) / 255.0
source_linear = srgb_to_linear(source_srgb)

# The runtime samples a 16x16 grid from the incoming Texture2D for AGC.
step_x, step_y = max(1, sw // 16), max(1, sh // 16)
sampled = source_linear[::step_y, ::step_x]
observed = np.mean(np.sum(sampled * [0.299, 0.587, 0.114], axis=2))
steady_bias = np.clip(np.log2(0.34 / observed), -0.8, 0.8) * 0.7

# 4:3 phone image contained in the 16:9 Quest screen.
fit_w = round(H * sw / sh)
left = (W - fit_w) // 2
rgb = np.zeros((H, W, 3), dtype=np.float32)
if DOLL:
    # Shown=1: 267 blocks across the Quest frame, about 200 across the image.
    sampled_source = source.resize((200, 150), Image.Resampling.BOX).resize(
        (fit_w, H), Image.Resampling.BILINEAR
    )
else:
    sampled_source = source.resize((fit_w, H), Image.Resampling.BILINEAR)
rgb[:, left:left + fit_w] = np.asarray(sampled_source, dtype=np.float32) / 255.0
col = srgb_to_linear(rgb)

yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
u = (xx + 0.5) / W
v = 1 - (yy + 0.5) / H
r2 = np.clip(((u - 0.5) ** 2 + (v - 0.5) ** 2) * 4, 0, 1)

# SampleBase: veiling glare from a broad texture mip (LOD + 4.5).
# The Quest path enables this only when the live source has mipmaps.
mip_w = max(1, round(sw / (2 ** 4.5)))
mip_h = max(1, round(sh / (2 ** 4.5)))
wide = source.resize((mip_w, mip_h), Image.Resampling.BOX).resize(
    (fit_w, H), Image.Resampling.BILINEAR
)
wide_linear = srgb_to_linear(np.asarray(wide, dtype=np.float32) / 255.0)
sharp = col[:, left:left + fit_w]
sharp += np.maximum(wide_linear - np.maximum(sharp, 0.45), 0) * (0.55 * 2.2)

# Lens -> sensor -> ISP. First-lap steady frame: no cue, no glitch, no night mode.
col *= 1 - (0.54 if DOLL else 0.38) * 0.58 * r2[..., None] ** 2
src_x = np.floor(np.clip((u - left / W) * sw * W / fit_w, 0, sw - 1))
src_y = np.floor(np.clip((1 - v) * sh, 0, sh - 1))
lum = np.maximum(np.sum(col * [0.299, 0.587, 0.114], axis=2), 0)
grain_px = 3.2 if DOLL else 1
amp = (np.sqrt(lum) * 0.030 + 0.10 * 0.30) * (3.6 if DOLL else 1) / grain_px ** 0.6
nx, ny = np.floor(src_x / grain_px), np.floor(src_y / grain_px)
col += (hash21(nx, ny) - 0.5)[..., None] * amp[..., None]
cx, cy = np.floor(nx * 0.5), np.floor(ny * 0.5)
cn = np.stack([
    hash21(cx, cy),
    hash21(cx + 17, cy + 17),
    hash21(cx + 41, cy + 41),
], axis=2) - 0.5
cn -= cn.mean(axis=2, keepdims=True)
col += cn * amp[..., None] * 0.55
fixed = (hash21(src_x, src_y) - 0.5) + (
    hash21(np.floor(src_x * 0.04), np.floor(src_y * 0.04)) - 0.5
) * 1.8
col *= (1 + fixed[..., None] * 0.035)
col *= 2 ** (-1.75 + steady_bias + (0.95 if DOLL else 0))
col[..., 0] *= 1 + 0.25 * 0.48
col[..., 2] *= 1 - 0.25 * 0.48
col = (col - 0.5) * (1.22 if DOLL else 1.12) + 0.5
col = col * (1 - 0.02) + 0.02
lum = np.sum(col * ([0.52, 0.34, 0.14] if DOLL else [0.299, 0.587, 0.114]), axis=2)
sat = 0.52 + (1 - 0.52) * np.clip(lum * 3.33, 0, 1) * 0.85
if DOLL:
    sat = np.zeros_like(sat)
col = lum[..., None] + (col - lum[..., None]) * sat[..., None]
if DOLL:
    display = linear_to_srgb(col)
    display_luma = np.sum(display * [0.299, 0.587, 0.114], axis=2)
    t = np.clip((display_luma - 0.10) / 0.60, 0, 1)
    target_luma = 0.90 * t * t * (3 - 2 * t) + 0.10 * display_luma
    col = srgb_to_linear(np.clip(display + (target_luma - display_luma)[..., None], 0, 1))

# CRT glass SDF: exact rounded-rectangle edge function and edge darkness.
aspect = W / H
px, py = (u - 0.5) * 2 * aspect, (v - 0.5) * 2
radius = 0.07
qx, qy = np.abs(px) - (aspect - radius), np.abs(py) - (1 - radius)
d = np.hypot(np.maximum(qx, 0), np.maximum(qy, 0)) + np.minimum(np.maximum(qx, qy), 0) - radius
inner = np.clip(-d / 0.17, 0, 1)
col *= (1 - 0.34 * (1 - inner) ** 2)[..., None]
aa = 2.4 / H
col *= np.clip(-d / aa, 0, 1)[..., None]

if DOLL:
    col = col[:, left:left + fit_w]
out = (linear_to_srgb(col) * 255 + 0.5).astype(np.uint8)
Image.fromarray(out, "RGB").save(TARGET)
print(f"{TARGET} | observed={observed:.4f} steadyBias={steady_bias:.4f} | {Image.open(TARGET).size}")
