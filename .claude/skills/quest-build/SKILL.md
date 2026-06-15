---
name: quest-build
description: 廻リ視（FixedCam）/ TableDuo の APK を Quest にビルド & インストールする。BuildVariants メニューで productName・パッケージ ID・含めるシーンを切り替えて 2 アプリを別々に出す仕組み、MCP メニュー実行のタイムアウト挙動、APK 完成のポーリング、adb --no-streaming インストールまで。「Quest にビルドして」「実機に入れて」「APK 作って」で呼ぶ。
---

# Quest へのビルド & インストール（廻リ視 / TableDuo）

Unity の **手動 Build Settings は使わない**。専用メニュー [BuildVariants.cs](../../../Assets/Editor/BuildVariants.cs) 経由が唯一の正。手動だと 2 アプリが同名・同パッケージ ID になって Quest 上で共存できなくなる。

## 2 アプリ分離の仕組み（なぜメニューが必要か）

`BuildVariants.BuildVariant()` がビルド時だけ以下を切り替える（終了後 `finally` で必ず復元 → ProjectSettings に差分を残さない）：

| | 廻リ視 (FixedCam) | TableDuo (Hand) |
|---|---|---|
| productName（Quest の表示名） | 廻リ視 | TableDuo |
| パッケージ ID | `com.roiril.mawarimi` | `com.roiril.tableduo` |
| 含めるシーン | `Assets/Scenes/Main.unity` | `Assets/TableDuo/Scenes/TableDuoMain.unity` |
| 出力 | `Builds/mawarimi.apk` | `Builds/tableduo.apk` |

- `productName` = Android のアプリラベル（ランチャー表示名）
- `BuildPlayerOptions.scenes` に **そのアプリのシーンだけ**渡すので、もう片方は APK に入らない（asmdef `FixedCamVr.*` / `TableDuoVr.*` は相互参照禁止なのでシーンを絞れば綺麗に分離）
- パッケージ ID が別 = Quest 上で独立 2 アプリとして共存（同 ID だと上書きになる）

## メニュー一覧

| メニュー | 出力 | 用途 |
|---|---|---|
| `Tools/FixedCamVr/Build FixedCam APK（廻リ視）` | `mawarimi.apk` | 廻リ視・Development（logcat 可） |
| `Tools/FixedCamVr/Build TableDuo APK` | `tableduo.apk` | TableDuo・Development |
| `Tools/FixedCamVr/Build FixedCam APK（廻リ視・Release）` | `mawarimi-release.apk` | 提出・配布用（Development なし） |
| `Tools/FixedCamVr/Build TableDuo APK（Release）` | `tableduo-release.apk` | 同上 |

## 手順

### 0. 事前確認（オペレータ卓に繋ぐ場合のみ）

Quest 単体起動でウェブの卓（cue 発火・カメラ固定・ポスト FX）を効かせたいなら、ビルド前に
[`Assets/Settings/ShowServer.asset`](../../../Assets/Settings/ShowServer.asset) の `host` を **PC の LAN IP** にする
（Editor+Link なら `127.0.0.1` で可だが、Quest 単体は PC を見つけられないので LAN IP 必須）。

```
# PC の LAN IP を調べる（PowerShell）
(Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -like "192.168.*" }).IPAddress
```

MCP で書き換え（host はローカル値なのでコミットしない — git-workflow.md）:
`manage_scriptable_object modify target={path:"Assets/Settings/ShowServer.asset"} patches=[{path:"host", value:"192.168.x.x"}]`

### 1. ビルド実行

`mcp__UnityMCP__execute_menu_item menu_path="Tools/FixedCamVr/Build FixedCam APK（廻リ視）"`

**⚠ 重要な挙動**: ビルドは数分かかるため `execute_menu_item` は**ほぼ確実に "Timeout receiving Unity response" を返す**。
これは**失敗ではない** — Unity 側ではビルドが走り続けている。タイムアウトしたら APK ファイルの更新を
ポーリングして完成を待つ（下記）。

### 2. 完成をポーリング（Bash, run_in_background 推奨）

`mawarimi.apk` の mtime が変わり、かつサイズが安定したら完成：

```bash
cd "C:/Users/kouga/Projects/Unity/fixed-cam-vr"
before=$(stat -c %Y Builds/mawarimi.apk 2>/dev/null || echo 0)
for i in $(seq 1 240); do
  sleep 5
  newest=$(stat -c %Y Builds/mawarimi.apk 2>/dev/null || echo 0)
  if [ "$newest" != "$before" ] && [ "$newest" != "0" ]; then
    s1=$(stat -c %s Builds/mawarimi.apk); sleep 6; s2=$(stat -c %s Builds/mawarimi.apk)
    if [ "$s1" = "$s2" ]; then echo "BUILD DONE ($s2 bytes)"; exit 0; fi
  fi
done
echo "TIMEOUT"; exit 1
```

代替: `read_console filter_text="[BuildVariants]"` に `OK:` ログが出れば成功（MCP が生きていれば）。

### 3. インストール（adb）

```
adb devices                          # device 状態を確認（unauthorized は HMD 内で許可）
adb -s <serial> install -r --no-streaming "C:\Users\kouga\Projects\Unity\fixed-cam-vr\Builds\mawarimi.apk"
adb -s <serial> shell dumpsys package com.roiril.mawarimi | Select-String "lastUpdateTime"  # 今の時刻なら成功
```

- **`--no-streaming` 必須級**: Quest/Pixel は streaming install が固まることがある（streamer-android-build スキルと同じ罠）
- 複数台繋がっている時は `-s <serial>` で明示（`adb devices` の左列）
- `lastUpdateTime` が今の時刻 = 確実に新ビルドが入った証拠

## 落とし穴

- **手動 Build Settings を使わない** — Main も TableDuoMain も同名・同 ID になり共存不可
- `execute_menu_item` のタイムアウトを失敗と誤認しない（バックグラウンドでビルド継続中）
- Unity がドメインリロード/コンパイル中はメニューが動かない → `unity-status` で Ready 確認してから
- ShowServer.asset / Phone*.asset の host はローカル値、**コミットしない**（git-workflow.md のユーザー所有ファイル）
- `Builds/` は gitignore 対象
- もう 1 台の Quest が `unauthorized` の時は、その HMD 内で「USB デバッグを許可」を承認するまで入らない

## 関連

- [adb-logcat](../adb-logcat/SKILL.md) — インストール後の実機ログ確認
- [streamer-android-build](../streamer-android-build/SKILL.md) — 配信側スマホアプリのビルド（同じ adb の罠）
- [unity-status](../unity-status/SKILL.md) — ビルド前の Editor 状態確認
