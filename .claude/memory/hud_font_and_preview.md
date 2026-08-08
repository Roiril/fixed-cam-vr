---
name: hud-font-and-preview
description: HMD内テキストの文言を変えたらフォント再生成が必須（静的ベイク）／HUD見た目確認は Play 禁止・HudPreviewScreenshot（batchmode可）で撮る
metadata: 
  node_type: memory
  type: project
  originSessionId: 2d1c5a0d-26a4-43f3-9ef9-6c8a4816b72a
  modified: 2026-07-30T07:09:02.904Z
---

# HMD 内テキストのフォントと見た目検証（2026-07-23 確立）

## 1. 文言を変えたら日本語フォントを再生成する（忘れると実機で豆腐）

`JapaneseHud SDF`（Resources/Fonts/）は**静的ベイク**のフォントアセット。ベイク時に存在しなかった文字は
**実機でも □ になる**（dynamic 焼きは効かない。U+FF1A 欠落を実測）。

- 再生成: **`.\tools\unity.ps1 menu hud-font`**（Editor を開かない。GUI なら
  `Tools/FixedCamVr/Setup/Generate Japanese HUD Font`）
- ベイク対象は **JapaneseHudFontSetup.cs の sources 配列の .cs から自動収集**。HMD に文字列を出す
  スクリプトを新設したら sources へ追加すること（2026-07-23 に ControllerGuidePanel / OvrControllerBridge、
  **2026-07-30 に IntroDirector** を追加）
- **⚠ 追加漏れは「文字を出す面が無い」と同時に起きると露見しない。** 導入演出（2026-07-30）は
  文言（`IntroDirector.PromptText`）を書いたが**読む面を作っていなかった**ので、フォント収集の
  漏れもテストも警告も何も鳴らなかった。**文言を書いたら、それを出す面と sources 追加を対で確認する**
- 生成ログの「欠落 N 文字」を確認（📍 等の絵文字サロゲートは Web 卓専用なので無視可）

## 2. HUD の見た目確認 = HudPreviewScreenshot（Play モード禁止）

**Link/HMD 無しで OVR シーンを Play すると EnterPlayMode がデッドロックし Unity ごと落ちる**
（mcp-unity.md 記載の既知事故。2026-07-23 にも再発させた）。HUD の見た目確認は Play を使わず:

- **`.\tools\unity.ps1 menu hud`** → `Assets/Screenshots/hud-preview/` に PNG（Editor を開かない。
  batchmode なら Main.unity を自分で開く）。GUI なら `Tools/FixedCamVr/Preview/HUD Preview (screenshot)`
  （**-nographics は付けない** — Camera.Render が動かない。`unity run` は付けないので気にしなくてよい）
- 仕組み: Edit モードのまま StatusHud / ControllerGuidePanel へ reflection で状態注入 → private 描画メソッド駆動 →
  RenderTexture 撮影。罠 3 つをツール内で処理済み（①Edit モードは Awake が走らずフォント未適用 → 明示適用 +
  ForceMeshUpdate×2 ②RenderContent の間引き分岐は text.enabled=false にしてから呼ぶ ③シーンのスクリーン Quad が
  head 前方 1.6m より手前にあり StatusHud が隠れる → プレビュー時のみ distance を 0.9m へ）
- batchmode はユーザーの Unity Editor と**同時実行不可**（"another instance" abort）。実行前にプロセス確認
