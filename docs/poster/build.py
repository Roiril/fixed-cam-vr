"""展示の横に貼る A4 縦ポスターを組む。

    py -3.11 docs/poster/build.py            # 素材を作り、poster-*.html を PDF と PNG に描く
    py -3.11 docs/poster/build.py a          # 1 案だけ

素材（題字の 3 層・QR）は docs/poster/assets/ へ、出力は docs/poster/out/ へ置く。
題字は実機の版 Assets/Resources/Title/MawarimiTitle.png から取る（R=廻リ、G=視、A=払いとルビ）。
QR は公式サイトの URL を segno で符号化する（py -3.11 -m pip install segno）。
描画は Chrome のヘッドレス。書体は Google Fonts から読むので、描くときだけネットワークが要る。
"""
import subprocess
import sys
from pathlib import Path

from PIL import Image

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent
ASSETS = HERE / "assets"
OUT = HERE / "out"
SITE_URL = "https://mawarimi.vercel.app/"
CHROME = r"C:\Program Files\Google\Chrome\Application\chrome.exe"
CREAM = (236, 228, 212)
RED = (176, 40, 44)
# A4 を 96dpi で 794×1123 として組み、3.125 倍で 2481×3509（300dpi）に描く
CSS_W, CSS_H, SCALE = 794, 1123, 3.125


def title_layers():
    plate = Image.open(ROOT / "Assets/Resources/Title/MawarimiTitle.png")
    r, g, _, a = plate.split()
    box = Image.merge("RGB", (r, g, a)).getbbox()
    pad = 24
    box = (max(box[0] - pad, 0), max(box[1] - pad, 0), min(box[2] + pad, plate.width), min(box[3] + pad, plate.height))
    for name, alpha, colour in (("title-white", r, CREAM), ("title-red", g, RED), ("title-sub", a, CREAM)):
        layer = Image.new("RGBA", plate.size, colour + (0,))
        layer.putalpha(alpha)
        layer.crop(box).save(ASSETS / f"{name}.png")
    print("title", box[2] - box[0], box[3] - box[1])


def dolls_layer():
    """地の下の人形を明るく起こした版と、題字の手前に来る前列だけの版を作る。"""
    from PIL import ImageChops, ImageDraw, ImageFilter

    g = Image.open(ASSETS / "ground.png").convert("RGB")
    w, h = g.size
    lifted = g.point(lambda v: min(255, int(255 * (v / 255) ** 0.62 * 1.25)))
    # 明るくするのは人形（暗い画素）だけ。床の光はそのまま
    dark = g.convert("L").point(lambda v: 255 if v < 92 else 0).filter(ImageFilter.GaussianBlur(5))
    ramp = Image.new("L", (1, h))
    ramp.putdata([max(0, min(255, int((y - 960) / 140 * 255))) for y in range(h)])
    ramp = ramp.resize((w, h))
    alpha = ImageChops.multiply(dark, ramp)
    out = lifted.copy()
    out.putalpha(alpha)
    out.save(ASSETS / "dolls-lift.png")
    # 前列: 中央の頭・左右の大きい頭・その下の体
    front = Image.new("L", (w, h), 0)
    d = ImageDraw.Draw(front)
    d.ellipse((405, 1188, 640, 1440), fill=255)
    d.ellipse((-10, 1005, 205, 1300), fill=255)
    d.ellipse((822, 1068, 1040, 1330), fill=255)
    d.polygon([(0, 1180), (215, 1255), (330, 1420), (700, 1420), (800, 1290), (1024, 1250), (1024, h), (0, h)], fill=255)
    front = front.filter(ImageFilter.GaussianBlur(4))
    out2 = lifted.copy()
    out2.putalpha(ImageChops.multiply(front, alpha.point(lambda v: min(255, v * 3))))
    out2.save(ASSETS / "dolls-front.png")


def hands():
    """1 周目 B の手形の版（白地に暗い手形）を、乗算で床へ置ける形に切る。"""
    src = ROOT / "tools/web-compositor/static-inputs/hands_B_ratio_20261113.png"
    im = Image.open(src).convert("L").crop((0, 0, 290, 420))
    red = Image.merge("RGB", (im.point(lambda v: 255 - int((255 - v) * 0.55)),
                              im.point(lambda v: 255 - int((255 - v) * 0.95)),
                              im.point(lambda v: 255 - int((255 - v) * 0.97))))
    red.save(ASSETS / "hands.png")


def laurel():
    """受賞の左右に置く月桂樹の枝を SVG で描く（左の枝。右は反転して使う）。"""
    import math

    leaves = []
    cx, cy, r = 60, 50, 42
    for i in range(8):
        a = math.radians(96 + i * 20)
        x, y = cx + r * math.cos(a), cy + r * math.sin(a)
        rot = math.degrees(a) + 90
        for side in (-1, 1):
            leaves.append(f'<ellipse cx="{x:.1f}" cy="{y:.1f}" rx="2.1" ry="7.6" '
                          f'transform="rotate({rot + side * 38:.1f} {x:.1f} {y:.1f}) translate({side * 3.2} -5)"/>')
    svg = ('<svg xmlns="http://www.w3.org/2000/svg" viewBox="8 2 60 96" fill="#ece4d4">'
           + "".join(leaves) + "</svg>")
    (ASSETS / "laurel.svg").write_text(svg, encoding="utf-8")


def qr():
    import segno

    code = segno.make(SITE_URL, error="q")
    code.save(str(ASSETS / "qr.svg"), border=0, dark="#14100d", light=None, xmldecl=False, svgns=True, omitsize=True)
    print("qr modules", code.symbol_size(border=0)[0])


def render(name):
    html = HERE / f"poster-{name}.html"
    if not html.exists():
        sys.exit(f"missing {html}")
    url = html.as_uri()
    common = [CHROME, "--headless=new", "--disable-gpu", "--hide-scrollbars", "--virtual-time-budget=8000"]
    png = OUT / f"mawarimi-poster-{name}.png"
    pdf = OUT / f"mawarimi-poster-{name}.pdf"
    subprocess.run(common + [f"--window-size={CSS_W},{CSS_H}", f"--force-device-scale-factor={SCALE}",
                             f"--screenshot={png}", url], check=True, stdin=subprocess.DEVNULL,
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    subprocess.run(common + ["--no-pdf-header-footer", f"--print-to-pdf={pdf}", url], check=True,
                   stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    print(name, Image.open(png).size, pdf.stat().st_size // 1024, "KB")


def main():
    ASSETS.mkdir(exist_ok=True)
    OUT.mkdir(exist_ok=True)
    title_layers()
    dolls_layer()
    hands()
    laurel()
    qr()
    names = sys.argv[1:] or sorted(p.stem.split("-", 1)[1] for p in HERE.glob("poster-*.html"))
    for name in names:
        render(name)


if __name__ == "__main__":
    main()
