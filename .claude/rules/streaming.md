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
- **配信プロファイル実測**（13 Pro 既定設定）: HTTP/1.0・非 chunked・`boundary=--BoundaryString`・パート毎 `Content-Length` 付き。1 フレーム ≈93KB、帯域 ≈30Mbps と **fixed-cam-streamer（quality 40 / 640x360）の数倍重い** → 3 台運用ではアプリ側で解像度/品質を下げること
- **解像度・アスペクト（2026-06-17 調査）**: 既定は **640×480（4:3）固定**。黒帯は無く素の 4:3（iPhone センサーのフル 4:3）。Unity スクリーン（16:9）や streamer（16:9）と揃えたいなら **アプリ Settings → Video Resolution で 1280×720（16:9）に変更**する（4:3→16:9 は上下画角が犠牲・帯域増 → 品質スライダで調整）。web-compositor のビューは live 実寸からアスペクトを自動追従するので、解像度を変えればコード変更なしで 16:9 化する
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
| `GET /info` | `{deviceName, lensId, lensFovDeg, widthPx, heightPx, rotationDeg, isPortrait, deviceRotationDeg}` | [`StreamMetadataFetcher`](../Assets/Scripts/Streaming/StreamMetadataFetcher.cs)、[`CameraStream.Start()`](../Assets/Scripts/Streaming/CameraStream.cs) で 1 回取得。`deviceRotationDeg`=端末の物理的な上方向(0/90/180/270、OrientationEventListener 検知)。配信フレームは常に正立済み（`rotationDeg=0`、横持ち=640x360 / 縦持ち=360x640） |
| `GET /health` | `{uptimeMs, totalFrames, totalBytes, fps, sentFrames, latestFrameAgeMs, clientCount, eisMode, oisMode, afMode, cropRatio, zoomRatio, aeState, aeLock, awbLock, expUs, iso, flicker}`（v0.5.0〜 後半は CaptureResult 直読みの実効値 = 決定性の観測用） | 任意。`CameraStream.RefreshHealthAsync()` で都度取得（HudDump からのモニタ用）。`sentFrames` と `totalFrames` の差分が広がる時は HTTP ワーカ詰まり（**⚠ sentFrames はクライアント接続ごとに加算される** — Quest + web 卓 /cam プロキシ等の多クライアント時は sent ≈ 接続数×totalFrames が正常。単一クライアント前提でしか差分ヒューリスティックを使わない）。`latestFrameAgeMs` が大きい時はカメラ stall |
| `GET /` | 簡易ステータス HTML | ブラウザ確認用 |

