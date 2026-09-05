# -*- coding: utf-8 -*-
"""待機者に渡す資料を 2 版そろえて焼く。  py -3.11 docs/onsite/build_handout.py

  handout.html        … 正本。文言はここだけを直す
  fig_route.py        … 順路図（fig-route.svg）
  → handout-route.html … 正本の 2 枚目の末尾に「歩く順路」を足した版（生成物・直接編集しない）
  → handout.pdf / handout-route.pdf … 配布物。刷るのはこの PDF

⚠ 2 版を別々の HTML として持たない。**同じ文言を 2 か所直すことになり、必ず片方が古くなる。**
   2026-09-05 に「Meta Quest 3 を使用した VR 体験」の削除を両版へ入れる話が出た時点でそう決めた。

⚠ ブラウザから直に刷ると日付と URL がヘッダ・フッタに乗る（作中資料に現実の言葉が混ざる）。
   だから PDF が配布物の正で、--no-pdf-header-footer を必ず付ける。
"""
import io, os, runpy, subprocess, sys

HERE = os.path.dirname(os.path.abspath(__file__))
CHROME = r"C:\Program Files\Google\Chrome\Application\chrome.exe"

ROUTE_CSS = """
    /* --- 順路（順路版だけ。build_handout.py が差し込む） --------- */
    .sheet.exhibit .route .cols {
      display: flex;
      gap: 9mm;
      align-items: flex-start;
    }

    .sheet.exhibit .route figure {
      margin: 0;
      flex: 0 0 auto;
      width: 84mm;
    }

    .sheet.exhibit .route figure svg {
      display: block;
      width: 84mm;
      height: auto;
    }

    .sheet.exhibit .route .cols>div {
      flex: 1;
    }

    .sheet.exhibit .route .cols p:last-child {
      margin-bottom: 0;
    }

    @media screen and (max-width: 230mm) {
      .sheet.exhibit .route .cols {
        display: block;
      }

      .sheet.exhibit .route figure {
        width: auto;
        margin: 0 0 4mm;
      }
    }
"""

# ⚠ 図は壁と順路だけ（2026-09-05 ユーザー判定）。ゾーンの数とカメラには触れない。
# ⚠⚠ **周回数はこの面にだけ書く**（2026-09-06 ユーザー判定で 3 周を明記）。
#     1 枚目の依頼書は今までどおり数を伏せる（`canon/LEDGER.md` 0080。三周と書くとメタ読みされる）。
# ⚠ 「右手で」は `tools/walk-guide/build_walk_guide.py` が正本。
#    順路が時計回りなので壁はつねに右手側にある。左手で持つと後ろ向きに歩くことになる。
ROUTE_HTML = """
    <div class="band route">
      <h2>歩く順路</h2>
      <div class="cols">
        <figure><!--FIG_ROUTE--></figure>
        <div>
          <p>壁のまわりを、矢印の向きに 3 周します。</p>
          <p>右手で手すりをたどりながら進みます。手は離さないでください。</p>
          <p>画面に映るのは、自分の視点の映像ではありません。</p>
          <p class="warn">足元が見づらいので、気を付けてゆっくり歩いてください。</p>
        </div>
      </div>
    </div>
"""

runpy.run_path(os.path.join(HERE, "fig_route.py"), run_name="__main__")

svg = io.open(os.path.join(HERE, "fig-route.svg"), encoding="utf-8").read()
svg = svg.replace('<?xml version="1.0" encoding="UTF-8"?>\n', "")

src = io.open(os.path.join(HERE, "handout.html"), encoding="utf-8").read()
for marker in ("/*ROUTE-CSS*/", "<!--ROUTE-->"):
    if marker not in src:
        raise SystemExit(f"handout.html に差し込み口 {marker} が無い")

route = src.replace("/*ROUTE-CSS*/", ROUTE_CSS.strip("\n"))
route = route.replace("<!--ROUTE-->", ROUTE_HTML.strip("\n").replace("<!--FIG_ROUTE-->", svg))
route = route.replace("<title>調査依頼書 / 展示の注意 — 待機者に渡す資料（廻リ視）</title>",
                      "<title>調査依頼書 / 展示の注意（順路つき） — 待機者に渡す資料（廻リ視）</title>")
route = route.replace("<body>", "<!-- ⚠ 生成物。直接編集しない。直すのは handout.html と "
                                "build_handout.py -->\n\n<body>", 1)

dst = os.path.join(HERE, "handout-route.html")
io.open(dst, "w", encoding="utf-8", newline="\n").write(route)
print("wrote", dst, len(route), "bytes")

if not os.path.exists(CHROME):
    sys.exit(f"Chrome が見つからない: {CHROME}")

for name in ("handout", "handout-route"):
    pdf = os.path.join(HERE, name + ".pdf")
    subprocess.run([CHROME, "--headless=new", "--disable-gpu", "--no-pdf-header-footer",
                    "--print-to-pdf=" + pdf, os.path.join(HERE, name + ".html")],
                   check=True, capture_output=True)
    print("wrote", pdf, os.path.getsize(pdf), "bytes")
