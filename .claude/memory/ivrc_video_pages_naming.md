---
name: ivrc_video_pages_naming
description: IVRC動画 pages/ の透過PNGはファイル名固定（編集ソフトが参照中・リネーム禁止）
metadata: 
  node_type: memory
  type: feedback
  originSessionId: 2d98e0a6-822f-4968-85e1-8d6de9fcb74a
---

# IVRC ビデオ審査 `docs/ivrc-video/pages/` のファイル名は固定

ユーザーは 2026-06-19 から、`docs/ivrc-video/pages/` の背景透過 PNG（`00_cold.png` … `12_endcard.png`）を**動画編集ソフトに読み込んで作業中**。

**Why**: ファイル名を変える（リネーム / 連番の振り直し）と、編集ソフトのタイムライン参照が切れて差し替えが壊れる。

**How to apply**:
- 既存ページの**内容修正は同じファイル名で上書き**する（`make_pages.py` の該当カットの文言・配置だけ変えて再生成）。
- **ページ追加は既存をずらさない名前**で（例: 02 と 03 の間なら `02b_xxx.png`、または末尾に追加）。`make_pages.py` は enumerate で連番を振るので、カットを挿入/削除すると後続が全部リネームされる点に注意 — 挿入時は採番ロジックを安易に通さない。
- **連番の全体振り直しが要るときは勝手にやらず先に確認**する。
- 関連: 動画は「秒数・音・尺はユーザーが編集ソフトで調整／シュビーは背景透過PNGをページ単位で出力するだけ」という分担（[[project_overview]] 配下の制作フロー）。素材生成は `docs/ivrc-video/draft/make_pages.py`。
</content>
