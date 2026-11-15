# 2 周目・3 周目の異変の絵（キービジュアル 4 枚・テレビの映像 4 枚・最後の顔 2 枚）を、生成した原本から配信用に作る。
# 使い方: py -3.11 scripts/prepare-anomaly-art.py <原本のフォルダ>
# 原本の置き場と生成条件は assets/anomaly-art-prompt.md。
#
# キービジュアルは、題字・惹句・担当表記を元の絵から書き戻す。生成は字を描き直すので、
# 字がずれると「頭へ戻った瞬間」に題字が動いて見える（写しと本物は同じ絵でも、1 周目と 2 周目で字が跳ぶ）。
import os, sys
import numpy as np
from PIL import Image, ImageFilter

if len(sys.argv) != 2:
    sys.exit("usage: prepare-anomaly-art.py <folder with out-*.png>")
RAW = sys.argv[1].rstrip("/\\") + "/"
A = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "assets") + "/"

# 生成名 → (元の絵, 配信名, 字を書き戻す枠（幅・高さに対する割合）)
HERO_TEXT = {
    "desktop": [(.03, .09, .49, .28), (.49, .13, .97, .64), (.79, .86, .995, .985)],
    "mobile": [(.15, .09, .95, .215), (.18, .205, .95, .48), (.64, .89, .97, .985)],
}
JOBS = [
    ("kv2-desktop", "hero-desktop.webp", "hero-lap2-desktop.webp", "desktop"),
    ("kv3-desktop", "hero-desktop.webp", "hero-lap3-desktop.webp", "desktop"),
    ("kv2-mobile", "hero-mobile.webp", "hero-lap2-mobile.webp", "mobile"),
    ("kv3-mobile", "hero-mobile.webp", "hero-lap3-mobile.webp", "mobile"),
    ("cam2-lap2", "camera-fiction-02.webp", "camera-fiction-02-lap2.webp", None),
    ("cam1-lap3", "camera-fiction-01.webp", "camera-fiction-01-lap3.webp", None),
    ("cam2-lap3", "camera-fiction-02.webp", "camera-fiction-02-lap3.webp", None),
    ("cam3-lap3", "camera-fiction-03.webp", "camera-fiction-03-lap3.webp", None),
]

# 最後の顔は元の絵が無い。画面を埋めるので、生成した寸法のまま配る
FINALS = [("final-desktop", "final-desktop.webp", (1672, 941)), ("final-mobile", "final-mobile.webp", (900, 1350))]

missing = [k for k, *_ in JOBS + FINALS if not os.path.isfile(f"{RAW}out-{k}.png")]
if missing:
    sys.exit("missing source images: " + ", ".join(missing))


def cover(img, size):
    """縦横比を保ったまま、元の寸法を覆うように拡大して中央を切る（引き伸ばさない）。"""
    w, h = size
    scale = max(w / img.width, h / img.height)
    img = img.resize((round(img.width * scale), round(img.height * scale)), Image.LANCZOS)
    left, top = (img.width - w) // 2, (img.height - h) // 2
    return img.crop((left, top, left + w, top + h))


def text_mask(orig, boxes):
    """元の絵の字（白い筆跡・赤い印）だけを取り出した、縁をぼかした覆い。"""
    rgb = np.asarray(orig, np.float32)
    bright = rgb.max(axis=2) > 120
    inside = np.zeros(bright.shape, bool)
    h, w = bright.shape
    for x0, y0, x1, y1 in boxes:
        inside[int(y0 * h):int(y1 * h), int(x0 * w):int(x1 * w)] = True
    m = Image.fromarray(((bright & inside) * 255).astype(np.uint8))
    m = m.filter(ImageFilter.MaxFilter(9)).filter(ImageFilter.GaussianBlur(3))
    return m


for key, src, dst, kind in JOBS:
    orig = Image.open(A + src).convert("RGB")
    gen = cover(Image.open(f"{RAW}out-{key}.png").convert("RGB"), orig.size)
    if kind:
        gen = Image.composite(orig, gen, text_mask(orig, HERO_TEXT[kind]))
    gen.save(A + dst, "WEBP", quality=82, method=6)
    print(f"{dst}: {gen.size[0]}x{gen.size[1]} {os.path.getsize(A + dst) // 1024}KB")

for key, dst, size in FINALS:
    img = cover(Image.open(f"{RAW}out-{key}.png").convert("RGB"), size)
    img.save(A + dst, "WEBP", quality=82, method=6)
    print(f"{dst}: {img.size[0]}x{img.size[1]} {os.path.getsize(A + dst) // 1024}KB")
