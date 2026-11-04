---
name: stream-link
description: 配信・接続・show.json の配布・Quest の :8090・診断の現行の契約。経緯と旧い記述は reference/archive/ の凍結版
paths:
  - "Assets/Scripts/Diagnostics/HmdTrajectoryRecorder.cs"
  - "Assets/Scripts/Streaming/Mjpeg*.cs"
  - "Assets/Scripts/Streaming/Stream*.cs"
  - "Assets/Scripts/Streaming/CameraStream*.cs"
  - "Assets/Scripts/Streaming/CameraSource*.cs"
  - "Assets/Scripts/Streaming/DiscoveryClient.cs"
  - "Assets/Scripts/Streaming/ShowServerSource.cs"
  - "Assets/Scripts/Streaming/ShowControlClient.cs"
  - "Assets/Scripts/Streaming/BakedShowManifest.cs"
  - "Assets/Scripts/Streaming/ShowAssetResolver.cs"
  - "Assets/Scripts/Streaming/RuntimeHealth.cs"
  - "Assets/Scripts/Streaming/LatencyEstimator*.cs"
  - "Assets/Scripts/Streaming/DecodedFrameEvidence.cs"
  - "Assets/Scripts/Streaming/DisplayRateInfo.cs"
  - "Assets/Scripts/Streaming/ShotPngWriter.cs"
  - "Assets/Scripts/Streaming/ExperienceShot*.cs"
  - "Assets/Scripts/Streaming/VisitorPortal*.cs"
  - "Assets/Scripts/Streaming/VisitorPrefs.cs"
  - "Assets/Scripts/Streaming/Diagnostics/**"
  - "Assets/Scripts/Streaming/Recording/**"
  - "Assets/Scripts/Diagnostics/ShowTelemetryHost.cs"
  - "Assets/Scripts/Diagnostics/StreamingDiagnosticsOverlay.cs"
  - "Assets/Scripts/Diagnostics/HudLogDumper.cs"
  - "Assets/Tests/Streaming/Mjpeg*Tests.cs"
  - "Assets/Tests/Streaming/CameraSource*Tests.cs"
  - "Assets/Tests/Streaming/Discovery*Tests.cs"
  - "Assets/Tests/Streaming/Stream*Tests.cs"
  - "Assets/Tests/Streaming/Latency*Tests.cs"
  - "Assets/Tests/Streaming/DecodedFrame*Tests.cs"
  - "Assets/Tests/Streaming/BakedShow*Tests.cs"
  - "Assets/Tests/Streaming/ShowAsset*Tests.cs"
  - "Assets/Tests/Streaming/ShowConfig*Tests.cs"
  - "Assets/Tests/Streaming/VisitorPortal*Tests.cs"
  - "Assets/Tests/Streaming/VisitorPrefs*Tests.cs"
  - "Assets/Tests/Streaming/ShotPng*Tests.cs"
  - "Assets/Tests/Streaming/ExperienceShot*Tests.cs"
  - "Assets/Scripts/Streaming/Replay/**"
  - "Assets/Tests/Streaming/XpRec*Tests.cs"
---

# 配信と接続

**現行の契約だけを書く。** 変わったら行を書き換える（日付の節を足さない・`rules/doc-sync.md`）。
体験者に見えることは `canon/NOW.md`、経緯は `reference/archive/streaming-2026-10-22.md`（S:）・`reference/archive/show-design-2026-10-22.md`（D:）。出典の行番号はその凍結版のもの。

