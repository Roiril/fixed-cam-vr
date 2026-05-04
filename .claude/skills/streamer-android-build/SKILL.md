---
name: streamer-android-build
description: 姉妹リポ fixed-cam-streamer (Android Kotlin) を Android Studio 無しでビルド & 実機インストールする手順。Unity Hub 同梱の OpenJDK を JAVA_HOME に使うので追加インストール不要。スマホに USB デバッグ ON で接続してから使う。
---

# fixed-cam-streamer のビルド & インストール（Android Studio 不要）

姉妹リポ `C:/Users/kouga/Projects/Mobile/fixed-cam-streamer` の APK をビルドして接続中の Android 端末にインストールする。Android Studio が入っていなくても、**Unity Hub に同梱されている OpenJDK** を JAVA_HOME に指定すれば `gradlew installDebug` が通る。

## 前提

- スマホで **開発者オプション → USB デバッグ ON**
- USB ケーブル接続（無線でも可）
- `adb devices` で `device` 状態（`unauthorized` は端末側で許可ダイアログ承認）
  - adb 自体は SideQuest 同梱（`C:/Users/kouga/AppData/Local/Programs/SideQuest/.../platform-tools/adb.exe`）が PATH に通っているケースが多い

## 実行

```bash
adb devices  # 端末認識確認

export JAVA_HOME="/c/Program Files/Unity/Hub/Editor/2022.3.62f2/Editor/Data/PlaybackEngines/AndroidPlayer/OpenJDK"
export PATH="$JAVA_HOME/bin:$PATH"

cd "C:/Users/kouga/Projects/Mobile/fixed-cam-streamer"
./gradlew installDebug
```

成功すると：
```
> Task :app:installDebug
Installing APK 'app-debug.apk' on 'Pixel 7 Pro - 16' for :app:debug
Installed on 1 device.
BUILD SUCCESSFUL in <Ns>
```

インストール後、**スマホで FixedCam Streamer アプリを再起動**（installDebug は既存プロセスを kill するので、再度ユーザが配信開始ボタンを押す必要あり）。

## Unity バージョン違いへの対応

`Unity/Hub/Editor/<ver>/.../OpenJDK` の `<ver>` はインストール済み Unity に依存。複数あるなら任意の 2022.3+ を使えば良い：

```bash
ls "/c/Program Files/Unity/Hub/Editor/" | head
# → 2022.3.62f1, 2022.3.62f2, 6000.2.9f1 等
```

## 動作確認

ビルド後、スマホでアプリ起動 + 配信開始 → PC から：

```bash
curl -s http://<phone-ip>:8080/info  # JSON が返れば OK
curl -s -m 3 http://<phone-ip>:8080/video -o /dev/null -w "size=%{size_download}\n"  # 帯域確認
```

`/health` の `totalBytes / uptimeMs` で平均帯域 (Mbps) を計算できる。Quality 40 で 1080² @ 30fps なら **8〜12 Mbps** 程度が目安。

## 落とし穴

- `JAVA_HOME` 未設定だと `'java' command could be found in your PATH.` で即死。エラーメッセージを誤読しがちなので JAVA_HOME 設定を最初にやる
- 実機が `unauthorized` 状態だと `Installed on 0 devices` で sliently 失敗 → スマホの USB デバッグ許可ダイアログを必ず承認
- `gradlew` は repo ルート直下にある（`gradlew.bat` は Windows 用、Bash からは `./gradlew` でOK）
- 初回ビルドは数分かかる（Gradle が deps DL）。2 回目以降は 25 秒程度
