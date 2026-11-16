"""DCEXPO（INTER BEE IGNITION×DCEXPO）のピッチ用スライドを 1 枚の HTML に組む。

    py -3.11 paper/dcexpo-pitch/build.py            # deck.html -> reports/2026-11-16_dcexpo-pitch.html
    py -3.11 paper/dcexpo-pitch/build.py variants   # variants.html -> out/variants.html
    py -3.11 paper/dcexpo-pitch/build.py --no-fonts # 書体を取りに行かない（Yu Mincho で組む）

ソースの中の @@パス@@ を data URI に、/*@@FONTS@@*/ を @font-face に、<!--@@QR@@--> を QR の SVG に置き換える。
パスはリポジトリのルートから。img/ の派生画像（キービジュアル・壁・幕・体験者の写真）はここで作る。
"""
import base64
import io
import os
import re
import sys
import urllib.parse
import urllib.request
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent
IMG = HERE / "img"
OUT = HERE / "out"
SITE = "https://mawarimi.vercel.app/"
VISITOR = Path(r"G:\マイドライブ\shubie\reports\2026-11-16_siggraph-entry\Mawarimi_photo_visitor.jpg")
UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36"
FONTS = (("Yuji Boku", "400"), ("Shippori Mincho", "400;600"))
MIME = {".jpg": "image/jpeg", ".png": "image/png", ".webp": "image/webp", ".woff2": "font/woff2", ".svg": "image/svg+xml"}


def save_atomic(im, path, **kw):
    tmp = path.with_name(path.stem + ".tmp" + path.suffix)
    im.save(tmp, **kw)
    os.replace(tmp, path)


def prep():
    IMG.mkdir(exist_ok=True)
    OUT.mkdir(exist_ok=True)

    # キービジュアル: 右下の担当表記を、文字の無い版の同じ場所で埋める
    a = Image.open(ROOT / "tools/title-art/keyvisual-layout-v3.png").convert("RGB")
    b = Image.open(ROOT / "tools/title-art/keyvisual-clean-v2.png").convert("RGB")
    assert a.size == b.size == (1672, 941), (a.size, b.size)
    m = Image.new("L", a.size, 0)
    ImageDraw.Draw(m).rectangle((1374, 829, 1640, 927), fill=255)
    m = m.filter(ImageFilter.GaussianBlur(9))
    kv = Image.composite(b, a, m).resize((1920, 1080), Image.LANCZOS)
    save_atomic(kv, IMG / "kv.jpg", quality=92)

    # 壁: 縦長の漆喰を横にして 16:9 に切る
    w = Image.open(ROOT / "website/assets/wall.webp").convert("RGB").rotate(90, expand=True)
    s = 1920 / w.width
    w = w.resize((1920, round(w.height * s)), Image.LANCZOS)
    top = (w.height - 1080) // 2
    save_atomic(w.crop((0, top, 1920, top + 1080)), IMG / "wall.jpg", quality=86)

    # 幕: ひだを縦に伸ばし、上にレールを掛ける
    folds = Image.open(ROOT / "website/assets/drape-folds.webp").convert("RGB").resize((1920, 1080), Image.LANCZOS)
    rail = Image.open(ROOT / "website/assets/drape-rail.webp").convert("RGBA").resize((1920, 300), Image.LANCZOS)
    # レールの絵の下端は、ひだへ溶かす（切れ目を残さない）
    fade = Image.new("L", (1, 300), 255)
    for y in range(150, 300):
        fade.putpixel((0, y), round(255 * (300 - y) / 150))
    alpha = Image.composite(rail.getchannel("A"), Image.new("L", rail.size, 0), fade.resize(rail.size))
    rail.putalpha(alpha)
    drape = folds.convert("RGBA")
    drape.alpha_composite(rail, (0, -52))
    save_atomic(drape.convert("RGB"), IMG / "drape.jpg", quality=86)

    # 体験者の写真
    assert VISITOR.exists(), VISITOR
    v = Image.open(VISITOR).convert("RGB")
    # 左右の黒い帯を落とす
    lum = v.convert("L").resize((v.width, 1), Image.BOX)
    xs = [x for x in range(v.width) if lum.getpixel((x, 0)) > 24]
    v = v.crop((xs[0] + 2, 0, xs[-1] - 1, v.height))
    v = v.resize((1280, round(v.height * 1280 / v.width)), Image.LANCZOS)
    save_atomic(v, IMG / "visitor.jpg", quality=88)


def data_uri(rel):
    p = (ROOT / rel).resolve()
    assert p.exists(), f"無い: {rel}"
    mime = MIME[p.suffix.lower()]
    return f"data:{mime};base64," + base64.b64encode(p.read_bytes()).decode("ascii")


def fetch(url):
    req = urllib.request.Request(url, headers={"User-Agent": UA})
    with urllib.request.urlopen(req, timeout=30) as r:
        return r.read()


def font_faces(text):
    chars = "".join(sorted({c for c in text if not c.isspace() and ord(c) > 0x20}))
    out = []
    for family, weights in FONTS:
        url = (
            "https://fonts.googleapis.com/css2?family="
            + family.replace(" ", "+")
            + ":wght@" + weights
            + "&text=" + urllib.parse.quote(chars)
            + "&display=block"
        )
        css = fetch(url).decode("utf-8")
        faces = re.findall(r"@font-face\s*{[^}]*}", css)
        assert faces, f"書体が取れない: {family}"
        for face in faces:
            src = re.search(r"url\((https://[^)]+)\)", face).group(1)
            b64 = base64.b64encode(fetch(src)).decode("ascii")
            out.append(face.replace(src, f"data:font/woff2;base64,{b64}"))
    for weight in ("400", "500"):
        uri = data_uri(f"website/assets/ibm-plex-mono-{weight}.woff2")
        out.append(f'@font-face{{font-family:"IBM Plex Mono";font-weight:{weight};src:url({uri}) format("woff2")}}')
    return "\n".join(out)


def qr_svg():
    import segno

    buf = io.BytesIO()
    segno.make(SITE, error="m").save(buf, kind="svg", scale=10, border=2, dark="#15110e", light="#ece5d8", xmldecl=False, svgns=True, nl=False)
    return buf.getvalue().decode("utf-8")


def build(src_name, out_path, with_fonts):
    src = (HERE / src_name).read_text(encoding="utf-8")
    visible = re.sub(r"<style.*?</style>|<script.*?</script>|<[^>]+>", "", src, flags=re.S)
    html = re.sub(r"@@([^@\n]+)@@", lambda m: data_uri(m.group(1)) if "/" in m.group(1) else m.group(0), src)
    html = html.replace("/*@@FONTS@@*/", font_faces(visible) if with_fonts else "")
    html = html.replace("<!--@@QR@@-->", qr_svg())
    left = re.findall(r"@@[^@\n]+@@", html)
    assert not left, left
    out_path.parent.mkdir(parents=True, exist_ok=True)
    tmp = out_path.with_suffix(".tmp")
    tmp.write_text(html, encoding="utf-8", newline="\n")
    os.replace(tmp, out_path)
    print(f"{out_path}  {out_path.stat().st_size / 1e6:.2f} MB")


def main():
    args = sys.argv[1:]
    with_fonts = "--no-fonts" not in args
    prep()
    if "variants" in args:
        build("variants.html", OUT / "variants.html", with_fonts)
    else:
        build("deck.html", ROOT / "reports" / "2026-11-16_dcexpo-pitch.html", with_fonts)


if __name__ == "__main__":
    main()
