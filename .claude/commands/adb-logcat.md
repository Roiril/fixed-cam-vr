---
description: Quest 3 / Android 実機の adb logcat をフィルタ付きで取得（Unity / Meta XR / streamer 各層）
---

ユーザーから引数が来たら、それをタグ / フィルタとして使う。引数がなければ Unity アプリ層の代表ログを取る。

## 取得パターン

`$ARGUMENTS` の中身で分岐：

| 引数 | 想定 | 実行コマンド |
|---|---|---|
| (空) / `unity` | Unity アプリの Debug.Log / 例外 | `adb logcat -d -s Unity:V CRASH:E AndroidRuntime:E -t 200` |
| `xr` / `oculus` / `meta` | Meta XR / OpenXR のランタイム | `adb logcat -d -s OVRPlugin:V VrApi:V OpenXR:V XrLoader:V -t 300` |
| `streamer` | fixed-cam-streamer (姉妹リポ Android) | `adb logcat -d -s "FixedCamStreamer:V" "MjpegServer:V" "CameraController:V" -t 300` |
| `crash` | クラッシュ追跡（IL2CPP / native も含む） | `adb logcat -d -b crash -t 200; adb logcat -d -s "DEBUG:V" "AndroidRuntime:E" "libc:F" -t 200` |
| `clear` | バッファクリア | `adb logcat -c` |
| 任意の文字列 | ユーザー指定タグ | `adb logcat -d -s "<tag>:V" -t 300` |

`-d` は dump-and-exit（追従しない）。実機にライブ追従したいときは `-d` を外して `run_in_background: true` で起動する。

## 端末選択

`adb devices` を先に出して、複数台つながっていたら：
- **Quest 3**: serial が大文字英数（`1WMHHA…` 形式）
- **Pixel 7a/Pro (streamer)**: serial が短い英数

複数つながっていれば `-s <serial>` を付けて絞る。1台なら `-s` 不要。

## 出力フォーマット

logcat の生出力をそのまま貼らず、以下に整形して返す：

```
📱 端末: <model> (<serial>)
🔍 フィルタ: <タグ>
─────
[エラー件数] / [警告件数]
直近のエラー（最大 5 行）:
  <timestamp> <tag>: <message>
─────
全文は以下に折りたたみ:
<details>...</details>
```

エラー / 警告ゼロなら「クリーン」と一言。

## 関連

- `.claude/skills/streamer-android-build/SKILL.md` — installDebug が落ちる時の adb 復旧手順
- `.agent/rules/troubleshooting.md` — どの層を疑うかの判定フロー
