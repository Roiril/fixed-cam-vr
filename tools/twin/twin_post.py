"""EXR（線形）→ 写真の仕上げ。Blender の外で走らせる: py -3.11 tools/twin/twin_post.py in.exr out.png [--look photo|phone]

狙いは「作った綺麗さ」を消すこと。レンズの光のかぶり・周辺減光・わずかな色ずれ・歪み、
撮像素子のノイズ（明るさに依存）、スマホ/カメラの現像（シャープと JPEG）。
"""
from __future__ import annotations

import argparse
import os
import sys

os.environ.setdefault("OPENCV_IO_ENABLE_OPENEXR", "1")
import cv2  # noqa: E402
import numpy as np  # noqa: E402

LOOKS = {
    # 暗所をフルサイズ機 ISO 3200 前後で撮った写真
    "photo": dict(key=0.03, wb=(0.88, 1.0, 1.14), glare=0.012, bloom=0.05, ca=0.0004, vignette=0.34, distort=0.012,
                  read=0.0042, shot=0.0048, chroma=0.42, sharpen=0.55, jpeg=91, black=0.0015, contrast=1.0),
    # 固定カメラ（スマホ・640×480 の配信を想定した小さな撮像素子）
    "phone": dict(key=0.035, wb=(0.86, 1.0, 1.16), glare=0.02, bloom=0.08, ca=0.0007, vignette=0.26, distort=0.0,
                  read=0.0052, shot=0.0058, chroma=0.5, sharpen=0.7, jpeg=82, black=0.002, contrast=1.08),
}


def tone(x: np.ndarray, contrast: float) -> np.ndarray:
    """フィルム風のトーン（AgX に近い肩と足）。色相の飽和崩れを防ぐため輝度寄りに混ぜる。"""
    lum = (x * np.array([0.2126, 0.7152, 0.0722])).sum(-1, keepdims=True)
    # 明るい所ほど白へ寄る（強い光で色が飛ぶ実際の露光）
    desat = np.clip((np.log2(np.maximum(lum, 1e-5)) - 0.2) / 3.2, 0, 1)
    x = x * (1 - desat) + lum * desat
    lg = np.log2(np.maximum(x, 1e-6) / 0.18)
    t = np.clip((lg + 10.0) / 16.5, 0, 1)
    # シグモイド
    y = (1.0 / (1.0 + np.exp(-(t - 0.52) * 10.2 * contrast)))
    lo = 1.0 / (1.0 + np.exp((0.52) * 10.2 * contrast))
    hi = 1.0 / (1.0 + np.exp(-(0.48) * 10.2 * contrast))
    return np.clip((y - lo) / (hi - lo), 0, 1)


def auto_exposure(x: np.ndarray, key: float) -> float:
    """カメラの中央重点測光。電球の白飛びに引っ張られないよう上位 2% を捨てて対数平均を取る。"""
    small = cv2.resize(x, (160, 90), interpolation=cv2.INTER_AREA)
    lum = (small * np.array([0.2126, 0.7152, 0.0722], dtype=np.float32)).sum(-1)
    yy, xx = np.mgrid[0:90, 0:160].astype(np.float32)
    w = np.exp(-(((xx - 80) / 70) ** 2 + ((yy - 45) / 42) ** 2))
    lim = np.percentile(lum, 98)
    mask = lum < lim
    mean = np.exp((np.log(np.maximum(lum, 1e-5)) * w * mask).sum() / max((w * mask).sum(), 1e-5))
    return float(np.clip(key / max(mean, 1e-5), 0.3, 7.0))


