---
name: table-duo-test-coverage
description: TableDuo の純ロジック分離 + EditMode テスト網（2026-07-23 拡充・20 ファイル +146 ケース）。ロジック変更時はテストを先に直す
metadata: 
  node_type: memory
  type: project
  originSessionId: e786cb11-5737-4417-8d64-87e1d39bcf64
  modified: 2026-07-23T07:37:41.067Z
---

# TableDuo テストカバレッジ（2026-07-23 拡充）

opus 設計 3 体 → 実装 3 体で純ロジック 12 クラスを behavior-preserving 抽出し、EditMode テストを 8 → 28 ファイル（全体 510 ケース）へ拡充。**該当ロジックを変えるときはテストを先に直す**。

**抽出済み純ロジック（すべて `TableDuoVr.Net` / `TableDuoVr.Hands`・時刻/乱数/入力注入）**:

- wire/記録: `HostBeaconMessage`（TDVB1 組立/パース/hash 照合）/ `PoseSeqGate`（seq 後着棄却・uint wraparound）/ `ReplayCursor`（時刻→フレーム二分探索）/ `SessionCsv` / `AutoModeResolver`（host/client 自動判定）/ `MarkLabelRouter` / `StudyFileName` / `TableDuoBuildInfo.ParseHash`。ファイル形式は `PoseRecordingFileTests`（TDV2 往復+Magic/boneCount ガード+ヘッダ fixture）と `PoseCodecLayoutTests`（layout wire）で固定
- ゲーム: `DealShuffle`（Fisher-Yates）/ `BandidoDealLogic`（分類・席分け・手札制約 l×1+g×2 or g×3・全単射）/ `DiceFaceLogic`（軸→出目 +X0/-X1/+Y2/-Y3/+Z4/-Z5・クランプ）。乱数は `Func<int,int>` 注入（本番 UnityEngine.Random.Range）
- 手/リグ: `StudyFlags`（study-flags ビットコーデック）/ `RemyHandGeometry`（手首基底・W 写像・FkLive — 最多バグ領域）+ `StudyLaunchFlags.ApplyFrom`（tdv_* 注入点）/ HandLandmarks / HandBoneTable / Haptic 系テスト。`InternalsVisibleTo("TableDuoVr.Tests")` を Hands/Net asmdef に追加済み

**テストの罠（実装時に踏んだもの）**:
- Unity 同梱 NUnit に `Is.AnyOf` は無い（bool アサートで代替）
- `HapticPatternPlayer` の総尺境界は float 累積誤差でフレークする — 境界ちょうどでなく明確に越える dt でテストする
- StudyConfig 等の static は EditMode で RuntimeInitializeOnLoad が発火しない — SetUp/TearDown で明示リセット
- `RemyAvatarRig.FkLive` は static 共有バッファ — 1 テスト 1 呼び出しで汚染回避

**未着手（P2/P3・設計メモは各 spec 参照）**: スナップ配線側（GeisterPieceSnap.OccupiedMask 等・PlayMode 相当でコスト過大）/ ConnectionManager の接続状態機械本体（NGO 結合）/ HandRetarget の実 rig 統合。既知の理論的問題: `BandidoDealer.PopCard` は gCards 枯渇で IndexOutOfRange の可能性（実バケ g24/l7 では到達不能・未修正）。
