---
name: paper-dcexpo-env
description: DCEXPO 向け資料（廻リ視の論文の新版）の執筆環境。paper/dcexpo で HTML→Edge→PDF。図を削らない・現行と合わない図は実物に差し替える・tune.py で段組を整える
metadata:
  node_type: memory
  type: project
  originSessionId: 611542f1-235f-4005-aa92-c927c0fefcaf
  modified: 2026-09-30T04:33:29.909Z
---

2026-09-30 に作った。元の Word（`G:\マイドライブ\研究\IVRC2026\IVRC2026_Roiril_v1.6.5.2.docx`）に寄せた
A4・2 段組を **HTML + CSS で書いて Edge で PDF 化**する。入口は `paper/dcexpo/README.md`。
`py -3.10 paper/dcexpo/tools/build.py --sheet`（3.10 のみ PyMuPDF あり）→ `out/sheet.png` を目視。

**Why:** Codex が作った版は図を削りすぎ・レイアウトが崩れていた（ユーザー指摘）。Word は機械操作しづらいので、
シュビーが直接編集できる形にした。DCEXPO は厳密に科学的でなくてよい・名前と所属は外す・概要から始める・
ページ数の縛りは無い（いずれもユーザー指示）。

**How to apply:**
- 本文を変えたら build → 必要なら `tune.py --apply`（段組が自動で流れるので下端の空き・段の空白が動く）
- **図は削らない。** 現行と合わないものは差し替える（台車の人形が写る CG 模型は現行に無い → 実機の画面録画・
  実写プレート・合成の実物へ）。カメラ A〜C は `Assets/StreamingAssets/show/assets/plate_*_20260924_*` が最新
- 実機の画面は `output/quest-recordings/2026-09-27/quest-alpha/` の録画から抜く。作品内の日時と周の表示が入る
- 体験の流れの事実は `canon/` と memory（`hmd_onboarding` `comms_takeover` `ending_result`）。企画書は読まない
- 文体は学会の版を引き継ぐ（である調・`，．`）。日本語の基準は `~/.claude/reference/japanese-writing-review.md`
- コミット済み（22e774c1）・**未 push**。画面録画に体験者（試験者）の姿と研究室が写るため公開の判断は保留
