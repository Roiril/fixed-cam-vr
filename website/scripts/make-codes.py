"""記録の荷札に刷るバーコードと、銘板に貼る QR を SVG で作る。

    py -3.11 scripts/make-codes.py

出力は scripts/codes/ の 3 本。index.html へはこの中身をそのまま貼る（インライン）ので、
dist/ へは配信しない。色は currentColor なので、貼った先の文字色がそのまま棒の色になる。

  code128-ivrc2026.svg   Code 128 B で "IVRC2026" を符号化したもの
  code128-dcexpo2026.svg 同じく "DCEXPO2026"
  qr-site.svg            https://mawarimi.vercel.app/ の QR（segno）

Code 128 は依存なしの自前実装。QR は segno（py -3.11 -m pip install segno）を使う。
どちらも読み取り機に通る本物で、当て字の模様ではない。
"""
import sys
from pathlib import Path

# Code 128 の符号表。添字が符号値で、値は 11 モジュールを黒白交互の幅で表したもの。
PATTERNS = [
    "212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312", "132212", "221213",
    "221312", "231212", "112232", "122132", "122231", "113222", "123122", "123221", "223211", "221132",
    "221231", "213212", "223112", "312131", "311222", "321122", "321221", "312212", "322112", "322211",
    "212123", "212321", "232121", "111323", "131123", "131321", "112313", "132113", "132311", "211313",
    "231113", "231311", "112133", "112331", "132131", "113123", "113321", "133121", "313121", "211331",
    "231131", "213113", "213311", "213131", "311123", "311321", "331121", "312113", "312311", "332111",
    "314111", "221411", "431111", "111224", "111422", "121124", "121421", "141122", "141221", "112214",
    "112412", "122114", "122411", "142112", "142211", "241211", "221114", "413111", "241112", "134111",
    "111242", "121142", "121241", "114212", "124112", "124211", "411212", "421112", "421211", "212141",
    "214121", "412121", "111143", "111341", "131141", "114113", "114311", "411113", "411311", "113141",
    "114131", "311141", "411131", "211412", "211214", "211232", "2331112",
]
START_B = 104
STOP = 106

SITE_URL = "https://mawarimi.vercel.app/"
CODES = [("code128-ivrc2026.svg", "IVRC2026"), ("code128-dcexpo2026.svg", "DCEXPO2026")]


def encode_b(text):
    """Code 128 B の符号値の列（開始・チェックディジット・終了つき）を返す。"""
    values = [START_B]
    for ch in text:
        code = ord(ch)
        if code < 32 or code > 126:
            raise ValueError("Code 128 B に入らない文字です: %r" % ch)
        values.append(code - 32)
    checksum = values[0]
    for i, v in enumerate(values[1:], start=1):
        checksum += i * v
    values.append(checksum % 103)
    values.append(STOP)
    return values


def to_svg(text, module=1.0, height=40.0):
    """棒 1 本を 1 つの矩形として 1 本の path にまとめた SVG を返す。"""
    x = 0.0
    parts = []
    for v in encode_b(text):
        for i, w in enumerate(PATTERNS[v]):
            width = int(w) * module
            if i % 2 == 0:
                parts.append("M%.2f 0h%.2fv%.2fh-%.2fz" % (x, width, height, width))
            x += width
    return (
        '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 %.2f %.2f" '
        'preserveAspectRatio="none" shape-rendering="crispEdges" '
        'aria-hidden="true" focusable="false">'
        '<path fill="currentColor" d="%s"/></svg>'
    ) % (x, height, "".join(parts))


def qr_svg(payload):
    """segno の出力から装飾用の class を外し、読み上げから隠す属性を足したものを返す。"""
    import io

    import segno

    buffer = io.BytesIO()
    segno.make(payload, error="m").save(
        buffer, kind="svg", border=0, scale=1, omitsize=True,
        xmldecl=False, nl=False, dark="#000000",
    )
    svg = buffer.getvalue().decode("utf-8")
    svg = svg.replace(' class="segno"', "").replace(' class="qrline"', "")
    for black in ('stroke="#000000"', 'stroke="#000"'):
        svg = svg.replace(black, 'stroke="currentColor"')
    if 'stroke="currentColor"' not in svg:
        raise ValueError("QR の線の色を currentColor に置き換えられませんでした")
    return svg.replace(
        "<svg ", '<svg shape-rendering="crispEdges" aria-hidden="true" focusable="false" ', 1
    )


def write(path, svg):
    tmp = path.with_suffix(path.suffix + ".tmp")
    tmp.write_text(svg, encoding="utf-8", newline="\n")
    tmp.replace(path)
    print("%s %d bytes" % (path.name, len(svg)))


def main():
    out_dir = Path(__file__).resolve().parent / "codes"
    out_dir.mkdir(exist_ok=True)
    for name, text in CODES:
        write(out_dir / name, to_svg(text))
    write(out_dir / "qr-site.svg", qr_svg(SITE_URL))
    return 0


if __name__ == "__main__":
    sys.exit(main())
