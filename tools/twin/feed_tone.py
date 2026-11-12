"""ツインの映像を「スマホが配る明るさ」へ直す階調。make_pack.py と make_assets.py が同じ表を使う。

    py -3.11 tools/twin/feed_tone.py            # 3 台の表と、直す前後の分布を出す
    py -3.11 tools/twin/feed_tone.py --sheet x.jpg   # 無人写真の 直す前 / 直した後 / 本番の写真 を並べる

なぜ要るか（2026-11-12）: Cycles の仕上げ（twin_post の phone）は、裸電球 1 つの部屋を暗いまま出す。
本番のスマホは自動露出で暗部を持ち上げて配る。本番の画面加工（露出を落として粒を乗せる）はその明るい映像に
合わせてあるので、ツインの映像をそのまま流すと幕だけが白く残って、代役も人形も壁も黒へ潰れた。
本番の無人写真（9/24 の A・B・C）の明るさの分布へ、台ごとに単調な表で寄せる。

- 表は台ごとに 1 本。ツインの無人写真（plates/twin_<台>_plate.jpg）の分位点を、本番の分位点へ運ぶ
- 同じ台の映像・無人写真・合成素材の全部に同じ表を掛ける。片方だけ掛けると継ぎ目になる
- 色ごとに同じ表を引く（輝度だけ動かす形より単純で、動画にも同じ表を掛けられる）
- 写実の静止画・ビューア・博士の説明の絵には掛けない。掛けるのは通し検証（twin-verify）の入力だけ
"""
from __future__ import annotations

import argparse
import hashlib
import sys
from pathlib import Path

import numpy as np
from PIL import Image

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]

QS = (1, 10, 25, 50, 75, 90, 99)
# 本番の無人写真 3 枚（tools/web-compositor/captures/plate_{A,B,C}_20260924_1722*.jpg）の輝度の分位点の平均。
# 2026-11-12 に測った。写真そのものは git 管理外なので数字で持つ。
TARGET = (13.0, 44.0, 76.0, 126.0, 169.0, 191.0, 221.0)
VERSION = "feed-tone-1"


def _luma(img: Image.Image) -> np.ndarray:
    return np.asarray(img.convert("L"), dtype=np.float32)


def lut_from(plate: Image.Image) -> np.ndarray:
    """無人写真 1 枚から 256 段の表を作る。単調で、0 は 0、255 は 255。"""
    src = np.percentile(_luma(plate), QS)
    xs = [0.0]
    ys = [0.0]
    for s, t in zip(src, TARGET):
        s = float(max(s, xs[-1] + 1.0))      # 分位点が重なっても単調を保つ
        t = float(max(t, ys[-1] + 0.5))
        xs.append(s)
        ys.append(t)
    xs.append(255.0)
    ys.append(255.0)
    lut = np.interp(np.arange(256, dtype=np.float32), xs, ys)
    return np.clip(np.maximum.accumulate(lut), 0, 255).astype(np.uint8)


def plate_path(cam: str) -> Path:
    return HERE / "plates" / f"twin_{cam}_plate.jpg"


_cache: dict[str, np.ndarray] = {}


def lut_for(cam: str) -> np.ndarray:
    if cam not in _cache:
        p = plate_path(cam)
        if not p.is_file():
            raise SystemExit(f"無人写真が無い: {p}")
        _cache[cam] = lut_from(Image.open(p))
    return _cache[cam]


def apply(img: Image.Image, cam: str) -> Image.Image:
    """RGB の画へ、その台の表を掛ける。アルファは触らない。"""
    lut = lut_for(cam)
    if img.mode == "RGBA":
        a = np.asarray(img)
        out = a.copy()
        out[..., :3] = lut[a[..., :3]]
        return Image.fromarray(out, "RGBA")
    return Image.fromarray(lut[np.asarray(img.convert("RGB"))], "RGB")


def signature() -> str:
    """表の中身の指紋。束と素材を作り直すかの判定に使う。"""
    h = hashlib.sha256(VERSION.encode())
    for cam in "ABC":
        h.update(lut_for(cam).tobytes())
    return h.hexdigest()[:16]


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser()
    ap.add_argument("--sheet", default="")
    a = ap.parse_args()
    print("本番の分位点 " + " ".join(f"p{q}={t:.0f}" for q, t in zip(QS, TARGET)))
    tiles = []
    for cam in "ABC":
        plate = Image.open(plate_path(cam)).convert("RGB")
        before = np.percentile(_luma(plate), QS)
        toned = apply(plate, cam)
        after = np.percentile(_luma(toned), QS)
        print(f"{cam} 前 " + " ".join(f"{v:5.0f}" for v in before) + f"  平均 {_luma(plate).mean():5.1f}")
        print(f"{cam} 後 " + " ".join(f"{v:5.0f}" for v in after) + f"  平均 {_luma(toned).mean():5.1f}")
        tiles.append((plate, toned))
    print("指紋", signature())
    if a.sheet:
        w, h = tiles[0][0].size
        sheet = Image.new("RGB", (w * 2, h * 3))
        for i, (p, t) in enumerate(tiles):
            sheet.paste(p, (0, i * h))
            sheet.paste(t, (w, i * h))
        sheet.save(a.sheet, quality=90)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
