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

**⚠ タイムアウト ≠ ビルド開始（2026-07-10 実害 ×2）**: `execute_menu_item` の timeout は
「ビルドが走っている」と「コンパイル/ドメインリロード中で **menu が握り潰され何も始まっていない**」を
区別できない。直前に .cs を編集した後は特に後者になりやすい。→ **menu 実行の直前に
`refresh_unity(mode=if_dirty, wait_for_ready=true)` で idle を確認**し、それでもポーリングが
TIMEOUT したら「ビルド失敗」ではなく「未開始」を疑って Editor.log / `Temp/` の更新を確認 →
idle 確認後にもう一度 menu を撃つ（2回目で normally 通る）。
また **desktop ビルド（Standalone）の完成判定は exe の mtime を見ない**こと —
Unity のインクリメンタルビルドはランチャー stub（TableDuo.exe）を書き換えない。
**センチネルは変更内容で使い分ける（2026-07-24 偽 TIMEOUT の実害）**:
シーン変更あり → `Builds/tableduo-desktop/TableDuo_Data/level0` / **スクリプトのみの変更 →
`TableDuo_Data/Managed/TableDuoVr.Net.dll`**（level0 は書き換わらないので level0 ポーリングは
偽陰性になる）。確実なのは Editor.log の `[BuildVariants] DESKTOP OK` 行の増加を見ること。
起動中の TableDuo.exe（PC ホスト）はビルド前に必ず kill（exe ロックで上書き失敗する）。
失敗ログには「scripts are compiling」の他に **「A domain reload is pending」** 変種もある
（refresh/テスト直後に menu を撃った時）— editor_state の `is_domain_reload_pending=false` を
確認してから撃ち直せば通る。MCP がタイムアウトした menu コマンドは**キューに残って
reload 完了後に再実行されることがある**（＝知らない間に 2 回ビルドが走る。OK 行の重複で判別）。

**⚠ desktop ビルドが「DESKTOP 失敗: result=Unknown errors=0」で落ちる（2026-07-24 実害×2・コード修正済み）**:
Editor.log に `Error building Player because scripts are compiling` が出ていたらこれ。旧
BuildTableDuoDesktop が手動 `SwitchActiveBuildTarget`（Android→Standalone）で script 再コンパイルを
予約した直後に BuildPlayer を呼ぶ競合で、**finally が Android へ戻すため再実行しても無限に同じ失敗を
再現**していた（2ed040a で APK 経路と同じ「切替は BuildPlayer 内部に任せる」形へ修正済み）。
教訓: **Editor メニューからのビルドで platform を手動切替してはいけない**（`BuildPlayerOptions.target`
に任せる）。ビルド失敗の診断は必ず Editor.log の `BuildVariants` 前後を読む — errors=0 の失敗は
コンパイル競合かシーン欠落で、リトライではなく原因の除去が要る。修正後の active target は
Standalone のまま残る（Library 管理・git 差分なし・次の APK ビルドが自分で Android へ切替する）。

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

## インストール後の起動の罠（TableDuo 実機運用で全部踏んだ・2026-07-06）

- **スリープ中（近接センサー OFF）だと `am start` が黙って失敗**（エラーなし・pid 立たず）→ `adb shell input keyevent KEYCODE_WAKEUP` か HMD を被る
- **USB 接続中の Quest Link ダイアログが起動をブロック** → `adb shell am force-stop com.oculus.systemux`
- install が「0 files pushed」で無言失敗することがある → リトライで通る
- TableDuo の起動フロー一式は `tools/tableduo-pc-host.ps1`（wake・ダイアログ潰し内包） / [.claude/memory/table_duo_pc_host_and_wiretap.md](../../memory/table_duo_pc_host_and_wiretap.md)

## 関連

- [adb-logcat](../adb-logcat/SKILL.md) — インストール後の実機ログ確認
- [streamer-android-build](../streamer-android-build/SKILL.md) — 配信側スマホアプリのビルド（同じ adb の罠）
- [unity-status](../unity-status/SKILL.md) — ビルド前の Editor 状態確認
