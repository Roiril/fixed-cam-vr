"""「固定視点は合成しやすい」を 4 段階で見せる説明用の画を作る。

    py -3.11 paper/dcexpo-pitch/make-composite-demo.py <出力フォルダ>

背景はカメラ A の実写（無人）。左に立つ人と、右に立つ幽霊の女は、どちらも緑の地で生成した画像。
作品に出てくる物は使わない（ネタバレを避ける代役）。
1 合成前（部屋と人）/ 2 合成後（幽霊をそのまま重ねる）/ 3 境界ブレンド後 / 4 画像加工後。
"""
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent
PLATE = ROOT / "Assets/StreamingAssets/show/assets/plate_A_20261115_125052.jpg"
W, H = 1600, 1200
# 元画像（1024x1536）の中の頭頂・足先の y と体の中心 x、置く所（足の中心）と身長（px）
PERSON = dict(src=HERE / "src/person-green.png", top=116, foot=1430, cx=500, at=(505, 1085), height=880)
GHOST = dict(src=HERE / "src/ghost-green.png", top=102, foot=1488, cx=512, at=(1075, 1080), height=850)
rng = np.random.default_rng(7)


def to_img(a):
    return Image.fromarray(np.clip(a * 255 + 0.5, 0, 255).astype(np.uint8))


def blur(a, r):
    mode = "L" if a.ndim == 2 else "RGB"
    im = Image.fromarray(np.clip(a * 255 + 0.5, 0, 255).astype(np.uint8), mode).filter(ImageFilter.GaussianBlur(r))
    return np.asarray(im).astype(np.float32) / 255


def load(fig):
    """緑の地の人物を、置く大きさへ縮めて画面へ並べる。返すのは RGB・緑の量・左上の位置。"""
    assert fig["src"].exists(), fig["src"]
    src = Image.open(fig["src"]).convert("RGB")
    s = fig["height"] / (fig["foot"] - fig["top"])
    im = src.resize((round(src.width * s), round(src.height * s)), Image.LANCZOS)
    a = np.asarray(im).astype(np.float32) / 255
    ox = round(fig["at"][0] - fig["cx"] * s)
    oy = round(fig["at"][1] - fig["foot"] * s)
    rgb = np.zeros((H, W, 3), np.float32)
    spill = np.ones((H, W), np.float32)
    y0, y1 = max(oy, 0), min(oy + a.shape[0], H)
    x0, x1 = max(ox, 0), min(ox + a.shape[1], W)
    part = a[y0 - oy:y1 - oy, x0 - ox:x1 - ox]
    rgb[y0:y1, x0:x1] = part
    spill[y0:y1, x0:x1] = part[..., 1] - np.maximum(part[..., 0], part[..., 2])
    return rgb, spill


def paste_raw(bg, fig):
    """切り抜いてそのまま重ねる。緑の縁・硬い境界・影なし・色は元のまま・手前の物も隠してしまう。"""
    rgb, spill = load(fig)
    alpha = (spill < 0.30).astype(np.float32)[..., None]
    return bg * (1 - alpha) + rgb * alpha


def paste_blended(bg, fig, front):
    """緑を押さえる・縁を柔らかく・色と明るさを部屋へ寄せる・影を落とす・手前の手すりの後ろへ回す。"""
    rgb, spill = load(fig)
    alpha = 1 - np.clip((spill - 0.04) / 0.30, 0, 1)
    d = rgb.copy()
    d[..., 1] = np.minimum(d[..., 1], np.maximum(d[..., 0], d[..., 2]))
    lum = (d * [0.299, 0.587, 0.114]).sum(axis=2, keepdims=True)
    d = lum + (d - lum) * 0.70
    d = d * 0.80 + 0.05
    d = d * np.array([0.97, 1.0, 1.04], np.float32)
    d = blur(d, 1.2)
    a = blur(alpha, 1.5) * (1 - front)

    # 足元の影と、幕へ落ちる淡い影
    fx, fy = fig["at"]
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    contact = np.clip(1 - ((xx - fx) / 150) ** 2 - ((yy - fy + 6) / 26) ** 2, 0, 1)
    contact = blur(contact, 10) * 0.55
    soft = np.zeros((H, W), np.float32)
    soft[8:, 34:] = alpha[:-8, :-34]
    soft = blur(soft, 30) * 0.22
    shadow = np.clip(contact + soft, 0, 0.7) * (1 - front)
    out = bg * (1 - shadow[..., None])
    out = out * (1 - a[..., None]) + d * a[..., None]
    return out + rng.normal(0, 0.006, out.shape).astype(np.float32) * a[..., None]


