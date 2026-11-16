"""「固定視点は合成しやすい」を 4 段階で見せる説明用の画を作る。

    py -3.11 paper/dcexpo-pitch/make-composite-demo.py <出力フォルダ>

背景はカメラ A の実写（無人）。重ねる物は作品に出てこない赤い風船（生成した画像・緑の地）。
1 合成前 / 2 合成後（そのまま重ねる）/ 3 境界ブレンド後 / 4 画像加工後。
"""
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent
PLATE = ROOT / "Assets/StreamingAssets/show/assets/plate_A_20261115_125052.jpg"
BALLOON = HERE / "src/balloon-green.png"
W, H = 1600, 1200
SCALE = 0.60          # 風船の縮尺（元の 1254px に対して）
CENTER = (540, 300)   # 風船の中心を置く所
SRC_CENTER = (620, 465)
rng = np.random.default_rng(7)


def to_img(a):
    return Image.fromarray(np.clip(a * 255 + 0.5, 0, 255).astype(np.uint8))


def blur(a, r):
    if a.ndim == 2:
        return np.asarray(Image.fromarray(np.clip(a * 255, 0, 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(r))).astype(np.float32) / 255
    return np.asarray(to_img(a).filter(ImageFilter.GaussianBlur(r))).astype(np.float32) / 255


def main(out_dir):
    out = Path(out_dir)
    out.mkdir(parents=True, exist_ok=True)
    assert PLATE.exists(), PLATE
    assert BALLOON.exists(), BALLOON

    plate = Image.open(PLATE).convert("RGB").resize((W, H), Image.LANCZOS)
    bg = np.asarray(plate).astype(np.float32) / 255

    # 風船を置く大きさへ縮める
    src = Image.open(BALLOON).convert("RGB")
    size = round(src.width * SCALE)
    b = np.asarray(src.resize((size, size), Image.LANCZOS)).astype(np.float32) / 255
    ox = round(CENTER[0] - SRC_CENTER[0] * SCALE)
    oy = round(CENTER[1] - SRC_CENTER[1] * SCALE)

    # 手すりの上端を探して、糸をそこで結ぶ（そこから下は消す）
    col = bg[:, CENTER[0] - 6:CENTER[0] + 6].mean(axis=(1, 2))
    band = np.arange(int(H * 0.42), int(H * 0.56))
    rail_y = int(band[np.argmax(col[band] < 0.22)]) if (col[band] < 0.22).any() else int(H * 0.49)
    cut = rail_y + 4 - oy

    spill = b[..., 1] - np.maximum(b[..., 0], b[..., 2])
    alpha_soft = 1 - np.clip((spill - 0.04) / 0.30, 0, 1)
    alpha_hard = (spill < 0.30).astype(np.float32)
    alpha_soft[cut:] = 0
    alpha_hard[cut:] = 0

    def place(rgb, alpha):
        fr = np.zeros((H, W, 3), np.float32)
        fa = np.zeros((H, W), np.float32)
        fr[oy:oy + size, ox:ox + size] = rgb
        fa[oy:oy + size, ox:ox + size] = alpha
        return fr, fa

    # 2 合成後: 切り抜いてそのまま重ねる（緑の縁・硬い境界・影なし・色は元のまま）
    fr, fa = place(b, alpha_hard)
    s2 = bg * (1 - fa[..., None]) + fr * fa[..., None]

    # 3 境界ブレンド後: 緑を押さえる・縁を柔らかく・色と明るさを背景へ寄せる・影を落とす・ぼけと粒を揃える
    d = b.copy()
    d[..., 1] = np.minimum(d[..., 1], np.maximum(d[..., 0], d[..., 2]))
    lum = (d * [0.299, 0.587, 0.114]).sum(axis=2, keepdims=True)
    d = lum + (d - lum) * 0.66                      # 彩度を落とす
    d = d * 0.80 + 0.07                             # 明るさの幅を背景へ寄せる
    d = d * np.array([0.97, 1.0, 1.04], np.float32)  # 部屋の光の色へ
    fr, fa = place(d, alpha_soft)
    fa = blur(fa, 1.6)
    fr = blur(fr, 1.3)
    shadow = np.zeros((H, W), np.float32)
    sh = alpha_soft.copy()
    sh[int(size * 0.66):] = 0                       # 影は風船の玉だけ
    sx, sy = ox + 78, oy + 96
    shadow[sy:sy + size, sx:sx + size] = sh[:H - sy, :W - sx] if sy + size > H or sx + size > W else sh
    shadow = blur(shadow, 30) * 0.46
    base = bg * (1 - shadow[..., None])
    s3 = base * (1 - fa[..., None]) + fr * fa[..., None]
    s3 = s3 + rng.normal(0, 0.006, s3.shape).astype(np.float32) * fa[..., None]

    # 4 画像加工後: 白黒に寄せる・明暗を強める・にじみ・走査線・周辺を落とす・粒
    g = (s3 * [0.299, 0.587, 0.114]).sum(axis=2)
    g = np.clip((g - 0.30) * 2.3 + 0.30, 0, 1) ** 1.15
    g = g + blur(np.clip(g - 0.55, 0, 1), 22) * 0.9
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    r = np.sqrt(((xx - W / 2) / (W * 0.62)) ** 2 + ((yy - H / 2) / (H * 0.62)) ** 2)
    g = g * np.clip(1.08 - r ** 2.6 * 1.05, 0.04, 1)
    g = g * np.where((yy.astype(int) % 4) == 3, 0.80, 1.0)
    g = g + rng.normal(0, 0.045, g.shape).astype(np.float32)
    g = np.clip(g, 0, 1)
    s4 = np.stack([np.roll(g, 2, axis=1) * 1.0, g * 0.965, np.roll(g, -2, axis=1) * 0.89], axis=2)

    frames = [("1_合成前", bg), ("2_合成後", s2), ("3_境界ブレンド後", s3), ("4_画像加工後", s4)]
    imgs = []
    for name, a in frames:
        im = to_img(a)
        im.save(out / f"{name}_1600x1200.png")
        imgs.append(im)

    # 境界の拡大（2 と 3 の差は引きでは見えにくい）
    box = (CENTER[0] + 70, CENTER[1] - 70, CENTER[0] + 250, CENTER[1] + 65)
    for name, im in zip(("2_合成後", "3_境界ブレンド後"), imgs[1:3]):
        im.crop(box).resize((960, 720), Image.LANCZOS).save(out / f"{name}_境界の拡大.png")

    # 切り抜いた風船（透過）
    cut_rgba = np.dstack([np.clip(d / 0.74 - 0.07 / 0.74, 0, 1) * 0 + np.where(spill[..., None] > 0, np.dstack([b[..., 0], np.minimum(b[..., 1], np.maximum(b[..., 0], b[..., 2])), b[..., 2]]), b), (1 - np.clip((spill - 0.04) / 0.30, 0, 1))[..., None]])
    Image.fromarray(np.clip(cut_rgba * 255 + 0.5, 0, 255).astype(np.uint8), "RGBA").save(out / "風船_透過.png")

    # 4 枚を並べた版（ラベルつき）
    pw, ph, gap, pad, lab = 800, 600, 28, 56, 96
    sheet = Image.new("RGB", (pad * 2 + pw * 4 + gap * 3, pad * 2 + ph + lab), (214, 204, 185))
    dr = ImageDraw.Draw(sheet)
    font = ImageFont.truetype("C:/Windows/Fonts/yumindb.ttf", 40)
    num = ImageFont.truetype("C:/Windows/Fonts/yumindb.ttf", 40)
    for i, (name, _) in enumerate(frames):
        x = pad + i * (pw + gap)
        sheet.paste(imgs[i].resize((pw, ph), Image.LANCZOS), (x, pad))
        n, label = name.split("_", 1)
        dr.rectangle((x, pad + ph + 34, x + 36, pad + ph + 38), fill=(176, 42, 50))
        dr.text((x, pad + ph + 46), f"{n}　{label}", font=font, fill=(21, 17, 14))
    sheet.save(out / "0_4段階を並べた版.png")
    print("rail_y", rail_y, "offset", ox, oy, "size", size)


if __name__ == "__main__":
    main(sys.argv[1])
