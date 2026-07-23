---
name: fixed-cam-review-backlog
description: 2026-06-10 の fixed-cam 本体レビュー結果 — 修正済み項目と残バックログ
metadata: 
  node_type: memory
  type: project
  originSessionId: 93b1d0df-ed63-4c85-99db-74093d710291
---

# fixed-cam 本体レビュー（2026-06-10）

**Why:** TableDuo 着手で fixed-cam 側が古くなったためユーザー依頼で全面レビュー + 自己改善 1 回実施。

## 修正済み
- MjpegScreen.cs 単一モードの Texture2D 未初期化（Quest GPU 白ノイズ罠 [[unity_pitfalls]]）→ 黒初期化を追加

## 誤検知として棄却（再調査不要）
- MjpegStreamReceiver 受信ループの「無音死」→ 最外層 `catch (Exception)` で保護済み
- CameraStream.DroppedFrames のレース → Tick() はメインスレッド専用、レースなし
- Dispose 漏れ → MjpegStreamReceiver.Dispose() は Cancel + Dispose で完備（2026-06-18 に Wait(500ms) を撤廃し非ブロッキング化。下記参照）

## 残バックログ（優先度順）
1. OvrBridge / Fx 系 57 ファイルに `#nullable enable` 未付与（一括追加可、機能影響なし）
2. StreamMetadataFetcher: HttpClient.Timeout(5s) と CTS(3s) の二重タイムアウト → `Timeout.InfiniteTimeSpan` + CTS 一本化
3. StreamMetadata / StreamHealth の public フィールドは JsonUtility 用 DTO（規約例外）→ コメントで意図明示
4. DroidCam 互換コード（CameraSource 既定値・Ping DroidCams メニュー）は意図的保持。Phase 4 で削除判断
5. /info 定期 refresh は 1.5s 間隔 → スマホ回転の反映が最大 1.5s 遅れる（仕様として許容中）

**How to apply:** fixed-cam 側を再度触るセッションで 1–2 を先に消化する。

# fixed-cam + web UI 自己判断レビュー（2026-06-18）

**Why:** ユーザー依頼で web-compositor と Unity fixedcam を自己判断レビュー改善ループ（スキル非依存）。サブエージェントで所見収集 → 各指摘を本体が実コードで検証 → 高確度のみ修正。Unity コンパイルクリーン / web は preview 実走確認。

## 修正済み（Unity）
- **OvrControllerBridge**: `_hudVisible=true` 固定 → HUD 既定 OFF で初回 Y 押下が空振り。`Start()` で `RuntimeDebugHud.IsVisible` から初期化（型は MonoBehaviour のまま安全キャスト）
- **MjpegStreamReceiver.Dispose**: `_loop.Wait(500ms)` を撤廃 → 非ブロッキング化（`ContinueWith` で背景 CTS dispose）。`ReapplyConnection`（show.json の IP 差し替え＝stall 中カメラに対して呼ぶ）の main-thread 500ms ヒッチを排除
- **ScreenOverlayController.OnDestroy**: DL 済み動画キャッシュ（temporaryCachePath の mp4）を削除 + `_videoFileCache.Clear()`。放置で端末ストレージ無限増を防止
- **OverlayCueData.SourceIsVideo**: `ToLowerInvariant()` の無駄アロケート → `EndsWith(..., StringComparison.OrdinalIgnoreCase)`

## 修正済み（web-compositor）
- **shaders.js の `FS_VIEW` 削除**: 未使用 dead code かつ式が古い（temperature 加算・vignette smoothstep）＝誤誘導。実際のビューは `pipeline.js` の `FS_POST`（Unity ScreenComposite と数式・順序一致を検証済み）。app.js の誤コメントも修正
- memory `web_compositor.md` の誤記訂正（ビュー=FS_VIEW、pipeline.js 撤去済み の 2 点）

## 誤検知/非採用（再調査不要）
- MjpegStreamReceiver の過大フレーム時バッファ非再利用 → 定常は latest/spare 2 枚 ping-pong でゼロアロケート、再確保は解像度増加時のみ＝非問題
- LoadImage が毎フレーム再確保 → LoadImage の仕様上不可避（AVPro 移行は別判断）。「ゼロアロケート」表現は厳密には live 取り込みには当てはまらないが許容
- ZoneCalibrator のゾーン名重複でセーブ取りこぼし（M4）→ シーン名がユニークか未確認・校正専用機能のため非採用（要なら save/match を安定キーへ）
- FxSandbox 系の material instantiate / DontSave 編集時リーク → サンドボックス専用シーン限定・実機 Main.unity に非波及＝非採用

## 教訓（自己改善）
ルール/罠を rules・memory に追加した時、**既存コードのスイープをセットでやる**こと。streaming.md に「Texture2D 初期化必須」と書いた時点で MjpegScreen が未修正のまま残っていた。

# 黒画面（HMD スクリーン真っ暗）調査（2026-07-22）

**Why:** ユーザー依頼「真っ暗になる原因を探せるだけ探して改善」。opus×3（表示合成/受信接続/演出postFX）で調査 → 親が実コード照合 → 8 件修正（commit 4513a9f、EditMode 240/240 パス）。修正内容は streaming.md に反映済み。

