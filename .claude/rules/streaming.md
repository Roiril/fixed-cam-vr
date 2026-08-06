---
name: streaming
description: MJPEG / WebRTC 取り込み規約。スマホからの映像受信パス
globs:
  - "Assets/**/Streaming/**"
  - "Assets/**/*Mjpeg*.cs"
  - "Assets/**/*Stream*.cs"
---

# ストリーミング取り込み規約

## 入力ソース

| ソース | プロトコル | レイテンシ実測 | 用途 |
|---|---|---|---|
| **fixed-cam-streamer** (自作 Android) | MJPEG over HTTP `:8080/video` + `/info` + `/health` | 100–200ms | **Android 標準（DroidCam から移行済み）** |
| **IP Camera Lite** (iOS) | MJPEG over HTTP `:8081/video`、**Basic 認証（既定 admin/admin）** | 未実測 | **iPhone 標準候補**（下記「iPhone（iOS）ソース」参照） |
| DroidCam (Android) | MJPEG over HTTP `:4747/mjpegfeed?WxH` | 150–300ms | フォールバック / 緊急時 |
| IP Webcam (Android) | MJPEG over HTTP `:8080/video` | 150–300ms | 代替 |
| WebRTC（自作 PWA / Larix） | WebRTC | 50–100ms | 低レイテンシ要件時 |

受信側（`MjpegStreamReceiver` / `CameraSource`）は **fixed-cam-streamer 専用ではなく汎用 MJPEG クライアント**。
boundary は Content-Type から自動検出、chunked は自動デチャンク、`X-Capture-Ns` / `X-Frame-Seq` /
`/info` / `/health` は全てオプショナル（無いサーバでは歯抜け検出・遅延推定・自動回転・lag 検出が
無効になるだけで映像は通常受信できる）。

## iPhone（iOS）ソース

fixed-cam-streamer は Android/Kotlin 専用で iPhone では動かない（iOS 移植は Mac + Xcode が必要で未着手）。
iPhone は既製の MJPEG 配信アプリで代替する。実運用想定: iPhone 13 Pro ×2 + Pixel ×1（2026-06 時点・未確定）。

- **標準: IP Camera Lite**（App Store、無料）。MJPEG を `http://<ip>:8081/video` で配信、Basic 認証（既定 admin/admin、アプリ内で変更可）。**2026-06-11 iPhone 13 Pro 実機で受信検証済み**
- CameraSource 設定: `port=8081` / `videoPath=/video` / `infoPath=""` / `healthPath=""` / `username,password` を入力
  - **Basic 認証対応は CameraSource の username/password フィールド**（2026-06-11 追加）。空なら Authorization ヘッダ自体を送らない。認証なしアクセスは 401（必須）
  - 実パスワードを設定した .asset はコミットしない（既定 admin/admin はコミット可）
- **超広角対応**: アプリ内カメラ選択で「Back Ultra Wide Camera」を選べる（13 Pro 実機確認済み）。広角配置にも iPhone を使える
- **配信プロファイル実測**（13 Pro 既定設定）: HTTP/1.0・非 chunked・`boundary=--BoundaryString`・パート毎 `Content-Length` 付き。1 フレーム ≈93KB、帯域 ≈30Mbps と **fixed-cam-streamer（quality 40 / 640x480）の数倍重い** → 3 台運用ではアプリ側で解像度/品質を下げること
- **解像度・アスペクト（2026-06-17 調査 / 2026-07-23 更新）**: 既定は **640×480（4:3）固定**。黒帯は無く素の 4:3（iPhone センサーのフル 4:3）。**streamer も v0.6.0 から 4:3（640x480）になったので既定のままでアスペクトが揃う**（旧記述「16:9 に変更して揃える」は廃止 — 16:9 は縦画角を 1.33 倍失う）。web-compositor のビューは live 実寸からアスペクトを自動追従する
- **遠隔制御・メタ endpoint は無し**: `/video?resolution=` 等の URL パラメータは効かない。`/settings` `/status` `/info` `/config` `/jpeg` `/photo.jpg` は全て 404。解像度・品質・カメラ選択は**アプリ UI でしか変えられない**。`/` も `/video` と同じ MJPEG を返す（`Server: IP Camera for iOS`）
- **Lite（無料）版は全フレームにウォーターマークが入る**。演出上問題なら有料版で除去
- `/info` 無し → 自動回転メタは来ない。**端末を横持ち固定**で運用するか、`MjpegScreen.uvRotSteps` で手動補正
- `/health` 無し → lag 検出・E2E 遅延推定は自動無効。カクつき調査はアプリ側 UI とルータで切り分け
- 401 が返ると `[MJPEG]` ログに「Basic 認証が必要」のヒント付きでエラーが出る
- 接続検証はブラウザ or `curl.exe -m 4 -s -D - -o NUL --user admin:admin http://<iphone>:8081/video` でヘッダ確認が最速（エンドレスストリームなので exit 28 = timeout が正常）
- **使えないアプリ**: iVCam は「iPhone → PC クライアント → 仮想 Web カメラ」型で iPhone 側に HTTP サーバが立たない → 本構成（Quest が直接 pull）では不可。同型（EpocCam / Camo 等）も同様

## fixed-cam-streamer エンドポイント

| URI | レスポンス | Unity 側で読む箇所 |
|---|---|---|
| `GET /video` | `multipart/x-mixed-replace; boundary=frame` の MJPEG。**各パートに `X-Width` / `X-Height` / `X-Rotation` / `X-Capture-Ns` / `X-Frame-Seq` ヘッダ付き** | [`MjpegStreamReceiver`](../Assets/Scripts/Streaming/MjpegStreamReceiver.cs) |
| `GET /info` | `{deviceName, lensId, lensFovDeg, widthPx, heightPx, rotationDeg, isPortrait, deviceRotationDeg}` + `{tiltPitchDeg, tiltRollDeg, tiltState}`（v0.9.0〜・下記「端末の傾き」） | [`StreamMetadataFetcher`](../Assets/Scripts/Streaming/StreamMetadataFetcher.cs)、[`CameraStream.Start()`](../Assets/Scripts/Streaming/CameraStream.cs) で 1 回取得。`deviceRotationDeg`=端末の物理的な上方向(0/90/180/270、OrientationEventListener 検知)。配信フレームは常に正立済み（`rotationDeg=0`、横持ち=640x480 / 縦持ち=480x640。v0.6.0 から 4:3）。**tilt は Unity 側の DTO に無く JsonUtility が無視する**（使うのは卓だけ） |
| `GET /health` | `{uptimeMs, totalFrames, totalBytes, fps, sentFrames, latestFrameAgeMs, clientCount, eisMode, oisMode, afMode, cropRatio, zoomRatio, aeState, aeLock, awbLock, expUs, iso, flicker}`（v0.5.0〜 後半は CaptureResult 直読みの実効値 = 決定性の観測用）+ `{thermalStatus, thermalHeadroom, throttleStage, encodeIdle, batteryTempC, plugged}`（v0.7.0〜 発熱抑制の観測。**⚠ clientCount=0 のとき fps=0・totalFrames 静止は需要駆動 encode 停止の正常動作**。throttleStage 1/2 は熱スロットル中＝fps/画質が自動降下している） | 任意。`CameraStream.RefreshHealthAsync()` で都度取得（HudDump からのモニタ用）。`sentFrames` と `totalFrames` の差分が広がる時は HTTP ワーカ詰まり（**⚠ sentFrames はクライアント接続ごとに加算される** — Quest + web 卓 /cam プロキシ等の多クライアント時は sent ≈ 接続数×totalFrames が正常。単一クライアント前提でしか差分ヒューリスティックを使わない）。`latestFrameAgeMs` が大きい時はカメラ stall |
| `GET /` | 簡易ステータス HTML | ブラウザ確認用 |