def process(s3):
    """白黒に寄せる・明暗を強める・にじみ・走査線・周辺を落とす・粒。作品の加工そのものではなく、説明用に似せたもの。"""
    g = (s3 * [0.299, 0.587, 0.114]).sum(axis=2)
    g = np.clip((g - 0.32) * 1.75 + 0.27, 0, 1) ** 1.1
    g = g + blur(np.clip(g - 0.6, 0, 1), 22) * 0.5
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    r = np.sqrt(((xx - W / 2) / (W * 0.62)) ** 2 + ((yy - H / 2) / (H * 0.62)) ** 2)
    g = g * np.clip(1.08 - r ** 2.6 * 1.05, 0.04, 1)
    g = g * np.where((yy.astype(int) % 4) == 3, 0.80, 1.0)
    g = np.clip(g + rng.normal(0, 0.045, g.shape).astype(np.float32), 0, 1)
    return np.stack([np.roll(g, 2, axis=1), g * 0.965, np.roll(g, -2, axis=1) * 0.89], axis=2)


def main(out_dir):
    out = Path(out_dir)
    out.mkdir(parents=True, exist_ok=True)
    assert PLATE.exists(), PLATE
    plate = Image.open(PLATE).convert("RGB").resize((W, H), Image.LANCZOS)
    bg = np.asarray(plate).astype(np.float32) / 255

    # 手前にある黒い手すりと支柱。人物はその後ろに立つ
    dark = Image.fromarray(((bg.mean(axis=2) < 0.23) * 255).astype(np.uint8))
    dark = dark.filter(ImageFilter.MaxFilter(5)).filter(ImageFilter.MinFilter(5)).filter(ImageFilter.MinFilter(3)).filter(ImageFilter.MaxFilter(3))
    front = np.asarray(dark.filter(ImageFilter.GaussianBlur(1.2))).astype(np.float32) / 255

    s1 = paste_blended(bg, PERSON, front)
    s2 = paste_raw(s1, GHOST)
    s3 = paste_blended(s1, GHOST, front)
    s4 = process(s3)

    frames = [("1_合成前", s1), ("2_合成後", s2), ("3_境界ブレンド後", s3), ("4_画像加工後", s4)]
    imgs = []
    for name, a in frames:
        im = to_img(a)
        im.save(out / f"{name}_1600x1200.png")
        imgs.append(im)
    to_img(bg).save(out / "素材_無人の部屋_1600x1200.png")

    # 境界の拡大（2 と 3 の差は引きでは見えにくい）。幽霊の肩と手すりが交わる所
    gx, gy = GHOST["at"]
    box = (gx - 40, 470, gx + 200, 650)
    for name, im in zip(("2_合成後", "3_境界ブレンド後"), imgs[1:3]):
        im.crop(box).resize((960, 720), Image.LANCZOS).save(out / f"{name}_境界の拡大.png")

    # 切り抜いた人物（透過）
    for fig, name in ((PERSON, "素材_人_透過.png"), (GHOST, "素材_幽霊_透過.png")):
        src = np.asarray(Image.open(fig["src"]).convert("RGB")).astype(np.float32) / 255
        spill = src[..., 1] - np.maximum(src[..., 0], src[..., 2])
        src[..., 1] = np.minimum(src[..., 1], np.maximum(src[..., 0], src[..., 2]))
        alpha = 1 - np.clip((spill - 0.04) / 0.30, 0, 1)
        Image.fromarray(np.clip(np.dstack([src, alpha]) * 255 + 0.5, 0, 255).astype(np.uint8), "RGBA").save(out / name)

    # 4 枚を並べた版（ラベルつき）
    pw, ph, gap, pad, lab = 800, 600, 28, 56, 96
    sheet = Image.new("RGB", (pad * 2 + pw * 4 + gap * 3, pad * 2 + ph + lab), (214, 204, 185))
    dr = ImageDraw.Draw(sheet)
    font = ImageFont.truetype("C:/Windows/Fonts/yumindb.ttf", 40)
    for i, (name, _) in enumerate(frames):
        x = pad + i * (pw + gap)
        sheet.paste(imgs[i].resize((pw, ph), Image.LANCZOS), (x, pad))
        n, label = name.split("_", 1)
        dr.rectangle((x, pad + ph + 34, x + 36, pad + ph + 38), fill=(176, 42, 50))
        dr.text((x, pad + ph + 46), f"{n}　{label}", font=font, fill=(21, 17, 14))
    sheet.save(out / "0_4段階を並べた版.png")
    print("ok", len(frames))


if __name__ == "__main__":
    main(sys.argv[1])
