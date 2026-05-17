# 実機検証ワークフロー（ユーザー確定方針）

ユーザー明示の優先方針（2026-05-17）：**APK ビルド → install → Quest 上でアプリ探し のループは遅すぎる。Quest を Link 接続 → Unity Editor で Play → シュビーが MCP `read_console` でログ取得して検証、が最良。**

## なぜ build/install ループを避けるか（実体験）

- IL2CPP Android ビルドは ~20 分。C ドライブ枯渇で `mergeDebugJniLibFolders` 失敗の前例あり（容量に注意）
- Pixel/Quest とも `adb install` が `failed to read copy response` で詰まる（`adb kill-server` 復旧が要る）
- 既定 APK は `label=''`（URP テンプレ名 `com.UnityTechnologies...urpblank`）で Quest メニューから見つけにくい
- adb 起動だと没入アプリのフォーカスを取り損ねる

## 推奨ループ

1. Quest を Link / Air Link で PCVR 接続（ユーザー操作）
2. Unity MCP で Play 開始（`manage_editor`）
3. `read_console` で `[MJPEG]` / `[CameraStream]` / `[HmdTrace]` を取得し検証
4. 修正 → ドメインリロード → 再 Play で即イテレーション（ビルド不要）

## Editor Play で再現できる/できない

- **できる**: HMD 着脱の suspend/resume 系（`OnApplicationPause`/`OnApplicationFocus` 実装のものは Editor フォーカス喪失 or Link でのヘッドセット脱着で発火）。MJPEG 受信・lag-detect・ゼロコピー配信の機能検証
- **注意/できない寄り**: OVR 専用 API（`OVR*`）の挙動は Meta XR Simulator が要る場合あり（[meta-xr.md] 参照）。実機固有のクラッシュ/熱/帯域は実機ビルドでしか出ない

## ビルド検証が要るケース（例外）

IL2CPP 固有のクラッシュ、実機 GPU 白ノイズ、Quest 熱/電池、Link で出ない遅延 — これらだけ実機ビルドに回す。それ以外は Play で潰す。
