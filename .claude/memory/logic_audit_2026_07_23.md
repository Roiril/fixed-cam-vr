---
name: logic-audit-2026-07-23
description: 2026-07-23 全体ロジック監査 11 件はすべて修正済み（EditMode 364/364・JVM 31/31・node 5/5）。残るは実機確認チェックリスト
metadata: 
  node_type: memory
  type: project
  originSessionId: e786cb11-5737-4417-8d64-87e1d39bcf64
  modified: 2026-07-23T04:55:37.063Z
---

# 2026-07-23 ロジック監査 — 全 11 件修正済み・実機確認待ち

監査（確定 8 + low 3）→ opus 設計 6 体 → opus 実装 6 体で全件根本修正。監査レポート:
`reports/2026-07-23_logic-audit.html`。検証済み物証: **EditMode 364/364**（+119 増）・**streamer JVM 31/31**（基盤新設）・**node 5/5**（web 契約）。

**修正の骨子**（詳細は rules/streaming.md・unity-vr.md の 2026-07-23 監査修正マーク）:

1. ✅ present-flag 契約 → `TimelinePresentFlags.Reconcile`（AND 確定・3 経路一律）+ Web は `timeline-model.js` 抽出でキー省略。共有 fixture `Assets/Tests/Fixtures/show_timeline_canonical.json`（再生成は `UPDATE_FIXTURE=1 node --test`）
2. ✅ B ボタン全死 → StreamingLogic.prefab 全書換 + `StreamingLogicPrefabFieldsTests`（missing/stale 機械監査）
3. ✅ _suspended 固着 → `StreamWatchdogLogic` 抽出（resume-gap で suspend 解除・Registry ラッチ二層）
4. ✅ override 中の保留 commit → `SwitchDirectorLogic._overrideActive` 第一級凍結 + 保留無条件クリア
5. ✅ 動画 cue デッドロック → `OverlayPlaybackLogic` 抽出 + OnVideoError / prepareTimeoutSec(6s) で AbortCurrentCue
6. ✅ 登録キャンセル → `CourseFrame` プレビューセッション（Begin/Commit/Rollback）+ `HoldAverageSampler`
7. ✅ stall watchdog の LastFrameRealtime 汚染 → 代入廃止・再発火ゲート 10s 化
8. ✅ streamer /info 0x0 固着 + RMW 競合 → `StreamInfoHolder` 単一オーナー化 + rebind 時の向き再適用（v0.5.1）
9. ✅ ConnectionKey に pass の FNV-1a 指紋（pass のみ変更で再接続）
10. ✅ MainActivity onDestroy → `LifecyclePolicy`（config 変更で Service を止めない）+ configChanges 拡張
11. ✅ カバレッジ P1: `CameraSourceEndpointTests` / `MjpegParserTests`（receiver に Stream seam）/ `ShowConfigPrecedenceTests`

**⚠ 実機未検証（現地/実機で確認するもの）**:
- タイムライン区間保存 → Quest で幽霊インサート・post 中立化が出ないこと（旧バグは Web 検証モードでは見えない）
- 実機 B ボタン（StatusHud トグル・登録 Verify 確定）
- HMD 着脱 → カメラ停止で砂嵐が出る・自動復旧が生きていること
- streamer: カメラ奪取→復帰後の `curl /info`（0x0 でない）・ダークモード切替で配信断しないこと・三脚固定の向き維持
- 動画 cue の Prepare 実測が 6s に収まるか（大容量なら `prepareTimeoutSec` を SerializeField で延長）

**P2 テスト候補（未着手・coverage-map 設計に一覧あり）**: FlattenCues / ApplyPostForActive 4 段解決 / Registry ActiveChanged 契約 / StartupFader / SignalLostFx 純抽出。