## 残バックログ（未修正・設計判断が必要）
1. **端末キャッシュの古い host が焼き直し .asset を潰す**: show_config.json（persistentDataPath）の非空 host は baked より優先。IP を焼き直して更新インストールしても、discovery 非対象カメラ（iPhone 等 cameraId 無し）は古い IP で恒久黒。対策候補＝ビルド世代印でキャッシュ無効化 / 現地キャッシュクリア操作（ShowControlClient.LoadAndApplyCache）
2. **post 異常値で全黒でもQuest側にリセット手段なし**: ApplyPostForActive は値クランプ無し。露出の極端な負値等が焼き込み/キャッシュに乗ると PC 不在で直せない。演出上の意図的暗黒と区別が必要なためクランプは未実装
3. **segment post が Zone 以外の切替（Web override・手動）で解除されない**: 暗い区間 post が別カメラに残留。ApplyPostForActive で「アクティブカメラ向け segment post を毎回再解決」するのが本筋
4. **静止画 cue に自動復帰経路が無い**（loop 動画 cue も同様）: PC 不通時は張り付き。グリップ無条件停止（2026-07-22 修正）が現場の脱出手段
5. 401 認証エラー・boundary 不一致・discovery 二重 ID は設計どおりの恒久黒（運用注意）。HUD への常時可視化が改善候補
6. lag 検出（recv/phone<0.7）はメインスレッドヒッチでも誤発火 → 周期的な短い黒 dip。/health 裏取りの併用が候補

**How to apply:** 次に fixed-cam の接続/演出系を触るセッションで 1 と 3 を優先消化。実機試着でこの黒バグ群の再現有無を確認する。

# カメラ切替の整合性監査（2026-07-23）

**Why:** ユーザー質問「切替が止まらないか・ABC 順が変にならないか」。opus×2（凍結解除経路/切替順序）で監査、親が実コード照合済み。**純ゾーン歩行のみなら両方とも安全**（単一 pending + dip 直列化で古い要求の遅延 commit は構造的に無い。中間ゾーン取りこぼし A→C は仕様）。危険は insert / Web 卓操作の絡む未修正バグ：

## 修正済み（2026-07-23・L0.5 統合テスト新設と同一変更セット）
1〜4 を修正し、XR 無し統合テスト [`Assets/Tests/Tracking/SwitchWiringTests.cs`](../../Assets/Tests/Tracking/SwitchWiringTests.cs)（EditMode + 手動 Tick・T1/T2/T4/T5）+ [`Assets/Tests/Streaming/GripStopLocalTests.cs`](../../Assets/Tests/Streaming/GripStopLocalTests.cs)（T3）で固定。EditMode 245/245 パス。各修正は red-check 済み（fix を外すと該当テストが赤）。

1. **HIGH `_insertActive` ストランド** → 修正: `InsertController.ResetRun` / `SetInserts` が進行中インサートを `CleanupActiveInsert`（insert cue 停止・insert post 解除・`director.InsertReturn`）で畳んでから `InsertLogic` をリセット。`SwitchDirectorLogic.Reset` でも `_insertActive`/`_cueActive` を false 初期化。テスト T1（ResetRun）/ T2（SetInserts）
2. **HIGH グリップ緊急停止がローカル発火 cue に無効（server 到達時）** → 修正: `ToggleActiveCameraCue` の server 到達停止分岐で `stopCue` 送信 + ローカル `StopOverlay` を併用。テスト `GripStopLocalTests`（T3）
3. **MED-HIGH insert 復帰が SwitchSource.Insert** → 修正: `EndInsert` の復帰先が開始時ゾーン（`InsertLogic.BaseZoneCamera`）と異なれば `InsertReturn(returnCamera, asZone:true)` で復帰 commit を `SwitchSource.Zone` として発火 → LapCounter/TimelineDirector に実ゾーン移動を反映。進行ポインタの順方向一致性で二重カウントなし。テスト T4
4. **MED override 解除後に現在ゾーンへ戻らない** → 修正: `PlayerZoneTracker.InvalidateCurrent()`（public）追加 + `OnEnable` で自己呼び出し。override 解除＝tracker 再有効化が OnEnable を発火 → `_current` 無効化 → 次 Update で再 Pick。当初案（ShowControlClient から SendMessage）は EditMode で `ShouldRunBehaviour` アサートを踏むため OnEnable 自己回復へ変更（Streaming→Tracking 循環も回避）。テスト T5

## 未修正バグ（残り）
5. **MED ResolveTiming に上限クランプ無し**: minDwellSec=99999 等の正の巨大値が素通り→切替不能。3経路（ライブ/キャッシュ/焼き込み）の present 判定自体は一貫・正常
6. LOW: in-flight insert と override/手動の綱引き（abort 経路が無い）/ exit と enter insert の同一 commit 排他 / グリップ2秒長押しリセットは LapCounter のみで Web runEpoch と非対称 / director 未配線時 LapCounter が全 source を数える

**How to apply:** 5 は show.json control の入力側 or ResolveTiming に上限クランプを足す。6 は必要になったら個別に。
