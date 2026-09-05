# -*- coding: utf-8 -*-
"""待機者に渡す資料を 4 版そろえて焼く。  py -3.11 docs/onsite/build_handout.py

  handout.html        … 正本（日本語）。文言はここだけを直す
  fig_route.py        … 経路図（fig-route.svg / fig-route-en.svg）
  → handout.pdf / handout-route.pdf / handout-en.pdf / handout-route-en.pdf
     （日本語 × English）×（経路なし × 経路つき）。刷るのは PDF

⚠ 版ごとに HTML を持たない。**同じ文言を何か所も直すことになり、必ずどれかが古くなる。**
   2026-09-05 に「Meta Quest 3 を使用した VR 体験」の削除を両版へ入れる話が出た時点でそう決めた。

⚠⚠ **英語版は正本を機械的に置き換えて作る。** `TRANSLATE` の左側は `handout.html` の中に
   そのまま在る文字列で、1 つでも見つからなければ**落ちる**。日本語を直したら英語も直る
   （直すまでビルドが通らない）ので、片方だけ古くなることが起きない。

⚠ 語はアプリ内の English に合わせる（`TitleNotice` / `CommsPanel` / `OutroReportText`）。
   調査 = survey / エージェント = agent / 装置 = device / 異変 = anomaly。
   **同じ人が紙と装置の両方を読む。**紙だけ別の語にしない。

⚠ ブラウザから直に刷ると日付と URL がヘッダ・フッタに乗る（作中資料に現実の言葉が混ざる）。
   だから PDF が配布物の正で、--no-pdf-header-footer を必ず付ける。
"""
import io, os, runpy, subprocess, sys

HERE = os.path.dirname(os.path.abspath(__file__))
CHROME = r"C:\Program Files\Google\Chrome\Application\chrome.exe"

ROUTE_CSS = """
    /* --- 経路（経路つきの版だけ。build_handout.py が差し込む） --- */
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

    /* ⚠ 繰り返しの語が途中で折れると読みが崩れる（「ゆっくりゆ／っくり」になった） */
    .sheet.exhibit .route .nb {
      white-space: nowrap;
    }

    /* ⚠ 最後の 1 行だけ枠で囲う（2026-09-06 ユーザー判定「強調しよう」）。
       この面でいちばん強い要素になる。太字だけだと上の 2 つの警告と区別が付かない */
    .sheet.exhibit .route .warn {
      border: .6pt solid var(--ink);
      padding: 3mm 4mm;
      margin: 1mm 0 0;
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

# ⚠ 日本語の組版の値をそのまま Latin に掛けると読めない（字間 .48em の "Survey Request" 等）。
#   ここは**見た目だけ**を直す。文言は TRANSLATE。
EN_CSS = """
    /* --- English 版だけ（build_handout.py が差し込む） ----------- */
    :root {
      --mincho: Georgia, "Times New Roman", "Yu Mincho", serif;
      --gothic: "Segoe UI", Arial, "Yu Gothic UI", sans-serif;
    }

    .sheet {
      letter-spacing: .01em;
    }

    h1 {
      font-size: 19pt;
      letter-spacing: .18em;
      text-indent: .18em;
    }

    .subject,
    .mgmt,
    dl.items dt {
      letter-spacing: .04em;
    }

    .slots .val {
      letter-spacing: .06em;
    }

    /* ⚠ 1 枚目の h2（字間 .22em）も掛かる。Latin だと「T h e   o b s e r …」になる */
    h2,
    .sheet.exhibit h2 {
      letter-spacing: .04em;
    }