リポジトリ: [Roiril/fixed-cam-streamer](https://github.com/Roiril/fixed-cam-streamer)（private）。APK ビルド・インストール手順はそちらの README 参照。

## MJPEG デコード

- **方針**: 自前実装（無料）でまず動かす。安定性が問題になったら AVPro Video（有料）に移行検討
- **ライブラリ**: `multipart/x-mixed-replace` の boundary パース → JPEG バイト列 → `Texture2D.LoadImage`
- **スレッド**: HTTP 受信は別スレッド or `async Task`。`Texture2D.LoadImage` はメインスレッド必須

### 性能ガイドライン（低遅延優先）

- **解像度**: 1080p は Quest 3 でも GC アロケーションが増える。fixed-cam-streamer 側は **既定 640x480（4:3・v0.6.0〜）**（`CameraController.streamWidth/Height` で変更可）。16:9(640x360) はセンサー 4:3 の縦を 1.33 倍クロップして垂直画角を失っていた（Pixel 7a 実測で純正カメラ .5x より狭く見えた真因。水平は zoom 0.53 で既に純正 .5x と一致していた）。4:3 化で純正 .5x と両軸一致（SIFT 実測 水平 0.998 / 垂直 0.989）
- **FPS**: streamer 側で `CONTROL_AE_TARGET_FPS_RANGE=[30,60]` を強制し、暗所で 15fps へ落ちないよう固定。capture フレーム間隔そのものが遅延の下限になる
- **JPEG quality**: 既定 40。視認性は維持しつつ Wi-Fi 帯域を半減 → kernel バッファ滞留減
- **バッファ**: 受信側は単一スロット最新フレーム + バッファ swap で再利用、毎フレ `new byte[]` 発生ゼロ
- **TCP**: 両側とも `TCP_NODELAY=true`（Nagle 抑止）。streamer は `SO_SNDBUF=64KB`、Unity は `SO_RCVBUF=64KB` で kernel 滞留を抑制
- **Texture**: `Texture2D` は事前確保し `LoadImage` で上書き。新規作成しない（**かつ初期化必須** — 未初期化は Quest GPU で白ノイズ化する [unity_pitfalls.md](../.claude/memory/unity_pitfalls.md)）

### 遅延対策（実装済み一覧）

| レイヤ | 対策 | 効果 |
|---|---|---|
| Capture (streamer) | `AE_TARGET_FPS_RANGE=[30,60]` で fps 下限固定 | 暗所での 15fps 化を防止（フレーム間隔 = 遅延下限） |
| Encode (streamer) | JPEG quality=40, 解像度 640x480（4:3） | エンコード時間 + 帯域を抑制（4:3 は 16:9 比 +33% 画素だが垂直画角優先） |
| Distribute (streamer) | `FrameDistributor` は単一最新フレームのみ保持 | 配信側の滞留ゼロ |
| Network (streamer→VR) | `TCP_NODELAY=true`, `SO_SNDBUF=64KB`/`SO_RCVBUF=64KB` | Nagle 待機 + kernel バッファ滞留を排除 |
| Multipart header | `X-Capture-Ns` / `X-Frame-Seq` 付与 | 受信側で歯抜け検出・古フレ判定が可能 |
| Receive (Unity) | 単一スロット + バッファ swap、毎フレ new ゼロ | GC 圧ゼロ |
| Drain (Unity) | 受信時点で「最新のみ」上書き | 古フレが Tick まで生き残らない |
| Lag detect | `recv_fps / min(phone_fps, 30) < 0.7` が 1.5s 続いたら強制再接続 | TCP cwnd 縮みっぱなし状態を自動復旧 |
| Monitor | `/health` の `latestFrameAgeMs` / `sentFrames` で原因切り分け | 配信側 stall vs ネットワーク詰まりを判別 |

## ビューア体験レイヤ（追従・切替・フェイルソフト）— 2026-07-19

計画 [.claude/plans/2026-07-19_viewer-ux.md](../plans/2026-07-19_viewer-ux.md)（設計契約・初期パラメータ表はここが正）。

- **追従の緩急**: [`ScreenAnchor`](../../Assets/Scripts/Streaming/ScreenAnchor.cs) は yaw 純スナップを廃止し
  SmoothDamp 0.3s + 角速度上限 110°/s + 逆走ガード（`YawFollowLogic` 純ロジック・テストあり）。
  提示 2.0m・中心 -8°・スクリーン 4/3 倍（角径維持）。再ロック/ロスト復帰はスナップ禁止・減衰合流
  - **⚠ 目標は「頭の正面ちょうど」で、そこで止まる**（2026-08-01 改訂・ユーザー指摘
    「頭の前までぎりぎり到達しないとかはやめて」）。`YawFollowLogic` は
    **deadzone（動き出す閾値）と trail（止まる位置）を別の引数で取る**。旧実装は 1 つの値（10°）を
    両方に使い、目標を `headYaw − sign(e)·10°` に置いていたので、**スクリーンは頭の正面へ一度も
    到達しなかった**。加えて「動き出した後も deadzone を効かせる」と目標へ寄る途中で止まるので、
    **動き出したら deadzone は見ない**（`_moving` のヒステリシス）。到達後は目標へ吸着して静止する
  - スクリーンは trail=0 / deadzone 0.5°（**非シリアライズ const** — SerializeField に戻すと
    シーン・prefab に焼かれた 10 がそのまま効いて再発する）。
    **[`StatusHud`](../../Assets/Scripts/Diagnostics/StatusHud.cs) は逆で trail=deadzone=10° のまま**
    （HUD が視界の真ん中に居座ると映像を隠す）
- **切替は [`CameraSwitchDirector`](../../Assets/Scripts/Streaming/CameraSwitchDirector.cs) に一本化**:
  クールダウン **0.5s**（画面層 `SwitchDirectorLogic`）・最小滞在 **0.5s**（dwell＝人の層 `ZoneProgressionLogic`・2026-07-25 段 B で分離）・
  **cue 再生中は自動切替凍結**（凍結するのは画面だけで時計は進む）・手動後 8s は自動抑止・
  dip-to-black 70/100ms + `SwitchAudioCue`（音源は空スロット）。Web cameraOverride は従来どおり即時
  - **dwell / クールダウン既定 0.5s（2026-07-22 改修）**: 旧 2s は 1.8m 四方の部屋（帯幅 ~0.45m を歩行 0.6〜1.5s で通過）に
    過大で「歩くと切替が起きず、止まった瞬間に遅れて dip 付きで切替」の不具合を出した。`minDwellSec` / `switchCooldownSec` は
    **非シリアライズ private**（既定 0.5s。旧 2/2 が焼き付いたシーン YAML を無効化する LongPressSec と同手法）。
  - **現場調整 = show.json control**: `control.minDwellSec` / `control.switchCooldownSec`（Web 卓「ライブ運用」パネルの数値 2 入力）。
    present 判定は **>0 で上書き / 0・未指定はコード既定 0.5s**（`SwitchDirectorLogic.ResolveTiming`）。ShowControlClient が
    ライブ / 端末キャッシュ / 焼き込みのいずれからでも `ApplyTimingOverride` で Director へ流す（`CachedConfig` へ往復＝PC 不在でも生きる）。
    ShowControlClient→Director 参照は既存シーンで未配線でも `ResolveSwitchDirector`（GetComponent→FindObjectOfType）で遅延解決。
  - **保留キャンセル**: 保留中の目標が現在表示カメラへ戻ったら pending をクリア（境界でうろついた後に古い切替が突然 commit されない）。
    無効カメラ index（ゾーン外・target<0・registry 範囲外）の要求は無視（現カメラ継続）。
  - **Web override 解除後の自己回復**（2026-07-23 修正・テスト `SwitchWiringTests.T5`）: override 中は `ShowControlClient` が
    `PlayerZoneTracker` を `enabled=false` にし、解除で `enabled=true` に戻す。再有効化は `PlayerZoneTracker.OnEnable` を発火し、
    そこで記憶ゾーン（`_current`）を無効化（`InvalidateCurrent`）する → 次 Update で現在位置から再 Pick し通常経路（RequestZone→dwell→Zone commit）で
    ゾーンカメラへ復帰する。これが無いと同一ゾーン滞在のまま `_current` が不変で、次のゾーン跨ぎまで override カメラに表示が固着していた。
    （asmdef 循環回避のため Streaming→Tracking の型参照は作らず、再有効化＝自己回復に委ねる設計。当初案の SendMessage は EditMode テストで
    `ShouldRunBehaviour` アサートを踏むため OnEnable 自己回復へ変更した。）
  - **Web override 中はゾーン自動切替を第一級凍結**（2026-07-23 監査修正・テスト `SwitchDirectorLogicTests`/`SwitchWiringTests.T6`）:
    `SwitchDirectorLogic._overrideActive`（cue/insert 凍結と対称）。`ShowControlClient.Apply` が `CameraSwitchDirector.SetOverrideActive` で
    掛け外しし、override enter/exit で **stale なゾーン保留を無条件クリア**する。旧実装は tracker 無効化だけで Director 内の保留
    （override 直前に積まれた別カメラの dwell 計時）が残り、約 cooldown+dwell 後に `SwitchSource.Zone` で commit して固定が破れ
    LapCounter も誤進行した。解除後の復帰は従来どおり PlayerZoneTracker OnEnable 自己回復が権威（保留は復活させない）
- **デッドバンド自動クランプ**: [`ZoneLayoutApplier`](../../Assets/Scripts/Tracking/ZoneLayoutApplier.cs) は tracker へ渡す
  hysteresisShrink を **`min(hysteresisM, overlapM/2)`**（`ZoneLayoutSolver.ClampHysteresis`）へクランプし、逆転時に 1 回警告。
  現 show.json（overlapM=0.08 / hysteresisM=0.12）は編集なしで実効 **0.04** になり、`Pick` の shrink 保持デッドバンドが復活する
  （shrink が overlap 帯より広いと shrink AABB を出た地点で隣ゾーンの重なり帯も抜けていてデッドバンドが消える）。grid/cuts 両経路に効く。
- **フェイルソフト**: [`SignalLostFx`](../../Assets/Scripts/Streaming/SignalLostFx.cs) — 配信断 600ms → 砂嵐へ 150ms、
  トラッキングロスト/pause 明けは追従凍結 + 弱ノイズ → 減衰合流。体験者には「信号ロスト」に見える
  - **未受信カメラも砂嵐で覆う**（2026-07-22）: 一度もフレームを受信していないカメラ（`LastFrameRealtime==0`・黒プレースホルダ表示中）は
    従来の「最終フレームから経過」判定が発火せず素の黒が露出した。アクティブ stream 参照の切替時刻を追跡し、アクティブ化から
    lostThresholdSec 超過で砂嵐化する（切替・resume-gap でタイマー再シード）。OnDisable で `_SignalLost` を 0 へ戻す
- **cue のマスク読込失敗時は cue を中止**（2026-07-22）: [`ScreenOverlayController`](../../Assets/Scripts/Streaming/ScreenOverlayController.cs) は
  `maskUrl` 指定ありでロード失敗した場合、白フォールバック（全面差し替え＝黒背景素材なら live 全面黒）に落ちず cue 発火を中止して live を守る
  （動画/静止画パス両方）。maskUrl 未指定の意図的な全面差し替えは従来どおり白フォールバック。StopOverlay は `_current==null` でも世代を進めて
  in-flight のロード完了を無効化する（stop 後に cue が復活する穴を塞ぐ）
- **動画 cue の Prepare 失敗/タイムアウトも cue を中止**（2026-07-23 監査修正・テスト `OverlayPlaybackLogicTests`/`OverlayVideoFailureTests`）:
  DL 失敗→ストリーミング URL フォールバック→Android `NuCachedSource2 -1` で Prepare が失敗すると `_current` が残留し、
  `CameraSwitchDirector` が cueActive=true のまま自動切替（LapCounter/CueScheduler 含む）を恒久凍結する穴があった。
  `errorReceived`（`OnVideoError`）+ `prepareTimeoutSec`（既定 6s・unscaled）で**現行世代の動画 cue を `AbortCurrentCue` して live へ復帰**。
  世代/Prepare ライフサイクルは純ロジック [`OverlayPlaybackLogic`](../../Assets/Scripts/Streaming/OverlayPlaybackLogic.cs) に分離
  （Prepare 中の `_current` 先出し=切替凍結は正しい挙動として維持。失敗時のみ畳む）。`[ScreenOverlay] cue aborted` ログで検知可
- **グリップ cue トグルは無条件停止優先**（2026-07-22 / 2026-07-23 補強）: [`ShowControlClient.ToggleActiveCameraCue`](../../Assets/Scripts/Streaming/ShowControlClient.cs) は
  何か再生中（`_overlay.Current != null`）なら id 一致に依存せず無条件で停止する。**停止は server 到達時も `stopCue`（空 id）送信に加えて必ずローカル `StopOverlay` を併用する**（2026-07-23 修正・テスト `GripStopLocalTests`）。
  スケジューラ発火 cue は server の activeCue が空のままなので、stopCue 送信だけでは Apply の遷移判定（cueId != _appliedCue）が起きずローカル再生が止まらない穴があった。コマンド送信は Web 表示・heartbeat との整合維持のため残す。
  スケジューラ発火の別 id cue（cue_A_1 等）が表示中でも黒/演出を確実に止められる緊急復帰。何も再生していない時だけ `cue_<camId>` を発火
- **⚠ 卓の停止ボタンには同じ穴が 2026-07-28 まで残っていた**（グリップ側だけ 2026-07-23 に塞がれていた）:
  Web の `■ 演出を止める` / `⛑ 全部解除して自律へ` は `stopCue` + `setCameraOverride:null` しか送らず、
  **タイムラインが自動発火した演出（Take）を一切止められなかった**。Take は `activeCue` を使わず空のまま走るので
  `liveSuppressed`（`ShowControlClient.cs` の `activeCue非空 || override非空`）が false→false で
  `TakeRunner.SetSuppressed` が畳む条件に入らない。皮肉にも「📺 カメラ固定 → 🚶 解除」の 2 段だけが実際に畳んでいた（UI に記載なし）。
  → **`control.takeAbortEpoch: int`（世代カウンタ）を追加**。`runEpoch` と同じ「値の変化で伝える」流儀
  （long-poll は control の差分でしか動けないので、「空を空にする」形の停止は原理的に届かない）。
  Web の `POST /command {type:"abortTake"}` が ++ し、`ShowControlClient` の変化検知が
  [`TimelineDirector.AbortActive`](../../Assets/Scripts/Streaming/TimelineDirector.cs) →
  [`TakeRunner.AbortActive`](../../Assets/Scripts/Streaming/TakeRunner.cs) を呼ぶ。
  **抑止フラグは立てず、once・周回も保つ**（走行中の 1 本だけを畳む＝体験をやり直しにしない）。
  ボタンは `■ 画面を取り返す` に改名し `stopCue` と `abortTake` を両方送る（卓 cue と Take の両系統を畳む）。
  初回 Apply は現在値へ同期するだけで発火しない（起動のたびに中止が走らない）
- **切替 dip は unscaledDeltaTime で進行**（2026-07-22）: `CameraSwitchDirector.AdvanceDip` は timeScale=0 で黒凍結しないよう
  unscaledDeltaTime で進める（StartupFader と同流儀）。OnDisable で dip を解除（`_SwitchDim` を 0・状態 Idle 化）
- **⚠ ScreenComposite の `_SwitchDim` / `_SignalLost` は post FX 数式（Web FS_POST 一致規約）の対象外**（別系統 uniform）
- ステータス表示（[`StatusHud`](../../Assets/Scripts/Diagnostics/StatusHud.cs)）は既定 OFF（右 B で表示トグル・lap/ゾーン/次の cue/信号 ●●○/要再登録 を単一サーフェスに緩追従表示。旧 RuntimeDebugHud の STATE 行相当を統合）
- **⚠ 実機試着未実施**（追従・フェイルソフト系パラメータは SerializeField・現場調整前提）。dwell/クールダウンは
  非シリアライズ既定 0.5s + show.json control で調整（上記）。ゾーン滞在が dwell 未満で駆け抜けると切替が発生しない点は同じ（値で調整）

## スクリーン表示モデル（固定枠 + シェーダ letterbox）

**web-compositor と同じモデル**（2026-06-11 移行）。Screen Quad の Transform は authored のまま**不変**で、フィット・回転・合成・ポスト FX は全部 [`ScreenComposite.shader`](../Assets/Art/Shaders/Streaming/ScreenComposite.shader) の UV 空間で完結する。

- [`MjpegScreen`](../Assets/Scripts/Streaming/MjpegScreen.cs) はテクスチャ供給 + contain-fit スケール（`_LiveScale`）計算のみ。Texture2D 実寸からアスペクトを毎フレーム追従（`/info` は使わない — JPEG 実寸が真実）
- ソースは contain-fit、はみ出しは黒 letterbox。**枠サイズはカメラ切替・縦横切替でも不動**
- streamer は常に正立フレームを送る（`rotationDeg=0`）ので回転補正は不要。緊急時は `MjpegScreen.uvRotSteps`（90 度単位の UV 回転）で手動補正
- 旧 `ApplyOrient`（/info メタで Transform を回転・変形）は**廃止**。`autoOrient` / `orientTarget` / `useIsPortraitForRotation` 等のフィールドはもう存在しない

### 映像差し替え（オーバーレイ合成）

[`ScreenOverlayController`](../Assets/Scripts/Streaming/ScreenOverlayController.cs) + [`OverlayCue`](../Assets/Scripts/Streaming/OverlayCue.cs)（ScriptableObject、`Create > FixedCamVr > Overlay Cue`）:

- cue = 事前撮影 VideoClip / 静止画 / URL ソース + マスク Texture（R チャンネル、スクリーン枠空間、白=差し替え）+ フェード時間 + 再生区間
- 固定視点なのでマスクは事前撮影フレームから作ればそのまま位置が合う（web-compositor で検証済みの理屈）
- **⚠ マスクだけは contain-fit を通らない。座標系は必ず「スクリーン枠空間」（16:9）**（2026-07-30 に是正）。
  シェーダは live / overlay / CG を `_LiveScale` / `_OverlayScale` / `_CgScale` で contain-fit するが、
  **`_MaskScale` は存在せず生 uv で読む**（[`ScreenComposite.shader`](../Assets/Art/Shaders/Streaming/ScreenComposite.shader) の `SampleBase`）。
  したがって 4:3 のソース座標のままマスクを焼くと、実機でだけ水平 1.33 倍・枠幅の最大 12.5% 外側へずれる。
  - **卓では原理的に見えない**バグだった（工房が 640×480 の枠でプレビューしていたため）。
    実際に現地の実素材 2 件（`cue_hand_B` / `cue_ningyo_A`）がずれたまま合成されていた
  - 生成器 3 つはすべて枠空間で焼く: 素材工房の自動差分（`atelier.js` の `bakeMask`）/
    cue エディタのスライダ / [`make-diff-mask.py`](../../tools/web-compositor/make-diff-mask.py)。
    寸法の正は `common.js` の `MW`/`MH`（640×360）で、CLI の `FRAME_W`/`FRAME_H` と対
  - **枠が 16:9 であることは Unity 側のテストが固定する**（`ScreenFrameAspectTests`）。
    Quad の localScale・`screenAspectOverride=0`・卓の 2 定数・シェーダに `_MaskScale` が無いこと を突き合わせる。
    **枠のアスペクトを変えるならこの 4 つを同時に直す**（片方だけだと沈黙して食い違う）
  - マスクは「素材のどこを使うか」ではなく「**スクリーン枠のどこを差し替えるか**」。素材が letterbox される
    領域に白を置いても、そこに overlay は無いので live が消えて黒が出るだけ（`ContainUv` が範囲外を 0 にする）
- 発火: キーボード（CueBinding.key、Editor+Link オペレータ用）/ `PlayCue()` / `StopOverlay()` / **Web オペレータ卓の演出 ON/OFF（show.json `control.activeCue`）**
- ポスト FX（vignette/grain/scanline 等）はシェーダ内 = スクリーン内容にだけかかる。視界全体への FullScreenPass とは独立

### Web 連携の挙動（2026-06-17 / show.json cue 由来）

- **フェード**: `cue.fadeIn/fadeOut`（Web の演出トグル横の秒入力）で ON=fade-in / OFF=fade-out
- **ループ無し + 再生終了で自動復帰**: `cue.loop=false`。動画が自然終端（`loopPointReached`）か `trimEnd` に達したら自動 `StopOverlay` → live へフェード復帰（[`ScreenOverlayController.Update`](../Assets/Scripts/Streaming/ScreenOverlayController.cs) の trimEnd 監視）
- **再生区間 trim**: `cue.trimStart` へシークして再生、`trimEnd>0` で停止（`trimEnd<=0`=最後まで）
- **⚠ 動画 URL は UnityWebRequest でローカル DL してから `file://` 再生**（[`GetLocalVideoUrlAsync`](../Assets/Scripts/Streaming/ScreenOverlayController.cs)）。Android ネイティブ VideoPlayer は Python http.server(HTTP/1.0) からの HTTP ストリーミングを扱えず `NuCachedSource2 error -1` で落ちるため（画像/マスクは UnityWebRequest なので直 URL で OK）。URL 毎にキャッシュ。スペース入りファイル名は Web 側が percent-encode
- **マスクのフェザー**は Web が cue 保存時に PNG へ焼き込む（Quest はマスクをそのままサンプル）。**色統計マッチング・ラプラシアンは Web プレビュー専用**で Quest 実機の ScreenComposite はハード合成（`lerp(live,overlay,mask)`）
- **post-FX 数式**は ScreenComposite と Web の `FS_POST` を一致させてある
  （露出 → 色温度 → **色かぶり** → コントラスト → **黒浮き** → 彩度 → ヴィネット → 走査線 → グレイン の順・式）。
  **色かぶり（tint・緑↔マゼンタ）と黒浮き（lift）は 2026-07-26 追加**（監視カメラらしさに要る 2 軸。既定 0 = 旧データと同じ絵）

## カメラ管理

- 各カメラは `ScriptableObject` で URL・解像度・名前を定義（`Assets/Settings/Cameras/` — Phone01/02/03 の 3 台。StreamingLogic prefab の sources に登録済み）
- 既定値: port=8080, videoPath=/video, infoPath=/info, healthPath=/health（fixed-cam-streamer 互換）
- DroidCam 互換時は port=4747, videoPath=/mjpegfeed?WxH, infoPath="" にする
- 切替時は **常時受信を維持**（再接続コスト回避）し、表示先 Quad の `enabled` または RenderTexture 切替で対応
- 同時 3 台までは Quest 3 で実用域、それ以上は要計測

## show.json = 設定契約（IP / カメラ別画像加工を実機へ流す）— 2026-06-16

Web オペレータ卓（`tools/web-compositor/`）の `show.json` が **Web と Quest 実機の共有設定**。
カメラの接続先（host/port/auth）と画像加工（post）をここで決め、実機が参照する。計画
[.claude/plans/2026-06-16_web-config-to-quest.md](../plans/2026-06-16_web-config-to-quest.md)。

- **`cameras[i].host/port/auth`**: 従来 Web プレビュー専用だったが、**Unity 実機も読む**ようになった。
  [`ShowControlClient.ApplyCameraEndpoints`](../Assets/Scripts/Streaming/ShowControlClient.cs) →
  [`CameraStreamRegistry.ApplyEndpoint`](../Assets/Scripts/Streaming/CameraStreamRegistry.cs) →
  [`CameraSource.ApplyRuntimeEndpoint`](../Assets/Scripts/Streaming/CameraSource.cs)（`[NonSerialized]` 実行時上書き、
  **焼き込み .asset を汚さない**＝git 巻き込み防止）→ 変化時のみ [`CameraStream.ReapplyConnection`](../Assets/Scripts/Streaming/CameraStream.cs) で MJPEG 張り直し。
  **空 host は override 解除＝焼き込み値へフォールバック**（Web 未設定カメラの保護）
- **`cameras[i].post`**（任意）: カメラ別の明るさ・色補正。アクティブカメラ切替時に
  [`ShowControlClient.ApplyPostForActive`](../Assets/Scripts/Streaming/ShowControlClient.cs) が適用。
  未設定カメラはトップレベル `post`（global＝全体グレーディング）にフォールバック。
  JsonUtility が null 入れ子を既定値で書く罠を避けるため「個別 post を持つか」は明示 `hasPost` bool を正にする。
  **⚠ 粒度はカメラ単位のみ** — cue にも周（schedule）にも紐づかないため周ごとの画質変化はできない（下記「編集の粒度と自由度」参照）
- **永続化（PC 不在でも参照）**: 受信 show.json を `persistentDataPath/show_config.json` にキャッシュし、
  起動時に再適用。**優先順位は 焼き込み .asset < 端末キャッシュ < ライブ long-poll（後勝ち）**。
  キャッシュは「一度ライブ受信した後」生成されるので、完全新規インストール＋PC 不在の初回は焼き込み値で起動
  - **⚠ この優先順位は「APK を焼き直せば設定も新しい」を成り立たなくする。** キャッシュが焼き込みより
    上なので、古いキャッシュが残っている端末はビルドし直しても前の設定で走る。実測（2026-07-31）で
    PC の show.json・焼き込み・Quest 2 台のキャッシュの **4 者がずれていて正しいのは 1 つだけ**だった
    （片方の Quest にだけ `run.intro.startLineId` があり、導入の始まり方が機ごとに違った）。
    しかも `timeline.rev` は全部 21 で一致していて **rev では気づけない**
  - → 実機は [`ShowControlClient.ConfigOrigin`](../../Assets/Scripts/Streaming/ShowControlClient.cs) /
    `DescribeConfig()` で**使った設定の出所と骨格**を持ち、`[XP] ev=config` として吐く。
    [`analyze-xp-log.py`](../../tools/analyze-xp-log.py) の `config_from_show()` が PC の show.json から
    同じ要約を作って突き合わせ、違えば **FAIL**（「出なかった演出」を設定ずれのせいで誤検出しないため）。
    **項目を足すときは C# と Python を対で直す** — 片方だけだと沈黙して食い違う。
    キャッシュの掃除は `python tools/quest-fleet.py reset-config <serial>`
- **server 不在でも ShowControlClient は動く**（旧コードは `enabled=false` で自滅していた）。
  long-poll / heartbeat だけスキップし、キャッシュ適用とカメラ別 post のゾーン切替連動は成立する

## 事前オーサリング済み cue スケジュール（周回×ゾーン発火 + APK 焼き込み）— 2026-07-17

「何周目のどのゾーンで、どの cue を差し込むか」をビルド前に決めて APK に焼き込み、現地 PC 不在でも
Quest 単体で自動発火する仕組み。計画 [.claude/plans/2026-07-17_pre-authored-cue-schedule.md](../plans/2026-07-17_pre-authored-cue-schedule.md)。

- **cues は複数化**: id は任意（Web 卓の規約は `cue_<camId>_<n>`）。1 カメラに複数 cue を持てる。旧単数 id（`cue_<camId>`）も後方互換で動く
- **`layout.regPoints[]`**: 位置合わせのタッチ基準点 `{x, z, label}`（course 座標・順序=タッチ順・2〜5 点。Web 卓フロアマップの「📍 位置合わせ点」で配置。不在なら Unity は従来既定 2 点にフォールバック）— 2026-07-19
- **`layout.course.order`**: 順方向のカメラ巡回順（`order[0]`=スタート領域）。Web 卓フロアマップの「周回コース」で編集（grid の塗りから角度順提案 + CW/CCW トグル）
- **`schedule.entries[]`**: `{lap, camera, cueId, delaySec, once}`。lap は **1 始まり**、camera は**カメラ index**。Web 卓の**タイムライン UI**（2026-07-19〜 マトリクスから改装。「1周目: A\|B\|C → 2周目: …」を `course.order` 順に横連結・区間クリックで cueId / delaySec / once を編集）で編集
- **周回検知** = [`LapCounter`](../../Assets/Scripts/Tracking/LapCounter.cs)（進行ポインタ方式）: 確定ゾーンが `order[(i+1)%n]` に一致した時だけ前進、`order[0]` 復帰で lap++。**逆走・行き来・スキップ・境界 jitter はカウントしない**
  - **⚠ 2026-07-25（段 B）で駆動点が変わった**: 旧「Director の `SwitchCommitted` の `SwitchSource.Zone` だけを算入」→ 新
    **`CameraSwitchDirector.ZoneCommitted`（ショーの時計）を購読**。時計は [`ZoneProgressionLogic`](../../Assets/Scripts/Streaming/ZoneProgressionLogic.cs)
    が dwell だけで確定させ、**画面の凍結（cue / インサート / override）にも dip にも影響されずに発火する**。
    したがって**演出中に体験者が歩いても周回は止まらない**（設計 [2026-07-25_shot-timeline-foundation.md](../plans/2026-07-25_shot-timeline-foundation.md) 不変条件 4）。
    スタッフ手動 A・Web cameraOverride・インサートの画面切替は `ZoneCommitted` を発火しないので、構造的に周回へ入らない
- **ラン（体験者 1 人分）**: `control.runEpoch`（int・既定 0）の**変化**で LapCounter リセット（lap=1・再シード）+ CueScheduler の once 発火済みクリア。Web ライブ運用パネルの「▶ ラン開始」= **runEpoch++ ＋ `cameraOverride=null` ＋ `activeCue=null` を 1 回の postState で同時に書く**（2026-07-25〜。旧実装は runEpoch だけで、前の体験者のカメラ固定・再生中 cue が次のランへ持ち越された。override / activeCue は端末キャッシュには載らないが show.json には永続するため、卓を立てて Quest を繋いだ瞬間に再適用される＝「歩いても切り替わらない」事故になっていた）。armed なラッチは Web 卓ヘッダ直下の警告バーが show.json 由来で常時可視化する（Unity 未接続でも出る）。PC 不在時は **右グリップ 2 秒長押し** = ローカルランリセット（2026-07-20〜。旧: Staff 左スティック押し込み）。heartbeat に `lap` / `cam` / `mode`（NORMAL/REG）を載せる（Web でラン状態が見える）
  - **2026-07-28 追記**: 「▶ ラン開始」は `slots=[]`（素材スロットの束縛解除）も書き、`abortTake` を併送する。
    旧実装は**前の体験者のために束縛した素材が次のランの演出に出る**穴があり、走行中の演出も畳めなかった。
    卓のラッチ列挙は `latches()` が単一の正で、警告バー・本番前チェック・⛑・▶ ラン開始 が同じ配列を消費する
    （固定 / 演出 / スロット = 解除対象、📌 手動固定 / 発見 OFF = 警告のみで外さない）
  - **⚠ heartbeat の `cam` は「画面に映っているカメラ」で、体験者の居場所ではない**（2026-07-28）。
    演出のカットが別カメラを映している間、両者は食い違う。人の居場所は **`zoneCam`**（ショーの時計
    `CameraSwitchDirector.TryGetCurrentZoneCamera` の確定値・-1 = 未確定）を新設して別に送る。
    卓は `cam` を「ゾーン」と称して出し、しかもその index で区間を引いていたため、
    **演出中は「このゾーン / 次の演出」が別区間の内容になっていた**。区間の照会は必ず `zoneCam` で行う。
    あわせて **`takeId`**（走行中の演出 id・空 = 演出なし）を送り、卓が「いま画面を握っているのは誰か」
    （自律 / 演出 / 卓が固定 / 手動 cue）を 1 行で出す

- **実測滞在時間**（2026-07-25〜）: 区間 (lap, camera) に体験者が実際に居た秒数を
  [`SegmentDwellLog`](../../Assets/Scripts/Streaming/SegmentDwellLog.cs)（純ロジック）が測り、heartbeat の
  `dwell[]`（`{lap,camera,sec}`）で卓へ送る。駆動は **`CueScheduler.CameraEntered`（＝ショーの時計 `ZoneCommitted` 由来）**
  だけなので、手動 A・Web 固定・インサートの**画面切替では動かない**。スタッフ介入中（`activeCue` / `cameraOverride` 非空）と
  ラン開始では計時中の区間を捨てる。送信失敗分は次の heartbeat へ戻す（上限 64・古い方から破棄）。
  卓は `capture-server.py` が `dwell_stats.json` へ集計し `GET /dwell/stats`（`POST /dwell/reset` でクリア）で返す。
  Web リボンはこれを区間フッタ（`実測 平均 8s（最短 6.5s / 2 回）`）と「開始位置が滞在を超える演出」の ⏱ 警告に使う
  — 計画 [2026-07-25_shot-timeline-foundation.md](../plans/2026-07-25_shot-timeline-foundation.md) §4 が
  「作者が『山場が出ない』危険に気づく唯一の手段」と位置づけたもの
- **発火** = [`CueScheduler`](../../Assets/Scripts/Streaming/CueScheduler.cs): (lap, camera) 一致 + delaySec 後に `ScreenOverlayController.PlayCue` を**ローカル直接**呼ぶ（サーバ不要）。**`control.activeCue` が非空の間は抑止**（ライブ手動操作が常に優先）。once=true はラン内 1 回
- **APK 焼き込み**: Web 卓「📦 ビルド用エクスポート」（`POST /export-build`）が show.json + 参照アセットを `Assets/StreamingAssets/show/` へコピーし、URL を `sa://assets/<file>` に書換。Unity 側は [`ShowAssetResolver`](../../Assets/Scripts/Streaming/ShowAssetResolver.cs) が `sa://` → `StreamingAssets/show/assets/` に解決（Android は jar: URL、動画は VideoPlayer 直接パス）。起動時に `StreamingAssets/show/show.json` を読み、優先順位は **焼き込み < 端末キャッシュ < ライブ**（従来の後勝ちを維持）
- **⚠ `Assets/StreamingAssets/show/` はコミット禁止**（gitignore 済み）。エクスポート時点のカメラ host（現場 DHCP IP）が verbatim に焼き込まれるため。ビルド直前に現場でエクスポートし直すのが正
- `CachedConfig`（端末キャッシュ）に cues / schedule / course を保存するようになった（旧: cues 欠落でオフライン発火不可だった）
- **⚠ 実機未検証**（2026-07-17 実装。コンパイル・EditMode テスト 75/75・Web UI・エクスポートは検証済み）

## タイムライン第一級オーサリング（旧 `timeline` v2）— 2026-07-19 / **2026-07-25 に読み取り専用**

> **この節は「過去データの意味」を読むための記録**。2026-07-25 に演出・カット（v3）へ一本化したので、
> **卓はもう v2 を書かないし、Unity にも v2 の実行体は無い**（旧 `InsertController` / 区間 `cues[]` の
> CueScheduler 供給は削除）。端末キャッシュ・焼き込みに残る v2 は
> [`TimelineMigration`](../../Assets/Scripts/Streaming/TimelineMigration.cs) が読み込み時に演出へ変換する。

体験オーサリングの正面を**周回×ゾーン区間（セグメント）のタイムライン**へ再設計した。計画
[.claude/plans/2026-07-19_webui-timeline-authoring.md](../plans/2026-07-19_webui-timeline-authoring.md)（スキーマ・セマンティクスの単一ソース）。
旧「カメラ列の下に cue がぶら下がる」構造と `schedule.entries` の (lap×camera) グリッドを置き換え、
区間 1 個から **cue 割当（複数・パラメータ上書き付き）・画像加工 post の上書き・インサートショット**を一括編集できる。

**セグメント = (lap, camera)**。show.json トップレベルに `timeline` を新設（後方互換：`timeline.rev>0 && segments 非空`
の時だけ Unity が `schedule.entries` を無視して supersede。無ければ従来経路が生きる）。

```jsonc
"timeline": { "rev": 1, "segments": [
  { "lap": 2, "camera": 0,                       // lap=1始まり / camera=index（course.order と同キー）
    "cues": [ { "cueId": "cue_A_1", "delaySec": 0, "once": true,
                "override": { "strength": 0.5, "fadeIn": 1.2, "fadeOut": 0.8,
                              "trimStart": 0, "trimEnd": 0 }, "hasOverride": true } ],
    "post": { /* PostParams 9 項目 */ }, "hasPost": true,          // このゾーン滞在中の post 上書き
    "insert": { "anchor": "exit", "camera": 2, "durationSec": 4,   // 別カメラを N 秒差し込む
                "delaySec": 0, "cueId": "cue_C_scare", "once": true,
                "post": { /* PostParams */ }, "hasPost": false }, "hasInsert": true } ] }
```

- **cue**: 区間進入（Zone commit）+ delaySec で発火。複数可。`override` は ResolveCue 結果（OverlayCueData 複製）への差分パッチ。`control.activeCue` 非空中は抑止（ライブ優先）
- **post 上書き**: 解決は **segment > cameras[i].post > global** の 3 段（Unity `ApplyPostForActive` を拡張。insert 中はさらに insert post が最優先の 4 段）。区間離脱（次の Zone commit）で解除。**「1 周目の B は普通・2 周目の B は赤く」が可能になった**（旧: カメラ単位固定で不可）
- **インサートショット**（旧実装 `InsertController` + `InsertLogic` は 2026-07-25 に削除。
  いまは同じ意味のものが「演出のカット（`source: live`）」として `TakeRunner` で走る）:
  - `enter`: 進入 + delaySec 後、insert.camera を durationSec 秒表示 → 最新ゾーンカメラへ復帰
  - `exit`: **このゾーンを Zone 切替で離れる瞬間**、dip の黒中に insert.camera へ差し替え durationSec 秒 → 最新ゾーンカメラへ復帰。体験者は A→黒→C(N秒)→黒→B と見え、中間カメラのフラッシュを見せない（ユーザー要求「A→B に切り替わる前に C に演出を N 秒」の実装形）
  - 切替は dip-to-black 付き（`CameraSwitchDirector` の `SwitchSource.Insert`）。insert 表示中はゾーン自動切替を凍結（cue 凍結と同型）。**周回カウントは実ゾーン移動の commit 時に通常どおり 1 回**（Insert 切替は LapCounter が数えない）
  - **insert 凍結の解除は必ず後片付け経由**（2026-07-23 修正・テスト `SwitchWiringTests.T1/T2`）: `InsertController.ResetRun` / `SetInserts` は進行中インサート（表示中）があれば先に `CleanupActiveInsert`（insert cue 停止・insert post 解除・`director.InsertReturn`）で畳んでから `InsertLogic` をリセットする。畳まないと `CameraSwitchDirector._insertActive`（凍結）が `InsertReturn` 単一経路でしか降りず、ゾーン自動切替が恒久凍結する（ショー中のタイムライン編集・体験者交代で踏む）。`SwitchDirectorLogic.Reset`（OnEnable 経路）でも `_insertActive`/`_cueActive` を false へ安全初期化する
  - **insert 中の実ゾーン移動**（2026-07-25 段 B で機構が変わった・テスト `SwitchWiringTests.T4`）: insert 表示中に体験者がゾーンを移ると、
    **その時点で時計（`ZoneCommitted`）が発火し周回・区間追跡へ即反映される**（画面は insert のまま）。insert 終了時の復帰先は
    `CameraSwitchDirector.TryGetCurrentZoneCamera`＝**時計の確定ゾーン**を読んで決める（復元ではなく再計算）。
    旧実装の `InsertReturn(returnCamera, asZone:true)`（復帰 commit を Zone に偽装して周回を辻褄合わせする）は**廃止**した
- **present-flag は宣言 bool が正**（2026-07-23 監査修正・テスト `TimelinePresentFlagsTests`/`TimelineFixtureContractTests`）:
  `hasPost` / `hasInsert` / `hasOverride` は必ず書く。Unity は [`TimelinePresentFlags.Reconcile`](../../Assets/Scripts/Streaming/TimelinePresentFlags.cs) で
  **`flag = 宣言bool && object != null`（AND）** に確定し、ライブ / 焼き込み / キャッシュの 3 経路一律に適用する。
  旧実装は `!=null` 純導出で、Web（`timeline.js serialize()`）が flag=false でも既定オブジェクトを常時出力していたため
  **全 present-flag が true に化け、全区間に幽霊 exit インサート（camera0 を 4 秒）が武装**する critical バグだった。
  Web 側も flag=false 時に入れ子キーを省略する形へ変更（`timeline-model.js` に serialize/normalize/migrate を純関数抽出・
  `node --test tools/web-compositor/*.test.mjs` で固定・共有 fixture `Assets/Tests/Fixtures/show_timeline_canonical.json` を生成）。
  **⚠ `CameraDef.hasPost` の `!=null` 導出は 2026-07-30 に破れた。** 「Web が未使用キーを削除する」
  という前提が崩れていて、卓は未設定のカメラに **`"post": null` を明示的に書いていた**。
  JsonUtility はそれを**既定値の実体**で埋めるので `post != null` が true に化け、
  **素通しのカメラ個別 post が global の加工を丸ごと上書き**する。実機は 134 サンプル中 122 が
  `sat1.00 / vig0.00`（無加工）で、3 周目の区間 post が効いた 12 サンプルだけが加工されていた。
  しかも **卓では `??` が null を global へフォールバックするので正しく見える** ＝
  「卓で見えている絵と実機が違う」という最悪の破れ方をしていた（ユーザーが実機で気づいた）。
  → 導出を **`post != null && !post.IsDefaultLike()`** に変更（`PostParams.IsDefaultLike()` は
  全 11 軸が既定かを見る）。回帰は `TimelinePresentFlagsTests` が固定する。
  卓が `post` キーごと省略するようになれば元の導出でも正しいが、**Unity 側で防御する方を正とする**
  （「卓がどう書くか」に依存する判定は、今回まさにそこで破れた）
- **JSON キー `override`** は C# 予約語のため Unity 側は `@override` フィールドで受ける（実行時フィールド名は `"override"` で JsonUtility が正しく往復。実型で確認済み）
- **CachedConfig に timeline を保存**（オフライン発火。旧「cues 欠落」事故の教訓を踏襲）
- **Web 検証モード**（実機不要）: Web 卓「▶ 検証」で **矢印キー**（→ 次ゾーン / ← 1手戻る / R 先頭 / Esc 終了）。Unity セマンティクス（LapCounter 順方向進行・CueScheduler・insert・post 3 段）を JS ミラーで再現し、発火順と「体験者に見える画」を WebGL プレビューで確認。**ローカルのみで show.json / 実機は書かない**
- **⚠ Quest 実機未検証**（2026-07-19 実装。Unity コンパイル・EditMode 126/126・Web 卓ロード + 検証エンジン + セグメントインスペクタはブラウザ検証済み。insert の dip 演出・post 切替の見た目は現場調整前提）

### 演出の 1 語彙化（show.json v3 `takes[]` / 多段カット）— 2026-07-25

**cue（オーバーレイ）と insert（カメラ差し込み）を「演出(Take) / カット(Step)」1 語彙へ畳んだ。**
設計・スキーマの正本は [2026-07-25_shot-timeline-foundation.md](../plans/2026-07-25_shot-timeline-foundation.md)（§6 が契約）。

- **実行体は [`TakeRunner`](../../Assets/Scripts/Streaming/TakeRunner.cs) 1 つ**（2026-07-25 に一本化）。
  `ShowControlClient.PushCueSource` は版を見ずに `TimelineMigration.EnsureTakes` で takes[] を確定させてから
  `TimelineDirector.SetTimeline` へ渡す。**画面の所有者は常に 1 人**という不変条件が、経路の本数からも保証される
- **区間 = (lap, camera) は変わらない**。区間に `takes[]` が 0..N 本ぶら下がる。1 本の演出が
  **カット列**を持つ（`live:<cam>` / `inherit` / `clip` / `still` ＋ オーバーレイ cueId ＋ 尺 ＋ 遷移）
- **`start.ifMissed`**: `enter+t` の演出が **t に達する前に体験者が区間を出たら、離脱の瞬間に発火**する
  （既定 `fireOnExit`）。`skip` なら出さない。**歩くのが速い体験者に山場が出ないまま終わる事故**への対策で、
  スキーマなので後から足せない＝最初から入っている
- **画面が塞がっているだけなら捨てない — 区間内で待つ**（2026-07-27 改訂・ユーザー報告による）:
  演出が走っているあいだに別の演出の開始条件が来たら、**その区間に居る限り武装したまま待ち、画面が空いた時点で出る**。
  旧実装は即座に決着させて捨てていたため、**著作した演出が黙って消えた**（実害: 3 周目 B の離脱演出 8 秒が C の滞在を
  丸ごと覆い、C の進入演出 3 カットが一度も出なかった）。開始条件は満たした瞬間に `Ready` へラッチする
  — **ライントリガーは事象で猶予 0.6 秒しか残らない**ので、ラッチしないと「別の演出が走っていた」だけで永久に失われる。
  ライブ卓の介入中（`activeCue` / `cameraOverride`）は従来どおり即決着（人間の判断が優先）
- **捨てたら必ず報告する**: `TakeRunnerLogic.TakeDropped` → 実機は `[TakeRunner] 演出が出ないまま区間が終わった` 警告、
  シミュレータは `drop` イベント → 卓のログに赤字。**黙って消えるのが最悪**で、著作者には発見手段が無い
- **作れない組み合わせは卓が選ばせない**（2026-07-27）: 「離脱時」は 1 区間 1 本まで（2 本目は配列順で必ず負ける）／
  「このラインを通過したら」はその区間のカメラ担当のラインが 1 本も無ければ選べない。追加ボタン・開始規則セレクト・
  リボンのドラッグ吸着の 3 経路すべてで塞ぐ
- **必ず終わる**: `maxDurationSec`（既定 45s）の watchdog。素材の Prepare 失敗等で止まっても画面は必ず戻る
- **戻り先は再計算**: 演出終了時は「いま体験者が居るゾーン」（時計 `ZoneCommitted` の確定値）へ。
  開始時のカメラへは戻さない
- **v2 は読み取り互換**: [`TimelineMigration`](../../Assets/Scripts/Streaming/TimelineMigration.cs) が
  `cues[]` → `inherit` カット / `insert` → `live` カット へ決定的に変換する（端末キャッシュ・焼き込みが v2 のため）。
  **1 点だけ挙動が変わる**: 旧実装は delay 待ちの cue / insert が**別の区間で遅れて誤爆**していたが、
  v3 では離脱の瞬間に決着する（設計 §8.1）
- **カットの遷移（`transition`）は source で効き方が違う**（2026-07-26 に実装）:
  `live` は画面のライブ層が 1 枚しかないので **`cut`=瞬時 / `dip`・`fade`=黒経由**。
  素材カット（`clip` / `still` / `inherit`）は **`cut`=瞬時に差し替え / `dip`=黒の瞬間に差し替え /
  `fade`=素材のクロスフェード（overlay の fadeIn。従来どおり）**。
  以前は素材カットに遷移が一切効かず、卓は全カットに遷移欄を出していた
- **§6.4 の不正値は本当に飛ばす**（2026-07-26 に実装）: 範囲外カメラの `live` カット・素材が解決できない
  `clip`/`still` カットは**画面に触らずに次のカットへ送る**。全カットが飛んだ演出は画面を掴まない
  （旧: 範囲外は registry の clamp で無言に別カメラへ / 素材無しは尺のあいだ画面が固まる）
- **`untilClipEnd` の終端はトークンで判定する**（2026-07-26 修正）: `ScreenOverlayController.PlayCue` は
  マスク / 素材のロードを await するため、`Current == null` を素朴に見ると**発火直後の 1 フレームで
  「終わった」と誤判定**する（マスク付き cue が一瞬光って消える）。`PlayCue` の戻りトークンを
  `IsFinished(token)` に渡す。**静止画は終端イベントを持たない**ので §6.4 どおり
  `durSec>0 ? durSec : 4s` で畳む（旧実装は watchdog の 45s まで画面が固まっていた）
- **ライブ卓の介入は走行中の演出も畳む**（2026-07-26 修正）: `activeCue` / `cameraOverride` が立った瞬間に
  演出を折り畳む。抑止フラグだけ立てて走らせ続けると、演出のカット終了が卓の cue を消し、
  演出の復帰 dip が卓の固定カメラを外していた。**卓がカメラも握っている（override 中）ならカメラは返さない**
- **現地のランリセット（右グリップ 2 秒長押し）も演出を畳む**（2026-07-26 修正）。卓が無い現場での唯一の出口
- **⚠ Quest 実機未検証**（2026-07-25。EditMode 655/655・fixture 契約テスト・卓のブラウザ実操作は通過）。
  **卓のオーサリング面は演出・カットのリボン 1 つ**（旧グリッド `timeline.js` は削除）。v2 の show.json も
  読み込み時に変換して表示し、💾 保存で `timeline.schema=3` として書き出す。
  **v2 データはもう「退避路」ではない**（実機も v2 を演出へ変換して同じ経路で走らせる）。
  ここまでの実装ごと戻したいときは、この一本化コミットを `git revert` する

### 演出の連続と、継ぎ目の遷移 — 2026-07-29（同日 2 回目）

「離脱時の演出があるとき、次の区間の進入演出が出せない」「演出を連続で置けるようにしたい」
「進入カットの暗転相乗りは本当に最適か」の 3 点を、実行系・卓のミラー・遷移の 3 視点で調べ直して作り替えた。
計画は [2026-07-29_take-continuity.md](../plans/2026-07-29_take-continuity.md)。

#### 判明していた実害

- 実行側は**待つ**（区間内では Ready のまま保持）。ここは正しい。壊れていたのはその先。
- 次の区間の**滞在が前の演出より短いと、待っている間に区間を出て捨てられる**。
  しかも離脱の瞬間は画面が塞がっているので `ifMissed:"fireOnExit"` のフォールバックにも入れない
  （`FindExitCandidate` の結果ごと捨てられる）＝**フォールバックにも入れずに消える**。
- 卓は**一度も警告していなかった**。離脱時の演出は次の区間ブロックに重ならないので絵からも読めない。
- 演出を 2 本並べると実行側では順に出るが、**2 本目の 1 カット目は必ず復帰の暗転（70/100ms 固定）へ
  割り込むので、作者が選んだ遷移が毎回消える**。卓は演出を足すとき常に「進入 +0s」に置くので、
  描かれた位置と実際の発火時刻も食い違っていた。
- 相乗り 3 経路（`InsertExitRedirect` / `TakeHoldBegin` / `InsertBegin`）は**どれもカット側の遷移指定を捨てる**。
  しかも効くのは Down 相の 70ms だけで、Up 相の 100ms に来た演出は今も二重暗転していた。
  「瞬時」は走行タイミングで 3 通りの見え方になっていた。
- 黒の瞬間の予約（`_blackAction` / `_blackRedirect`）は単一スロットで、**上書きされたカットは
  一度も画面に出ないまま報告もされない**。

#### 待ちの寿命 `wait`（持ち越し）

演出に `wait: "segment"（既定）| "chain"` を足した。**既定は 1 ビットも変わらない**。

`chain` は「**自分を塞いでいた演出が終わるまで、区間を出ても待つ**」。持ち越すのは因果が
はっきりしている場合だけで、4 条件すべてが要る:

1. 著作者が `chain` と明示した
2. 離脱の瞬間に**実際に画面が塞がっていた**（歩くのが速かっただけでは持ち越さない — それは `ifMissed` の担当）
3. 開始条件は既に満たしていた（Ready / DeferredToExit）
4. `at:"exit"` ではない（離脱時の演出を別の区間で出すと文脈が最も壊れる）

上限は `MaxCarrySlots = 2` / `CarryMaxWaitSec = 60s`。持ち越しは**いま居る区間の演出に劣後する**
（先に出すと、体験者が居る場所の演出が毎回後ろへずれる）。
捨てる出口（上限・寿命・卓の介入・`AbortActive`）は**すべて報告する**（`CarryExpired` / `BlockerGone` を新設）。
あわせて `policy:"yield"` の打ち切りで消える武装も報告するようにした（旧実装はここだけ漏れていた）。

#### 連続の渡し（`chainNext`）

演出が終わるとき、次に出せるものが控えていれば**画面を返さない**（復帰の暗転を打たない）。
占有は保ったままなので画面の所有者が空白になる瞬間は無い。watchdog の強制終了では立てない。

⚠ 判定と発火のあいだ（1 フレーム）に「次」が消えると占有が降りないので、
`TakeRunner` に安全網を置いてある（次のフレームで始まらなければ画面を返す + 警告）。
**凍結が解けない事故をこの codebase は 4 回踏んでいる**ので、ここを外さないこと。

**⚠ その安全網は「必ず次のフレーム以降」で判定する**（2026-08-03 に実機で発覚し修正）。
`EndTake` は `Update` の中の `Apply(d)` から呼ばれて `_chainPending` を立てるので、
**同じフレームで見ると次の演出はまだ始まりようがない**。旧実装は同フレームで判定していたため、
`chainNext` は**成立した瞬間に必ず取り消され**、避けたかった復帰の暗転（70/100ms）が毎回入っていた。
実測（`logs/capture/20260803_150205`）: 「そのまま渡す」と「取り消し」が**同じ ms** に出て、
次の演出はその **9ms 後＝次フレーム**に始まっていた（2 回とも）。
＝ **連続の渡しは一度も働いていなかった**。`_chainPendingFrame`（立てたフレーム番号）で境界を跨がせる。
安全網そのものは残っている（次フレームで始まらなければ従来どおり画面を返す）ので、凍結の危険は増えない。

#### 継ぎ目の遷移は「いま画面を取る側」が所有する（`HandoffDip`）

相乗り 3 経路を [`CameraSwitchDirector.HandoffDip`](../../Assets/Scripts/Streaming/CameraSwitchDirector.cs) 1 本へ集約した。

| 進行中の相 | 動作 |
|---|---|
| Idle | 従来どおり `StartDip` |
| Down（暗くなっている途中） | **暗さを比率で保ったまま**、残りの尺と見た目を割り込む側の指定へ差し替える |
| Up（明るくなっている途中） | いまの暗さから Down へ**折り返す**。旧実装は 0 から張り直していたので「黒 → 明るくなりかけ → また黒」だった |
| 瞬時（0/0） | 進行中の尺をそのまま使い、差し替え先だけ変える。作者の「瞬時」は「自分から暗転を足さない」意図であって「既にある暗転を打ち切る」意図ではない |

理由：体験者にとっての単位は**継ぎ目**で、継ぎ目は 1 回だけ起き、その見え方は作者が決めるのが正しい。
旧実装は前半（1 回にする）だけを満たし、後半（作者が決める）を捨てていた。しかも捨てるかどうかが
実行時のフレームタイミングで決まるので、**同じ show.json が実行のたびに違う絵になる**（再現しないものは著作できない）。

あわせて `InsertReturn` が遷移引数を取れるようにし、黒の予約の上書きを `TransitionPreempted` で報告する。

#### 卓

- 演出インスペクタに「**前の演出が終わるまで待つ**」（`at:"exit"` では選べない）
- **ひとつ前の区間の離脱時演出がこの区間の滞在を食う**危険を著作の段階で言う（実測滞在があれば具体秒で）
- 演出を足すと 2 本目以降は「前の進入演出が終わったあたり」に置く（全部 +0s に積まない）
- 遷移ラベルが `glitch` を「暗転」と表示していたのを直した
- シミュレータに持ち越し数と、新しい drop 理由 2 種を出す

⚠ 実機未検証。EditMode 861/861・node 226/226。

### 体験の骨格（導入 → 3 周 → 終了）と「映像の乱れ」— 2026-07-29

企画書が学会論文版（`PR0490_1.pdf`）に差し替わり、体験構成が「**3 区間を 3 周・導入を含め 3 分以内・各周およそ 30 秒**」に確定した。
計画・要求対応表は [2026-07-29_proposal-v3-implementation.md](../plans/2026-07-29_proposal-v3-implementation.md)。

#### run（体験 1 回の骨格）

show.json トップレベルに `run` を新設。**キーが無くてもコード既定（3 周・導入あり）で終わる** — 終端が無いことが欠落だったので、既定でも走り切ったら終わる。

```jsonc
"run": { "totalLaps": 3, "introEnabled": true, "introMinSec": 20, "introAutoAdvance": true,
         "targetSec": 180, "hardLimitSec": 300, "endFadeSec": 1.5,
         "endGraceSec": 3, "endHoldMaxSec": 60 }
```

相は `Intro`（導入）→ `Run`（本編）→ `Finished`（終了）の 3 つ。判定は純ロジック
[`ShowRunLogic`](../../Assets/Scripts/Streaming/ShowRunLogic.cs)、配線は [`ShowRunDirector`](../../Assets/Scripts/Streaming/ShowRunDirector.cs)。

##### 帰りの A まで体験する（2026-08-02）

体験は「3 周 ＋ **元の位置に戻った A 区間**」で終わる。周回は進行ポインタ方式で `order[0]` へ戻った時に
上がるので、**`lap = totalLaps + 1` の `order[0]` は構造的に必ず踏む**。`totalLaps` は 3 のままで、
この 1 区間だけを追加で到達可能として扱う（`totalLaps=4` にすると帰りの B・C まで生き、
企画書の「3 周」とも表示上食い違う）。

到達可能な区間の式は **3 箇所が同じものを持つ**（[`ShowRunReach.IsSegmentReachable`](../../Assets/Scripts/Streaming/ShowRunLogic.cs) /
卓の [`run-model.js`](../../tools/web-compositor/run-model.js) / [`analyze-xp-log.py`](../../tools/analyze-xp-log.py) の
`is_segment_reachable`）。期待値を 3 者のテストにハードコードして突き合わせてある。

```
lap <= totalLaps || (lap == totalLaps + 1 && camera == order[0])
```

- **⚠ `endGraceSec` が無いと帰りの A の演出は始まる前に暗転する。** その区間に入ったフレームで
  周回が上がって終了条件が立つ一方、`at:"enter"` の演出はその同じ連鎖では**武装されるだけ**で、
  開始は次フレーム以降の `TakeRunner.Update`。スクリプト実行順は未定義なので、「演出が走っているか」
  だけを見ると走り出す前に終わる順序が実在する。**終了条件成立後は演出が走っていなくても
  `endGraceSec`（既定 3s）のあいだ必ず待つ。**
- 走行中の演出は `endHoldMaxSec`（既定 **60s**・旧 const 12s）まで見せ切る。帰りの A で流す録画は
  前の周の実滞在ぶん（20〜40 秒）になるので、12 秒では途中で切れた。
- **時間切れ（`hardLimitSec`）では待たない。** 待つと hardLimit の意味が壊れる。
- 実機は起動時に「踏まれない区間に置かれた演出」を名指しで 1 回警告する
  （`ShowControlClient.WarnUnreachableSegments`）。**実行は止めない**。
- StatusHud の 1 行目は帰りの区間だけ「もどり ・ 経過 2:05」と出す（「4周目/全3周」は壊れて見える）。

- **導入はランの第 1 相**であって「ラン開始前の待機」ではない。Intro→Run は
  `ShowControlClient.BeginMainRun()`（周回・演出・BGM・滞在ログだけ初期化）を打ち、
  **端末内録画の世代は切り替えない**。ここを録画のリセット系統に載せるとラン開始が複数系統に増え、
  遅れて届いた runEpoch が 1 周目の録画を消す（＝ 3 周目の素材が黙って消える）。
- 導入の自動終了は「`introMinSec` 経過 **かつ** スタート区間（`course.order[0]`）に居る **かつ
  導入演出が進行中でない**」の AND。`introAutoAdvance:false` にすればスタッフの明示操作だけで進む。
  - **⚠ 3 つめ（演出が進行中でない）は 2026-07-30 の実機テストで足した。** `introElapsed` は
    アプリ起動から数え始めるので、設営や待機で `introMinSec`（既定 20 秒）はとうに過ぎている。
    そこへ体験者が開始位置に立つと、**演出が始まったその瞬間に本編へ飛ぶ**。実測では
    Real → Degrade の 3.5 秒だけ流れ、核心（枠が閉じて中がカメラ映像へ変わる Structure /
    Frame / Swap）が一度も出なかった。起動から 20 秒以内に体験者が立つ運用は非現実的なので、
    **この穴があると導入演出はほぼ常に打ち切られる**。
  - 判定は `IntroDirector.Active && !Holding`（足踏み＝開始待ちは含めない — 含めると
    開始位置に立つまで永久に本編へ進めない）。スタッフの明示操作（`RequestAdvance`）は
    演出中でも従来どおり効く（人の判断を優先する）。
  - 演出が終われば `RestartIntroClock()` で計時が 0 に戻り、そこから `introMinSec` ぶんの
    慣らし歩行が始まる ＝ 「演出 → 慣らし歩行」の順序が設計どおりになる。
- **終了の判定は必ず次フレームの `Tick`**。周回の確定と離脱時演出の発火は同じ同期連鎖の中で起きるので、
  周回の変化を受けたその場で終了させると 3 周目最後の区間の離脱時演出が始まる前に終わる。
  走行中の演出があれば見せ切る（上限 `run.endHoldMaxSec` = 既定 60s）。
- **凍結ラッチは増やしていない。** 終了しても画面のカメラ切替は裏で回り続け、見えなくなるのは
  [`ShowEndingFader`](../../Assets/Scripts/Diagnostics/ShowEndingFader.cs) の黒のおかげ。
  凍結を足すと「解除されずに残る」事故を新しく作る（この codebase は 4 回踏んでいる）。
- 導入・終了の間は [`CueScheduler.SetShowGate(false)`](../../Assets/Scripts/Streaming/CueScheduler.cs) で
  区間進行を下流へ流さない。**演出の武装・端末内録画・区間 post / BGM・実測滞在がまとめて止まる単一の首**
  （下流それぞれに条件を配ると必ず片方を忘れる）。画面のカメラ切替は Director 側なので止まらない。
  - **⚠ ゲートの適用は必ずイベント配布の前**（2026-07-29 監査で修正）。`RunBegan` の処理は
    `BeginMainRun` → `LapCounter.ResetRun` → `SeedCurrentZone` → `CueScheduler.NotifyCameraEntered` を通る。
    旧実装は `ApplyGate()` がこの後にあったため、**本編 1 周目のスタート区間の進入が閉じたゲートに捨てられていた**。
    周回は進行ポインタ方式で、次に `course.order[0]` へ入る時は lap 2 なので (1, order[0]) は二度と来ない
    → **スタート区間だけ 1 周目が録画されず**、それを背景に使う 3 周目の録画カットが実機で無言で飛ぶ。
- 現地の右グリップ長押しは `ShowControlClient.BeginNewVisitorRunLocal()` を通す（卓の ▶ ラン開始と同じ号令元）。
  旧実装は個別に叩いており、実測滞在が前の体験者と混ざる非対称があった。
- 卓: `/command` の `advanceIntro` / `endRun`（いずれも世代カウンタ）、ラン状態パネルに相・経過・目安、
  ⚙ 欄で run を編集。**本番前チェックが「走り切る周数を超えた周の演出」を ❌ で出す**（実機だけが黙って落とすため）。
- ▶ 検証（シミュレータ）も `run.totalLaps` で止まる。止めないと「3 周で終わる設定なのに 4 周目が卓では動く」嘘になり、
  逆に 3 周目最後の離脱時演出が出るかを卓で確かめられない。

#### 導入演出 — 現実が映像になる（2026-07-30）

**企画書には無い新規追加。** 設計の正本は
[2026-07-30_intro-passthrough-to-screen.md](../plans/2026-07-30_intro-passthrough-to-screen.md)。
`Intro` 相の最初の **13.1 秒**で、**パススルー（現実）を 2D スクリーン（映像）へ格下げする**
（当初 33 秒で設計したが、体験の入口としてテンポが遅く飽きるので同日に詰めた）。

- **始まり方は 3 つ**（いずれも **HMD を被っていることが前提**・時間では進めない）:
  1. **通過ライン**（`run.intro.startLineId` → `layout.lines[].id`）を横切る ← **2026-07-30 に追加・現行の既定運用**
  2. **開始位置**（`layout.startSpot` の円）に 0.5 秒留まる（`startLineId` が空のとき）
  3. スタッフの明示操作（⏭）

  ⚠ **course 座標なので位置合わせが済んでいないと 1 も 2 も判定できない** — 未登録ならスタッフ操作へ
  縮退し、HMD 内に理由を出す（黙って「立っても始まらない」を起こさない）。
- **なぜ線を足したか**: 円は「その場所に立つ」＝状態、線は「横切る」＝事象。**歩いて入ってくる動きの
  まま始められる**ので、開始位置に立ち止まって待つ手順が要らなくなる。ラインは担当カメラを持つので、
  順路の入口に引けば「入ってきた人が最初に踏む線」になる。横断は 1 フレームの事象なので
  `IntroDirector` 側でラッチする（取り逃すと永久に始まらない）。
- **`UserPresentProvider`（HMD 装着）で門を閉じる**。机に置いた HMD が位置条件をたまたま満たして
  勝手に始まり、体験者が被った時には終わっている、という事故を防ぐ。Streaming asmdef は OVR を
  参照しない規約なので、`OvrControllerBridge` が `OVRPlugin.userPresent` を差し込む（未設定＝被っている扱い）。
  **被り直すとラッチも落ちる**（前の体験者が踏んだ線で次が始まらない）。
  ⚠ 自動走行（`ShowWalkDebugDriver`）は被らずに走らせるので、この provider を true で上書きする。
- `startSpot` を `regPoints` と**別の集合**にしたのは、位置合わせ点が「HMD で手が届く」
  「タッチ順に意味がある」という別の制約を持つため（兼用すると片方を動かしてもう片方が壊れる）。
- ⚠ **演出の合計秒は単純和ではない**（段 3 は段 2 の後半から始まるので重なった分は二度流れない）。
  `IntroTiming.TotalSec` と卓の `introStageSec` が**同じ式**を持ち、両方のテストに 13.1 を
  ハードコードして突き合わせてある。**片方だけ直すと沈黙して食い違う**

- **視点は 1 度も動かさない。** 動かすのは現実の側の身分。段は
  黒 → 現実 → 格下げ（色が抜け輪郭が浮く）→ 構造（部屋の線とカメラの印）→ 枠 → すり替え → 慣らし歩行
- **`ShowPhase` は増やさない。** `Intro` の内側のサブ状態（`IntroStage`）で、
  ゲート・終了判定・heartbeat・卓・シミュレータへの分岐を増やさない
- **判断は [`IntroLogic`](../../Assets/Scripts/Streaming/IntroLogic.cs)**（dt 注入・テスト 22 本）。
  各層への配布は `IntroWeights`（passthrough / degrade / edge / structure / frame / live / grain / glitch）1 本で、
  **見え方の判断を Director に散らさない**。段 2 と段 3 は時間的に重なるので、段の直列ではなく重みで表す
- **枠は本編のスクリーンそのもの**。開口の形と不透明度だけを動かす（`IntroVeil` が
  `ScreenAnchor` の Quad の見かけの形を逆算）。だから「枠を運ぶ」処理が無く、
  「入ったのに前方にスクリーンが浮いている」矛盾も発生しない
- **⚠ 枠の判定は「眼とスクリーンの 4 辺を通る平面」**（2026-08-01 に置き換え）。
  覆いの面は head-lock（視界を必ず覆い切るため）だが、**開口はスクリーンの実位置から毎フレーム解く**。
  - 旧実装は開口を覆いの面の **uv 中心に固定**していた。本編のスクリーンは頭から
    `heightOffset`（-0.28m / 2.0m ＝ 約 8°）下に置かれるので、**半画角 18.4° に対して 43% 縦にずれていた**
    （ユーザー報告「四角だけになるこの領域とスクリーンの位置が一致していない」）
  - 中心を動かすだけでも足りない。**スクリーンは水平（ヨーだけ追従）なので、頭を上下に振ると
    スクリーン面と覆い面が必ず傾く**。矩形として投影する方式は両面が平行なときしか正しくなく、
    20° の見下ろしで四隅が約 2.8° ずれる。平面 4 枚なら頭の向き・追従の遅れ・首の傾きに関係なく厳密で、
    除算も特異点も無い（シェーダは `dot` 4 回 + `max`）
  - **縁のぼけ幅は「閉じ切った枠」を基準にする**。いまの大きさに比例させると、開いているとき
    6° 以上に膨らんで**覆いの四隅がぼけ帯に入り、段 1〜3 で現実が四隅で翳る**
  - C# の [`IntroVeil.SignedDistance`](../../Assets/Scripts/Streaming/IntroVeil.cs) が
    **シェーダと同じ式**を持ち、`IntroVeilApertureTests` がスクリーンの実点を食わせて
    枠の縁との一致を固定する（Play 不要）。**片方だけ直すと沈黙して食い違う**
- **⚠ `run.intro` が無い show.json では JsonUtility が「全部 0」の実体を作る**。
  `ShowIntroDef.LooksUnset` で検出して既定へ落とす — これが無いと `enabled=false` に化けて
  **演出が黙って出なくなる**（焼き込み・端末キャッシュが古いときに踏む）
- **⚠ 段 3 の構造の線は既定で出さない**（2026-08-01・ユーザー判断「雰囲気ぶち壊しだから要らない」）。
  細い寒色の線が現実に重なると計測器に見え、「現実がそのまま格下げされていく」という段 2 → 段 4 の筋を切る。
  位置合わせの現地検証は登録リチュアル（Review フェーズ）のワイヤー表示が担うので、導入から消しても検証手段は残る。
  - 既定 false は **4 者で揃える**（`ShowIntroDef` / 卓の `INTRO_DEFAULT` / `capture-server.py` の
    `_default_show` / `IntroStructureWire` の SerializeField）。卓は `src.x === true` で読む
    （`!== false` だと未指定が true に化け、欠落キーを false で埋める JsonUtility と食い違う）
  - **⚠ それまで `run.intro.showRoomWire` / `showCameraMarks` は実行体へ 1 度も届いていなかった**。
    `IntroDirector` は 2 フラグの **OR** で `Apply` を呼ぶかだけを決めており、個別の値は
    `IntroStructureWire` の SerializeField（どちらも true）のままだった ＝
    **卓で「壁の線を出さない」にしても壁の線が出続けた**。`SetSources` で渡すようにした
  - 段 3 自体は残す（線が出なくても「輪郭だけの世界」が保たれる溜めとして働く。実尺 1.1 秒）
  - 本番前チェックの幾何要求（床の寸法・部屋の壁）と「較正済みのカメラが 0 台」の警告は、
    **線を出す設定のときだけ**言う（起きようのない不備を直させない）
- **⚠ 段 3 の壁の線を出す設定にしたときは `layout.room` からしか出ない**（2026-07-30 追記）。
  `IntroStructureWireLogic` は壁と箱を `AppendRoom`（= `layout.room` だけ）から起こし、床の外周を
  `TryFloorExtents`（`layout.floor` → `room.floorW/D`）から起こす。**旧 `layout.wall` は 1 本も読まない**
  （あれは HMD 位置合わせリチュアルのワイヤー専用）。卓のフロアマップ **🧱 部屋**で壁を引いていないと、
  段 3 は床の四角とカメラの印だけになり、**§8-6 の「線が実物に重なるか＝位置合わせの現地検証」が成立しない**。
  本番前チェック（`intro-model.js` の `introWireGeometry`）もこの 2 キーを見る — 以前は `layout.wall` の
  既定値判定だったので、**壁を著作しても ❌ が消えず、指示された直し方（床の寸法入力）でも消えなかった**
- **⚠ 尺の既定は 3 箇所に現れる**（`ShowIntroDef` のフィールド初期値 / `IntroTiming.Default` / 卓の
  `INTRO_DEFAULT`）。13 秒へ詰めた時に `ShowIntroDef` だけ旧値（33 秒）が残っていた。
  `Sanitized()` が 0 を既定で埋めるので実害は出ていなかったが、**フィールドを直接読む経路が増えた瞬間に食い違う**
- **「右手を上げる」は導入と 3 周目だけの専用合図**。導入では画面の中の自分も上げるが、3 周目の背景は
  1 周目の録画なので上がらない → 言葉なしに反転が成立する。**手を上げたことは検出しない**
  （検出を入れると「上げたと判定されなかった」失敗モードが増えるだけ）。壁をたどる右手とは別動作
- **導入で上げた手が 1 周目の録画に混入することは無い**。導入は `CueScheduler.SetShowGate(false)` の
  内側で、端末内録画はゲートで止まっている。ゲートを触るときはこの保証を壊していないか確認する
- パススルーの API の制約（切ると数百 ms 黒 / 走査線は掛けられない / ガーディアンは消せない）は
  [meta-xr.md](meta-xr.md) の「パススルー」節が正本
- **⚠ Quest 実機未検証**（2026-07-30。EditMode 883/883・node 299/299。パススルーの見た目・枠の閉じ方・
  段 5 で「自分だと分かるか」は実機でしか判定できない）

#### 映像の乱れ（グリッチ）

企画書 2.3「ノイズやグリッチ等の乱れを一時的に重畳でき、差し替えの継ぎ目の隠蔽や、体験者の注意・移動の誘導に用いる」。

- uniform は **`_Glitch` / `_GlitchSeed`**。障害表示の `_SignalLost` とは別系統で、書き手は
  [`GlitchFx`](../../Assets/Scripts/Streaming/GlitchFx.cs) 1 つだけ（SignalLostFx と同じ流儀）。
  シェーダでは 乱れ → 障害表示 の順に掛かるので、**実際に信号が切れたら障害表示が勝つ**。
- 乱れの中身は 2 段: ①帯ごとの水平シフト + 垂直の同期ずれを**全層のサンプル前の uv** に掛ける
  （ライブ・素材・マスク・CG が一緒にずれる = 差し替えの継ぎ目も一緒に乱れる）②post の後に砂嵐混合と明滅。
- 出し方は 4 つ:
  - カットの遷移 `transition:"glitch"`（既定 220ms）。dip と同じ状態機械を通り、黒の代わりに乱れで覆って
    その最中に差し替える。live / 素材の両経路に一様に効く
  - カット頭の単発 `steps[].glitch` / `glitchSec`（注意・移動の誘導）
  - ゾーン切替への重畳 `control.switchGlitch`
  - 卓の手動 `⚡ 乱れ` → `/command {type:"glitch"}` → `control.glitchEpoch`（世代カウンタ）
- 時間包絡は純ロジック [`GlitchEnvelopeLogic`](../../Assets/Scripts/Streaming/GlitchEnvelopeLogic.cs)。
  持続と単発は **max で合成**（加算だと 1 を超えて飽和し遷移の形が潰れる）。

#### 撮像の質と残像（`feel` / `hold` / `burn` / `aura`）— 2026-08-06

**「加工が足りない＝怖くない」の診断は、強度ではなく『言い訳が破れていない』こと**だった。
post 12 項目もグリッチも全域に一様で、一様な乱れは全部「機材のせい」で説明が付く。だから安全に見え、
20 秒で慣れて「そういう画」になる。ここで足したのは、その説明が効かなくなる 4 つ。

**別系統である理由**: post は shader / 卓の FS_POST / common.js / pipeline.js の 4 箇所を手作業で同期していて
機械テストが無い。そこへ**時間軸**を持ち込むと沈黙した食い違いが必ず出る。だから
`_NoiseDark` / `_NoiseFixed` / `_ExposureBias` / `_VignetteBias` / `_Echo` / `_ActorFocus` は
`_Glitch` / `_SignalLost` と同じ「別 uniform・単一 writer」の枠に置いた。

| 何 | どこ | 効き目 |
|---|---|---|
| **暗部ノイズ**（`feel.noiseDark`） | 全域・常時 | 暗いところほど粒が乗る。**暗がりが物を隠せる状態**を作る。固定視点では映っていない領域の存在を確かめられないので、物を足すより観測できる面積を削るほうが安い |
| **固定パターンノイズ**（`feel.noiseFixed`） | 全域・常時 | 時間で動かない粒＝画面に貼り付いた汚れ。静止した基準ができ、**その中で動くものだけが浮く** |
| **自動露出の追従遅れ**（`feel.agc` ほか） | 全域・常時 | 映像の明るさへ**遅れて**追いつき、減衰 0.55 で**行き過ぎて戻る**。映っている範囲では何も変わっていないのに明るさが動くと「画面の外で何かが起きた」と読まれる |
| **凍らせた 1 枚**（`steps[].hold` / `burn`） | カット単位 | hold = 画が止まる（ライブも素材も一緒に）／ burn = 少し前の姿が薄く残る。**動いていない画素は同じ値なので、動いたものの跡だけが残る** |
| **人形に付き従う劣化**（`steps[].aura`） | カット単位 | 人形のまわりだけ色が抜け・沈み・粒が増える。**人形が動くと荒れも動く**ので機材の不調では説明が付かない。この作品に唯一無かった「対象に紐づく非一様性」 |

- 実装: [`CameraFeelLogic`](../../Assets/Scripts/Streaming/CameraFeelLogic.cs)（純ロジック・テスト 11 本）+
  [`CameraFeelFx`](../../Assets/Scripts/Streaming/CameraFeelFx.cs)（writer）。`aura` の中心と半径は
  `ShowCgLayer.WriteActorFocus` が人形の投影から毎フレーム供給する
- **`feel` はキーが無くても既定値で効く**（設定を書かないと怖くならない、では現場で使われない）。
  既定は **C# `ShowFeelDef` / 卓 `FEEL_DEFAULT` / `capture-server.py` の `_default_show` の 3 者**で揃える
- **ホールドは必ず秒で明ける**（`CameraFeelLogic` が保証）。加えて演出の中止・体験の終了で `ResetAll`。
  「凍結が解けない」はこの codebase が 4 回踏んだ事故の型なので二重に閉じてある
- **hold 中は差し替え素材の時計も止める**（`ScreenOverlayController.SetFrozen`）。ライブだけ止めて
  録画が動くと「装置が固まった」に見えない。解除では止まっていた分だけ開始時刻をずらす（続きから）
- **⚠ 卓のプレビューには出ない**（実機だけが持つ「装置の挙動」）。UI にその旨を明記してある。
  一様に掛かるだけでマスクや立ち位置の判断を狂わせないので、著作は数値だけで足りる
- **やってはいけないこと**（設計批評の結論・実装しなかったもの）:
  - **周回で単調に劣化させない**。post は合成の**後**に掛かるので、録画も 3 周目の加工を浴びる
    ＝ 一番見せたい「1 周目の自分」が一番見えなくなる。破るのは 3 周目に一度だけ
  - **datamosh 風のフレーム履歴を持たない**。状態を持つと卓と実機が必ず食い違う（この codebase が
    何度も踏んだ型）。`_Echo` が「指定した瞬間の 1 枚」なのはそのため — 決定的で、同じ show.json は同じ絵になる
  - **選択的彩度（特定色相だけ残す）を入れない**。装置は色を選べないので、色相キーは作者の指さしになる
  - **過去と現在で加工を変えない**（overlay 専用の post を作らない）。区別できた時点で 3 周目は種明かしになる
  - **1 フレームの顔・突然の暗転を入れない**。この作品の恐怖は「装置は正直に映している」という信頼の上に乗る
- 現行 show.json の著作（2026-08-06）: 2 周目の差し替え 3 本に `burn 0.4/2.0s`（一瞬見えたものが消えた後に
  残像＝見間違いではなかったと分かる）、3 周目と帰りの A の録画に `aura 0.55` / 無人プレートに `aura 0.45`

#### 進入カットも切替の黒に相乗りする

企画書 2.3「差し替えは体験者の歩行や提示映像の切替のタイミングに同期させ、差し替えの知覚的検出を抑える」。
`CameraSwitchDirector.InsertBegin` に「進行中の dip が Down 相なら相乗り」を追加した
（`TakeHoldBegin` / `InsertExitRedirect` には元からあり、ここだけ欠けていて **at=enter の演出が暗転を 2 回出していた**）。

#### post が 12 項目になった（色収差・低解像度化・走査線の本数）

`aberration` / `pixelate` / `scanlineCount` を追加。既定 0（`scanlineCount` の 0 は「未指定 → 240」）で旧データと同じ絵。

- 色収差と低解像度化は**色ではなくサンプル位置**を動かすので合成の前段に入る。実機・卓とも同じ式。
- 走査線の本数を持たせたのは、実機が material の 240 固定・卓が canvas の縦画素（360〜480）で、
  **同じ値でも縞のピッチが 1.5〜2 倍食い違っていた**ため。グレインの座標も等方 480 + `fract(時間)` へ揃えた。
- ⚠ post に項を足すときは **ScreenComposite.shader / shaders.js の FS_POST / common.js の FX 表 / pipeline.js の uniform 受け渡し**
  の 4 箇所を同時に直す（機械テストが無く、片方だけだと沈黙して食い違う）。

#### 色統計マッチングが実機に届くようになった

企画書 2.3「差し替え素材の全体には色統計マッチングを施し、実写映像と継ぎ目なく合成する」。

卓が Reinhard per-channel を解いて **RGB の gain/offset（6 float）へ落とし cue へ焼く**
（[`color-match.js`](../../tools/web-compositor/color-match.js)・node テストあり）。実機は
`_OverlayGain` / `_OverlayOffset` を掛けるだけ。**向きは「素材を実写へ寄せる」**（実機で動かせるのは素材の側だけ）。
焼くかどうかは卓の「境界ブレンド」の色統計トグルに従う。
多重帯域ブレンディングは Quest で重いので実装していない（企画書は「フェザリング**や**多重帯域」と選択で書いている）。

**焼く経路は [`bakeColorMatch`](../../tools/web-compositor/color-match.js) 1 本**（2026-07-30 に一本化）。
cue を作る面はすべてこれを通す — 素材工房は通っておらず、**卓は色を合わせた絵を出すのに実機は素の色**
という食い違いが出ていた。

- **⚠ マスクがあるなら「差し替えない所」だけで解く**（2026-08-05）。差し替える所には素材にしか
  居ないもの（人形・手形）が写っていて、実写側には居ない。そこを平均に混ぜると
  「主題が背景と違う」ぶんまで補正しようとして、**もともと合っていた背景の方がずれる**。
  実測: 全画素で解いたら背景の一致が 1.5 → 20.4 に悪化した（人形が画面の 17% を占める素材）。
  マスクの外だけで解けば 2.5 に収まる。`bakeColorMatch(live, src, cfg, maskEl)` の第 4 引数
- マスクは**枠空間（16:9）**、素材は**ソース空間（ふつう 4:3）**なので、統計を絞るときは
  contain-fit を逆にたどってマスクの中央 75% を読む（[`maskRectForSource`](../../tools/web-compositor/color-match.js)・node テストあり）
- **棚卸しは [`mask-audit.py`](../../tools/web-compositor/mask-audit.py)**。いま演出が指している素材の
  マスク・合成結果・色ずれを 1 枚の HTML に出す。素材を差し替えたら通す

- **動画は尺全体から複数点サンプルして畳む**（`statsFromMedia` → `averageStats`）。実機が持てる補正は
  1 組だけなので、保存した瞬間の 1 フレームで解くと暗→明の素材で後半が破綻する。
  プールした sd は**群平均のばらつきを含める**（単純平均だと明暗の振れ幅がまるごと落ち、gain が倍以上ずれる。
  `color-match.test.mjs` が数値で固定）
- **ラプラシアン（多重帯域）は卓プレビュー専用なので既定 OFF**（`common.js` の `blendCfg`）。
  ON にすると卓だけ継ぎ目が消えて見えるので、チェックボックスに「卓だけ」の印を出す。
  色統計は焼かれて実機に届くので既定 ON のまま。**この 2 つを同じ見た目で並べない**

#### 遅延の「管理」で測っているもの / 測っていないもの

企画書 2.3「映像伝送の視覚遅延は 100 ms 程度以内を目標として管理する」。

**絶対の end-to-end は測っていない。** `X-Capture-Ns` は配信端末の monotonic、Unity 側は自前の Stopwatch で
基準が違い、引き算すると端末間の時計のずれがそのまま遅延として出る。推定の定数を足して「100ms でした」と言うより、
測れる 3 つを分けて出す方が正しい（[`LatencyEstimatorLogic`](../../Assets/Scripts/Streaming/LatencyEstimatorLogic.cs)）:

| 出しているもの | 何 |
|---|---|
| `ArrivalJitterMs` | 到着の揺らぎ。`受信 − 撮影` の 10 秒窓の**最小値からの超過**。時計のずれは最小値の側に吸われるので差分だけは正しい |
| `DecodeMs` | 受信スレッドの払い出し → メインスレッドの展開完了 |
| `PresentMs` | 表示レートから 1.5 フレーム |
| `SourceAgeMs` | `/health.latestFrameAgeMs`（配信側のカメラ stall） |

絶対値が要るなら**配信アプリ側に「`X-Capture-Ns` と同じ基準の現在時刻」を返す口**（例 `/clock`）が要る。
それが入れば `ObserveArrival` のオフセット推定に差し替えるだけで済む形にしてある。

あわせて `/health` の熱フィールド（`thermalStatus` / `throttleStage` / `thermalHeadroom` / `batteryTempC`）を
DTO へ追加し、**熱で降格している間は lag 判定を抑止**する。旧実装は熱で落ちた fps を経路の詰まりと誤認して
5 秒ごとに MJPEG を張り直し、黒 / 砂嵐を出しながら事態を悪化させていた。

表示レートは [`DisplayRateRequester`](../../Assets/Scripts/OvrBridge/DisplayRateRequester.cs) が実行時に 90Hz を要求する
（共有 ProjectSettings は触らない）。**遅延対策の主役ではない** — 縮むのは提示ぶん約 4ms で、快適性の項目。

#### CG 人形は較正だけでも出るようになった

実機は `cameras[].pose` が無いと人形を出さず、卓は「較正があれば出る」と判定して輪郭を描いていた
（卓で ✅ に見えて実機で出ない）。較正の解から概算姿勢を作って出すようにして両者を揃えた。

**⚠ ここまで Quest 実機未検証**（2026-07-29。EditMode 849/849・node 219/219・卓はブラウザで DOM と GLSL の実測確認）。

### 開始規則「このラインを通過したら」（通過ライン）— 2026-07-27

設計の正本は [2026-07-27_position-trigger.md](../plans/2026-07-27_position-trigger.md)。v3 への**追加のみ**（既存 show.json はそのまま読める）。

- **演出（Take）の開始規則が 3 つになった**: `at:"enter"`（進入 +t 秒）/ `at:"exit"`（離脱時）/
  **`at:"line"`（床の線分を横切ったら）**。演出は今までどおり**区間 (lap, camera) に属する** — 変わるのは
  due になる条件だけなので、`once` / `ifMissed` / 「同時 1 本」/ 離脱時の決着 / ランリセットは**すべてそのまま効く**
  - 当初案は円（エリア進入・`layout.spots` / `at:"spot"`）だったが、同日にユーザー指示で
    **ライン通過へ変更**した（エリア＝状態 / ライン＝事象で、開始規則と型が合うのは後者）。円版は 1 コミットで消えたので互換読みは無い
- **線は `layout.lines[]`**（`{id, camera, x1, z1, x2, z2, dir, label}`・course 空間）。卓のフロアマップ
  **📏 通過ライン**モードでドラッグして引く。`layout` の一部なので端末キャッシュ・APK 焼き込み・shallow 置換保存に自動で乗る
  （`regPoints` と同じ立ち位置。サーバ側の変更は不要）
- **⚠ ラインは担当カメラに紐づく（二重の縛り）**: ①演出の武装は区間限定（既存機構）＋
  ②`lines[].camera` と演出の区間カメラが**一致しないと due にしない**（`TakeRunnerLogic.IsDue`）。
  これで「手違いで別の領域のラインを踏んでも何も起こらない」が構造的に成立する。卓も担当が一致する線しか選択肢に出さない
- **take 側**: `lineId`（空 / 未定義 id / 担当違いは**発火しない** + ログ 1 回）。`offsetSec` は at=line では無視
  - **⚠ 実装規約（2026-07-27 監査で両側修正）**: 使えないラインでも **`onLine` を下ろしてはいけない**。
    下ろすと時刻トリガー扱いになり `offsetSec`(=0) で**区間に入った瞬間に発火**する（契約の真逆）。
    「決して発火しない」は `lineIndex = -1`（`TakeRunnerLogic.IsDue` が常に false）で表す。武装はされるので
    `ifMissed=fireOnExit` は従来どおり効く。旧実装は実機＝空 lineId が進入即発火、卓＝3 ケースすべて進入即発火で、
    卓は「発火しません」と警告した当の演出を再生していた（`TakeWiringTests.LineTake_WithoutLineId_*` で固定）
- **通過方向** `dir`: `both`（既定）/ `fwd`（卓の矢印の向きだけ）/ `back`（逆だけ）
- **判定** = [`LineCrossLogic`](../../Assets/Scripts/Streaming/LineCrossLogic.cs)（純ロジック）:
  **毎フレームの移動線分 × ライン線分の交差**（端の外を回り込んだら横切っていない）/ 横断直後は
  線から 0.06m 離れるまで再検出しない / dt 不連続（>0.5s）・1 フレーム 1m 超の移動は数えない（HMD 着脱・recenter）/
  **高さは見ない** / 0.05m 未満の線は無効。位置は `ShowControlClient.HeadCourseXZProvider`（登録済み course 座標）
- **横断には 0.6s の猶予（`CrossLatchSec`）**: 武装はゾーン確定（dwell 既定 0.5s）で起きるので、
  ゾーンの入口すぐに引いた線は「入った瞬間に横切る → 0.5s 後に武装」で**永久に沈黙する**。
  横断時刻を覚えて猶予内なら due と認める（担当カメラ照合は猶予中も効く）
- **発火は 1 区間滞在につき最大 1 回**。`once=true` ならラン内 1 回
- **`ifMissed` はそのまま効く**（この区間に居るあいだに通らなかった時）。**卓の既定は `skip`**（時刻トリガーの `fireOnExit` と違う）
- **▶ 検証（シミュレータ）でも発火する**（JS ミラー `LineCross`）。一致は golden
  [`scenario_line.trace.json`](../../Assets/Tests/Fixtures/scenario_line.trace.json)（C# が生成・JS が照合）で機械固定
  - **⚠ シミュレータは起動時にスタート区間をシードする**（トレース先頭の `seg`）。実機の
    [`LapCounter.SeedCurrentZone`](../../Assets/Scripts/Tracking/LapCounter.cs) と対。これが無いと
    **1 周目スタート領域の演出が永久に武装されない**（2026-07-27 実害: 1 周目の通過ラインが卓で沈黙。実機では出る）
  - **⚠ ドラッグ位置は tick へ等速で分配する**（`show-sim.js` の `advance`）。1 フレーム分の tick に同じ位置を
    配ると「テレポートして立ち止まる」動きになり、1 tick の移動が 1m（`LINE_MAX_STEP_M`）を超えた瞬間に
    **横断が数えられない**（×4 / ×16 の早送りでは普通のドラッグで超える）
- **⚠ Quest 実機未検証**（2026-07-27。EditMode 730/730・node 81/81・卓はブラウザ実操作で確認）

### 演出の素材と層を増やす（録画 / スロット / CG / 演出専用カメラ）— 2026-07-26

設計の正本は [2026-07-26_show-sources-and-cg-layer.md](../plans/2026-07-26_show-sources-and-cg-layer.md)。v3 への**追加のみ**で、既存 show.json はそのまま読める。

- **`source:"rec"`（端末内録画）**: `camera` + `recLap` で「この体験の N 周目・カメラ X の区間映像」を指す。
  録画の単位は区間（`(lap,camera)`）で、駆動は**ショーの時計**（画面が何を映していても録る）。
  保存は `Application.temporaryCachePath/rec/<runEpoch>/L<lap>C<cam>.mjr` の**生 JPEG 列**
  （mp4 化しない＝再エンコード劣化ゼロ・デコード経路はライブと同一）。**ラン開始で端末の録画を全部消す**
  （現ランの epoch だけ残すと、現地リセットで進んだ番号に卓の runEpoch が後から追いつき、
  前の体験者の映像が「このランの録画」として再生される）。録れていなければそのカットを飛ばす（§6.4）
  - **⚠ 「録れた」と「画に出た」は別**（2026-08-02 に観測を分けた）。ファイルを開けただけのカットは
    絵が 1 枚も出ないまま尺を消費し、ログ上は演出が走ったように見える。**暗い現場では目視で区別できない。**
    → `[XP] ev=rec v=stop … frames=`（書けた枚数。バイト数はヘッダだけの空ファイルでも 0 にならない）と
    `ev=recplay v=open/close … presented= failed= luma=`（**実際にテクスチャへ載せた枚数**と輝度）を出す。
    `analyze-xp-log.py` の「## 端末内録画 — 録れたか / 再生されたか」が両方を FAIL 判定する
- **設定は show.json トップレベル `record`（既定 無効）** — 卓の **⏺ 端末内録画パネル**（タイムラインの下）で編集する。
  `laps` は**録る周の配列**で、既定 `[1]`。「1周目 B・1周目 C・2周目 A を録って 3 周目で流す」なら `[1,2]` が要る
  - **録画係（[`SegmentRecorder`](../../Assets/Scripts/Streaming/Recording/SegmentRecorder.cs)）はシーンに置かれていない。**
    `ShowControlClient` が `record.enabled` を見て自動生成する（`EnsureRecorder`）。生成点は
    **起動時（焼き込み / 端末キャッシュ）と ライブ受信の両方**。2026-07-29 まではライブ受信の側にしか
    無く、**卓が居ない現地では `record.enabled` を焼き込んでも 1 フレームも録れなかった**
  - **⚠⚠ 残すのは区間の「末尾 `tailSec` 秒」だけ**（2026-08-06 に「頭から `maxSegmentSec` 秒」を置き換えた）。
    再生は 3 周目にその区間へ**入った瞬間**に始まるので、頭から録ると**映像の中の過去の自分も入口に居て、
    体験者の現在位置に立つ CG 人形と重なる**。末尾＝区間を出る直前なら、過去の自分は出口側に居て位置が分かれる。
    しかも「さっき自分が出て行った直後の残り香」という筋になり、その後の無人プレートへ自然に繋がる。
    - 実装は [`SegmentRecordWriter`](../../Assets/Scripts/Streaming/Recording/SegmentRecordWriter.cs) のメモリリング。
      区間中ずっと積み、時間と容量の 2 条件で古い側を落とす。書き出しは区間の切れ目に 1 回だけ
      （背景スレッド。3s × 15fps × 60KB ≒ 2.7MB なので実測数十 ms）
    - **pts は先頭 0 起点へ振り直す**。振り直さないと `RecordedFramePlayer` が頭を空回りして、
      再生開始から絵が出るまで無音の間ができる
    - **1 周目 A も使える**ようになった（導入直後ではなく「A を出る直前」が残るため）。
      現行 show.json は `laps:[1,2]` で、3 周目の A/B/C ← 1 周目の末尾、帰りの A ← 2 周目 A の末尾
    - **容量が滞在時間に比例しなくなった**のが副次的だが重要な効き目。旧方式は区間 60s × 4 = 216MB で
      `maxTotalMB` 200 を超え、**ゆっくり歩く体験者では後半の区間が録れなかった**
    - 旧キー `maxSegmentSec` は読むだけで**使わない**（古い show.json / 端末キャッシュのため）。卓の UI からは消した
  - **`maxTotalMB` は「ラン全体」の上限**（区間ごとに残量を配る）。旧実装は各区間へ満額を渡していたので、
    実効は区間数倍だった。上限に達したらリングの古い側から落として入る分だけ残す（`Capped=true`）
  - **卓が対応を照合する**: 「録画」カットが指す (周, カメラ) を録る設定になっていなければ、
    ⏺ パネル・本番前チェック・カットの警告・▶ 検証がすべて ❌ と理由を出す（判定は
    [`record-model.js`](../../tools/web-compositor/record-model.js) が単一の正・node テストあり）。
    これが無いと**実機だけが黙ってカットを飛ばす**（気づけるのは実機のログだけ）
  - **尺は `tailSec` で確定する**（末尾方式にして著作時に決まるようになった）。卓は `≈` を付けずに
    `長さ 3s（この区間を出る直前の末尾）` と出す。滞在が `tailSec` より短ければ録れているのはその分だけなので、
    実測滞在があれば短い方を採る。**尺を別に決めたいなら秒指定**（頭から N 秒。rec カットに trim は効かない）
- **`slot://<name>`（素材スロット）**: ラン中に卓が実体を束縛する素材（入口で撮って生成した人形動画など）。
  束縛は `POST /command {type:"bindSlot", name, url}` → `control.slots`。**timeline は触らない**のが要点で、
  timeline を保存し直すと発火済み（`once`）の演出が再武装される。未束縛のスロットを指すカットは飛ばす
- **`cg` / `cgMode`（CG 人形）**: `ScreenComposite` の 3 層目 `_CgTex` に、実カメラ姿勢
  （`cameras[].pose`・course 空間）で構えた**双子の仮想カメラ**が CG レイヤだけを描く。
  合成は**ポスト FX の前**（映像と同じ露出・走査線・グレインを浴びないと必ず浮く）。
  `follow` = 体験者の HMD XZ に立つ / `fixed` = 著作位置。**姿勢が未著作のカメラでは出さない**
- **`cameras[].role`**: `"fx"` は演出専用カメラ（＝カメラ D）。ゾーンに割り当てず、スタッフの A ボタン巡回にも出さない

### 無人プレート（`source:"plate"`）と「次にカメラが切り替わるまで」（`durKind:"untilZoneChange"`）— 2026-08-05

3 周目の「録画を流す → 流し終わったら**無人の部屋だけ**が映り、そこに体験者の分身（CG 人形）が立っている」
を作るための 2 つ。どちらも**追加のみ**で、既存 show.json はそのまま読める。

- **`source:"plate"`** = そのカメラで撮った**無人の実写プレート**（`camera` + `cueId` / `assetUrl`）。
  絵の出し方は `still` と同じだが、**CG 人形を許す点が違う**。判定は
  [`TakeSchema.MatchesCameraPerspective`](../../Assets/Scripts/Streaming/ShowTakeSchema.cs)（`rec` と `plate` だけ true）。
  一般の素材（`clip` / `still`）は「いつどこで撮ったか分からない画」なので、どのカメラの較正を当てても
  人形のパースが合わない。プレートは `rec` と同じく step.camera で撮った画なので較正がそのまま効く
  - **`camera` を必ず指す**。指さないと人形の構図が決まらない（卓の本番前チェックが ❌ で止める）
  - 画面のカメラは動かさない（`rec` と同じ非 live 経路 = `TakeHoldBegin`）。プレートが枠を覆う
- **`durKind:"untilZoneChange"`** = 体験者が**次の区間へ移るまで**そのカットを出し続ける。
  畳むのは **ショーの時計（`ZoneCommitted`）だけ**で、素材の終端では畳まない
  （[`TakeRunnerLogic.NotifyZoneChanged`](../../Assets/Scripts/Streaming/TakeRunnerLogic.cs)）。
  スタッフの手動送り・Web の cameraOverride・インサートの画面切替は時計を動かさないので終わらない
  - **尺の負値は「待つ相手」を表す**: `WaitClipEnd = -1`（素材の終端）/ `WaitZoneChange = -2`（次の区間）。
    `NotifyCurrentStepFinished` と `NotifyZoneChanged` は**それぞれ自分の待ち相手のカットしか畳まない**
  - 体験者が動かなければ終わらないので、上限は watchdog（`maxDurationSec` 既定 45s）が保証する。
    録画（前の周の滞在ぶん）＋ プレート（この区間に居るあいだ）は 45s を超えうるので、
    **3 周目の演出には `maxDurationSec` を明示する**（現行 show.json は 120）
  - **卓は秒へ推定しない**。ゾーン確定は卓にもあるので実機と同じ所で畳める
    （`show-scenario.resolveStepDuration` → `WAIT_ZONE_CHANGE` / `scenario-engine.notifyZoneChanged`）。
    リボンの幅は仮置きで、必ず `≈` が付く

**3 周目の演出は `policy:"yield"` + `wait:"segment"` + `ifMissed:"skip"`**（2026-08-05 に組み直し）。
録画の長さは「前の周にその区間へ居た時間」なので、3 周目に速く歩くと録画が終わらないまま区間を出る。
旧設定（`hold` + `chain`）だとその演出が持ち越され、**3 周目 B の演出が C の区間で 12.7 秒遅れて出ていた**
（実測）。yield なら区間を出た瞬間に畳まれ、次の区間の演出が定刻で始まる。

⚠ **その代わり「録画が終わらなければ無人プレートは出ない」**。プレートが出るのは、体験者が
3 周目にその区間へ 1 周目と同じかそれ以上とどまった時だけ。これは仕様（録画の再生が終わってから
無人へ変わる、という筋がそのまま出ている）。

**⚠ Quest 実機未検証**（2026-08-05。EditMode 1008/1008・node 386/386・卓のブラウザ実操作で
「録画 → 無人の部屋を全面」まで確認）。

### 演出専用カメラ D と CG 人形の実配線（2026-07-27）

計画 [2026-07-27_cg-actor-hand-tracking.md](../plans/2026-07-27_cg-actor-hand-tracking.md)（設計・不変条件の正本）。

- **カメラ D は 4 本目の普通のカメラ**。`Assets/Settings/Cameras/Phone04.asset`（cameraId=D）+ `StreamingLogic.prefab` の
  `sources[]` 4 本目。show.json の `cameras[3]` に `role:"fx"` を入れると、ゾーン塗りパレット（卓）・スタッフ巡回・
  ゾーン自動切替から外れ、**演出のカットからだけ映る**。配信アプリは v0.8.0 で cameraId に **D** を追加済み。
  **常時受信する**（演出のときだけ繋ぐ、はしない — 張り直しで 1〜2 秒の黒が出るため）
- **CG 人形**（`steps[].cg` / `cgMode`）は [`ShowCgLayer`](../../Assets/Scripts/Streaming/Cg/ShowCgLayer.cs) が
  実カメラ姿勢（`cameras[].pose`）の双子の仮想カメラで **ShowCg レイヤだけ**を RT へ描き、`_CgTex`（ポスト FX の前）で合成する。
  腕は体験者のハンドトラッキング（[`ShowActorRig`](../../Assets/Scripts/Streaming/Cg/ShowActorRig.cs) +
  [`TwoBoneIk`](../../Assets/Scripts/Streaming/Cg/TwoBoneIk.cs) + [`ActorArmLogic`](../../Assets/Scripts/Streaming/Cg/ActorArmLogic.cs)）。
  入力は Assembly-CSharp の [`OvrHandTrackingBridge`](../../Assets/Scripts/OvrBridge/OvrHandTrackingBridge.cs) が
  `ShowBodyInput`（頭 + 左右手のワールド位置のみ）で push する（**Streaming asmdef は OVR を参照しない**の維持）
- **course→world は `CourseFrame` 経由**。`ShowControlClient.CourseToWorldProvider` / `CourseYawProvider`
  （ZoneLayoutApplier が注入）を使う。**親子付けでは駄目**（CourseFrame は transform を動かさない）
- **決めたこと**: 手の**回転は使わない**（位置だけ。OVR の手 basis の罠を避ける）／ 全身アニメは持たない
  （T ポーズから腕だけ下ろす＝マネキン。AnimatorController 不要でどの humanoid FBX でも動く）／
  カメラ目線が欲しければ `fixed` + `fixedYawDeg`
- **フェイルソフト**: 姿勢未著作のカメラでは出さない / actor 未定義・プレハブ欠落は代用の箱 /
  手が取れない間は idle（体側）へ 0.35s で合流し**人形は消えない**

#### 腕・体の向き・立ち位置の作り直し（2026-08-05）

計画と実測値は [2026-08-05_actor-follow-and-arms.md](../plans/2026-08-05_actor-follow-and-arms.md)。
検証は `Diagnostics/Preview Actor Motion`（体験者の動きを合成して**数値と絵**を出す。Play 不要）。

- **腕は「肩から手」を腕の長さの比で写す**（[`ActorArmLogic`](../../Assets/Scripts/Streaming/Cg/ActorArmLogic.cs)）。
  旧実装は「頭から手」を**全高比**で写しており、人形と体験者の体の比率が同じことを仮定していた。
  実測でプレハブの頭−肩は身長比 **人体 9.0% / 市松人形 2.8%（3.2 倍差）**で、仮定が成り立たない。
  人体比率の Remy でも**腕を下ろした姿勢で目標が腕の届く範囲を 26% 超え**、常時 IK がクランプして
  腕が棒のように伸び切っていた（肩基準にして 5%＝人間も伸び切る範囲へ）。
  体験者の肩は頭から推定する（**目から 0.15 × 身長 下・0.104 × 身長 横 / 腕長 0.29 × 身長**。
  いずれも Remy のボーン実測）
- **人形の向きは「体験者の体の向き」**。頭の向きを半減期 0.35s で追い、**首のねじれ 50°** で引かれる
  （`ActorArmLogic.SmoothYawDeg`）。遅れだけだと「首を振った」と「体ごと回った」を区別できず、
  90° 振り向いた場面で体が置いていかれて**手が背中側へ回る**（実測）。
  ⚠ **腕の写像の正規化にも同じ体の向きを使う**（頭の向きで写すと、手が動いていないのに腕が振り回される）
- **立ち位置も腕・向きと同じ時刻を読む**（[`CourseTrack`](../../Assets/Scripts/Streaming/Cg/CourseTrack.cs)）。
  旧実装は位置だけ「いま」で、向きと腕は 0.15 秒前だった。実測で**歩行中 12cm・停止時 0cm**
- **人形の可動域を絞る**（[`ActorArmLogic.LimitToDollRange`](../../Assets/Scripts/Streaming/Cg/ActorArmLogic.cs)）。
  ハンドトラッキングへ 100% 追従させると、**着物の袖のように体へ付いた布が引き伸ばされる**。
  市松人形で角度を振って実測すると、腕を下ろした向きから **30° まで自然 / 45° で袖の先から手が
  突き出て破綻**した（`sweep` シナリオ）。
  - **腕を下ろした向きからの振れは 45° まで**（上限の 60% までは 1:1、そこから指数で飽和）。
    単純に切ると、体験者が動かしているのに人形が途中で止まってカクつく
  - **肩から手首は腕長の 0.85 まで**（常に肘を少し曲げておく）。角度と距離の両方を抑えると、
    45° でも袖の形が保たれる（距離を制限しない 45° は破綻する）
  - 実物の市松人形も肩の球体関節がわずかに動くだけで、真横に上げたり万歳したりはしない。
    **可動域を絞るのは妥協ではなく人形として正しい**
  - ⚠ 判定は**本番で見える大きさ**でする（`*_far.png`）。寄りでは本番より大きく見えて判断を誤る
    — 遠景では袖から覗く手は数 px にしかならない
  - **袖そのものが腕に付いてくるようにもした**（2026-08-05・`tools/doll-model/assemble.py`）。
    袖のスキニングが「腕の高さからの落差」だけで減衰していて、**前後へ膨らんだ頂点が体に残り、
    腕を動かすと手が袖を突き抜けていた**。腕の軸からの距離（上下と前後の両方）× 袖口への近さ
    へ変えたら、袖が腕と一緒に持ち上がるようになった。
    ⚠ 脇まで腕へ付けると**袖が体から剥がれて裂け目**ができる（実測）— 横方向の重みで体に残す。
    人形を作り直したら `Build Show Actor Prefab` は不要（プレハブは FBX を参照している）
  - 値は `ShowActorRig` の **const**（SerializeField にすると既存プレハブで 0 に読まれて腕が固まる）。
    人形ごとに変えたくなったら show.json の actor 定義へ出す。**Remy のような体にフィットした服の
    人形でも同じ制限が掛かる**（破綻はしないが動きは小さくなる）
- ⚠ **身長は「ボーンの最高点」だけでは測れない**。市松人形は Head が頭の中ほどまでしか無く、
  実寸を 3 割小さく見積もって **2.27m の巨人**になっていた（目標 1.6m）。メッシュ上端がボーンより
  25% 以上高ければメッシュ側を採る。**Remy は 11%**（髪と服の膨らみ）なのでボーンのまま。
  実寸は `ShowActorPrefabSizeTests` が固定する（**変わると人形の大きさが黙って変わる**）
- **Editor メニュー**: `Setup/Build Show Actor Prefab`（humanoid FBX → `Resources/ShowActors/<名前>.prefab`。
  Humanoid でも Generic でも可 — Generic は手のボーン名から親を 2 つ遡って肘・肩を取る）/
  `Diagnostics/Preview Show Actor`（Play せず 4 ポーズ × 2 角度を PNG 化）/
  **`Diagnostics/Preview Actor Motion`**（体験者の動きを時系列で合成し、腕の到達率・伸び率・
  手の前後・追従の遅れを `report.md` に、全身と腕の寄りを連番 PNG に出す。**見る人形は
  show.json の `actors[0]` から決まる** — 現場と違う人形を測っても意味が無いため）
- **人形は実物の色を持てる**（2026-08-03〜）: `ShowActor.shader` の `_BaseMap` に元モデルのアルベドが乗る。
  ビルダーが FBX のテクスチャを拾って**人形ごとのマテリアル**（`ShowActor_<名前>.mat`）を作る。
  テクスチャを持たないモデルは従来どおり共有 `ShowActor.mat` で灰色のマネキンになる（見た目不変）。
  実物をスキャンした人形（市松人形など）を入れる前提で足したもので、
  これが無いと**赤い着物を撮ってきても灰色で映る**。計画は
  [2026-08-03_doll-scan-to-showactor.md](../plans/2026-08-03_doll-scan-to-showactor.md)
  - ⚠ **テクスチャは 1 枚へまとめる**。人形は 1 マテリアルへ潰す設計で、これは影の付け方
    （`ShowCgLayer.AttachShadowMaterial` が `sharedMaterials` の末尾へ 1 枚足す）と対になっている。
    サブメッシュが複数あると**影が最後の 1 つにしか出ない**。2 枚以上あればビルダーが警告する
  - ⚠ **シェーダの陰影は wrap lighting で作る**ので、テクスチャに撮影時の影が焼き込まれていると二重になる。
    スキャン撮影で影を消す理由がこれ
  - **法線マップと鏡面反射も持てる**（2026-08-03〜）: `_BumpMap` / `_BumpScale` / `_Spec` / `_Gloss`。
    ビルダーがアルベドと同じ場所の `<...>_normal` を自動で拾い、Texture Type も Normal map へ直す。
    **鏡面は「光を動かすとハイライトが動く」ために要る** — リムは視線だけで決まるので光に反応せず、
    それ自体が「CG である」合図になっていた。法線を強くしすぎるとテクスチャの模様
    （目・眉・帯の柄）まで凹凸になって彫刻に見えるので控えめに
  - 実物の市松人形から作った人形が `Resources/ShowActors/Ichimatsu`（全高 0.40m）。
    作り方・罠は [tools/doll-model/README.md](../../tools/doll-model/README.md)。
    **フォトグラメトリでも AI 生成でもなく、正面写真のシルエットを前後へ押し出している**
    （髪は原理的に解けず、白磁の面も対応点が立たない）
- **`Diagnostics/Preview Show Actor` は Project ウィンドウで選んだ人形を見る**（2026-08-03〜）。
  `Resources/ShowActors/` 配下のプレハブを選んでから実行する。未選択なら従来どおり Remy
- **卓の著作面**: カメラ列の役割セレクトと 📐 姿勢欄・フロアマップの **📐 カメラ姿勢**モード（印ドラッグ＝位置 /
  矢印の先ドラッグ＝向き・扇＝画角）・**🎭 CG 人形**パネル（actors[]）・カットの「CG 人形」ドロップダウン
- **⚠ 実機未検証**（2026-07-27。EditMode 695/695・卓はブラウザ実操作・人形は Editor 静止画で確認済み）

### 合成の作り直し — 像空間・較正・影・部屋プロキシ（2026-07-27）

設計の正本は [2026-07-27_cg-compositing-rebuild.md](../plans/2026-07-27_cg-compositing-rebuild.md)。**Step 0（土台の是正 + スキーマ骨格）まで実装済み**。

人形が映像に合わなかった真因は姿勢の精度**以前**で、像空間の対応付けが 3 箇所ずれていた
（設計批評とコード監査が独立に同一結論へ到達）。**姿勢を完璧に測っても旧実装では合わない**：

| 何を | 旧 | 新 |
|---|---|---|
| 合成 UV | CG は生の `screenUv`（枠いっぱい） | ライブと**同じ contain-fit 枠**（`_CgScale` = [`MjpegScreen.ContainScale`](../../Assets/Scripts/Streaming/MjpegScreen.cs)）。4:3 の映像が 16:9 の枠へ letterbox される分（`_LiveScale.x=0.75`）だけ人形が水平 1.33 倍外側にずれていた |
| RT | スクリーン枠の 16:9・1280×720 | **ソース映像の実寸**（640×480）。映像より鮮明な CG は「貼り付けた絵」に見えるのでわざと同じ粗さへ落とす（`renderHeightPx` は上限） |
| 画角 | `fovDeg` を Unity は**垂直**、卓は**水平**の扇で描画（約 25% 相違） | **`hfovDeg`（水平）が正**。Unity が映像アスペクトから垂直へ変換（`ShowCgLayer.HorizontalToVerticalFovDeg`）。旧キーは読まない（卓が 4:3 前提で 1 回だけ移行して落とす） |
| 合成式 | `lerp(bg, cg.rgb, cg.a)`（straight alpha） | `bg*(1-a*s) + rgb*s`（**premultiplied over**）。影を「rgb=0 / a=濃さ」の断片として同じ RT に描けば、この式が自動的に乗算になる。不透明な人形に対しては旧式と同値 |
| 未登録時 | course→world が identity へ落ち、人形が全く違う場所に立つ（警告も無し） | **出さない**（`ShowControlClient.CourseRegisteredProvider` を `ZoneLayoutApplier` が注入）。登録が入れば次フレームから出る |
| 光の向き | `_LightDir` がワールド固定 → トラッキング原点の向き次第で部屋に対する陰影が変わる | **course 相対**（`ShowCgLayer.CourseLightDirToWorld` が MaterialPropertyBlock で毎フレーム供給。将来は `layout.room.light` が正） |

**新スキーマ（キーは切ってあり、中身を埋めるのは後段）**:

- **`cameras[].calib`** — 較正の解（卓が実映像上の床点から平面ホモグラフィで解く。位置・向き・roll・内部行列 px・k1）。
  **`pose` と同居させない**（pose = 人がフロアマップでドラッグする概算 / calib = 実測。統合すると卓のドラッグが解を壊す）。
  **レンズ・解像度に従属する**ので `srcW/srcH/lensId` で照合し、不一致なら較正無効（`ShowCameraCalibDef.MatchesSource`）。
  `refs[]` に対応点を残して再解決を 30 秒にする
- **`layout.room`** — 部屋の 3D プロキシ（壁・箱・床・光源）。**1 幾何で 3 用途**（CG のオクルーダ / 影の落ち先 / 較正参照）。
  別々に持つと必ずズレる。Blender FBX や Meta Scene API を使わないのは「卓が読めない＝シミュレータが必ず嘘をつく」から
- **`steps[].placement`** — 人形の立ち位置は**カットが持つ**。actor 側に持たせていた旧設計では
  「同じ人形を別のカットで別の場所に立たせる」ができなかった。未指定なら actor の `fixedX/Z/YawDeg` へフォールバック（後方互換）

**卓は CG 人形を描かない**（実描画の正は Unity。glTF + スキニング + IK + 影を素の WebGL2 で二重実装すると一致を検証する手段が無い）。
卓が持つのは投影計算のミラーと輪郭プロキシだけ。**シミュレータは「人形が出るカット」を必ず文字で明示する**
（`show-sim.js` の `resolveScreen`。黙って落とすのは「著作した演出が黙って消える」と同型の罪）。

#### ⚠ 自動求解は廃止した（2026-08-04）— いまは**手で合わせる**

以下の「解く」系の記述（ホモグラフィ・LM・leave-one-out・点の質・`lenses[]` の信頼判定）は
**読み取り専用の記録**。卓の UI は [`align-ui.js`](../../tools/web-compositor/align-ui.js) に置き換わり、
判断は [`align-model.js`](../../tools/web-compositor/align-model.js)（node テストあり）。
設計は [2026-08-04_manual-camera-align.md](../plans/2026-08-04_manual-camera-align.md)。

**なぜ**: 実測で **4 台とも一度も解けていなかった**（2026-07-29 に UI を全面立て直した後も 6 日間）。
失敗は数値ではなく**入力**にあった — 候補点が「既定値の壁」「仮想の格子」から作られていて、
**現場の床にその目印が実在しない**。実在しない点をどれだけ丁寧に打っても解は歪む。
現場で生きているのは人が置いた `pose`（4 台とも `hfovDeg: 77.4` は人が打った値）。

**いま**: 実映像へ部屋のワイヤーと人形を重ねて、人が見ながらカメラを動かす。
書き先は従来どおり `cameras[].calib`（**Unity 側は 1 行も変えていない**）。手で置いた印として
`method:"manual"` を立て、`rmsPx` / `accuracyM` / `refs` は**書かない**
（自動で解いたときの指標を手で置いた値に付けると嘘になる）。

**掴めるのは外部 6 自由度だけ**（x/y/z・yaw/pitch/roll）。**画角と歪みはドラッグ対象にしない** —
「画角を広げて近づける」と「狭めて遠ざける」は画の上でほぼ同じで、実測では画角を未知にすると
位置が 27cm・固定すれば 2cm。手ドラッグは曖昧さを消さず**残差の表示だけ**を消すので、
掴めるようにした瞬間 solver より悪くなる。レンズ単位で一度だけ決める。

**動かすのはカメラであって部屋ではない。** `layout.room` / `layout.floor` は **4 台 × 3 用途**
（オクルーダ / 影の落ち先 / 較正参照）の共有資産で、1 台に合わせて頂点をずらすと他のカメラの
合成が黙って狂い、人形の course 座標がカメラごとに食い違う。卓の UI は常にそれを言う。

**「卓は CG 人形を描かない」を部分的に覆した。** 卓が描くのは**幾何だけ**
（ワイヤー・人形の輪郭・足元・平面投影の影）。**陰影・素材・法線・鏡面・色温度は描かない** —
ここを移植すると「卓では正しく見えて実機が違う」を再生産する。
**合成の見た目の正は Unity**（`Preview Show Composite`）で、卓の画面に常時その旨を出す。
⚠ 人形の寸法は必ず渡すこと（`actor-proxy.js` の既定は**身長 1.6m の人間**用で、
そのまま 0.40m の人形に使うと足元の円が人形の背丈と同じ幅になる。実際そうなっていた）。

#### 端末の傾きを取り込む — 手で合わせるのは 4 自由度だけになった（2026-08-05）

配信アプリ（**streamer v0.9.0〜**）が `/info` で**配信画像の重力に対する姿勢**を配る。
卓の 🎯 カメラを合わせる の **📱 端末の傾き**パネルが取り込む。設計は
[2026-08-05_device-tilt-align.md](../plans/2026-08-05_device-tilt-align.md)。

| キー | 中身 |
|---|---|
| `tiltPitchDeg` | 上下（**+ が上向き**・`calib.pitchDeg` と同じ規約） |
| `tiltRollDeg` | 傾き（`calib.rollDeg` と同じ規約） |
| `tiltState` | `ok` / `moving`（動いている）/ `steep`（真下すぎて傾きが定義できない）/ `unknown`（センサ無し・未読）。**ok のときだけ取り込ませる** |

- **来るのは上下と傾きだけ。左右の向き（方位）は入っていない。** 方位は磁気コンパスでしか
  出せないが、室内・三脚・電子機器の近くで ±10〜30° ずれ、しかも course 空間の X/Z 軸と
  磁北の対応が未知。**磁気は送っていないし、送るようにもしない**（「参考」で出すと必ず誰かが使う）
- **上下と傾きが方位と独立に決まる理由**: 姿勢は Unity の Euler（ZXY 順・`R = Ry(yaw)·Rx(-pitch)·Rz(roll)`）で、
  yaw はワールド Y 軸まわりの最外回転だから**どのベクトルの Y 成分も変えない**。よって
  `pitch = asin(fwd·U)` / `roll = atan2(right·U, up·U)`（U = 上向き）が yaw を含まない。
  ⚠ 前提は **course 空間の Y 軸が重力の上向き**であること（位置合わせリチュアルが動かすのは
  XZ と yaw と床の高さだけなので成立している）。ここが崩れたらこの機能ごと嘘になる
- **計算は配信側でやり切る**（[`TiltMath`](https://github.com/Roiril/fixed-cam-streamer) / `TiltFilter`）。
  受信側に端末座標系を解釈させると、センサの取り付け向き・`targetRotation`・正立化が絡んだ
  二重実装になって必ず食い違う。配るのは「**いま配信されている絵**の向き」だけ
- **両側の式が繋がっていることは機械で固定してある**: streamer の `TiltMathTest`（置き方 → 期待角）と
  卓の `align-model.test.mjs`（`cameraBasis` から同じ式で pitch/roll を復元・yaw を振っても不変）。
  **片方だけ直すと沈黙して食い違う**
- **取り込みは明示操作**。ライブ値へ自動追従はしない（カメラを触っていないのに保存値が変わると、
  作業者が何をして絵が変わったのか追えなくなる）。ずれていれば「センサとのずれ 3.2°」と出す
- **取り込むと「この傾きを保つ」が入る**。以後 **X / Z 軸まわりの回転だけ**を止める
  （床を回す＝ Y 軸と平行移動は姿勢を変えないので触らない＝合わせ作業の本体は残る）。
  Y 軸回転は数学的に上下・傾きを変えないが、Euler へ戻す往復で誤差が乗るので値を焼き直している
- **`/info` は卓のサーバ経由で引く**（`GET /caminfo?host=&port=&auth=`・2s タイムアウト）。
  ブラウザ直だと端末ごとに CORS も到達性も違い、「届かない」と「その端末は /info を持たない」を
  区別できない。iPhone の IP Camera Lite は `/info` 自体が無いので、パネルは
  「この端末は傾きを送っていません」と出して従来どおり手で合わせる
- **Unity は 1 行も変えていない。** `StreamMetadata` に tilt のフィールドは無く、JsonUtility が
  未知キーを無視する。姿勢は従来どおり `cameras[].calib` から読む

#### 較正（cameras[].calib）の実装と、現場運用が「そうでなければならない」理由

数学は [`tools/web-compositor/calib.js`](../../tools/web-compositor/calib.js)（純関数・node テスト 26 本）。
床の既知点を映像上でクリックした対応から **平面ホモグラフィ**（Zhang）で姿勢・焦点距離・歪みを解く。
Unity 側の適用は [`ShowCgLayer.ApplyCameraCalib`](../../Assets/Scripts/Streaming/Cg/ShowCgLayer.cs)
（内部行列は `Camera.projectionMatrix` へ直接。physical camera を経由すると符号・軸の取り違えが必ず起きる）。

**卓と実機の投影が一致することは機械的に固定してある** — 同一の入力に対する期待画素を
`calib.test.mjs`（JS）と `CgProjectionTests.Projection_MatchesPinholeFormula`（C#・実 Camera 経由）の
両方にハードコードして突き合わせる。**片方だけ直すと沈黙して食い違う**ので必ず両方直すこと。

実測で分かった、運用に直結する 3 つの事実（`calib.test.mjs` がすべて固定している）:

| 事実 | 数値 | 運用への帰結 |
|---|---|---|
| **焦点距離を既知にすると精度が一桁変わる** | 人のクリック誤差 1.5px に対し、f も推定すると位置 **27cm** ずれる。f 固定なら **2cm** | **内部（画角）は一度だけ丁寧に / 外部（置き場所）は現場で毎回**。2 回目以降は前回の `fxPx` を固定して打ち直す（30 秒で復旧） |
| **歪みを無視すると高さが 4 割狂う** | 広角（k1=0.18）を歪み無しとして解くと、カメラ高さ 1.5m → 0.91m | k1 を「後で」にはできない。人形が床にめり込む／浮く原因になる |
| **f 未知のまま k1 を解くと縮退する** | 5 点だと歪みを f に吸わせた偽解（f 430→300）に落ちる | f 未知なら点 6 個以上（`MIN_POINTS_FOR_K1`）。f 固定なら 4 点でも歪みごと解ける |

**高さの点（壁の縦エッジ）を入れる**（2026-07-29・ユーザー報告「床だけじゃ精度に不安が残る／うまくいかない」）。
床の点だけだと**平面 1 枚しか見ていない**ので、「画角を広げて近づける解」と「狭めて遠ざける解」が
床の上ではほとんど同じ絵になる（k1 も同じ効き方なので巻き込まれる）。壁の角と**その真上 h[m]** を
対にして打つと平面の外から拘束が入り、画角・高さ・傾き・歪みがまとめて決まる。

| 打ち方 | 誤差（クリック 1.5px・カメラ位置） |
|---|---|
| 床 7 点だけ | 7.4cm（高さ 4.5cm / f 7.3px / k1 0.10 ずれ） |
| ＋ 縦エッジ 3 本 | **1.2cm**（高さ 0.6cm / f 1.0px / k1 0.01） |
| クリック誤差ゼロ・縦エッジ 3 本 | 真値へ収束（0.0cm） |

- 操作は「床の角を打つ → ▲ 高さの点を足す → **同じ角の真上**をクリック」。高さは 1 つの入力
  （壁の高さ）で、**検証ワイヤーの壁の高さと同じ値**を使う（別々に持つと打った上端と重ねた線が食い違う）
- 実装は [`refineCalib`](../../tools/web-compositor/calib.js)（Levenberg–Marquardt・数値ヤコビアン）。
  **順投影は `projectPoint` をそのまま使う**＝ Unity / シェーダと同じ式。ここで別式を書くと
  「卓では合うのに実機で合わない」という最悪の破れ方をする
- 初期解はこれまでどおり床のホモグラフィ。高さの点があるときだけ全点で精密化し、
  **同じ点集合で測った rms が改善した時だけ**採用する（点が増えたぶんの悪化を退化と誤判定しない）
- `calib.refs` は高さの点も `y` つきで残す（復元しないと次に解いた時に拘束が黙って消える）
- 高さの点が 2 本以上あれば、床の点が 6 個未満でも k1 を推定してよい（`MIN_RAISED_FOR_K1`）
- **LM の残差は理想（歪み無し）正規化座標で測る**。画素で測ると「理想 → 歪んだ」向きの変換が要り、
  除算モデルのこの向きは半径が大きいと解が無い（null）。探索の途中で予測が画面外へ飛ぶと
  残差が定義できず勾配が死ぬ。観測を歪み無しへ戻す向きは常に定義できるので、そちらで比べる
- **カメラ後方へ回った点の残差を定数にしない**（2026-07-29 実害）。定数だと数値微分が 0 になり、
  「後ろに回っている」を直す勾配が消えて、その点を置き去りにしたまま収束する
  （高さの点 3 つが投影不能のまま `誤差 3464px` と表示された）。pz に比例させて前へ押し戻す
- 解いた後に投影できない点が残るなら、**平均に混ぜず番号で名指しして失敗にする**
  （`badPoints`）。誤差の大きい点も上位 3 つを名指しし、映像では打った点と解の言う位置を赤で結ぶ
- **`layout.wall` が既定値（1m × 1m の L）のままだと、いくら丁寧に打っても合わない**。
  「壁の外角」の座標が実物と違うため。較正パネルは開いた時にこれを警告する（`wallLooksDefault`）

- レンズ歪みは**除算モデル** `r_u = r_d / (1 + k1 r_d²)`。順・逆の両方に解析解があるので、
  卓の順投影（ワイヤー重畳）と `ScreenComposite` の逆変換が**厳密に一致する**。
  多項式モデルだと互いに近似の逆になり、画面端で食い違って**較正の検証そのものが成立しない**
- 4 点ちょうどのときは **どの 3 点も一直線でないこと**が要る（DLT が正方系になるため）。卓は理由を文で返す
- 較正は **レンズ・解像度に従属**する。`ShowCgLayer.ResolveCalib` が受信フレームの実寸と `/info` の
  `lensId` を毎フレーム照合し、食い違えば概算 pose へ落として警告を出す（黙って狂わせない）

**打つ点は地図から選ぶ**（2026-07-29）。候補は `layout.calibPoints`（作者が置いた印）→ `layout.regPoints` →
部屋の角（`layout.wall` / `layout.floor` / **`layout.room` の壁の端・箱の床接地角**）→ タイルの角、の union で、
較正パネルの中の**部屋を上から見た略図**（[`floor-sketch.js`](../../tools/web-compositor/floor-sketch.js)）に
すべて描かれる。地図の点をクリックすると「打つ点」が切り替わり、［印を置く］にすると
`layout.calibPoints`（`{x, z, label}`・**ラベル必須**）を足せる（タイルの角と部屋の角へ吸着）。
- **`calibPoints` は `regPoints` と別の集合**。HMD の位置合わせ点はタッチ順という意味を持ち上限 5 点で、
  手が届く必要がある。較正点は**そのカメラに写る**必要があり、画角も解くなら 6 点以上要る。
  候補としては union で並べるが、**どちらかへコピーはしない**（2 コピーは必ずずれる）
- **地図で決めた座標は、現場の床に実物の目印が無ければ使えない**（メジャーで測らない限り同定できず、
  誤差として表面化しないまま解を歪める）。UI は「床の上・実物がある点だけ」を明示し、印にラベルを要求する
- **点の質は解く前に出す**（`pointQuality`）: 点数と必要数（画角固定なら 4 / 推定なら 6）・一直線・
  床の広がり・**画面の広がり**（床で散っていても画面の隅に固まれば解は暴れる）

**卓の較正 UI** = [`calib-ui.js`](../../tools/web-compositor/calib-ui.js)（描画とイベントだけ）+
[`calib-session.js`](../../tools/web-compositor/calib-session.js)（**判断**・DOM 非依存・
`calib-session.test.mjs` が固定）。カメラ列の［🎯 姿勢を合わせる］から入る。
ライブ映像を 1 枚静止させ、候補点（印 → 位置合わせ点 → 部屋の角 → タイルの角 → 手入力）を選んでクリックし、
✨ 解く → **床格子・壁・通過ラインのワイヤーを実映像に重ねて目で検証** → 💾 保存。
前回の `refs`（正規化 uv）を復元するので「ずれた点だけ直して解き直す」が回る。**解像度・レンズが食い違う較正では
ワイヤーを引かず理由を出す**（Unity の `MatchesSource` と同判定 — ずれの原因が「解」か「前提」か分からなくなるため）。

#### 較正 UI の立て直し（2026-07-29）— 判断の分離・レンズ・精度・出典

計画は [2026-07-29_calib-ui-rebuild.md](../plans/2026-07-29_calib-ui-rebuild.md)。**この道具は一度も現場で
成功していなかった**（show.json の 4 台とも `hasCalib=false`・`layout.wall` は既定値のまま）。
原因は点の打ち方ではなく、その手前の 3 つ。

**1. 状態遷移がテストを通っていなかった。** 開く / フレームを取り直す は同じ判断をしなければならないのに
別々の式で書かれ、片方だけが正しかった。判断を `calib-session.js` へ出して固定した（実バグ 5 件を同時に修正）:

| 直したもの | 壊れ方 |
|---|---|
| 🔄 で解像度が変わっても固定画角を捨てなかった | 別解像度の焦点距離で解いた嘘の解が「画角固定済み」として保存され、実機で人形が別の場所に立つ |
| 点の質判定が高さの点を床の点と混ぜた | 高さ点は基準点と (x,z) が同一 → **必ず**「3 点が一直線」と誤警告し、正しく打っているのに直せと言う |
| 映像が来る前に開くと `refs` が復元されず 🔄 でも戻らなかった | LIVE 前に開くのは現場で普通に起きる。全点を打ち直すことになる |
| 「壁の高さ」の後変更が既存点に届かなかった | 拘束は 1.80 のまま・検証線だけ 2.40 になる。**高さは点ごとに持ち、打った後も直せる**ようにした（壁と箱で高さが違ってよい） |
| 卓が lensId を照合しなかった | Unity は照合して較正を捨てる → 卓は「較正済み」なのに実機だけ黙って概算姿勢 |

ほかに、解に失敗した直後は古いワイヤーを描かない（「解けません」の横で線が合って見えた）／未保存の解を
確認なしで閉じない／復元した点に**名前を戻す**（`resolvePointLabels`・番号だけでは現場で直せない）／
🗑 全部消す（置き直し用）／↩ 元に戻す（保存の 1 世代）を足した。

**2. 実測していない幾何を実測のように扱っていた。** 既定値の壁（1m×1m の L）・定数のタイル格子
（0.15m × 12×12）から作った点は、**現場の床にその目印が無い**。座標は綺麗な数字なので打てば解は出るが、
実物と違う場所を指すので合わない。候補点と検証線に**出典**（`SOURCE_MEASURED` / `SOURCE_ASSUMED`）を持たせ、

- 候補は既定で実測点のみ（実測点が 4 個未満なら推測も出すが `⚠未測定` を付け、パネル上部で理由を言う）
- **実測点が足りないときは、較正パネルの中で床の寸法を直せる**（幅 × 奥行 → 保存）。
  メジャーで 30 秒で測れて、入れた瞬間に**四隅が打てる実測点になる**。ここを別の面（🧱 部屋）
  へ送ると、作業者はパネルを閉じて往復することになる。入れた値が既定と同じ 1.8×1.8 のときは
  「実際に測った値かどうかは判定できません」と正直に言う
- 検証線の推測部分は**点線**で描く（消すと部屋の見当が付かず、実線だと較正のせいだと誤診する）
- `wireSegments(layout, {includeAssumed:false})` で実測だけに絞れる。`hasMeasuredGeometry` で有無を判定

**3. レンズ（内部パラメータ）という概念が無かった。** 画角を固定する値の供給源が「**このカメラの前回の解**」
しか無く、4 点でフリーに解いた f が翌日「正確な内部パラメータ」として再利用される。しかも
**f を固定すると rms はむしろ下がる**ので誤りが良い数字に化ける。→ show.json トップレベルに `lenses[]` を新設:

```jsonc
"lenses": [{ "id": "lens_1", "name": "Pixel 7a 超広角", "fxPx": 431.2, "srcW": 640, "srcH": 480,
             "k1": 0.18, "measuredAtIso": "...", "pointCount": 9, "raisedCount": 3,
             "rmsPx": 0.8, "accuracyM": 0.02 }]
```

- カメラは `lensRef` で参照する。**同型機（iPhone 13 Pro ×2）で共有できる**のが要点
- 固定画角の供給源は **レンズ > 前回の解**。測定条件を一緒に焼くので素性が追える
  （`lensTrustIssues` が「高さの点 0 本で測った画角」等を選ぶ前に言う）
- **Unity は `lenses` を読まない**（解いた値は `cameras[].calib.fxPx` に入る）。契約は広がらない。
  サーバ側は `capture-server.py` の `_STATE_KEYS` と `_default_show()` に足すだけ

**4. 精度を「実際のずれ cm」で言う。** `rmsPx` は当てはまりであって精度ではない（4 点なら誤差 0 でも
解が嘘のことがある）。`leaveOneOutError` が 1 点を伏せて解き直し、伏せた点がどれだけ外れるかの中央値を
**その場所の床の上での距離**（`pixelToMeters`）へ直して結果の 1 行目に出す。`calib.accuracyM` に焼き、
カメラ列のバッジと本番前チェックが同じ数字を使う。

**5. 本番前チェックに `🎯 較正` の行**（`app.js` の `preflightRows`）。**CG 人形を出すカットが指すカメラ**が
未較正 / 解像度不一致なら ❌。人形を出さないカメラの未較正は黙る（要らないので）。

**6. 保存した映像から較正できる**（📁 保存した映像から）。現場では「撮るだけ撮って後で解く」ことがあるし、
ライブが切れている間も作業を進められる（＝**実カメラが無くても UI を検証できる**）。
- 画像の読み込みは `fetch` + `createImageBitmap`。`<img>` の `load` / `decode` は描画パイプラインに
  依存し、裏タブ・非表示ウィンドウでは**永久に解決しない**（2026-07-29 に実際に踏んだ）
- プレートの寸法は配信そのままとは限らない（Quest 経由の記録は 480×360・配信は 640×480）。
  **縦横比が同じなら保存時に内部行列を比例で移す**（`rescaleCalib`。ピンホールの内部行列は解像度に比例し、
  k1 は正規化半径なので不変、姿勢も不変）。縦横比が違えば移さず理由を出す — クロップされた映像で
  比例させると黙って嘘の較正になる
- プレートで解いている間は結果欄が「これを撮った後にカメラを動かしていたら合いません」と言い続ける

**同日の監査で直した 6 件**（新規実装に対する初回監査。いずれも実コードで確認済み）:

| 直したもの | 壊れ方 |
|---|---|
| `liveSize` がカメラを跨いで残っていた | A を開いて閉じ、映像の無い D でプレートを解くと **A の配信実寸へ移して**焼く。Unity の `MatchesSource` が落ちて人形が出ないのに卓は「較正済み」と言う。`open`/`close` で必ず null に戻す |
| プレート読み込みに世代ガードが無かった | 読み込み中に別カメラへ移ると、**B のパネルに A の絵と A の点**が載る。そのまま保存すると A 由来の較正が B に焼かれる。`gen` を open/close で ++ し、await の後に照合する |
| leave-one-out の cm 換算に 0 が混ざっていた | 高さの点の画素は水平線より上に来ることがあり `pixelToMeters` が 0 を返す。`Number.isFinite(0)` は真なので中央値が下へ引かれ、**精度が実際より良く出て** `accuracyM` に焼かれていた。cm 換算は床の点だけで測る |
| `applyFrameChange` が候補セレクトを作り直さなかった | 復元した点は候補から除かれるので、セレクトの選択値が実体を失い、映像をクリックしても打てない（＝「映像前に開いて 🔄 で復元」が行き止まり） |
| 実測点の数え方が「まだ打っていない候補」だった | 前回の点を全部復元した状態で開くと measured=0 になり、**実測済みなのに「実測点が 0 個です」と嘘**をついて未測定のタイル角を候補に混ぜていた |
| 移して保存した直後に「無効です」と言っていた | `saved` は配信実寸・`frame` はプレート寸法なので判定が偽になり、成功の瞬間にワイヤー（唯一の一次証拠）が消えていた。**縦横比が同じなら移して重ねる**（`viewCalib`）|

あわせて、床と高さの境界を数学側と揃え（`RAISED_MIN_Y = 0.01`）、置き直しの疑いの閾値を
画の対角の 3% にし（固定 px は解像度で意味が変わる）、保存判断を純関数 `decideSaveCalib` へ出した。

#### 影・接地・オクルージョン（2026-07-27）

**人形だけを描くと必ず浮いて見える**ので、同じ RT へ 3 つを重ねる。すべて「rgb=0 / a=濃さ」の
premultiplied 断片なので、合成側の over が自動的に乗算（背景を (1-a) 倍）になる。

| 何を | 実装 | 要点 |
|---|---|---|
| 床への影 | [`ShowShadowProjector.shader`](../../Assets/Art/Shaders/Cg/ShowShadowProjector.shader) | **平面投影シャドウ**。人形メッシュを光線方向で床平面へ潰して描く。URP のシャドウマップは使わない |
| 接地影 | [`ShowGroundBlob.shader`](../../Assets/Art/Shaders/Cg/ShowGroundBlob.shader) | 足元の楕円。**「浮いている」に一番効くのはこれ** |
| オクルージョン | [`ShowOccluder.shader`](../../Assets/Art/Shaders/Cg/ShowOccluder.shader) + [`ShowRoomProxy`](../../Assets/Scripts/Streaming/Cg/ShowRoomProxy.cs) | `Blend Zero One` + ZWrite On で色を書かず深度だけ置く。人形が壁・箱の裏へ回れる |

- **なぜ URP のシャドウマップを使わないか**: 人形は「シーンのライトを一切参照しない」のが不変条件
  （現場の照明・URP 設定・同居アプリの都合で見えが変わると演出が壊れる／URP の Light は cullingMask を
  尊重せず HMD が見る実 VR 空間まで照らす）。シャドウマップ方式は未検証の仕掛け（`beginCameraRendering` での
  ライト on/off とシャドウマップ culling の順序）が要り、失敗時の代替が共有 URP Asset の変更＝同居 2 アプリの規約と衝突する。
  平面投影ならライトが 1 つも要らない。セルフシャドウは出ないが、この画質では観測できない
- **⚠ ステンシルを外すと影が「濃い斑」になる**（腕と胴の投影が重なった所だけ二重に暗くなる）。
  `Stencil { Ref 1 Comp NotEqual Pass Replace }` で 1 画素 1 回だけ描く。そのため
  **RT の depth buffer は 24（depth24 + stencil8）でなければならない** — 16 に戻すと沈黙して斑に化ける
- **`layout.room` が未著作でも人形と影は出す**（床は course y=0 の無限平面）。プロキシに依存するのは
  オクルージョンだけ。ここを止めるとフェイルソフトが壊れる
- 描画順は Queue で決まる: オクルーダ(-200) → 影(-100) → 接地影(-99) → 人形(Geometry)

#### なじませ（2026-07-27）

- **映像遅延の補償**: スクリーンに映っているのは撮影から 100〜200ms 前の姿。「いま」の体験者の位置に
  人形を描くと歩行中ずっと先行してずれる（1m/s で 15cm）。[`BodyInputHistory`](../../Assets/Scripts/Streaming/Cg/BodyInputHistory.cs)
  が身体入力を時刻つきで貯め、`ShowCgLayer.CurrentBody` が **0.15 秒前を補間して読む**。
  **手の有効フラグは補間しない**（取れ／取れないの境界を混ぜると無効座標 (0,0,0) へ引っ張られて腕が飛ぶ）。
  遅延値は当面 const — `CameraStream.EstimatedLatencyMs` は「Unity 受信からテクスチャ反映まで」しか測れておらず
  （撮影・エンコード・伝送を含まない）、そのまま足すと過小補償になるので使わない
- **CG だけを鈍らせる**: `ScreenComposite` の `SampleCgSoft`（9-tap tent・既定 0.7 テクセル）。
  RT を映像実寸まで落としたうえでさらに掛ける。**premultiplied なので rgb と a を同じ重みでぼかす**
  （別々だと rgb > a の画素ができて縁に明るい滲みが出る）
- **色収差は入れない**と決めた。効果が小さい割に、premultiplied の rgb/a 整合を崩して縁を壊すリスクがある

#### なじませ 2 — 時間・接地・輪郭（2026-08-06）

「人形が CG っぽい」を実装レベルで監査した結果。**陰影や解像度の合わせ込みより、この 3 つが先に効く**。

- **⚠⚠ 人形を実写の刻みで描く**（最重要）。旧実装は仮想カメラが `enabled=true` で、
  **表示レート（72/90Hz）で毎フレーム RT を描き直していた**。実写は 15〜30fps なので、
  人形と腕だけが滑らかに動き、背景は刻んで動く。**動いている最中に一番強く「別レイヤ」に見える差**で、
  陰影をどれだけ合わせても消えない。
  → `enabled=false` + 手動 `Render()`。`CameraStream.LastFrameSeq` が進んだ時だけ描く
  （`ShowCgLayer.RenderIfDue`）。実写が止まっている間（録画・プレート・受信断）は 15fps の刻みで描く。
  **位置・腕は毎フレーム進める**（履歴と内部状態のため）。間引くのは絵にする瞬間だけ
- **接地影の下限が人間用だった**。`BlobRadiusMinM = 0.15` は成人 1.6m 前提で、全高 0.40m の市松人形では
  本来 0.088m のところ 0.15m へ**持ち上がっていた**（＝背丈の 3/4 の黒い円盤が足元に敷かれ、
  「切り抜きを板に貼った」ように見える）。下限を 0.03m へ。**人形の大きさに比例させる**
- **投影シャドウの縁がシャープすぎた**。平面投影は形をそのまま床へ潰すので縁をぼかす場所が無い。
  代わりに **`shadowSoftM` を「濃さの高さ減衰」として効かせる**（`1/(1 + h/softM)`）。
  実際の半影も遮蔽物と受光面の距離に比例して薄くなるので物理的にも正しく、
  **硬い縁が目立つのは接地点の近くだけ**になる（そこは接地影 blob の feather がぼかしている）
- **影の `ZTest` を `Always` → `LEqual`**。壁の裏に回った人形の影が壁の手前に出ていた
  （`ShowRoomProxy` は床を描かないので z-fight しない）
- **RT に MSAA 4x**。人形の輪郭だけが 1bit のギザギザで、実写側の JPEG のなまった縁と食い違っていた
- **人形の明るさを映像へ寄せる**（`ShowCgLayer.LumaGain`）。カメラごとに露出も現場の照明も違うのに、
  人形はどのカメラでも同じ明るさで出ていた。`MjpegScreen.SourceLuma`（post 前の生の平均輝度・4Hz の疎サンプル）
  を基準に、**主光源と環境光へ同じ倍率**を掛ける（片方だけだと陰影の比が変わって材質が変わって見える）。
  同じ輝度は `CameraFeelFx` の自動露出も読む

#### 卓の著作面（2026-07-27）

| 何を | どこで | 実体 |
|---|---|---|
| カメラの較正 | カメラ列の［🎯 姿勢を合わせる］ | [`calib-ui.js`](../../tools/web-compositor/calib-ui.js) |
| 部屋のプロキシ（壁・箱・床） | フロアマップの **🧱 部屋**モード | [`room-model.js`](../../tools/web-compositor/room-model.js) + `floormap.js` |
| CG 照明 | フロアマップ下の **💡 CG 照明**パネル（モードに紐づかない） | 同上。マップ上に光の向きを ☀ + 破線矢印で描く |
| 人形の立ち位置 | 演出リボンのカット詳細 → ［📍 画面で置く］ | [`actor-proxy.js`](../../tools/web-compositor/actor-proxy.js) + `calib.unprojectToFloor` |

- **映像の床をクリックして人形を置ける**（`unprojectToFloor` = `projectPoint` の厳密な逆）。
  数値入力とも双方向。向きは矢印の先をドラッグ。**較正済みでないカメラでは「実機では人形が出ません」と出す**
- **輪郭プロキシ**（足元の楕円・身長ボックス・頭・向き矢印）を映像とシミュレータに重ねる。
  **意図的に写実にしない**（寒色の半透明の線だけ）— 写実に見えると著作者がそれを信じて Unity の最終確認を飛ばす
- **人型シルエット**（2026-07-28 追加・`actorBodyGeometry`）: 箱のワイヤーだけでは事前オーサリングの段階で
  構図（大きさ・立ち位置・向き）を判断できなかった（ユーザー指摘）。身長比で定義した骨格 13 本 + 頭を
  投影し、**太さを「その位置での 1m あたりの画素数」から解いて**線幅で描く（遠いほど細い＝距離感が出る）。
  姿勢は実機と同じ「T ポーズから腕を下ろしたマネキン」。**写実にしない方針は変えていない**
  （同色の半透明シルエット 1 色で、陰影も素材も付けない）。
  透視のため**近距離では頭が人体比率より大きく写る**が、これは正しい挙動（実測 1.5m で 18% / 4.5m で 13%）。
  テストは正射影に近い遠距離で人体比率を固定している（`actor-proxy.test.mjs`）
- **シミュレータの follow は体の向きを歩行方向で代用する**（2026-07-28）。実機の follow は
  **体験者の頭 yaw**（分身）だが、卓は頭の向きを持っていない。1cm 未満の揺れでは向きを変えず、
  止まっている間は最後の向きを保つ。**UI で必ず「歩いた方向で代用。実機は頭の向き」と注記する**
  （黙って別物を見せない）
- **照明の 7 項目はすべて絵に効く**（2026-07-27 に接続）。向き・仰角は影の方向、`tempK` × `intensity` は
  人形に当たる光の色（`ShowCgLayer.KelvinToLinearColor` → `ShowActor.shader` の `_LightColor`）、
  `ambient` は影側の持ち上げ、`shadowDensity` は影の濃さ、`shadowSoftM` は**接地影の縁のぼけ幅**。
  - 色温度の式は**卓と C# で同一**（`room-model.js` の `kelvinToRgb` / `ShowCgLayer.KelvinToLinearColor`）。
    4000K の期待値を両側のテストにハードコードして突き合わせている。**片方だけ直すと沈黙して食い違う**
  - `shadowSoftM` を**平面投影シャドウには効かせない**（形をそのまま床へ潰すので縁をぼかす場所が無い）。
    受け口だけ作って効かせないのは著作者への嘘なので、シェーダからプロパティごと削除した
- `placementOf` は「placement の 3 成分がすべて 0 なら actor の既定へ落とす」ヒューリスティックを使っている
  （正規化が常に `{0,0,0}` を作るため「著作された (0,0,0)」と「未著作」を区別できない。actor 既定も通常 0 なので実害なし）

### BGM（区間で切替・停止・ループ範囲）— 2026-07-25

旧: `[Bgm]` の AudioSource が 1 曲を起動中ずっとループ（固定）。新: **タイムライン区間で切り替わる**。

- **データ**: show.json トップレベル `bgmTracks[]`（`{id,name,url,loopStartSec,loopEndSec,volume}`）+
  `bgm`（ラン既定）+ `timeline.segments[].bgm` / `hasBgm`（区間指示）。
  区間指示 = `{action:"play"|"stop"|"continue", trackId, loop, startSec, loopStartSec, loopEndSec, volume, fadeInSec, fadeOutSec, restart}`。
  **-1 = トラック既定を継承**。present-flag は `hasBgm`（[`TimelinePresentFlags`](../../Assets/Scripts/Streaming/TimelinePresentFlags.cs) が AND で確定）
- **セマンティクス**（純ロジック [`BgmPlanLogic`](../../Assets/Scripts/Streaming/BgmPlanLogic.cs)・テスト `BgmPlanLogicTests` 16 本 /
  JS ミラー `resolveBgmLane`）: 指示の無い区間は**曲が途切れず継続**（continue が既定）/ 同一トラックは
  **Retune**（再生位置を保ちループ範囲・音量だけ更新）/ 別トラックは **Start**（クロスフェード）/ **Stop** はフェードアウト
- **再生** = [`BgmDirector`](../../Assets/Scripts/Streaming/BgmDirector.cs)（AudioSource ×2 でクロスフェード・
  ループ範囲は `Update` で監視して巻き戻す＝`AudioSource.loop` は全長専用のため）。クリップは URL から
  `UnityWebRequestMultimedia` で取得しキャッシュ（`sa://` 焼き込み / ライブ URL 双方）。**擬似トラック id
  `__default__` = APK 同梱の既定クリップ**（音源を web 側に二重に置かず「元の曲へ戻す」が書ける）
- **区間への配線**: [`TimelineDirector`](../../Assets/Scripts/Streaming/TimelineDirector.cs) がゾーン進入で
  `ApplySegment`。ラン開始（runEpoch 変化 / 右グリップ長押し）で**ラン既定へ戻る**。show.json の rev が上がる
  たびに鳴り直さないよう、既定はシグネチャ比較で変化時のみ適用する
- **演出（Take）は音も一時占有できる**（2026-07-26）: `timeline.segments[].takes[].bgm` / `hasBgm`（型は区間と同じ
  `ShowBgmDef`）。**画面と同じ規則** — 演出開始で BGM レーンを占有し、終了・中止・ライブ卓の介入・ランリセットで
  必ず返す（[`TakeRunner`](../../Assets/Scripts/Streaming/TakeRunner.cs) → [`BgmDirector.BeginTakeOverride` /
  `EndTakeOverride`](../../Assets/Scripts/Streaming/BgmDirector.cs)）。
  **戻り先は開始時のスナップショットではなく「いまのレーン」** — 演出中に体験者がゾーンを移ると、そのあいだに
  届いた区間指示は鳴らさず**戻り先だけ更新**され、演出明けにそこへ行く（画面の「戻り先は再計算」と対称）。
  同じ曲へ戻る時は中断位置から続く。判定は純ロジック `BgmPlanLogic.DecideRestore`（テストあり）。
  **指示を書かなければ演出は音に触らない＝その区間（カメラ）の曲がそのまま流れる**。
  卓は演出インスペクタに「🎵 この演出のあいだ: ◯◯（この区間の曲のまま / この演出で切り替え / 無音）」を常時表示し、
  変える時だけリボンのチップに `♪ 曲名` / `🔇` が出る（JS ミラー `resolveTakeBgm`）
- **後方互換**: show.json に bgm 指定が無ければ `BgmDirector.defaultClip`（HorrBGM）を従来どおりループ。
  **⚠ シーンは `Setup Main Demo Scene` の再実行で [Bgm] を BgmDirector 化する必要がある**（未実行なら
  BgmDirector 不在 → 区間指示は無視され旧 AudioSource の固定ループが鳴る＝安全側）
- **⚠ Quest 実機未検証**（2026-07-25。EditMode 568/568・Web 卓 UI / 検証モードはブラウザ実測）

### 編集の粒度と自由度（2026-07-19 timeline v2 で更新）

| 調整対象 | 周×カメラ（セグメント）粒度で変えられるか | どこで |
|---|---|---|
| どの cue を出すか / delaySec / once / 複数 cue | ✅ **タイムライン区間で直接** | `timeline.segments[].cues[]` |
| cue パラメータ上書き（strength・フェード・trim） | ✅ **区間ごとに直接**（同じ cue を周ごとに違う強度で使える） | `cues[].override`（hasOverride） |
| マスク領域・素材（動画/画像） | ✅ 区間インスペクタで既存 cue 割当 or その場で新規 cue 作成 | cue 単位（`cues[]` ライブラリ）。マスクはカメラ構図に対して作るため camera 帰属は維持 |
| **画像加工 post（露出・コントラスト・彩度・色温度・ヴィネット・グレイン・走査線）** | ✅ **区間ごとに上書き可**（旧: カメラ単位固定で不可だった） | `timeline.segments[].post`（hasPost）。未設定は camera→global にフォールバック |
| **別カメラのインサートショット（N 秒差し込み）** | ✅ **区間ごとに enter/exit で** | `timeline.segments[].insert`（hasInsert） |
| **BGM（曲の切替・停止・ループ範囲・音量・フェード）** | ✅ **区間ごとに**（指示の無い区間は継続） | `timeline.segments[].bgm`（hasBgm）+ `bgmTracks[]` / ラン既定 `bgm` |
| **演出中だけの BGM**（インサート・離脱時演出を含む） | ✅ **演出ごとに**（無指定＝その区間の曲のまま。終われば戻る） | `timeline.segments[].takes[].bgm`（hasBgm） |
| **開始規則**（進入 +t 秒 / 離脱時 / **ライン通過**） | ✅ **演出ごとに**（ラインは担当カメラに紐づく共有資産。周は演出側が決める） | `takes[].at` / `lineId` + `layout.lines[]` |
| **画のホールド・焼き付き・人形に付き従う劣化** | ✅ **カットごとに** | `steps[].hold` / `burn` / `burnSec` / `aura`（卓のカット詳細） |
| **撮像の質**（暗部ノイズ・貼り付く粒・自動露出の追従） | ❌ **ラン全体で 1 組**（装置の素性なので途中で変えない） | show.json `feel`（卓の ⚙ 欄） |

- **cue（`OverlayCueData`）自体は色補正 post を持たない**（従来どおり）。画面全体のグレーディングは segment post > camera post > global の 3 段で解決される
- キー空間は現状維持（`cues[].camera`=文字列 id / `timeline.segments[].camera`=int index。変換は Web の `cuesForCam` 流儀）。統一は Unity 共有契約の破壊を避けるため見送り

## 接続の堅牢化 — 端末内在 ID + 発見プロトコル（fixedcam-discovery/1）— 2026-07-18

スロット（A/B/C）⇔端末の対応を **DHCP の IP 頼みにしない**。ID を正・IP を手段にする。
計画 [.claude/plans/2026-07-18_connection-robustness.md](../plans/2026-07-18_connection-robustness.md)。

- **端末内在 ID**: streamer v0.3.0 が cameraId（A/B/C）を永続保持・画面巨大表示。`/info` に `cameraId` / `uuid`（install 毎）/ `show`（トークン・既定 mawarimi）が載る
- **発見**: UDP :8830。**Quest/PC が probe をブロードキャスト送信 → スマホが unicast で announce 応答**（Quest はブロードキャスト受信しない設計 = MulticastLock・共有 manifest 不要）+ スマホ/PC 卓は 5s 毎の定期 announce も送出
- **Unity 側** = [`DiscoveryClient`](../../Assets/Scripts/Streaming/DiscoveryClient.cs)（`MainDemoSceneSetup` が配線）:
  発見表 id→{ip,port,uuid,lastSeen} を維持。**張り替えは実害駆動・2 系統**（2026-07-23 監査修正） — (1) **フレーム断** 5s 継続、または (2) **id 継続照合の不一致** 5s 継続（受信中 `/info` の cameraId+show を期待スロットと毎回照合。**streamer 側で配信を続けたまま cameraId を A→B に切り替えた/2 台で入れ替えたケースは断が起きないため、旧実装では永遠に反映されなかった** — `DiscoveryLogic.IsIdentityMismatch`・テストあり）。いずれか確定 + 発見表に別エンドポイント + **HTTP /info で cameraId+show 照合 OK** の時だけ `ApplyDiscoveryEndpoint` → 再接続。beacon 断だけでは何もしない（フラッピング防止）。id 不一致でメタが古い（/info 失敗継続・4.5s 超）場合は照合しない（誤検知防止）。不一致確定でも候補が無ければ現フィード継続 + HUD ×（黒より誤映像 + 警告を選ぶ）
- **優先順位**: `manual-pin（show.json cameras[i].pinned）> live discovery > show.json（ライブ>キャッシュ>焼き込み）> .asset 焼き込み`。Web 卓で host を手入力すると自動 pin（📌 表示・解除ボタンあり）。**pin 適用時は稼働済み discovery 層を `ClearDiscoveryEndpoint` で剥がす**（2026-07-23 修正。旧実装は discovery 層が上に残り pin host が無視される優先順位逆転があった）。**断 5s + 発見表に候補なし + discovery 層稼働中**なら discovery 層を解除して show.json/焼き込みへ自動フォールバック（旧: discovery 層は失効せず貼り付いたままだった）
- **二重 ID**（同 id 別 uuid）: **切り替えず現接続維持 + 警告**（HUD / Web 卓の発見パネル）。同 uuid の IP 変化（roam）は追従
- **PC 卓** (capture-server.py): beacon 受信で `cameras[].host` を**自動追従**（グローバルトグル・pinned 除外・実測 1 秒で復元）。`GET /discovery`（発見表）/ `GET /diag`（PC→カメラ HTTP・beacon・Quest heartbeat の 3 経路診断 = **AP クライアントアイソレーション即検出**）。PC 自身も role:"show-server" で announce → Quest が卓サーバを自動発見（`ShowServerSource` runtime override）
- **キルスイッチ**: Unity は `control.discoveryEnabled`（show.json）+ DiscoveryClient の SerializeField、サーバは env `FIXEDCAM_DISCOVERY=0`。切れば従来の静的 IP 運用へ縮退
- **守れない障害**: AP のクライアントアイソレーション（c2c 全遮断）は技術で救えない → 診断パネルで即検出し**自前 AP 持ち込み**へ切替（運用の第一選択は自前 AP + MAC 静的リース。発見機構は保険）
- **⚠ Quest 実機未検証**（2026-07-18。streamer 3 台 + PC 卓は実測検証済み）

## ⚠ host 未設定のカメラは「異常」ではない（2026-07-31 実害）

現場に置いていないカメラ枠（`cameras[].host` が空・焼き込み .asset も空）は**接続しないのが正しい**。
解析器も「show.json で host 未設定（接続しないのが正しい）」と扱う。ところが実装は例外を投げていた:

- `MjpegStreamReceiver` のコンストラクタが `new Uri("http://:8080/video")` で `UriFormatException`
- `CameraStreamRegistry.Awake` は**全カメラを 1 本の for で作る**ので、1 台が投げると
  **残り全部が初期化されない** → 実測で 3 台とも `con=0`、体験が丸ごと砂嵐（96%）になった
- 表面化していなかったのは、たまたま全カメラに host が焼かれていたから。
  **`Phone04.asset` の host を空にした瞬間に全カメラが死んだ**

→ 二重に堰き止めてある: (1) receiver は `Uri.TryCreate` で失敗を許容し、`Start()` が
`_uri == null` なら何もしない（後から show.json / discovery で host が入れば `ReapplyConnection` が
作り直す）、(2) Registry の生成ループを try/catch で包み、1 台の失敗が他を巻き込まないようにした。

## エラーハンドリング

- 接続失敗時は **指数バックオフ**で再接続（1s → 2s → 4s、上限 30s）
- **バックオフは接続確立でリセット**（2026-07-22）: 一度ヘッダのパースまで到達した接続の切断は、次リトライを 1s から始める（`MjpegStreamReceiver._connectionEstablished` を確立時に立て、catch でバックオフを 1s に戻してから倍化）。長時間安定した後の単発切断が 30s 待ちにならない
- **バックオフ待機は RequestReconnect で中断可能**（2026-07-22）: バックオフ Delay 中も CTS を `_connectionCts` に公開し、stall watchdog 等の `RequestReconnect` が待機（最大 30s）を即中断して即リトライ（`DelayWithReconnect`）
- **LoadImage 失敗時の再接続**（2026-07-22）: 壊れ JPEG 連続で `Texture2D.LoadImage` が false（texture 未更新＝黒/フリーズ）のとき受信統計を進めず、連続 30 枚 or 2 秒相当を超えたら（cooldown 明けで）強制再接続（`CameraStream`）。成功時のみ `_lastFrameTime`/seq/E2E を更新するので lag 検出・stall watchdog・SignalLostFx が沈黙しない
- **エンドポイント張替（`ReapplyConnection`）の後片付け**（2026-07-23 監査修正）: ①**suspend 中でも新 receiver を必ず起動**（旧: focus 喪失中に show.json/discovery 適用で張替が走ると新 receiver が未起動のまま残り、resume も起動しないため恒久黒画面）②**エンドポイント世代トークン**で in-flight の /info・/health フェッチを無効化（旧: 旧端末のメタが書き戻され、`_metaInflight` ガードが新エンドポイントの再取得をブロック）③`X-Frame-Seq` 系列・DroppedFrames・E2E 推定をリセット（別端末の seq 系列と混線して偽 Dropped を計上していた）
- **健全性判定は純ロジック [`StreamWatchdogLogic`](../../Assets/Scripts/Streaming/StreamWatchdogLogic.cs) に集約**（2026-07-23 監査修正・テスト `StreamWatchdogLogicTests`）: suspend/resume-gap/stall/lag/decode-fail の判定を UnityEngine 非依存（時刻・dt 注入）で分離し、`CameraStream` は薄いシェル。挙動を変えるときはテストを先に直す
  - **resume-gap は suspend も解除する**（A1 修正）: HMD 着脱で OS の resume コールバック（`OnApplicationPause(false)`）が来ないまま Update が再開すると `_suspended=true` が固着し、stall watchdog・砂嵐・discovery 自動張替が全停止していた。`BeginTick` の resume-gap 検知（dt>0.5s）で `_suspended=false` を揃えて解除（Registry 側ラッチも `Update` で自己回復・二層冪等）
  - **stall watchdog は `LastFrameRealtime` を汚さない**（A2 修正）: 旧実装は再接続発火時に `_lastFrameTime=now` を代入し、SignalLostFx が「復旧した」と誤認して真の信号断中に砂嵐が約 10 秒毎に 0.6 秒消灯していた。代入を廃し、再発火ゲートを `StallReconnectSec`（10s）に変更（周期は従来どおり）
- **`ConnectionKey` はパスワード変化も検知**（A3 修正・テスト `CameraSourceTests`）: `host|port|user|FNV-1a(pass)` 形式。show.json で auth のパスワードだけ変えても `ReapplyConnection` が発火する。生パスワードはキー文字列に露出しない（指紋のみ）
- タイムアウトは 3 秒
- `/info` `/health` 取得失敗は無視（DroidCam フォールバック互換）
- 画面には接続状態を表示（VR 内デバッグ UI）

## WebRTC（将来）

- Unity WebRTC パッケージ `com.unity.webrtc` を使用
- スマホ側は `getUserMedia` + RTCPeerConnection を吐く PWA を別途用意 / もしくは fixed-cam-streamer に WebRTC 配信パスを追加
- シグナリングは小さな WebSocket サーバー（Node.js）を立てる前提