## 契約
### 配布（Player = baked-only-v1）
- Player（Editor 以外）は同梱 `StreamingAssets/show/show.json` だけで動く。卓の `/state` も端末キャッシュも使わない — `ShowControlClient.BakedOnly` / `LoadAndApplyCache` / `SaveCache` 〔S:12-17〕
- 同梱 show.json は `manifest.json` と sha256 で照合し、不一致なら `ContentVerified=false` — `BakedShowManifest.TryVerify` / `BakedShowManifestTests` 〔S:14-21〕
- 素材は show/ 配下の `sa://` だけ。`slot://` と逸脱経路は空にし HTTP へ行かない。卓 URL は `assetMap` の分だけ読み替え — `ShowAssetResolver.ResolveBakedOnly` / `TryRemapToBaked` / `ShowControlClient.ResolveAssetUrl` 〔S:16〕
- Editor だけ旧経路（焼き込み < キャッシュ < ライブ）。別 APK のキャッシュは捨てる — `ShowControlClient.ApplyBaked` / `ShowConfigPrecedenceTests.LoadAndApplyCache_DropsCacheWrittenByAnotherApk` 〔S:408-416〕
- 焼き込みは `build fixedcam` が `tools/export-show-build.py` で毎回作る。APK と `.apk.content.json` は対 — `FixedCamContentBuildGuard` 〔S:20-21, S:524-531〕
- 設定の出所は `[XP] ev=config` に出し `analyze-xp-log.py` が照合する。対で直す — `ShowControlClient.DescribeConfig` / `ShowTelemetryHost` 〔S:418-422〕
- heartbeat は Player でも送るが、卓の ⏭ ■ ▶ と cameraOverride は届かない — `ShowControlClient.HeartbeatLoopAsync` 〔memory/unexpected_event_recovery.md〕
### 接続先
- 層は discovery > show.json `cameras[i]` > .asset。認証は discovery 外。空 host は層を外す — `CameraSource.ApplyRuntimeEndpoint` / `ApplyDiscoveryEndpoint` / `CameraSourceEndpointTests` 〔S:398〕
- `ConnectionKey`（host|port|user|FNV-1a(pass)）が変わった時だけ張り直す — `CameraStreamRegistry.ApplyEndpoint` / `CameraStream.ReapplyConnection` 〔S:3379〕
- Player では host のあるカメラを pinned と扱い付け替えない。A/B/C は固定 IP — `ShowControlClient.IsCameraPinned` 〔S:20, S:3323-3331〕
- host 未設定の枠は繋がないのが正常。1 台の失敗で他を止めない — `MjpegStreamReceiver`/ `CameraStreamRegistry.Awake` 〔S:3354-3367〕
### 受信と健全性
- 汎用 MJPEG。boundary は Content-Type、chunked 自動、X-* と /info /health は任意。username 空なら認証なし — `MjpegStreamReceiver.ParseBoundary` / `CameraSource.BasicAuthToken` / `MjpegParserTests` 〔S:36-39, S:48〕
- NoDelay・受信 64KB・接続 3 秒。バックオフ 1→30 秒、確立後の切断は 1 秒から — `MjpegStreamReceiver._connectionEstablished` / `DelayWithReconnect` 〔S:214, S:3371-3380〕
- 表示中と録画中は毎フレーム展開し、他は 1 フレーム 1 台ずつ — `StreamDecodeScheduler.NextBackground` 〔出典: コード〕
- 判定は `StreamWatchdogLogic`。lag は比 0.7 未満が 1.5 秒（熱降格中は抑止）、stall 10 秒、展開失敗 30 枚か 2 秒、resume-gap 0.5 秒で suspend も解く — `LagThresholdRatio` / `StallReconnectSec` / `StreamHealth.IsHot` / `StreamWatchdogLogicTests` 〔S:228, S:1568, S:3374-3378〕
- 張り直しは世代で旧 /info /health を捨て、suspend 中も新 receiver を起動する — `CameraStream.EndpointGeneration` 〔S:3375〕
- 映っている証拠は同じ接続で 1.5 秒以内の実展開 — `DecodedFrameEvidence.FreshSec` / `CameraStream.HasFreshDecodedFrame` 〔出典: コード〕
- 遅延は絶対値を出さず分けて出す — `LatencyEstimatorLogic.ArrivalJitterMs` / `DecodeMs` / `PresentMs` / `SourceAgeMs` 〔S:1553-1566〕
- アスペクトは JPEG 実寸から決める。回転は `MjpegScreen.uvRotSteps` だけで補う 〔S:340-342〕
### 発見
- UDP :8830 の `fixedcam-discovery/1`。張り替えは断か id 不一致が 5 秒続き、別候補を /info で照合できた時だけ。二重 ID は切り替えない。止めるのは `control.discoveryEnabled`。卓 IP は show-server announce で差す — `DiscoveryLogic.ShouldSwitch` / `IsIdentityMismatch` / `ShowServerSource.ApplyRuntimeEndpoint` 〔S:3338-3350〕
### Quest :8090
- `VisitorPortal`（:8090・実行時生成）の API は GET /status、POST /set・/clear・/tablet/pulse。`/` と `/asset/` は 410 — `ShowControlClient.EnsureVisitorPortal` / `VisitorPortalLogic.Route` 〔S:3295-3306〕
- /set /clear は 2 つのセッション ID が必須（欠落 400・起動 ID 不一致 409）。force:true の reset は設定番号を見ない — `VisitorPortalLogic.TryParseSet` / `VisitorPortal.ForceResetProvider` 〔memory/visitor_tablet.md〕
- 正は `VisitorPrefs`（永続化しない）。書くのは注意書きの段だけで、戻してから載せる — `VisitorPrefs.ApplyAtTitle` / `Consume` 〔S:3307-3310〕
### 診断と記録
- 例外の計数と工程の隔離 — `RuntimeHealth.ExceptionCount` / `Step` 〔memory/unexpected_event_recovery.md〕
- 録画は `temporaryCachePath/rec/<runEpoch>/L<lap>C<cam>.mjr`。既定は切替の 3 秒前〜2 秒後、`maxTotalMB` はラン全体 — `SegmentRecordWriter.DefaultTailSec` / `DefaultPostSec` / `RecordedSegmentFormat` 〔S:1628-1731〕
- 左グリップ撮影は Dev で ON・Release で OFF、`shots/OFF` が勝つ — `ExperienceShotLogic.IsEnabled` 〔memory/experience_shots.md〕
- 体験の記録は `files/xprec/<日時>/`。`xprec/ON` があるときだけ、スタッフの準備画面が閉じた瞬間から録る。映像は展開できた JPEG をそのまま残す — `ExperienceRecorder` / `XpRecFormat` / `CameraStream.AnyFrameDecoded` 〔memory/experience_replay.md〕
- 再生中（`XpReplay.Active`）は焼き込みだけで動き、卓・heartbeat・:8090・発見・配信スマホへ繋がない。カメラの JPEG は `XpReplay.Feed` から来る — `ShowControlClient.BakedOnly` / `CameraStream.TryTakeFrame` 〔memory/experience_replay.md〕
## 罠（破ると壊れる）
- 止まった配信も `/health.fps` は 30 前後を返す。`encodeIdle` で見る。`latestFrameAgeMs=-1` は未生成 〔S:74-86〕。`totalFrames=0` は需要駆動で故障ではない 〔memory/camera_fleet.md〕
- 未初期化 `Texture2D` は Quest で白ノイズ。mipChain 無しだと劣化が効かない 〔S:215, S:1473-1506〕
- `Phone*.asset` / `ShowServer.asset` の host と `StreamingAssets/show/` はコミットしない 〔S:532〕
- 卓サーバ稼働中は show.json を直接書かない 〔memory/show_json_is_live_config.md〕
- カメラの正常は完成 JPEG 2 枚以上の連番で決める 〔S:3332〕
- 上流の無い展示網は次の起動で Android が自動接続を切る 〔memory/no_internet_autojoin_kill.md〕
## 深く読むとき
- 受信 → S:26-230, S:3369-3383 ／ 別ソース → S:41-60 ／ 配信アプリ → S:88-200
- 旧配布 → S:387-535 ／ 発見 → S:3338-3353 / memory/camera_fleet.md ／ 録画 → S:1622-1758
- タブレット → memory/visitor_tablet.md ／ 当日 → memory/onsite_day_ops.md
