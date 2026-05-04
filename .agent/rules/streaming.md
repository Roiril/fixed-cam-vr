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
| `GET /video` | `multipart/x-mixed-replace; boundary=frame` の MJPEG | [`MjpegStreamReceiver`](../Assets/Scripts/Streaming/MjpegStreamReceiver.cs) |
| `GET /info` | `{deviceName, lensId, lensFovDeg, widthPx, heightPx, rotationDeg, isPortrait}` | [`StreamMetadataFetcher`](../Assets/Scripts/Streaming/StreamMetadataFetcher.cs)、[`CameraStream.Start()`](../Assets/Scripts/Streaming/CameraStream.cs) で 1 回取得 |
| `GET /health` | `{uptimeMs, totalFrames, totalBytes, fps}` | 任意。`CameraStream.RefreshHealthAsync()` で都度取得（HudDump からのモニタ用） |
| `GET /` | 簡易ステータス HTML | ブラウザ確認用 |

リポジトリ: [Roiril/fixed-cam-streamer](https://github.com/Roiril/fixed-cam-streamer)（private）。APK ビルド・インストール手順はそちらの README 参照。

## MJPEG デコード

- **方針**: 自前実装（無料）でまず動かす。安定性が問題になったら AVPro Video（有料）に移行検討
- **ライブラリ**: `multipart/x-mixed-replace` の boundary パース → JPEG バイト列 → `Texture2D.LoadImage`
- **スレッド**: HTTP 受信は別スレッド or `async Task`。`Texture2D.LoadImage` はメインスレッド必須

### 性能ガイドライン

- **解像度**: 720p (1280x720) を上限。1080p は Quest 3 でも GC アロケーションが増える。fixed-cam-streamer 側は `setTargetResolution(960, 540)` 既定
- **FPS**: スマホ側 30fps 出力で十分。VR 側 90Hz とは独立
- **バッファ**: JPEG バイト列は `byte[]` プールで再利用。毎フレ `new byte[]` は禁止
- **Texture**: `Texture2D` は事前確保し `LoadImage` で上書き。新規作成しない（**かつ初期化必須** — 未初期化は Quest GPU で白ノイズ化する [unity_pitfalls.md](../.claude/memory/unity_pitfalls.md)）

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
