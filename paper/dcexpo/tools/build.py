"""paper.html → out/paper.pdf → out/page-N.png ＋ 機械検査。

    py -3.10 paper/dcexpo/tools/build.py            # PDF 化・ページ画像・検査
    py -3.10 paper/dcexpo/tools/build.py --sheet    # ページを 1 枚に並べた out/sheet.png も作る

PyMuPDF（fitz）が入っている 3.10 で動かす（3.11 には無い）。PDF 化は Edge（headless）。
検査は「ページ数」「本文が余白を越えていないか」「図が全部ページ上にあるか（画像の数と図題の位置）」
「ページ下端の空き」「フォントの埋め込み」。合格 = 終了コード 0。
"""
import re
import subprocess
import sys
import time
from pathlib import Path

import fitz  # PyMuPDF

sys.stdout.reconfigure(encoding="utf-8")

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "out"
OUT.mkdir(exist_ok=True)
HTML = ROOT / "paper.html"
PDF = OUT / "paper.pdf"
EDGE = [r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        r"C:\Program Files\Microsoft\Edge\Application\msedge.exe"]

MM = 72 / 25.4
# paper.css の :root と合わせる（変えたら両方）
M_TOP, M_BOTTOM, M_SIDE = 16, 15, 19
MAX_PAGES = None           # ページ数の上限。DCEXPO 側の規定が分かったら数を入れる（2026-09-30 時点では上限なし）
GAP_LIMIT_MM = 22          # 最終頁以外で、本文の下端が下余白よりこれ以上手前なら「空き」


def render_pdf(html=HTML, pdf=PDF, quiet=False):
    edge = next((p for p in EDGE if Path(p).exists()), None)
    if not edge:
        sys.exit("Edge が見つからない")
    if pdf.exists():
        pdf.unlink()
    cmd = [edge, "--headless=new", "--disable-gpu", "--no-pdf-header-footer",
           "--run-all-compositor-stages-before-draw", "--virtual-time-budget=8000",
           f"--print-to-pdf={pdf}", html.as_uri()]
    t = time.time()
    subprocess.run(cmd, check=True, capture_output=True, timeout=120)
    for _ in range(50):                      # 書き出し完了待ち
        if pdf.exists() and pdf.stat().st_size > 0:
            break
        time.sleep(0.2)
    if not quiet:
        print(f"PDF: {pdf.name} {pdf.stat().st_size/1e6:.2f}MB ({time.time()-t:.1f}s)")


def html_with_vars(vars_: dict, name="_tune.html") -> Path:
    """paper.html の :root の CSS 変数を上書きした一時 HTML（同じ場所に置くので相対パスが生きる）。"""
    css = ":root{" + ";".join(f"--{k}:{v}" for k, v in vars_.items()) + "}"
    src = HTML.read_text(encoding="utf-8")
    dst = ROOT / name
    with open(dst, "w", encoding="utf-8", newline="\n") as f:
        f.write(src.replace("</head>", "<style>" + css + "</style>\n</head>", 1))
    return dst


def page_metrics(doc):
    """ページごとの下端の空き（左右の段別・mm）と、見出しが段の最下部に取り残されていないか。"""
    import numpy as np
    W, H = doc[0].rect.width, doc[0].rect.height
    dpi = 60
    px = dpi / 25.4
    res = []
    for pno, page in enumerate(doc, 1):
        pm = page.get_pixmap(dpi=dpi, colorspace=fitz.csGRAY)
        a = np.frombuffer(pm.samples, dtype=np.uint8).reshape(pm.height, pm.width)
        ink = a < 235
        t, b = int((M_TOP - 1) * px), int((H / MM - M_BOTTOM + 1) * px)
        l, r = int(M_SIDE * px), int((W / MM - M_SIDE) * px)
        mid = (l + r) // 2
        def gap(x0, x1):
            rows = np.where(ink[t:b, x0:x1].any(axis=1))[0]
            y = (t + (rows.max() if len(rows) else 0)) / px
            return (H / MM - M_BOTTOM) - y
        # 見出し（太字のゴシック）が段の下端 18mm 以内に居て、その下に本文が続いていない
        orphan = 0
        for blk in page.get_text("dict")["blocks"]:
            for ln in blk.get("lines", []):
                txt = "".join(sp["text"] for sp in ln["spans"]).strip()
                if re.match(r"^\d(\.\d)?[．.　 ]", txt) and ln["spans"][0]["flags"] & 16 and ln["bbox"][3] > H - (M_BOTTOM + 18) * MM:
                    orphan += 1
        def blank_run(x0, x1):
            """段の中で、上余白から最後の描画までの間にある最長の空白行（mm）。"""
            rows = ink[t:b, x0:x1].any(axis=1)
            idx = np.where(rows)[0]
            if not len(idx):
                return 0.0
            best = run = 0
            for v in rows[: idx.max() + 1]:
                run = 0 if v else run + 1
                best = max(best, run)
            return best / px
        res.append({"page": pno, "gap_l": gap(l, mid), "gap_r": gap(mid, r), "gap": gap(l, r), "orphan": orphan,
                    "blank_l": blank_run(l, mid), "blank_r": blank_run(mid, r)})
    return res


