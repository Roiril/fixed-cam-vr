# 廻リ視 DCEXPO 版 資料の執筆環境

元の Word（`IVRC2026_Roiril_v1.6.5.2.docx`・学会論文フォーマット）に寄せた A4・白地・2 段組の紙面を、
**HTML + CSS で書いて Edge で PDF にする**環境。シュビーが本文と図を直接編集し、機械検査で崩れを見つける。
Word には戻さない（図・注釈・段組の往復で壊れやすいため）。

```
paper.html            本文の正。図の並びと図番号もここ
paper.css             紙面（余白・文字サイズ・図の枠）。数値は :root に集約
figures/              図（生成物。出所は figures/SOURCES.json）
tools/prep_figures.py 図の素材を集めて figures/ へ（出所を記録・再実行可）
tools/fig_env.py      図1（配置図）をベクタで描く
tools/build.py        PDF 化 → ページ画像 → 検査
tools/tune.py         文字サイズ・行間を探索（下端の空き・段の空白・見出しの取り残しを点数化）
out/                  生成物（git 管理外）  out/paper.pdf  out/page-N.png  out/sheet.png
```

## 回し方

```bash
py -3.10 paper/dcexpo/tools/build.py --sheet   # PDF・ページ画像・検査（Python 3.10 = PyMuPDF あり）
py -3.10 paper/dcexpo/tools/tune.py --apply    # 本文を変えたあと。約 2 分。paper.css の文字サイズ・行間を更新
py -3.11 paper/dcexpo/tools/prep_figures.py    # 図を作り直すとき
```

段組は自動で流れるので、**本文か図を変えたら build → 必要なら tune → 目視**。
`out/sheet.png`（全ページ 1 枚）で全体、`out/page-N.png` で細部を見る。

## 検査（build.py）

- ページ数（`MAX_PAGES`。DCEXPO の規定が分かったら入れる。2026-09-30 時点は上限なし）
- 余白のはみ出し（画素で判定。`object-fit` の画像は PDF 座標では測れない）
- ページ下端の空き（最終頁以外で 22mm 超は NG）
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
  現行の構成と合わなくなったものを差し替えた
  - 図1 配置図：ハンドアウト用の `docs/onsite/fig_room.py` を論文の 1 段幅で読める文字にした版（実寸は同じ）
  - 図2 提示映像：CG 模型（台車の人形が写る）→ 実機の画面録画に
  - 図3 合成：CG の怪物 → 実写プレート・生成素材・マスク・提示映像の実物 4 枚
  - 図4 経路：元図のまま。台車の人形だけ画像から消した（`cv2.inpaint`＋帯の描き直し）
  - 図5 各カメラ：アプリが実際に使っている最新のプレート（`Assets/StreamingAssets/show/assets/plate_*_20260924_*`）
- 実機の画面は `output/quest-recordings/2026-09-27/quest-alpha/` の録画から抜く（`prep_figures.py`）。
  録画には作品内の日時・周の表示が入るので、そのまま証拠になる
- 引き伸ばしはしない。切り出しは黒帯の除去（`autocrop`）と `object-fit: cover` のみ

## 元の Word

`G:\マイドライブ\研究\IVRC2026\IVRC2026_Roiril_v1.6.5.2.docx`（読むだけ・変更しない）。
図の原本と本文は `prep_figures.py` がこのファイルから直接取り出す。
