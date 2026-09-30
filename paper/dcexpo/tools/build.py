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
    """_render_pdf_once を最大 3 回試す（headless Edge は起動直後に "Printing is not available" を返すことがある）。"""
    last = None
    for attempt in range(3):
        try:
            return _render_pdf_once(html, pdf, quiet)
        except (RuntimeError, OSError, ConnectionError) as e:
            last = e
            if not quiet:
                print(f"  [retry {attempt + 1}] {e}")
            time.sleep(1.5)
    raise last


def _render_pdf_once(html=HTML, pdf=PDF, quiet=False):
    """Edge を CDP（--remote-debugging-port）で動かし、組版スクリプトの完了を待って PDF 化する。
    戻り値は paginate.js の報告（dict）。ページ内の例外・console.error は標準出力へ出す。
    ⚠ msedge.exe は GUI サブシステムで --dump-dom の標準出力が空になるため、CDP で読む。"""
    import base64
    import json
    import socket
    import tempfile
    import urllib.request
    import websocket

    edge = next((p for p in EDGE if Path(p).exists()), None)
    if not edge:
        sys.exit("Edge が見つからない")
    with socket.socket() as sk:
        sk.bind(("127.0.0.1", 0))
        port = sk.getsockname()[1]
    prof = tempfile.mkdtemp(prefix="edge-paper-")
    proc = subprocess.Popen([edge, "--headless=new", "--disable-gpu", f"--remote-debugging-port={port}",
                             f"--user-data-dir={prof}", "--no-first-run", "--no-default-browser-check", "about:blank"],
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    t0 = time.time()
    try:
        ws_url = None
        for _ in range(100):
            try:
                tabs = json.load(urllib.request.urlopen(f"http://127.0.0.1:{port}/json", timeout=2))
                ws_url = next(t["webSocketDebuggerUrl"] for t in tabs if t.get("type") == "page")
                break
            except Exception:
                time.sleep(0.2)
        if not ws_url:
            sys.exit("Edge の CDP に繋がらない")
        ws = websocket.create_connection(ws_url, timeout=60, suppress_origin=True)
        mid = [0]
        logs = []

        def call(method, params=None):
            mid[0] += 1
            ws.send(json.dumps({"id": mid[0], "method": method, "params": params or {}}))
            while True:
                msg = json.loads(ws.recv())
                if msg.get("method") == "Runtime.exceptionThrown":
                    d = msg["params"]["exceptionDetails"]
                    logs.append("例外: " + (d.get("exception", {}).get("description") or d.get("text", "")))
                elif msg.get("method") == "Runtime.consoleAPICalled" and msg["params"]["type"] in ("error", "warning"):
                    logs.append("console." + msg["params"]["type"] + ": " + " ".join(str(a.get("value", a.get("description", ""))) for a in msg["params"]["args"]))
                if msg.get("id") == mid[0]:
                    if "error" in msg:
                        raise RuntimeError(f"{method}: {msg['error']}")
                    return msg.get("result", {})

        call("Page.enable")
        call("Runtime.enable")
        call("Page.navigate", {"url": html.as_uri()})
        report = None
        for _ in range(150):                                   # 最大 30 秒、組版の完了（data-ready）を待つ
            r = call("Runtime.evaluate", {"expression": "document.body && document.body.dataset.ready === '1' ? document.getElementById('report').textContent : ''", "returnByValue": True})
            v = r.get("result", {}).get("value")
            if v:
                report = json.loads(v)
                break
            time.sleep(0.2)
        res = call("Page.printToPDF", {"printBackground": True, "preferCSSPageSize": True,
                                        "displayHeaderFooter": False, "marginTop": 0, "marginBottom": 0,
                                        "marginLeft": 0, "marginRight": 0})
        pdf.write_bytes(base64.b64decode(res["data"]))
        ws.close()
    finally:
        # ⚠ proc.kill() は親のブラウザだけを止め、子（renderer・gpu・utility）が生き残って一時プロファイルを掴み続ける。
        #   2026-09-30 に 1119 プロセス・256 個のプロファイルが溜まり、C: の空きが 0 になった（実害）。
        #   必ず木ごと終了し、プロファイルの削除は失敗したら少し待って再試行する。
        #   proc.pid は起動用の短命なプロセスで、実体のブラウザは別の PID になることがある。
        #   そこで「このプロファイルのパスを CommandLine に持つ msedge」を全部止める（他の Edge には触れない）。
        name = Path(prof).name
        subprocess.run(["powershell", "-NoProfile", "-Command",
                        "Get-CimInstance Win32_Process -Filter \"Name='msedge.exe'\" | "
                        f"Where-Object {{ $_.CommandLine -like '*{name}*' }} | "
                        "ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }"],
                       capture_output=True)
        try:
            proc.wait(timeout=5)
        except Exception:
            pass
        import shutil
        for _ in range(10):
            shutil.rmtree(prof, ignore_errors=True)
            if not Path(prof).exists():
                break
            time.sleep(0.3)
    for m in logs:
        print("  [page]", m)
    if not quiet:
        print(f"PDF: {pdf.name} {pdf.stat().st_size/1e6:.2f}MB ({time.time()-t0:.1f}s)")
    return report


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


def check_layout(rep, quiet=False):
    """図を各ページの上端か下端に置く方針の検査（paginate.js の報告から）。"""
    ng = []
    if rep is None:
        return ["組版の報告が取れない（paginate.js が動いていない）"]
    if not quiet:
        print("図の置き場所（割り当て頁・位置 / 最初に引用している頁）:")
    for f in rep["figs"]:
        cited = f["cited_page"]
        if not quiet:
            print(f"  {f['id']}: p{f['page']} {f['pos']:6s} / 引用 p{cited}")
        if not f["placed"]:
            ng.append(f"{f['id']}: 割り当て頁 p{f['page']} が本文の頁数を超えている")
        elif cited is not None and not (cited - 1 <= f["page"] <= cited + 1):   # 引用の前後 1 頁まで
            ng.append(f"{f['id']}: 引用 p{cited} から 2 頁以上離れている（図 p{f['page']}）")
    pages = rep["pages"]
    for pg in pages[:-1]:
        if pg.get("text_mm", 999) < 45 * rep.get("cols", 1):
            ng.append(f"p{pg['page']}: 本文が {pg.get('text_mm', 0):.0f}mm しか無い（図で埋まっている。図の頁を分ける）")
        if pg["slack_mm"] > 9.5:
            ng.append(f"p{pg['page']}: 本文の窓に {pg['slack_mm']:.1f}mm の余り（行の区切りで詰められない。図の頁か寸法を見直す）")
    if not quiet:
        print("窓の余り(mm):", ", ".join(f"p{p['page']}={p['slack_mm']:.1f}" for p in pages))
    return ng


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
    report = render_pdf()
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
    ng = check_layout(report) + check(doc)
    if ng:
        print("\nNG:")
        for m in ng:
            print(" -", m)
        sys.exit(1)
    print("\nOK: 検査を通過")


if __name__ == "__main__":
    main()