def process(hdr: np.ndarray, look: str = "photo", seed: int = 1, exposure: float | None = None) -> np.ndarray:
    p = LOOKS[look]
    rng = np.random.default_rng(seed)
    h, w = hdr.shape[:2]
    x = np.maximum(hdr[..., :3], 0)
    if exposure is None:
        exposure = auto_exposure(x, p["key"])
    x = x * exposure
    x = x * np.array(p["wb"], dtype=np.float32)
    # 高輝度のかぶり（レンズ内の散乱）。全体を薄く持ち上げ、光源のまわりをにじませる
    small = cv2.resize(x, (w // 4, h // 4), interpolation=cv2.INTER_AREA)
    bloom = np.zeros_like(small)
    for sigma, wt in [(1.2, 0.5), (4.0, 0.3), (12.0, 0.16), (36.0, 0.04)]:
        bloom += cv2.GaussianBlur(np.minimum(small, 60.0), (0, 0), sigma) * wt
    veil = cv2.GaussianBlur(cv2.resize(small, (w // 16, h // 16), interpolation=cv2.INTER_AREA), (0, 0), 16.0)
    bloom = cv2.resize(bloom, (w, h), interpolation=cv2.INTER_LINEAR)
    veil = cv2.resize(veil, (w, h), interpolation=cv2.INTER_LINEAR)
    x = x * (1 - p["bloom"]) + bloom * p["bloom"] + veil * p["glare"]
    # 歪みと倍率色収差（放射方向）
    yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
    cx, cy = (w - 1) / 2, (h - 1) / 2
    dn = np.hypot(cx, cy)
    u, v = (xx - cx) / dn, (yy - cy) / dn
    r2 = u * u + v * v
    out = []
    for ch, ca in zip(range(3), (p["ca"], 0.0, -p["ca"])):
        k = 1 + p["distort"] * r2 + ca * (1 + r2)
        out.append(cv2.remap(x[..., ch], (u * k * dn + cx).astype(np.float32), (v * k * dn + cy).astype(np.float32), cv2.INTER_LANCZOS4, borderMode=cv2.BORDER_REPLICATE))
    x = np.stack(out, -1)
    # 周辺減光（cos^4 風）
    x = x * (1 - p["vignette"] * np.clip(r2, 0, 1) ** 1.25)[..., None]
    y = tone(x, p["contrast"])
    # 撮像素子のノイズ（線形に近い段階で、明るさに比例して増える）
    lum = y.mean(-1, keepdims=True)
    sigma = np.sqrt(p["read"] ** 2 + (p["shot"] ** 2) * np.maximum(lum, 0) * 9.0)
    grain = rng.normal(0, 1, (h, w, 1)).astype(np.float32)
    grain = cv2.GaussianBlur(grain, (0, 0), 0.62)[..., None] * 1.55
    chroma = rng.normal(0, 1, (h // 2, w // 2, 3)).astype(np.float32)
    chroma = cv2.resize(cv2.GaussianBlur(chroma, (0, 0), 0.8), (w, h), interpolation=cv2.INTER_CUBIC)
    chroma = chroma - chroma.mean(-1, keepdims=True)
    # 飽和した画素には雑音が乗らない（白飛びの上の粒は後から足した印）
    keep = np.clip((0.985 - lum) / 0.08, 0, 1)
    y = y + sigma * keep * (grain + chroma * p["chroma"] * 1.6)
    y = y + p["black"]
    # カメラ内シャープ
    blur = cv2.GaussianBlur(y, (0, 0), 1.1)
    y = y + (y - blur) * p["sharpen"]
    img = (np.clip(y, 0, 1) * 255 + 0.5).astype(np.uint8)
    # JPEG を 1 度通す（ブロックとリンギングが写真の痕跡になる）
    ok, enc = cv2.imencode(".jpg", img[..., ::-1], [int(cv2.IMWRITE_JPEG_QUALITY), p["jpeg"], int(cv2.IMWRITE_JPEG_CHROMA_QUALITY), max(p["jpeg"] - 6, 50)])
    assert ok
    return cv2.imdecode(enc, cv2.IMREAD_COLOR)  # BGR


def read_exr(path: str) -> np.ndarray:
    """twin_render.py が EXR の横に置く線形 RGB の .npy を読む。"""
    return np.load(os.path.splitext(path)[0] + ".npy").astype(np.float32)


def write_image(path: str, bgr: np.ndarray) -> None:
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    ext = os.path.splitext(path)[1]
    ok, enc = cv2.imencode(ext, bgr, [int(cv2.IMWRITE_JPEG_QUALITY), 93] if ext.lower() in (".jpg", ".jpeg") else [])
    assert ok
    # 日本語パスのため imwrite を使わない
    with open(path, "wb") as f:
        f.write(enc.tobytes())


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("src")
    ap.add_argument("dst")
    ap.add_argument("--look", default="photo", choices=sorted(LOOKS))
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--exposure", type=float, default=None)
    a = ap.parse_args()
    bgr = process(read_exr(a.src), a.look, a.seed, a.exposure)
    write_image(a.dst, bgr)
    print("[twin_post]", a.dst, bgr.shape)
    return 0


if __name__ == "__main__":
    sys.exit(main())
