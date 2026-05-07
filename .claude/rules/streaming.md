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
| **fixed-cam-streamer** (自作 Android) | MJPEG over HTTP `:8080/video` + `/info` + `/health` | 100–200ms | **標準（DroidCam から移行済み）** |
| DroidCam (Android) | MJPEG over HTTP `:4747/mjpegfeed?WxH` | 150–300ms | フォールバック / 緊急時 |
| IP Webcam (Android) | MJPEG over HTTP `:8080/video` | 150–300ms | 代替 |
| WebRTC（自作 PWA / Larix） | WebRTC | 50–100ms | 低レイテンシ要件時 |

## fixed-cam-streamer エンドポイント

| URI | レスポンス | Unity 側で読む箇所 |
|---|---|---|
| `GET /video` | `multipart/x-mixed-replace; boundary=frame` の MJPEG。**各パートに `X-Width` / `X-Height` / `X-Rotation` / `X-Capture-Ns` / `X-Frame-Seq` ヘッダ付き** | [`MjpegStreamReceiver`](../Assets/Scripts/Streaming/MjpegStreamReceiver.cs) |
| `GET /info` | `{deviceName, lensId, lensFovDeg, widthPx, heightPx, rotationDeg, isPortrait}` | [`StreamMetadataFetcher`](../Assets/Scripts/Streaming/StreamMetadataFetcher.cs)、[`CameraStream.Start()`](../Assets/Scripts/Streaming/CameraStream.cs) で 1 回取得 |
| `GET /health` | `{uptimeMs, totalFrames, totalBytes, fps, sentFrames, latestFrameAgeMs}` | 任意。`CameraStream.RefreshHealthAsync()` で都度取得（HudDump からのモニタ用）。`sentFrames` と `totalFrames` の差分が広がる時は HTTP ワーカ詰まり。`latestFrameAgeMs` が大きい時はカメラ stall |
| `GET /` | 簡易ステータス HTML | ブラウザ確認用 |

リポジトリ: [Roiril/fixed-cam-streamer](https://github.com/Roiril/fixed-cam-streamer)（private）。APK ビルド・インストール手順はそちらの README 参照。

## MJPEG デコード

- **方針**: 自前実装（無料）でまず動かす。安定性が問題になったら AVPro Video（有料）に移行検討
- **ライブラリ**: `multipart/x-mixed-replace` の boundary パース → JPEG バイト列 → `Texture2D.LoadImage`
- **スレッド**: HTTP 受信は別スレッド or `async Task`。`Texture2D.LoadImage` はメインスレッド必須

### 性能ガイドライン（低遅延優先）

- **解像度**: 1080p は Quest 3 でも GC アロケーションが増える。fixed-cam-streamer 側は **既定 720x405**（`CameraController.streamWidth/Height` で変更可）。VR 内での視認性とレイテンシのバランスでこの値が現状最適
- **FPS**: streamer 側で `CONTROL_AE_TARGET_FPS_RANGE=[30,60]` を強制し、暗所で 15fps へ落ちないよう固定。capture フレーム間隔そのものが遅延の下限になる
- **JPEG quality**: 既定 40。視認性は維持しつつ Wi-Fi 帯域を半減 → kernel バッファ滞留減
- **バッファ**: 受信側は単一スロット最新フレーム + バッファ swap で再利用、毎フレ `new byte[]` 発生ゼロ
- **TCP**: 両側とも `TCP_NODELAY=true`（Nagle 抑止）。streamer は `SO_SNDBUF=64KB`、Unity は `SO_RCVBUF=64KB` で kernel 滞留を抑制
- **Texture**: `Texture2D` は事前確保し `LoadImage` で上書き。新規作成しない（**かつ初期化必須** — 未初期化は Quest GPU で白ノイズ化する [unity_pitfalls.md](../.claude/memory/unity_pitfalls.md)）

### 遅延対策（実装済み一覧）

| レイヤ | 対策 | 効果 |
|---|---|---|
| Capture (streamer) | `AE_TARGET_FPS_RANGE=[30,60]` で fps 下限固定 | 暗所での 15fps 化を防止（フレーム間隔 = 遅延下限） |
| Encode (streamer) | JPEG quality=40, 解像度 720x405 | エンコード時間 + 帯域を半減 |
| Distribute (streamer) | `FrameDistributor` は単一最新フレームのみ保持 | 配信側の滞留ゼロ |
| Network (streamer→VR) | `TCP_NODELAY=true`, `SO_SNDBUF=64KB`/`SO_RCVBUF=64KB` | Nagle 待機 + kernel バッファ滞留を排除 |
| Multipart header | `X-Capture-Ns` / `X-Frame-Seq` 付与 | 受信側で歯抜け検出・古フレ判定が可能 |
| Receive (Unity) | 単一スロット + バッファ swap、毎フレ new ゼロ | GC 圧ゼロ |
| Drain (Unity) | 受信時点で「最新のみ」上書き | 古フレが Tick まで生き残らない |
| Lag detect | `recv_fps / phone_fps < 0.7` が 1.5s 続いたら強制再接続 | TCP cwnd 縮みっぱなし状態を自動復旧 |
| Monitor | `/health` の `latestFrameAgeMs` / `sentFrames` で原因切り分け | 配信側 stall vs ネットワーク詰まりを判別 |

## 自動回転 / アスペクト補正

`/info` の `rotationDeg` / `widthPx` / `heightPx` を [`MjpegScreen.autoOrient`](../Assets/Scripts/Streaming/MjpegScreen.cs) が読み取り、Screen Quad の `localRotation.z` と `localScale` を自動補正する。

- 縦持ちスマホ (`rotationDeg=90, isPortrait=true`) → スクリーンが縦向きに自動切替
- 横持ちスマホ (`rotationDeg=0, isPortrait=false`) → 既定の横向き
- 補正対象: `MjpegScreen.orientTarget` で別 Transform を指定可能。null なら自分自身

`/info` 取得失敗時（DroidCam 等）は補正なし、Quad の初期 transform のまま。

## カメラ管理

- 各カメラは `ScriptableObject` で URL・解像度・名前を定義（`Assets/Settings/Cameras/`）
- 既定値: port=8080, videoPath=/video, infoPath=/info, healthPath=/health（fixed-cam-streamer 互換）
- DroidCam 互換時は port=4747, videoPath=/mjpegfeed?WxH, infoPath="" にする
- 切替時は **常時受信を維持**（再接続コスト回避）し、表示先 Quad の `enabled` または RenderTexture 切替で対応
- 同時 3 台までは Quest 3 で実用域、それ以上は要計測

## エラーハンドリング

- 接続失敗時は **指数バックオフ**で再接続（1s → 2s → 4s、上限 30s）
- タイムアウトは 3 秒
- `/info` `/health` 取得失敗は無視（DroidCam フォールバック互換）
- 画面には接続状態を表示（VR 内デバッグ UI）

## WebRTC（将来）

- Unity WebRTC パッケージ `com.unity.webrtc` を使用
- スマホ側は `getUserMedia` + RTCPeerConnection を吐く PWA を別途用意 / もしくは fixed-cam-streamer に WebRTC 配信パスを追加
- シグナリングは小さな WebSocket サーバー（Node.js）を立てる前提