"""

# ⚠ 図は壁と経路だけ（2026-09-05 ユーザー判定）。ゾーンの数とカメラには触れない。
# ⚠⚠ **周回数はこの面にだけ書く**（2026-09-06 ユーザー判定で 3 周を明記）。
#     1 枚目の依頼書は今までどおり数を伏せる（`canon/LEDGER.md` 0080。三周と書くとメタ読みされる）。
# ⚠ 「右手で」は `tools/walk-guide/build_walk_guide.py` が正本。
#    経路が時計回りなので壁はつねに右手側にある。左手で持つと後ろ向きに歩くことになる。
ROUTE_HTML = """
    <div class="band route">
      <!-- ⚠ 語は 1 枚目の依頼書に合わせる（2026-09-06 ユーザー判定）。
           あちらが「所定の経路を移動すること」なので、この面も「経路」。
           順路・道・ルートに書き換えない -->
      <h2>経路</h2>
      <div class="cols">
        <figure><!--FIG_ROUTE--></figure>
        <div>
          <p>壁のまわりを、矢印の向きに 3 周します。</p>
          <p>右手で手すりをたどりながら進みます。手は離さないでください。</p>
          <p>画面に映るのは、自分の視点の映像ではありません。</p>
          <p class="warn">足元が見づらいので、<span class="nb">ゆっくりゆっくり</span>、慎重に歩いてください。</p>
        </div>
      </div>
    </div>
"""

ROUTE_HTML_EN = """
    <div class="band route">
      <h2>Route</h2>
      <div class="cols">
        <figure><!--FIG_ROUTE--></figure>
        <div>
          <p>You walk around the wall three times, in the direction of the arrows.</p>
          <p>Follow the handrail with your right hand as you go. Do not let go of it.</p>
          <p>What you see on the screen is not the view from where you are standing.</p>
          <p class="warn">Your feet are hard to see. Walk <span class="nb">slowly, very slowly</span>,
            and with care.</p>
        </div>
      </div>
    </div>
