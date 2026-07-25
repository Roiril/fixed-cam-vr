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
| `GET /info` | `{deviceName, lensId, lensFovDeg, widthPx, heightPx, rotationDeg, isPortrait, deviceRotationDeg}` | [`StreamMetadataFetcher`](../Assets/Scripts/Streaming/StreamMetadataFetcher.cs)、[`CameraStream.Start()`](../Assets/Scripts/Streaming/CameraStream.cs) で 1 回取得。`deviceRotationDeg`=端末の物理的な上方向(0/90/180/270、OrientationEventListener 検知)。配信フレームは常に正立済み（`rotationDeg=0`、横持ち=640x480 / 縦持ち=480x640。v0.6.0 から 4:3） |
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
| Lag detect | `recv_fps / phone_fps < 0.7` が 1.5s 続いたら強制再接続 | TCP cwnd 縮みっぱなし状態を自動復旧 |
| Monitor | `/health` の `latestFrameAgeMs` / `sentFrames` で原因切り分け | 配信側 stall vs ネットワーク詰まりを判別 |

## ビューア体験レイヤ（追従・切替・フェイルソフト）— 2026-07-19

計画 [.claude/plans/2026-07-19_viewer-ux.md](../plans/2026-07-19_viewer-ux.md)（設計契約・初期パラメータ表はここが正）。

- **追従の緩急**: [`ScreenAnchor`](../../Assets/Scripts/Streaming/ScreenAnchor.cs) は yaw 純スナップを廃止し
  deadzone 10° + SmoothDamp 0.3s + 角速度上限 110°/s + 逆走ガード（`YawFollowLogic` 純ロジック・テストあり）。
  提示 2.0m・中心 -8°・スクリーン 4/3 倍（角径維持）。再ロック/ロスト復帰はスナップ禁止・減衰合流
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
- 発火: キーボード（CueBinding.key、Editor+Link オペレータ用）/ `PlayCue()` / `StopOverlay()` / **Web オペレータ卓の演出 ON/OFF（show.json `control.activeCue`）**
- ポスト FX（vignette/grain/scanline 等）はシェーダ内 = スクリーン内容にだけかかる。視界全体への FullScreenPass とは独立

### Web 連携の挙動（2026-06-17 / show.json cue 由来）

- **フェード**: `cue.fadeIn/fadeOut`（Web の演出トグル横の秒入力）で ON=fade-in / OFF=fade-out
- **ループ無し + 再生終了で自動復帰**: `cue.loop=false`。動画が自然終端（`loopPointReached`）か `trimEnd` に達したら自動 `StopOverlay` → live へフェード復帰（[`ScreenOverlayController.Update`](../Assets/Scripts/Streaming/ScreenOverlayController.cs) の trimEnd 監視）
- **再生区間 trim**: `cue.trimStart` へシークして再生、`trimEnd>0` で停止（`trimEnd<=0`=最後まで）
- **⚠ 動画 URL は UnityWebRequest でローカル DL してから `file://` 再生**（[`GetLocalVideoUrlAsync`](../Assets/Scripts/Streaming/ScreenOverlayController.cs)）。Android ネイティブ VideoPlayer は Python http.server(HTTP/1.0) からの HTTP ストリーミングを扱えず `NuCachedSource2 error -1` で落ちるため（画像/マスクは UnityWebRequest なので直 URL で OK）。URL 毎にキャッシュ。スペース入りファイル名は Web 側が percent-encode
- **マスクのフェザー**は Web が cue 保存時に PNG へ焼き込む（Quest はマスクをそのままサンプル）。**色統計マッチング・ラプラシアンは Web プレビュー専用**で Quest 実機の ScreenComposite はハード合成（`lerp(live,overlay,mask)`）
- **post-FX 数式**は ScreenComposite と Web の `FS_POST` を一致させてある（露出/温度/コントラスト/彩度/ヴィネット/走査線/グレインの順・式）

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

## タイムライン第一級オーサリング（show.json v2 `timeline`）— 2026-07-19

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
    "post": { /* PostParams 7項目 */ }, "hasPost": true,           // このゾーン滞在中の post 上書き
    "insert": { "anchor": "exit", "camera": 2, "durationSec": 4,   // 別カメラを N 秒差し込む
                "delaySec": 0, "cueId": "cue_C_scare", "once": true,
                "post": { /* PostParams */ }, "hasPost": false }, "hasInsert": true } ] }