def check(doc):
    ng = []
    html = HTML.read_text(encoding="utf-8")
    # ラスタ画像だけ数える（SVG はベクタで、PDF 上の「画像」には数えられない）
    n_img = len(re.findall(r'<img\b[^>]*src="[^"]+\.(?:jpg|jpeg|png|webp)"', html))
    captions = re.findall(r"<figcaption>(図\d+)", html)
    W, H = doc[0].rect.width, doc[0].rect.height
    print(f"ページ数 {len(doc)}" + (f" / 上限 {MAX_PAGES}" if MAX_PAGES else "（上限なし）"))
    if MAX_PAGES and len(doc) > MAX_PAGES:
        ng.append(f"ページ数 {len(doc)} > {MAX_PAGES}")

    seen_img = 0
    cap_pages = {}
    import numpy as np
    for pno, page in enumerate(doc, 1):
        blocks = [b for b in page.get_text("blocks") if b[4].strip()]
        imgs = page.get_image_info()
        seen_img += len(imgs)
        # 余白・下端の検査は画素で行う（object-fit の画像は PDF 上では切り抜き前の枠で出るため、座標では測れない）
        dpi = 60
        pm = page.get_pixmap(dpi=dpi, colorspace=fitz.csGRAY)
        a = np.frombuffer(pm.samples, dtype=np.uint8).reshape(pm.height, pm.width)
        ink = a < 235
        px = dpi / 25.4                                   # 1mm あたりの画素
        l, r = int((M_SIDE - 1) * px), int((W / MM - M_SIDE + 1) * px)
        t, b = int((M_TOP - 1) * px), int((H / MM - M_BOTTOM + 1) * px)
        if ink[:, :l].any() or ink[:, r:].any():
            ng.append(f"p{pno}: 内容が左右の余白にはみ出している")
        if ink[b:, :].any():
            ng.append(f"p{pno}: 内容が下余白にはみ出している")
        if ink[:t, :].any():
            ng.append(f"p{pno}: 内容が上余白にはみ出している")
        rows = np.where(ink[t:b, l:r].any(axis=1))[0]
        y1_mm = (t + (rows.max() if len(rows) else 0)) / px
        gap = (H / MM - M_BOTTOM) - y1_mm
        last = pno == len(doc)
        print(f"  p{pno}: 画像{len(imgs)}枚 本文ブロック{len(blocks)} 下端の空き {gap:.1f}mm{'（最終頁）' if last else ''}")
        if not last and gap > GAP_LIMIT_MM:
            ng.append(f"p{pno}: 下端に {gap:.1f}mm の空き（図の置き方か段組を見直す）")
        for cap in captions:
            if page.search_for(cap + "　"):
                cap_pages.setdefault(cap, pno)
        for f in page.get_fonts():
            if f[2] != "Type3" and (f[1] in ("", None) or f[3] == ""):   # Type3 は SVG 内の文字
                ng.append(f"p{pno}: フォント {f[3]} が埋め込まれていない可能性")
        if "�" in page.get_text():
            ng.append(f"p{pno}: 文字化け（U+FFFD）")

    print(f"図: <img> {n_img} 個 / PDF 上の画像 {seen_img} 枚")
    if seen_img < n_img:
        ng.append(f"画像が足りない: html {n_img} / pdf {seen_img}")
    for cap in captions:
        if cap not in cap_pages:
            ng.append(f"{cap} の図題が PDF に無い")
    print("図題の頁:", ", ".join(f"{c}=p{cap_pages.get(c, '?')}" for c in captions))
    return ng


def main():
    render_pdf()
    doc = fitz.open(PDF)
    for old in OUT.glob("page-*.png"):
        old.unlink()
    for i, page in enumerate(doc, 1):
        page.get_pixmap(dpi=110).save(OUT / f"page-{i}.png")
    if "--sheet" in sys.argv:
        from PIL import Image
        ims = [Image.open(OUT / f"page-{i}.png") for i in range(1, len(doc) + 1)]
        sheet = Image.new("RGB", (sum(i.width for i in ims) + 10 * (len(ims) - 1), ims[0].height), (120, 120, 120))
        x = 0
        for im in ims:
            sheet.paste(im, (x, 0)); x += im.width + 10
        sheet.save(OUT / "sheet.png")
    ng = check(doc)
    if ng:
        print("\nNG:")
        for m in ng:
            print(" -", m)
        sys.exit(1)
    print("\nOK: 検査を通過")


if __name__ == "__main__":
    main()