"""

# 左は handout.html の中にそのまま在る文字列。1 つでも欠けたら落ちる。
TRANSLATE = [
    ('<html lang="ja">', '<html lang="en">'),
    ("<title>調査依頼書 / 展示の注意 — 待機者に渡す資料（廻リ視）</title>",
     "<title>Survey Request / Visitor Notice — handout (Mawarimi)</title>"),

    # 1 枚目 — 作中資料
    ("調査者番号 ", "Surveyor No. "),
    ('発行日 <span class="val">２０２６年９月６日</span>',
     '発行日 <span class="val">6 September 2026</span>'),
    ("発行日 ", "Date of issue "),
    ("<h1>調査依頼書</h1>", "<h1>Survey Request</h1>"),
    ("件名：壁面周辺の観測調査について",
     "Subject: observational survey of the area around the wall"),
    ("下記のとおり、観測調査を依頼する。",
     "The observational survey set out below is hereby requested."),
    ('<p class="ki">記</p>', ""),
    ("<dt>一、調査対象</dt>", "<dt>1. Subject of the survey</dt>"),
    ('<span class="redact">██</span>地点にて回収された壁面',
     'Wall surface recovered at site <span class="redact">██</span>'),
    ("管理番号：ＳＨ-ＫＢ-０２", "Control No.: SH-KB-02"),
    ("<dt>二、対象の詳細</dt>", "<dt>2. Details of the subject</dt>"),
    ("調査者への開示は行わない。", "Not disclosed to the surveyor."),
    ("<dt>三、調査方法</dt>", "<dt>3. Method</dt>"),
    ("観測装置を装着し、表示される映像を確認しながら所定の経路を移動すること。",
     "Wear the observation device and proceed along the designated route "
     "while watching the images shown."),
    ("映像は、調査者の位置に応じて自動的に切り替わる。",
     "The images switch automatically according to the surveyor's position."),
    ("観測中、エージェントが装置を通じて調査者の活動を支援する。",
     "During observation, an agent assists the surveyor through the device."),
    ("指示を受けた場合は、その内容に従うこと。", "Follow any instruction given."),
    ("<dt>四、記録</dt>", "<dt>4. Records</dt>"),
    ("映像内に異変を見つけた場合は、その都度、手元のボタンを長押しすること。",
     "Whenever an anomaly is found in the images, hold down the button in your hand."),
    ("エージェントが解析し、異変が検出された場合は装置が対処を試みる。",
     "The agent analyses it, and if an anomaly is detected the device attempts to remove it."),
    ('<p class="owari">以上</p>', ""),
    ("<h2>観測装置について</h2>", "<h2>The observation device</h2>"),
    ("本装置は、通常では確認できない怪異を観測するための装置である。",
     "This device exists to observe phenomena that cannot ordinarily be perceived."),
    ("観測中は、調査を中断する場合を除き、装置を外さないこと。",
     "Do not remove the device during observation, except to discontinue the survey."),
    ("上記の内容を確認した。", "I have read and understood the above."),
    ("調査者署名", "Surveyor's signature"),
    ("本書は調査終了後に回収する。", "This document is collected at the end of the survey."),

    # 2 枚目 — 展示の注意。⚠ 訳文はアプリの TitleNotice と同じ文（安全の掲示なので割らない）
    ("<h2>ご体験の前に</h2>", "<h2>Before you begin</h2>"),
    ("本作品には、ホラー表現および不安や恐怖を感じる演出が含まれます。",
     "This work contains horror imagery and scenes meant to unsettle or frighten."),
    ("体験中に気分が悪くなった場合は、その場で立ち止まり、ヘッドセットを外してスタッフにお声がけください。",
     "If you feel unwell at any point, stop where you are, remove the headset "
     "and let a member of staff know."),
]

TITLE_ROUTE = {
    "ja": ("<title>調査依頼書 / 展示の注意 — 待機者に渡す資料（廻リ視）</title>",
           "<title>調査依頼書 / 展示の注意（経路つき） — 待機者に渡す資料（廻リ視）</title>"),
    "en": ("<title>Survey Request / Visitor Notice — handout (Mawarimi)</title>",
           "<title>Survey Request / Visitor Notice with route — handout (Mawarimi)</title>"),
}

runpy.run_path(os.path.join(HERE, "fig_route.py"), run_name="__main__")


def read_svg(name):
    s = io.open(os.path.join(HERE, name), encoding="utf-8").read()
    return s.replace('<?xml version="1.0" encoding="UTF-8"?>\n', "")


SVG = {"ja": read_svg("fig-route.svg"), "en": read_svg("fig-route-en.svg")}

SRC = io.open(os.path.join(HERE, "handout.html"), encoding="utf-8").read()
for marker in ("/*ROUTE-CSS*/", "<!--ROUTE-->"):
    if marker not in SRC:
        raise SystemExit(f"handout.html に差し込み口 {marker} が無い")


def build(lang, with_route):
    s = SRC
    if lang == "en":
        for ja, en in TRANSLATE:
            if ja not in s:
                raise SystemExit(f"訳の元が handout.html に無い（文言を変えたら TRANSLATE も直す）: {ja}")
            s = s.replace(ja, en)

    css = (ROUTE_CSS.strip("\n") if with_route else "")
    if lang == "en":
        css = (css + "\n" + EN_CSS.strip("\n")).strip("\n")
    body = ""
    if with_route:
        html = ROUTE_HTML_EN if lang == "en" else ROUTE_HTML
        body = html.strip("\n").replace("<!--FIG_ROUTE-->", SVG[lang])

    s = s.replace("/*ROUTE-CSS*/", css).replace("<!--ROUTE-->", body)
    if with_route:
        s = s.replace(*TITLE_ROUTE[lang])
    s = s.replace("<body>", "<!-- ⚠ 生成物。直接編集しない。直すのは handout.html と "
                            "build_handout.py -->\n\n<body>", 1)

    name = "handout" + ("-route" if with_route else "") + ("-en" if lang == "en" else "")
    if name == "handout":
        # ⚠⚠ 日本語・経路なしは**正本そのもの**。書き出すと handout.html を上書きして
        #    差し込み口（/*ROUTE-CSS*/ と <!--ROUTE-->）が消える（2026-09-06 に 1 度やった）。
        #    どちらも註釈なので、正本をそのまま刷れる
        print("keep ", os.path.join(HERE, "handout.html"), "（正本をそのまま刷る）")
        return name
    dst = os.path.join(HERE, name + ".html")
    io.open(dst, "w", encoding="utf-8", newline="\n").write(s)
    print("wrote", dst, len(s), "bytes")
    return name


names = [build(lang, r) for lang in ("ja", "en") for r in (False, True)]

if not os.path.exists(CHROME):
    sys.exit(f"Chrome が見つからない: {CHROME}")

for name in names:
    pdf = os.path.join(HERE, name + ".pdf")
    subprocess.run([CHROME, "--headless=new", "--disable-gpu", "--no-pdf-header-footer",
                    "--print-to-pdf=" + pdf, os.path.join(HERE, name + ".html")],
                   check=True, capture_output=True)
    print("wrote", pdf, os.path.getsize(pdf), "bytes")