```

- **cue**: 区間進入（Zone commit）+ delaySec で発火。複数可。`override` は ResolveCue 結果（OverlayCueData 複製）への差分パッチ。`control.activeCue` 非空中は抑止（ライブ優先）
- **post 上書き**: 解決は **segment > cameras[i].post > global** の 3 段（Unity `ApplyPostForActive` を拡張。insert 中はさらに insert post が最優先の 4 段）。区間離脱（次の Zone commit）で解除。**「1 周目の B は普通・2 周目の B は赤く」が可能になった**（旧: カメラ単位固定で不可）
- **インサートショット** = [`InsertController`](../../Assets/Scripts/Streaming/InsertController.cs) + `InsertLogic`（純ロジック・テストあり）:
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
  `CameraDef.hasPost` の `!=null` 導出は Web が未使用キーを削除するため今も正（timeline は AND 必須）
- **JSON キー `override`** は C# 予約語のため Unity 側は `@override` フィールドで受ける（実行時フィールド名は `"override"` で JsonUtility が正しく往復。実型で確認済み）
- **CachedConfig に timeline を保存**（オフライン発火。旧「cues 欠落」事故の教訓を踏襲）
- **Web 検証モード**（実機不要）: Web 卓「▶ 検証」で **矢印キー**（→ 次ゾーン / ← 1手戻る / R 先頭 / Esc 終了）。Unity セマンティクス（LapCounter 順方向進行・CueScheduler・insert・post 3 段）を JS ミラーで再現し、発火順と「体験者に見える画」を WebGL プレビューで確認。**ローカルのみで show.json / 実機は書かない**
- **⚠ Quest 実機未検証**（2026-07-19 実装。Unity コンパイル・EditMode 126/126・Web 卓ロード + 検証エンジン + セグメントインスペクタはブラウザ検証済み。insert の dip 演出・post 切替の見た目は現場調整前提）

### 演出の 1 語彙化（show.json v3 `takes[]` / 多段カット）— 2026-07-25

**cue（オーバーレイ）と insert（カメラ差し込み）を「演出(Take) / カット(Step)」1 語彙へ畳んだ。**
設計・スキーマの正本は [2026-07-25_shot-timeline-foundation.md](../plans/2026-07-25_shot-timeline-foundation.md)（§6 が契約）。

- **切替は show.json の版で決まる**: `timeline.schema >= 3`（または `takes` を持つ区間がある）→ **v3 経路**
  （[`TakeRunner`](../../Assets/Scripts/Streaming/TakeRunner.cs) が演出を実行し、旧 cue / insert 経路は空にされる）。
  そうでなければ**従来経路のまま完全に不変**（＝ v2 の show.json に戻せば全部元通り＝退避路）
- **区間 = (lap, camera) は変わらない**。区間に `takes[]` が 0..N 本ぶら下がる。1 本の演出が
  **カット列**を持つ（`live:<cam>` / `inherit` / `clip` / `still` ＋ オーバーレイ cueId ＋ 尺 ＋ 遷移）
- **`start.ifMissed`**: `enter+t` の演出が **t に達する前に体験者が区間を出たら、離脱の瞬間に発火**する
  （既定 `fireOnExit`）。`skip` なら出さない。**歩くのが速い体験者に山場が出ないまま終わる事故**への対策で、
  スキーマなので後から足せない＝最初から入っている
- **必ず終わる**: `maxDurationSec`（既定 45s）の watchdog。素材の Prepare 失敗等で止まっても画面は必ず戻る
- **戻り先は再計算**: 演出終了時は「いま体験者が居るゾーン」（時計 `ZoneCommitted` の確定値）へ。
  開始時のカメラへは戻さない
- **v2 は読み取り互換**: [`TimelineMigration`](../../Assets/Scripts/Streaming/TimelineMigration.cs) が
  `cues[]` → `inherit` カット / `insert` → `live` カット へ決定的に変換する（端末キャッシュ・焼き込みが v2 のため）。
  **1 点だけ挙動が変わる**: 旧実装は delay 待ちの cue / insert が**別の区間で遅れて誤爆**していたが、
  v3 では離脱の瞬間に決着する（設計 §8.1）
- **⚠ Quest 実機未検証**（2026-07-25。EditMode 667/667・fixture 契約テスト・卓のブラウザ実操作は通過）。
  **Web 卓の編集面は既定が v3（リボン）**（2026-07-25〜）。v2 の show.json も読み込み時に変換して表示し、
  **💾 保存を押した時点で `timeline.schema=3` として書き出される**（それまで show.json は v2 のままで実機は従来経路）。
  退避路: 保存前なら卓の「▤ 旧グリッド」、保存後は `show.json.bak`（卓サーバが 1 世代残す）を戻して再起動 —
  Unity は版で経路が分かれるので、データを戻せば従来動作に戻る

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
