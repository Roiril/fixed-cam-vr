---
name: troubleshooting
description: 「うまく動かない」時に層を切り分けるための診断フロー。Unity / 配信側 / ネットワーク / Meta XR / ビルド の責任境界マップ
globs:
  - "Assets/**"
  - ".claude/**"
---

# 層別トラブルシュートガイド

fixed-cam-vr は **配信側 Android アプリ + ネットワーク + Unity Editor + Meta XR SDK + Quest 実機** が直列に並ぶ構成のため、症状から責任層を即断するのが難しい。**「層ごとに最小再現を持つ」** ことで責任境界を明確にする。

## 責任マップ

| 層 | 主な失敗例 | 最小再現 / 切り分けツール | 関連ドキュメント |
|---|---|---|---|
| **配信側 (Android/Kotlin)** | カメラ権限なし・MJPEG が出ない・解像度違い | ブラウザで `http://<phone>:8080/` 直接確認 / `curl /info` | [streaming.md](streaming.md), [streamer-android-build SKILL](../../.claude/skills/streamer-android-build/SKILL.md) |
| **ネットワーク** | 別 LAN・ポート閉鎖・Wi-Fi 帯域不足 | PC から `curl -m 3 /health` / `ping <phone>` | [streaming.md `/health`](streaming.md) |
| **Unity スクリプト** | デコード失敗・GC スパイク・Texture 白ノイズ | Editor Console + Profiler / [streaming-offline-test SKILL](../../.claude/skills/streaming-offline-test/SKILL.md) | [unity_pitfalls.md](../../.claude/memory/unity_pitfalls.md) |
| **Unity Editor / MCP** | コンパイル失敗・MCP 切断・prefab 再生時に値リセット | `unity-status` スキル / `read_console` | [mcp-unity.md](mcp-unity.md), [unity-prefab-fields SKILL](../../.claude/skills/unity-prefab-fields/SKILL.md) |
| **Meta XR / OpenXR** | パススルー出ない・トラッキング崩れ・90Hz 出ない | `adb-logcat` スキル (xr) / OVR Metrics Tool | [meta-xr.md](meta-xr.md) |
| **ビルド / 実機** | APK 起動しない・即落ち・黒画面 | `adb-logcat` スキル (unity) / `adb-logcat` スキル (crash) | [unity-vr.md](unity-vr.md) |

## 症状から逆引き

### 「映像が出ない」

順に確認：

1. **配信側を疑う** — ブラウザで `http://<phone>:8080/` を開く。HTML が出るか？ → 出ないなら streamer アプリ未起動 or ポート違い
2. **ネットワークを疑う** — PC から `curl -m 3 http://<phone>:8080/health`。タイムアウトなら同一 LAN にいない / FW ブロック
3. **Unity スクリプトを疑う** — `streaming-offline-test` で fake server に向け Editor 単体検証。ここで出るなら実機構成側の問題
4. **MCP / コンパイル** — `unity-status` スキル でエラー有無
5. **Meta XR** — Editor で出るが Quest で出ない → ビルド層 (`adb-logcat` スキル (unity))

### 「映像はカクつく / 遅い」

1. `curl /health` で配信側の `fps` / `latestFrameAgeMs` 確認 → 配信側 stall 判別
2. `recv_fps / phone_fps` が 0.7 未満なら lag detect 発火（自動再接続）— [streaming.md](streaming.md) 参照
3. それでもダメなら Wi-Fi 帯域 / 解像度 / JPEG quality を [streaming.md 性能ガイドライン](streaming.md) で見直し

### 「Quest で起動しない / 黒画面」

1. `adb devices` で実機認識
2. `adb-logcat` スキル (crash) で native クラッシュ確認
3. `adb-logcat` スキル (unity) で `Unity` タグの初期化エラー
4. `adb-logcat` スキル (xr) で OVRPlugin / VrApi 初期化失敗

### 「コンパイルが通らない / Unity 重い」

1. `unity-status` スキル — 接続・コンパイル状態
2. `read_console types=["error"]` — エラー全文
3. `manage_editor action=ping` で応答性確認 — [mcp-unity.md](mcp-unity.md)

### 「prefab に保存した値が再生時に変わる / 0 になる」

→ [unity-prefab-fields SKILL](../../.claude/skills/unity-prefab-fields/SKILL.md) を読む。SerializeField 追加直後の prefab YAML 未反映パターン。

### 「Web で演出 ON にしても Quest にオーバーレイが出ない」（2026-06-17 実害）

1. **cue 未保存が最多**: 演出 ON は `cue_<camId>` を発火するだけ。合成素材+マスクを選び 💾 cue 保存していないと show.json に cue が無く、Quest は「unknown cue id」で何も出せない（Web 側は未保存なら警告を出すようにした）
2. **動画だけ出ない（画像は出る）**: ログに `E/NuCachedSource2: source returned error -1`。Android ネイティブ VideoPlayer が Python http.server(HTTP/1.0) からの HTTP ストリーミングを扱えない。→ Quest は UnityWebRequest でローカル DL してから `file://` 再生する（[`ScreenOverlayController.GetLocalVideoUrlAsync`](../Assets/Scripts/Streaming/ScreenOverlayController.cs)）。`[ScreenOverlay] video cached` ログが出れば DL 成功。画像/マスクは UnityWebRequest なので直 URL で出る（=切り分けに使える）
3. ファイル名のスペース/括弧は URL を percent-encode（Web の cue 保存で対応済み）

### 「コントローラのボタンが意図と違う / 左右どちらも同じ操作になる」（2026-06-17 実害）

- **`OVRInput.GetDown(Button.One/Two)` はコントローラ未指定だと両手から拾う**。`Button.One`=A(右)**または**X(左)、`Button.Two`=B(右)**または**Y(左)。なので左の X/Y までカメラ Next/Prev に化けていた
- → 用途ごとに `OVRInput.Controller.RTouch` / `LTouch` を明示する（[`OvrControllerBridge`](../Assets/Scripts/OvrBridge/OvrControllerBridge.cs)：カメラ切替=右手、anchor/HUD トグル=左手）

## 鉄則

- **Unity の中で全部見ようとしない** — 配信側は配信側のツール（ブラウザ / curl）で先に潰す
- **実機ビルドは最後** — Editor で再現できないことだけを実機に持ち込む
- **fake server で Unity 単体検証** — [streaming-offline-test](../../.claude/skills/streaming-offline-test/SKILL.md) が用意してある
- **ログは層タグで切る** — `[CameraStream]` `[MJPEG]` (Unity) / `FixedCamStreamer` (Android) / `OVRPlugin` (XR) — タグでフィルタすれば層がすぐ分かる

## 対応スキル

| スキル | 用途 |
|---|---|
| `unity-status` | Unity MCP / Editor / コンソールエラー一括 |
| `adb-logcat` | 実機 Quest / Pixel のログ取得（`unity` / `xr` / `streamer` / `crash` フィルタ） |
| `unity-mcp` | MCP 接続診断と再接続 |
| `streaming-offline-test` | スマホ無しで Unity の MJPEG パイプライン検証 |
| `quest-build` | 廻リ視 / TableDuo の APK を Quest にビルド & インストール |
| `streamer-android-build` | 姉妹リポ APK ビルド & 実機インストール |
| `unity-prefab-fields` | prefab YAML / SerializeField 不整合の修正 |
