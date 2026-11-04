"""生成した素材写真から、Cycles に渡す色と面の情報を作る。tools/twin/textures/ へ置く。
py -3.11 tools/twin/prep_textures.py

<名前>.jpg     色（つなぎ目を溶かしたもの）
<名前>_s.jpg   面の情報。R = 高さ（0.5 が基準）、G = 層（壁は塗膜が残る所が 1）、B = 細かい明暗の比（0.5 が平均）
can_label_*.jpg / box_print.jpg  文字を読める形でコードで組んだ印刷物（生成させると文字が崩れる）

高さは明るさのままにしない。写真の明暗には汚れと影が混ざっていて、明るい欠けが盛り上がって見える。
大きな明暗を引いた残りを高さにし、壁は色から塗膜の層を分けて、塗膜を地より高くする。
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

import cv2
import numpy as np
from PIL import Image, ImageDraw, ImageFont

OUT = Path(__file__).resolve().parent / "textures"
SRC = Path(r"G:\マイドライブ\shubie\renders\twin-cycles\textures-src")
SRC2 = SRC / "v2"
# 名前: (つなぎ目を溶かす軸, 生成元, 大きな明暗を引く半径 px)
JOBS = {
    "wall_a": ("x", SRC, 60),
    "wall_b": ("x", SRC, 60),
    "floor": ("xy", SRC, 40),
    "ceiling": ("xy", SRC, 40),
    "cloth": ("xy", SRC, 40),
    "wall_detail": ("xy", SRC2, 40),
    "rust": ("xy", SRC2, 40),
    "steel": ("xy", SRC2, 40),
    "curtain": ("xy", SRC2, 30),
    "cardboard": ("xy", SRC2, 40),
    "cloth_weave": ("xy", SRC2, 30),
    "floor_dust": ("xy", SRC2, 40),
}
FONT = r"C:\Windows\Fonts\YuGothB.ttc"
FONT_R = r"C:\Windows\Fonts\YuGothM.ttc"


def fade(a: np.ndarray, axis: int, ratio: float = 0.14) -> np.ndarray:
    n = a.shape[axis]
    o = int(n * ratio)
    a = np.moveaxis(a, axis, 0)
    w = (0.5 - 0.5 * np.cos(np.linspace(0, np.pi, o)))[(...,) + (None,) * (a.ndim - 1)]
    body = a[:n - o].copy()
    body[:o] = a[n - o:] * (1 - w) + a[:o] * w
    return np.moveaxis(body, 0, axis)


def wrap_blur(a: np.ndarray, sigma: float) -> np.ndarray:
    return cv2.GaussianBlur(a, (0, 0), sigma, borderType=cv2.BORDER_WRAP if False else cv2.BORDER_REFLECT)


def smooth(x: np.ndarray, lo: float, hi: float) -> np.ndarray:
    t = np.clip((x - lo) / (hi - lo), 0, 1)
    return t * t * (3 - 2 * t)


def surface(rgb: np.ndarray, radius: float, is_wall: bool) -> np.ndarray:
    lin = (rgb / 255.0) ** 2.2
    lum = lin @ np.array([0.2126, 0.7152, 0.0722], dtype=np.float32)
    log = np.log(lum + 0.01).astype(np.float32)
    # 細かい明暗の比（色へ戻さず、粗さと高さの変化に使う）
    detail = log - wrap_blur(log, 3.0)
    detail = np.clip(detail / (detail.std() * 5 + 1e-5) + 0.5, 0, 1)
    hp = log - wrap_blur(log, radius)
    hp = hp / (hp.std() * 2.5 + 1e-5)
    if is_wall:
        # 上塗り（青緑）と、はがれて見えた下塗り・下地（黄緑〜ベージュで周りより明るい）を色で分ける。
        # 錆やカビの筋は暗いので、はがれに数えない（筋が溝になると彫刻に見える）
        g, b = lin[..., 1], lin[..., 2]
        teal = smooth(wrap_blur((b / (g + 0.01)).astype(np.float32), 1.2), 0.66, 0.8)
        lighter = smooth(wrap_blur(log, 1.5) - wrap_blur(log, 45.0), -0.12, 0.08)
        mask = 1.0 - (1.0 - teal) * lighter
        # 塗膜の端はめくれて少し持ち上がる
        lift = np.clip(mask - wrap_blur(mask, 9.0), 0, 1)
        height = 0.34 + 0.34 * mask + 0.3 * lift + 0.07 * np.clip(hp, -1.5, 1.5)
    else:
        mask = smooth(hp, -0.6, 0.6)
        height = 0.5 + 0.2 * np.clip(hp, -2.2, 2.2)
    return np.clip(np.dstack([height, mask, detail]) * 255 + 0.5, 0, 255).astype(np.uint8)


def label(name: str, band: tuple, title: str, sub: str, vol: str, age: np.ndarray) -> None:
    """ペンキ缶の紙ラベル。巻きつけるので横長。"""
    W, H = 1400, 500
    im = Image.new("RGB", (W, H), (214, 208, 190))
    d = ImageDraw.Draw(im)
    d.rectangle((0, 0, W, 86), fill=band)
    d.rectangle((0, H - 58, W, H), fill=band)
    big, mid, small = ImageFont.truetype(FONT, 104), ImageFont.truetype(FONT, 46), ImageFont.truetype(FONT_R, 19)
    d.text((40, 14), "屋内用 水性", font=ImageFont.truetype(FONT, 52), fill=(235, 232, 220))
    d.text((60, 126), title, font=big, fill=(32, 34, 40))
    d.text((64, 262), sub, font=mid, fill=band)
    d.text((W // 2 - 90, 330), vol, font=ImageFont.truetype(FONT, 76), fill=(32, 34, 40))
    notes = ["用途 屋内の壁・天井・木部", "乾燥 夏期 1時間 冬期 2時間", "うすめ液 水（5〜10%）", "塗り面積 約 7 平方メートル（2回塗り）",
             "よくかき混ぜてからご使用ください。", "換気をよくして作業してください。", "火気のない涼しい場所に保管。", "ロット 26-0417"]
    for side in (W - 470, 40):
        y = 340 if side == 40 else 112
        for t in notes[:4] if side == 40 else notes:
            d.text((side, y), t, font=small, fill=(52, 52, 54))
            y += 25
    a = np.asarray(im).astype(np.float32) / 255.0
    # 紙の焼けと擦れ。写真の細かい明暗をそのまま掛ける
    ag = cv2.resize(age, (W, H), interpolation=cv2.INTER_CUBIC)[..., None]
    a = a * (0.62 + 0.76 * ag) * np.array([0.92, 0.86, 0.72], dtype=np.float32)
    rub = smooth(cv2.GaussianBlur(ag[..., 0], (0, 0), 1.2), 0.56, 0.68)[..., None]
    a = a * (1 - rub * 0.55) + np.array([0.46, 0.42, 0.34], dtype=np.float32) * rub * 0.55
    Image.fromarray(np.clip(a * 255, 0, 255).astype(np.uint8)).save(OUT / f"{name}.jpg", quality=92, subsampling=0)


def box_print(age: np.ndarray) -> None:
    """段ボールの印刷と貼り札。黒地に白で描いて、黒 = インク無し として使う（貼る範囲の外も黒）。"""
    W, H = 1200, 800
    im = Image.new("L", (W, H), 0)
    d = ImageDraw.Draw(im)
    f = ImageFont.truetype(FONT, 92)
    d.text((70, 60), "取扱注意", font=f, fill=235)
    d.text((70, 170), "天地無用", font=f, fill=235)
    for k in range(2):  # 上向きの矢印 2 本
        x = 830 + k * 150
        d.polygon([(x, 60), (x + 58, 150), (x + 22, 150), (x + 22, 290), (x - 22, 290), (x - 22, 150), (x - 58, 150)], fill=235)
    d.text((70, 640), "品番 2431-B   入数 12", font=ImageFont.truetype(FONT, 50), fill=215)
    d.rectangle((60, 610, W - 60, 616), fill=215)
    a = np.asarray(im).astype(np.float32) / 255.0
    ag = cv2.resize(age, (W, H), interpolation=cv2.INTER_CUBIC)
    a = a * smooth(ag, 0.3, 0.52) * 0.92  # 版のかすれ
    Image.fromarray((a * 255).astype(np.uint8)).save(OUT / "box_print.jpg", quality=92)


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    OUT.mkdir(parents=True, exist_ok=True)
    stats = {}
    for name, (axes, src, radius) in JOBS.items():
        p = src / f"{name}.png"
        if not p.exists():
            raise SystemExit(f"[prep] 無い: {p}")
        a = np.asarray(Image.open(p).convert("RGB")).astype(np.float32)
        if "x" in axes:
            a = fade(a, 1)
        if "y" in axes:
            a = fade(a, 0)
        Image.fromarray(np.clip(a + 0.5, 0, 255).astype(np.uint8)).save(OUT / f"{name}.jpg", quality=93, subsampling=0)
        s = surface(a.astype(np.float32), radius, name.startswith("wall_") and name != "wall_detail")
        Image.fromarray(s).save(OUT / f"{name}_s.jpg", quality=94, subsampling=0)
        lin = ((a / 255.0) ** 2.2).mean((0, 1))
        stats[name] = {"size": list(a.shape[:2]), "albedo_linear": [round(float(v), 4) for v in lin], "layer_mean": round(float(s[..., 1].mean() / 255), 3)}
        print(name, stats[name])
    age = np.asarray(Image.open(OUT / "cardboard_s.jpg").convert("RGB"))[..., 2].astype(np.float32) / 255.0
    age = np.clip((cv2.GaussianBlur(age, (0, 0), 1.5) - 0.5) * 2.2 + 0.5, 0, 1)
    label("can_label_a", (40, 66, 110), "つやけし白", "かべ・天井用", "1.6 L", age)
    label("can_label_b", (122, 44, 34), "下ぬり シーラー", "しみ止め兼用", "0.7 L", np.roll(age, 211, 1))
    label("can_label_c", (52, 86, 72), "ライトグレー", "木部・鉄部 兼用", "0.7 L", np.roll(age, 97, 0))
    box_print(np.roll(age, 301, 1))
    print(json.dumps(stats, ensure_ascii=False)[:0], end="")
    return 0


if __name__ == "__main__":
    sys.exit(main())