リポジトリ: [Roiril/fixed-cam-streamer](https://github.com/Roiril/fixed-cam-streamer)（private）。APK ビルド・インストール手順はそちらの README 参照。

## MJPEG デコード

- **方針**: 自前実装（無料）でまず動かす。安定性が問題になったら AVPro Video（有料）に移行検討
- **ライブラリ**: `multipart/x-mixed-replace` の boundary パース → JPEG バイト列 → `Texture2D.LoadImage`
- **スレッド**: HTTP 受信は別スレッド or `async Task`。`Texture2D.LoadImage` はメインスレッド必須

### 性能ガイドライン（低遅延優先）

- **解像度**: 1080p は Quest 3 でも GC アロケーションが増える。fixed-cam-streamer 側は **既定 640x360**（`CameraController.streamWidth/Height` で変更可。旧 720x405 から低遅延化のため縮小）。VR 内での視認性とレイテンシのバランスでこの値が現状最適
- **FPS**: streamer 側で `CONTROL_AE_TARGET_FPS_RANGE=[30,60]` を強制し、暗所で 15fps へ落ちないよう固定。capture フレーム間隔そのものが遅延の下限になる
- **JPEG quality**: 既定 40。視認性は維持しつつ Wi-Fi 帯域を半減 → kernel バッファ滞留減
- **バッファ**: 受信側は単一スロット最新フレーム + バッファ swap で再利用、毎フレ `new byte[]` 発生ゼロ
- **TCP**: 両側とも `TCP_NODELAY=true`（Nagle 抑止）。streamer は `SO_SNDBUF=64KB`、Unity は `SO_RCVBUF=64KB` で kernel 滞留を抑制
- **Texture**: `Texture2D` は事前確保し `LoadImage` で上書き。新規作成しない（**かつ初期化必須** — 未初期化は Quest GPU で白ノイズ化する [unity_pitfalls.md](../.claude/memory/unity_pitfalls.md)）

### 遅延対策（実装済み一覧）

| レイヤ | 対策 | 効果 |
|---|---|---|
| Capture (streamer) | `AE_TARGET_FPS_RANGE=[30,60]` で fps 下限固定 | 暗所での 15fps 化を防止（フレーム間隔 = 遅延下限） |
| Encode (streamer) | JPEG quality=40, 解像度 640x360 | エンコード時間 + 帯域を半減 |
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
  クールダウン 2s・最小ショット長 2s（dwell）・**cue 再生中は自動切替凍結**・手動後 8s は自動抑止・
  dip-to-black 70/100ms + `SwitchAudioCue`（音源は空スロット）。Web cameraOverride は従来どおり即時
- **フェイルソフト**: [`SignalLostFx`](../../Assets/Scripts/Streaming/SignalLostFx.cs) — 配信断 600ms → 砂嵐へ 150ms、
  トラッキングロスト/pause 明けは追従凍結 + 弱ノイズ → 減衰合流。体験者には「信号ロスト」に見える
- **⚠ ScreenComposite の `_SwitchDim` / `_SignalLost` は post FX 数式（Web FS_POST 一致規約）の対象外**（別系統 uniform）
- HUD は既定 OFF（左 Y で表示・STATE 行にスタッフ用の断/凍結/抑止状態）
- **⚠ 実機試着未実施**（パラメータは全て SerializeField・現場調整前提）。ゾーン 2 秒未満で駆け抜けると
  dwell により切替が発生しない点に注意（minDwellSec で調整）

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
- **周回検知** = [`LapCounter`](../../Assets/Scripts/Tracking/LapCounter.cs)（進行ポインタ方式）: アクティブカメラが `order[(i+1)%n]` に一致した時だけ前進、`order[0]` 復帰で lap++。**逆走・行き来・スキップ・境界 jitter はカウントしない**。さらに 2026-07-19 から **Director の Zone 由来切替のみ算入**（スタッフ手動 A/B・Web cameraOverride・外部切替では周回も cue 進入通知も動かない — `SwitchSource` タグ）
- **ラン（体験者 1 人分）**: `control.runEpoch`（int・既定 0）の**変化**で LapCounter リセット（lap=1・再シード）+ CueScheduler の once 発火済みクリア。Web ライブ運用パネルの「▶ ラン開始」= runEpoch++。PC 不在時は **Staff モードで左スティック押し込み** = ローカルランリセット。heartbeat に `lap` / `cam` を追加（Web でラン状態が見える）
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
- **present-flag 必須**（JsonUtility 制約）: `hasPost` / `hasInsert` / `hasOverride` を必ず書く。null 入れ子は既定値で書かれるため、ライブ・焼き込みパース直後に `!=null` から確定する（`CameraDef.hasPost` と同じ手法）
- **JSON キー `override`** は C# 予約語のため Unity 側は `@override` フィールドで受ける（実行時フィールド名は `"override"` で JsonUtility が正しく往復。実型で確認済み）
- **CachedConfig に timeline を保存**（オフライン発火。旧「cues 欠落」事故の教訓を踏襲）
- **Web 検証モード**（実機不要）: Web 卓「▶ 検証」で **矢印キー**（→ 次ゾーン / ← 1手戻る / R 先頭 / Esc 終了）。Unity セマンティクス（LapCounter 順方向進行・CueScheduler・insert・post 3 段）を JS ミラーで再現し、発火順と「体験者に見える画」を WebGL プレビューで確認。**ローカルのみで show.json / 実機は書かない**
- **⚠ Quest 実機未検証**（2026-07-19 実装。Unity コンパイル・EditMode 126/126・Web 卓ロード + 検証エンジン + セグメントインスペクタはブラウザ検証済み。insert の dip 演出・post 切替の見た目は現場調整前提）

### 編集の粒度と自由度（2026-07-19 timeline v2 で更新）

| 調整対象 | 周×カメラ（セグメント）粒度で変えられるか | どこで |
|---|---|---|
| どの cue を出すか / delaySec / once / 複数 cue | ✅ **タイムライン区間で直接** | `timeline.segments[].cues[]` |
| cue パラメータ上書き（strength・フェード・trim） | ✅ **区間ごとに直接**（同じ cue を周ごとに違う強度で使える） | `cues[].override`（hasOverride） |
| マスク領域・素材（動画/画像） | ✅ 区間インスペクタで既存 cue 割当 or その場で新規 cue 作成 | cue 単位（`cues[]` ライブラリ）。マスクはカメラ構図に対して作るため camera 帰属は維持 |
| **画像加工 post（露出・コントラスト・彩度・色温度・ヴィネット・グレイン・走査線）** | ✅ **区間ごとに上書き可**（旧: カメラ単位固定で不可だった） | `timeline.segments[].post`（hasPost）。未設定は camera→global にフォールバック |
| **別カメラのインサートショット（N 秒差し込み）** | ✅ **区間ごとに enter/exit で** | `timeline.segments[].insert`（hasInsert） |

- **cue（`OverlayCueData`）自体は色補正 post を持たない**（従来どおり）。画面全体のグレーディングは segment post > camera post > global の 3 段で解決される
- キー空間は現状維持（`cues[].camera`=文字列 id / `timeline.segments[].camera`=int index。変換は Web の `cuesForCam` 流儀）。統一は Unity 共有契約の破壊を避けるため見送り

## 接続の堅牢化 — 端末内在 ID + 発見プロトコル（fixedcam-discovery/1）— 2026-07-18

スロット（A/B/C）⇔端末の対応を **DHCP の IP 頼みにしない**。ID を正・IP を手段にする。
計画 [.claude/plans/2026-07-18_connection-robustness.md](../plans/2026-07-18_connection-robustness.md)。

- **端末内在 ID**: streamer v0.3.0 が cameraId（A/B/C）を永続保持・画面巨大表示。`/info` に `cameraId` / `uuid`（install 毎）/ `show`（トークン・既定 mawarimi）が載る
- **発見**: UDP :8830。**Quest/PC が probe をブロードキャスト送信 → スマホが unicast で announce 応答**（Quest はブロードキャスト受信しない設計 = MulticastLock・共有 manifest 不要）+ スマホ/PC 卓は 5s 毎の定期 announce も送出
- **Unity 側** = [`DiscoveryClient`](../../Assets/Scripts/Streaming/DiscoveryClient.cs)（`MainDemoSceneSetup` が配線）:
  発見表 id→{ip,port,uuid,lastSeen} を維持。**張り替えは実害駆動** — フレーム断 5s 継続 + 発見表に別エンドポイント + **HTTP /info で cameraId+show 照合 OK** の時だけ `ApplyDiscoveryEndpoint` → 再接続。beacon 断だけでは何もしない（フラッピング防止）
- **優先順位**: `manual-pin（show.json cameras[i].pinned）> live discovery > show.json（ライブ>キャッシュ>焼き込み）> .asset 焼き込み`。Web 卓で host を手入力すると自動 pin（📌 表示・解除ボタンあり）
- **二重 ID**（同 id 別 uuid）: **切り替えず現接続維持 + 警告**（HUD / Web 卓の発見パネル）。同 uuid の IP 変化（roam）は追従
- **PC 卓** (capture-server.py): beacon 受信で `cameras[].host` を**自動追従**（グローバルトグル・pinned 除外・実測 1 秒で復元）。`GET /discovery`（発見表）/ `GET /diag`（PC→カメラ HTTP・beacon・Quest heartbeat の 3 経路診断 = **AP クライアントアイソレーション即検出**）。PC 自身も role:"show-server" で announce → Quest が卓サーバを自動発見（`ShowServerSource` runtime override）
- **キルスイッチ**: Unity は `control.discoveryEnabled`（show.json）+ DiscoveryClient の SerializeField、サーバは env `FIXEDCAM_DISCOVERY=0`。切れば従来の静的 IP 運用へ縮退
- **守れない障害**: AP のクライアントアイソレーション（c2c 全遮断）は技術で救えない → 診断パネルで即検出し**自前 AP 持ち込み**へ切替（運用の第一選択は自前 AP + MAC 静的リース。発見機構は保険）
- **⚠ Quest 実機未検証**（2026-07-18。streamer 3 台 + PC 卓は実測検証済み）

## エラーハンドリング

- 接続失敗時は **指数バックオフ**で再接続（1s → 2s → 4s、上限 30s）
- タイムアウトは 3 秒
- `/info` `/health` 取得失敗は無視（DroidCam フォールバック互換）
- 画面には接続状態を表示（VR 内デバッグ UI）

## WebRTC（将来）

- Unity WebRTC パッケージ `com.unity.webrtc` を使用
- スマホ側は `getUserMedia` + RTCPeerConnection を吐く PWA を別途用意 / もしくは fixed-cam-streamer に WebRTC 配信パスを追加
- シグナリングは小さな WebSocket サーバー（Node.js）を立てる前提
