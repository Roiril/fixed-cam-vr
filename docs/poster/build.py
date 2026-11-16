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
    qr()
    names = sys.argv[1:] or sorted(p.stem.split("-", 1)[1] for p in HERE.glob("poster-*.html"))
    for name in names:
        render(name)


if __name__ == "__main__":
    main()
