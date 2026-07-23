# 検証基盤の 4 層化（対話的 Unity 依存からの脱却）

2026-07-23 ユーザー確定（「動作検証や見た目検証の仕組みを整えたほうがいい」→ シュビー提案に全て同意）。

## 背景・原則

Unity/Quest 開発でシュビーと相性が悪いのは Unity そのものではなく**「対話的な Unity」に依存する検証**：

- MCP ライブ編集は連続使用で wedge する（mcp-unity.md）
- Play モードは Link/HMD 無しでデッドロック → Unity ごと落ちる（実害 2 回。**guard-unity-play hook で機械ブロック済み・2026-07-23**）
- batchmode はユーザーの Editor と排他（"another instance" abort）
- 実機ビルドは遅く、装着確認はシュビーには不可能

**原則: 検証は「Unity と対話する」から「Unity に成果物（テスト結果・PNG・ログ）を吐かせて機械で読む」へ寄せる。**
今日の実証: フォント豆腐（実機に出てから気づくはずのバグ）を HudPreviewScreenshot が出荷前に検出した。
ルール増強より「hook で強制・パイプラインが機械的に拾う」を優先する（読まれないルールは存在しないのと同じ）。

## 4 層構成

| 層 | 内容 | 状態 | 実行方法 |
|---|---|---|---|
| **L0 ロジック** | EditMode テスト（純ロジック 240 件・0.3 秒） | ✅ 強い（維持） | run_tests / batchmode -runTests |
| **L0.5 統合** | **XR 無し統合テスト**（下記・最優先の新設） | ❌ 未着手 | 同上 |
| **L1 見た目** | HudPreviewScreenshot（Edit モード PNG 化） | ✅ 2026-07-23 新設 | メニュー / batchmode CaptureBatch |
| **L2 通し** | XR 抜きデスクトップビルド（TableDuo L0 方式の fixedcam 版） | ❌ 未着手 | batchmode ビルド → CLI 起動 → 自動スクショ |
| **L3 実機** | ビルド & インストール & チェックリスト生成まで＝シュビー / 装着確認＝ユーザー | ✅ 分業確立（quest-build） | adb |

## L0.5: XR 無し統合テスト（最優先）

**根拠**: 2026-07-23 の切替整合性監査で見つかった 6 バグは*全部*テストの無い MonoBehaviour 統合層
（InsertController↔Director の凍結受け渡し・override 再同期・TimelineDirector の prevCam 追跡等）に集中。
ロジック層は固いのに配線層が無防備、が現状の唯一の大穴。

- OVR に依存しないテストシーン（または実行時組み立て）で Director + InsertController + TimelineDirector +
  CueScheduler + LapCounter + ShowControlClient(オフライン) を実配線し、fake のゾーン要求・時間進行で駆動する
  PlayMode（または EditMode + 手動 Tick）テスト
- 最初のテストケースは監査バックログそのもの（fixed_cam_review_backlog.md 2026-07-23 節）:
  1. insert 表示中の ResetRun/SetInserts → 凍結が解除されること（未修正バグ 1 の再現テスト → 修正）
  2. insert 復帰後のゾーン Feed（バグ 3）
  3. override 解除後の現在ゾーン復帰（バグ 4）
  4. グリップ緊急停止のローカル/リモート両経路（バグ 2）
- **バグ修正とテスト追加を同一コミットで**（修正だけ先行させない）

## L2: fixedcam デスクトップ通しビルド

- TableDuo L0 方式（batchmode ビルド → CLI 起動 → ScreenCapture 自動保存 → Read で確認）を fixedcam に移植
- ゾーン移動はキー入力 or 起動引数のシナリオ（例: `--walk A,B,C,A --lap 2`）で偽装
- MJPEG 入力は streaming-offline-test の合成ソースを流用（実カメラ不要）
- 切替 dip・cue 発火・insert・砂嵐が「見た目として」正しいかを PNG 列で機械確認
- 詳細設計は着手時に。参照: table_duo_l0_desktop_test.md / KeyVisualDirector.cs / SpectatorController.cs

## 運用ルール（確立済み・再掲）

- HMD 内文言を変えたら **Generate Japanese HUD Font 再実行**（静的ベイク。忘れると実機豆腐 → hud_font_and_preview.md）
- Play が本当に必要な時だけ `.claude/allow-unity-play` でワンショット解錠（Link/Simulator 確認後）
- batchmode 実行前に Unity プロセス確認（ユーザー Editor と排他）

## 着手順

1. **L0.5 統合テスト + 監査バックログ 4 バグの修正**（1 セット。効果最大）
2. L2 デスクトップ通しビルド（実機試着前に通し確認できる状態を作る）
3. L1 の対象拡大（スクリーン合成・cue 演出のプレビュー）は必要が生じたら
