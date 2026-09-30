# 廻リ視 DCEXPO 版 資料の執筆環境

元の Word（`IVRC2026_Roiril_v1.6.5.2.docx`・学会論文フォーマット）に寄せた A4・白地・2 段組（本文）の紙面を、
**HTML + CSS で書いて Edge で PDF にする**環境。シュビーが本文と図を直接編集し、機械検査で崩れを見つける。
Word には戻さない（図・注釈・段組の往復で壊れやすいため）。

```
paper.html            本文の正。図の並びと図番号もここ
paper.css             紙面（余白・文字サイズ・図の枠）。数値は :root に集約
figures/              図（生成物。出所は figures/SOURCES.json）
tools/prep_figures.py 図の素材を集めて figures/ へ（出所を記録・再実行可）
paginate.js           組版（ページ割り付け・図の上下固定）
tools/build.py        PDF 化（CDP）→ ページ画像 → 検査
tools/tune.py         文字サイズ・行間を探索（窓の余り・図の規則違反・ページ数を点数化）
out/                  生成物（git 管理外）  out/paper.pdf  out/page-N.png  out/sheet.png
```

## 回し方

```bash
py -3.10 paper/dcexpo/tools/build.py --sheet   # PDF・ページ画像・検査（Python 3.10 = PyMuPDF あり）
py -3.10 paper/dcexpo/tools/tune.py --apply    # 本文を変えたあと。約 3 分。paper.css の文字サイズ・行間を更新
py -3.11 paper/dcexpo/tools/prep_figures.py    # 図を作り直すとき
```

**本文か図を変えたら build →（文字量が変わったら）tune → 目視。**
`out/sheet.png`（全ページ 1 枚）で全体、`out/page-N.png` で細部を見る。

## 組版のしくみ（paginate.js）

- 本文は 2 段（`paper.css` の `--cols`。1 にすると 1 段組）。**図は各ページの上端か下端に、段をまたぐ全幅で固定**する（2026-09-30 ユーザー指示）。
  以前の CSS の段組は図の位置で崩れたので、段の割り付けも paginate.js が行う（左段 → 右段 → 次頁。最終頁は段を均等に割る）
- 本文から図の各コマを指す（図6(a) など）。図のパネル記号と本文の参照は `paper.html` の中で対になっている。図の枚数・順序を変えたら両方を直す
- `tools/assign.py "1b 2t 2b 3b 3t 3t 4t 4t"` で図の頁・位置（t=上端 b=下端）を試す（1 回 6 秒）
- 本文（`#stream`）を行単位でページに割り付ける。ページの本文の窓の高さ ＝ 内寸 − 見出し部 − 上の図 − 下の図。
  切れ目は行と行の間だけ。見出しでページを終えない・4 行以上の段落は前後に 2 行以上残す・3 行以下の段落は割らない
- 図の置き場所は `<figure>` の属性で決める: `data-page`（1 始まり）・`data-pos`（top | bottom）・
  同じ `data-row` の図は横に並べる（図式 2 枚など）。**図は本文の引用と同じ頁か次の頁に置く**（build が検査する）
- ページ数は本文と図の量で決まる。溢れたら `data-page` を動かすか、`paper.css` の図の幅（`#figN`）を小さくする
- Edge は CDP（`--remote-debugging-port`）で動かす。`msedge.exe` の `--dump-dom` は標準出力が空になる（GUI サブシステム）。
  組版の報告（図の頁・窓の余り）は CDP で読む

## 検査（build.py）

- ページ数（`MAX_PAGES`。DCEXPO の規定が分かったら入れる。2026-09-30 時点は上限なし）
- 余白のはみ出し（画素で判定。`object-fit` の画像は PDF 座標では測れない）
- 本文の窓の余り（最終頁以外で 9.5mm ＝ 約 1.5 行 超は NG）と、ページ下端の空き（22mm 超は NG）
- 図の置き場所（引用より前・2 頁以上あとは NG）
- 図が全部 PDF に載っているか・図題の頁
- フォントの埋め込み・文字化け
- 通っても**目視は必要**。数値では拾えない: 図の切れ方・ラベルの重なり・「その図が本文と合っているか」

## 文体（学会の版を引き継ぐ）

- 「である」調。句読点は `，` `．`（元の Word と同じ）。半角括弧は使わず `（ ）`
- 和欧の境界に半角スペースを入れない。数値と単位の間は入れる（`1.8 m`）
- 日本語の書き方は `~/.claude/reference/japanese-writing-review.md`（学術・技術文書の基準）
- DCEXPO は厳密に科学的である必要はない（ユーザー指示 2026-09-30）。SR の引用 [4] は着想として残し、
  「確信が保たれる」などの効果の主張は評価結果として書かない

## 図の方針

- **削らない**。元の Word の 5 図の役割（配置・提示映像・合成・経路・各カメラ）は残し、
  現行の構成と合わなくなったものを差し替えた（2026-09-30）
  - 図1 環境構成：**ユーザーが作った図**（生成した設営イメージ写真＋カメラA〜C・HMD・L字の壁の説明線）。
    `figures/env-fig1.png`。元ファイルは `~/Downloads/無題のプレゼンテーション.png`（`prep_figures.py` が複製）
  - 図2 提示映像：同じ瞬間の固定カメラ映像と体験者の視界（HMD）。`logs/shots` の撮影から
  - 図3 合成：生映像・合成している層・提示映像を、手形・別の空間・CG 人形の 3 場面で。`logs/shots` の撮影から
  - 図4 周回経路：**ユーザーが作った図**（真上から見た区間1〜3・カメラA〜C）。`figures/route-fig4.png`。
    元ファイルは `~/Downloads/無題のプレゼンテーション (2).png`（`prep_figures.py` が複製）
  - 図5 各カメラ：体験中の生映像（Phone 01/02/03）。`logs/shots` の撮影から
  - 図6〜8：実機（Quest）の画面録画と撮影から。導入〜結果表示・2周目の4画面・各周の画面
- 撮影（`logs/shots/<serial>/`）は左グリップの撮影機能の出力（`.claude/memory/experience_shots.md`）。
  1 回の押下で 生映像・合成層・最終合成・体験者の視界が同じ瞬間で揃う
- 実機の画面は `output/quest-recordings/2026-09-27/quest-alpha/` の録画から抜く（`prep_figures.py`）。
  録画には作品内の日時・周の表示が入るので、そのまま証拠になる
- 引き伸ばしはしない。切り出しは黒帯の除去（`autocrop`）と `object-fit: cover` のみ

## 元の Word

`G:\マイドライブ\研究\IVRC2026\IVRC2026_Roiril_v1.6.5.2.docx`（読むだけ・変更しない）。
図の原本と本文は `prep_figures.py` がこのファイルから直接取り出す。
