---
date: 2026-05-04
phase: 3
status: in-progress
goal: Phase 0〜3 を Quest 3 実機で連続体験できるデモを完成させ、現場での切り分け手順を整備する
---

# Phase 3 まで実機デモ統合計画

## 背景

Phase 0〜3 はコンパイル OK・Editor 検証済みだが **実機 Quest 3 で 1 度も動かしていない**。
明日（2026-05-04）実機が使えるが、現状のままでは「動かない時に接続が悪いのか実装が悪いのか」を判定する材料が無い。

## ゴール

1. **統合 Main シーン**: Phase 2 / 2.5 / 2.7 / 3 を 1 シーンで連続体験できる
2. **HMD 内 HUD**: 実機を被ったまま接続状態 / FPS / 現ゾーン / FX 状態が見える
3. **切り分け手順書**: HUD の表示から接続問題か実装問題かを判定するフローチャート

## 体験フロー（明日のデモ用）

```
起動
  ↓
HUD 表示（FPS / Connected / Active Camera / Zone / Fx）
  ↓
[センター] Phone01 の生映像 + Fx OFF                  ← Phase 2 確認
  ↓ (右へ歩く)
[右ゾーン] Phone02 へ自動切替 + CRT エフェクト         ← Phase 2.7 + 3 確認
  ↓ (左へ歩く)
[左ゾーン] Phone03 へ自動切替 + Dust エフェクト        ← Phase 2.7 + 3 確認
  ↓ (中央に戻る)
[センター復帰] Phone01 + CRT+Dust 全部入り            ← Phase 3 統合確認
  ↓
コントローラ A/B で手動上書き切替も動作              ← Phase 2.5 確認
```

## 成果物

### A. 統合 Main シーン
- 既存 Main.unity に PlayerZone Sandbox の `[Zones]` `[Tracker]` を移植
- Fx Sandbox の RendererFeature 経由 CRT を本配線
- HUD Canvas を OVRCameraRig 配下（World Space, head 前方 0.7m, layer = UI）
- 既存 Phase 2.5 の OvrControllerBridge は維持（手動切替を残す）

### B. RuntimeDebugHud
- 新規 `Assets/Scripts/Diagnostics/RuntimeDebugHud.cs`
- TextMeshPro 1 枚で複数行表示
- 取得値:
  - `Time.unscaledDeltaTime` → FPS
  - `MjpegStreamReceiver.IsConnected` → 接続マーク
  - `CameraStreamRegistry.ActiveIndex` / `Active.Source.DisplayName` → 現カメラ
  - `PlayerZoneTracker._current.Label` → 現ゾーン
  - `Camera.main.transform.position` → HMD 座標
  - 現在有効な Fx の名前（FxSourceBinder 拡張）
- HMD コントローラ Y で表示 ON/OFF（OvrControllerBridge 拡張）
- 起動時は ON 既定

### C. URP Phase 3 本配線
- `Assets/Settings/UniversalRenderPipelineRendererData.asset` に `FullScreenPassRendererFeature` を追加
- CRT Material をリンク（Editor サンドボックスと同じ Material）
- Dust は ParticleSystem を Main の `=== Stage ===` 配下に Inactive で配置 → Tracker からの enable

### D. 切り分け手順
- 新規 `docs/onsite-checklist.md`
  - 機材セットアップ表
  - HMD 起動後 60 秒の確認順序（HUD 行ごと）
  - **判定フローチャート**（接続問題 vs ロジック問題）
- `TROUBLESHOOTING.md` に「明日の現場で詰まったら」セクション追加

### E. CameraSource 事前準備
- Phone01〜04 の SO を `Assets/Settings/Cameras/` に揃える
- README に「現場で host を実 IP に書き換えるだけ」明記

## マイルストーン

| 順 | タスク | 担当 | 並列可 |
|---|---|---|---|
| 1 | RuntimeDebugHud.cs 実装 | エージェント | ◎ |
| 2 | onsite-checklist.md 作成 | エージェント | ◎ |
| 3 | TROUBLESHOOTING.md 拡張 | エージェント | ◎ |
| 4 | OvrControllerBridge に Y ボタン HUD トグル追加 | エージェント | ◎ |
| 5 | Main シーンに [Zones] / [Tracker] / Fx / HUD 統合 | シュビー本体（Unity MCP） | × |
| 6 | URP RendererData に FullScreenPassRendererFeature 追加 | シュビー本体（Unity MCP or 手動） | × |
| 7 | コミット → push → PR → merge | シュビー本体 | × |

## リスク・未解決

- **URP RendererData 編集**: Unity MCP で ScriptableObject を編集できるか未確認。失敗時は手動セットアップ手順を README に書いて回避
- **HUD の Y ボタン**: OvrControllerBridge の現状実装と衝突しないか要確認（X はスクリーン head-lock）
- **物理空間レイアウト**: 室内の畳数によってゾーン半径要調整。Inspector で即変えられる設計を維持

## 次の一手（明日以降）

- 実機で動作 → 不具合フィードバックを TROUBLESHOOTING に追加
- Phase 4-1（額縁モニタ）着手
