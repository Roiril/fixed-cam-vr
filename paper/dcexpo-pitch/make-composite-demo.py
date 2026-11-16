"""「固定視点は合成しやすい」を 4 段階で見せる説明用の画を作る。

    py -3.11 paper/dcexpo-pitch/make-composite-demo.py <出力フォルダ>

同じ環境画像（カメラ A の無人の実写）から、画像生成で 2 枚を別々に作ってある。
  src/gen-person.png  左に、右を向いて立つ人（手すりの手前）
  src/gen-ghost.png   右に、左へ襲い掛かる幽霊の女（手すりの手前）
カメラが動かないので 2 枚は同じ構図になり、片方の右側をもう片方の右側へ差し替えるだけで合成になる。
作品に出てくる物は使わない（ネタバレを避ける代役）。プロンプトは src/*.prompt.txt。
1 合成前 / 2 合成後（右側をそのまま差し替える）/ 3 境界ブレンド後 / 4 画像加工後。
"""
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent
ENV = ROOT / "Assets/StreamingAssets/show/assets/plate_A_20261115_125052.jpg"
PERSON = HERE / "src/gen-person.png"
GHOST = HERE / "src/gen-ghost.png"
SEAM = 0.50      # 差し替える境目（画面の幅に対して）。ちょうど半分
FEATHER = 150    # 境界ブレンドの幅（px）。幽霊の指先が境目のすぐ右にあるので、境目から左へ取る
rng = np.random.default_rng(7)


def to_img(a):
    return Image.fromarray(np.clip(a * 255 + 0.5, 0, 255).astype(np.uint8))


def blur(a, r):
    im = Image.fromarray(np.clip(a * 255 + 0.5, 0, 255).astype(np.uint8), "L").filter(ImageFilter.GaussianBlur(r))
    return np.asarray(im).astype(np.float32) / 255


def process(s3):
    """白黒に寄せる・明暗を強める・にじみ・走査線・周辺を落とす・粒。作品の加工そのものではなく、説明用に似せたもの。"""
    h, w = s3.shape[:2]
    g = (s3 * [0.299, 0.587, 0.114]).sum(axis=2).astype(np.float32)
    g = np.clip((g - 0.32) * 1.75 + 0.27, 0, 1) ** 1.1
    g = g + blur(np.clip(g - 0.6, 0, 1), 22) * 0.5
    yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
    r = np.sqrt(((xx - w / 2) / (w * 0.62)) ** 2 + ((yy - h / 2) / (h * 0.62)) ** 2)
    g = g * np.clip(1.08 - r ** 2.6 * 1.05, 0.04, 1)
    g = g * np.where((yy.astype(int) % 4) == 3, 0.80, 1.0)
    g = np.clip(g + rng.normal(0, 0.045, g.shape).astype(np.float32), 0, 1)
    return np.stack([np.roll(g, 2, axis=1), g * 0.965, np.roll(g, -2, axis=1) * 0.89], axis=2)


def main(out_dir):
    out = Path(out_dir)
    out.mkdir(parents=True, exist_ok=True)
    for p in (ENV, PERSON, GHOST):
        assert p.exists(), p
    a_im = Image.open(PERSON).convert("RGB")
    b_im = Image.open(GHOST).convert("RGB")
    assert a_im.size == b_im.size, (a_im.size, b_im.size)
    w, h = a_im.size
    a = np.asarray(a_im).astype(np.float32) / 255
    b = np.asarray(b_im).astype(np.float32) / 255
    seam = round(w * SEAM)

    # 2 合成後: 境目から右を、幽霊の画でそのまま置き換える
    s2 = a.copy()
    s2[:, seam:] = b[:, seam:]

    # 3 境界ブレンド後: 境目の近くで明るさと色を揃え、幅を持たせて混ぜる
    band = slice(seam - 120, seam)
    gain = a[:, band].mean(axis=(0, 1)) / b[:, band].mean(axis=(0, 1))
    xs = np.arange(w, dtype=np.float32)
    t = np.clip((xs - (seam - FEATHER)) / FEATHER, 0, 1)
    m = (t * t * (3 - 2 * t))[None, :, None]
    fade = np.clip((xs - seam) / (w * 0.25), 0, 1)[None, :, None]      # 色の補正は境目から離れるほど弱める
    b2 = b * (gain[None, None, :] * (1 - fade) + fade)
    s3 = a * (1 - m) + b2 * m

    s4 = process(s3)

    frames = [("1_合成前", a), ("2_合成後", s2), ("3_境界ブレンド後", s3), ("4_画像加工後", s4)]
    imgs = []
    for name, arr in frames:
        im = to_img(arr)
        im.save(out / f"{name}_{w}x{h}.png")
        imgs.append(im)
    Image.open(ENV).convert("RGB").resize((w, h), Image.LANCZOS).save(out / f"素材_環境画像_無人_{w}x{h}.png")
    a_im.save(out / f"素材_生成1_左に人_{w}x{h}.png")
    b_im.save(out / f"素材_生成2_右に幽霊_{w}x{h}.png")

    # 境目の拡大（2 と 3 の差は引きでは見えにくい）と、境目の位置を引いた版
    box = (seam - 160, round(h * 0.24), seam + 80, round(h * 0.24) + 180)
    for name, im in zip(("2_合成後", "3_境界ブレンド後"), imgs[1:3]):
        im.crop(box).resize((960, 720), Image.LANCZOS).save(out / f"{name}_境目の拡大.png")
    guide = imgs[1].copy()
    ImageDraw.Draw(guide).line((seam, 0, seam, h), fill=(176, 42, 50), width=4)
    guide.save(out / f"2_合成後_境目に線_{w}x{h}.png")

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
    print(f"size={w}x{h} seam={seam} gain={np.round(gain, 3)}")


if __name__ == "__main__":
    main(sys.argv[1])
