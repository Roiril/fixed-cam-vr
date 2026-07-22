---
name: hud-font-and-preview
description: HMD内テキストの文言を変えたらフォント再生成が必須（静的ベイク）／HUD見た目確認は Play 禁止・HudPreviewScreenshot（batchmode可）で撮る
metadata:
  type: project
---

# HMD 内テキストのフォントと見た目検証（2026-07-23 確立）

## 1. 文言を変えたら日本語フォントを再生成する（忘れると実機で豆腐）

`JapaneseHud SDF`（Resources/Fonts/）は**静的ベイク**のフォントアセット。ベイク時に存在しなかった文字は
**実機でも □ になる**（dynamic 焼きは効かない。U+FF1A 欠落を実測）。

- 再生成: `Tools/FixedCamVr/Setup/Generate Japanese HUD Font`、または batchmode
  `-executeMethod FixedCamVr.Streaming.EditorTools.JapaneseHudFontSetup.Generate`
- ベイク対象は **JapaneseHudFontSetup.cs の sources 配列の .cs から自動収集**。HMD に文字列を出す
  スクリプトを新設したら sources へ追加すること（2026-07-23 に ControllerGuidePanel / OvrControllerBridge を追加）
- 生成ログの「欠落 N 文字」を確認（📍 等の絵文字サロゲートは Web 卓専用なので無視可）

## 2. HUD の見た目確認 = HudPreviewScreenshot（Play モード禁止）

**Link/HMD 無しで OVR シーンを Play すると EnterPlayMode がデッドロックし Unity ごと落ちる**
（mcp-unity.md 記載の既知事故。2026-07-23 にも再発させた）。HUD の見た目確認は Play を使わず:

- Editor GUI: `Tools/FixedCamVr/Preview/HUD Preview (screenshot)` → `Assets/Screenshots/hud-preview/` に PNG
- MCP 不通/Unity 未起動: batchmode `-executeMethod FixedCamVr.Streaming.EditorTools.HudPreviewScreenshot.CaptureBatch`
  （**-nographics は付けない** — Camera.Render が動かない）
- 仕組み: Edit モードのまま StatusHud / ControllerGuidePanel へ reflection で状態注入 → private 描画メソッド駆動 →
  RenderTexture 撮影。罠 3 つをツール内で処理済み（①Edit モードは Awake が走らずフォント未適用 → 明示適用 +
  ForceMeshUpdate×2 ②RenderContent の間引き分岐は text.enabled=false にしてから呼ぶ ③シーンのスクリーン Quad が
  head 前方 1.6m より手前にあり StatusHud が隠れる → プレビュー時のみ distance を 0.9m へ）
- batchmode はユーザーの Unity Editor と**同時実行不可**（"another instance" abort）。実行前にプロセス確認
