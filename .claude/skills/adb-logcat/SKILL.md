---
name: adb-logcat
description: Quest 3 / Android 実機 (Pixel/streamer 端末) の logcat を層別フィルタで取得。実機ビルドが起動しない / 黒画面 / クラッシュ / Meta XR 初期化失敗 / 配信側アプリ（fixed-cam-streamer）の挙動調査時に呼ぶ。Editor で再現しない実機固有の問題を切り分ける入り口。
---

# adb logcat 層別取得

実機 Quest 3 / Android 端末のログを層別タグでフィルタ取得する。Editor で再現しない問題はここから始める。

## ユーザーの意図 / 状況から層を選ぶ

| 状況 | 層 | 実行コマンド |
|---|---|---|
| 何の指定もない / Unity アプリの挙動 | `unity` | `adb logcat -d -s Unity:V CRASH:E AndroidRuntime:E -t 200` |
| パススルー出ない / トラッキング崩れ / OVR 初期化 | `xr` | `adb logcat -d -s OVRPlugin:V VrApi:V OpenXR:V XrLoader:V -t 300` |
| 配信側 (fixed-cam-streamer) アプリの挙動 | `streamer` | `adb logcat -d -s "FixedCamStreamer:V" "MjpegServer:V" "CameraController:V" -t 300` |
| クラッシュ追跡（IL2CPP / native も含む） | `crash` | `adb logcat -d -b crash -t 200` の後に `adb logcat -d -s "DEBUG:V" "AndroidRuntime:E" "libc:F" -t 200` |
| バッファクリア | `clear` | `adb logcat -c` |
| 任意のタグ指定 | (raw) | `adb logcat -d -s "<tag>:V" -t 300` |

`-d` は dump-and-exit（追従しない）。実機にライブ追従したいときは `-d` を外して Bash の `run_in_background: true` で起動。

## 端末選択

複数台つながっているなら先に `adb devices` を出して serial を確認：
- **Quest 3**: serial が大文字英数（`1WMHHA…` 形式）
- **Pixel 7a/Pro (streamer)**: serial が短い英数

複数なら `-s <serial>` で絞る。1台なら `-s` 不要。

## 出力フォーマット

logcat 生出力をそのまま貼らず、以下に整形して返す：

```
📱 端末: <model> (<serial>)
🔍 フィルタ: <タグ>
─────
[エラー件数] / [警告件数]
直近のエラー（最大 5 行）:
  <timestamp> <tag>: <message>
─────
```

エラー / 警告ゼロなら「クリーン」と一言。生ログ全文は折りたたむか省略。

## 関連スキル

- `streamer-android-build` — installDebug が落ちる時の adb 復旧手順
- `.claude/rules/troubleshooting.md` — どの層を疑うかの判定フロー
