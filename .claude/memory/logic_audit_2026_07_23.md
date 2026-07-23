---
name: logic-audit-2026-07-23
description: 2026-07-23 全体ロジック監査の確定バグ 8 件（未修正バックログ）。修正着手時はここから
metadata: 
  node_type: memory
  type: project
  originSessionId: e786cb11-5737-4417-8d64-87e1d39bcf64
  modified: 2026-07-23T02:52:11.134Z
---

# 2026-07-23 ロジック監査 — 確定バグバックログ

廻リ視（Assets/Scripts/）+ fixed-cam-streamer v0.5.0 + 境界契約の全体監査。
6 領域並列監査（opus）→ 敵対的検証（critical/high 2 票・全会一致のみ確定）→ 親 grep 照合。
EditMode 245/245 全通過（純ロジック層は健全・バグは全て配線/契約/実機ライフサイクル層）。
詳細レポート: `reports/2026-07-23_logic-audit.html`（Artifact: https://claude.ai/code/artifact/317b18a3-f795-4a92-ba77-bb73ded27cd1）

**確定 8 件（未修正）**:

1. **[critical] timeline present-flag 契約不整合** — ShowControlClient.cs:848 が has* を `!=null` 再導出、Web timeline.js:476-485 は false でも非 null を常時出力 → 全 true 化（幽霊 exit インサート・グレーディング中立化・cue override 全置換）。Web 検証モードでは見えない
2. **[high] 右 B ボタン全死** — StreamingLogic.prefab に `statusButton` キー無し → Button.None（[[unity-prefab-fields]] 罠の実例）。StatusHud トグル + 登録 Verify の B 確定が実機で不能
3. **[high] _suspended 固着** — CameraStream.cs:253 resume-gap 復帰が _suspended を解除しない → stall watchdog / 砂嵐 / discovery 張替が全停止
4. **[high] override 中の古いゾーン保留 commit** — ShowControlClient.cs:748 / CameraSwitchDirector.cs:100。dwell 中に override すると約 1s 後に固定が破れ LapCounter も誤進行
5. **[medium] 動画 cue Prepare 失敗で _current 残留** — ScreenOverlayController.cs:200 → 自動切替デッドロック（グリップ停止まで復帰不能）
6. **[medium] 登録キャンセルがプレビュー変換を復元しない** — CourseRegistrationController.cs:437（save:false 適用後、キャンセルで戻さない）
7. **[medium] stall watchdog が _lastFrameTime を汚す** — CameraStream.cs:369 → 真の信号断中に砂嵐が 10s 毎に 0.6s 消灯
8. **[medium] streamer 再バインド後 /info が 0x0 固着** — CameraController.kt:314（lastInfoW/H 未リセット）

low 未検証 3 件（ConnectionKey に pass 不参加 / MainActivity onDestroy 無条件 stop / distributor.info RMW 競合）はレポート参照。

**Why:** 修正はユーザー未依頼（調査のみの依頼だった）。着手時は severity 順に。①は Web/Unity どちらを直すか設計判断が要る（CameraDef 方式=キー省略 vs bool 信頼）。
**How to apply:** 修正したら本ファイルの該当行に「✅修正済 (commit)」を付け、全件消えたらファイルごと削除。
