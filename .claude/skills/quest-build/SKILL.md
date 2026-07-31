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

### 2.5 MCP が wedge して menu が届かないとき → batchmode で焼く（2026-07-31 実害・15 分溶かした）

上の「タイムアウト ≠ ビルド開始」は **menu が握り潰される**話だが、それとは別に
**MCP ブリッジ自体が死んで、どんなコマンドも Unity に届かない**状態がある。

**見分け方**（この 3 つが揃ったら wedge）:

1. `refresh_unity` / `read_console` / `run_tests` / `execute_menu_item` が**すべて** timeout（データ返却系が全滅）
2. `Library/ScriptAssemblies/*.dll` の mtime が **.cs の編集より古いまま数分動かない**
3. それでも **Unity 本体は健全**（`Get-Process Unity` の `Responding=True`）

この状態では待っても永久に進まない。**Editor を閉じて batchmode で焼く**のがいちばん速い。

```bash
# ① 閉じる前に未保存を確認 — タイトルバーに `*` が付いていなければ未保存の変更は無い
#    （Editor のメモリ側に未保存があると、閉じるときの保存で外部編集を上書きされる）
```
```powershell
Get-Process Unity | Where-Object { $_.MainWindowTitle -ne "" } | Select-Object Id, MainWindowTitle
```
```bash
# ② 閉じる（`*` が無いことを確認してから）。プロセスが消えるまで待つ
```
```bash
until ! tasklist //FI "PID eq <PID>" 2>/dev/null | grep -q <PID>; do sleep 3; done
```
```bash
# ③ batchmode で焼く。ログは必ずファイルへ（run_in_background 推奨・5〜10 分）
"/c/Program Files/Unity/Hub/Editor/2022.3.62f2/Editor/Unity.exe" -batchmode -quit \
  -projectPath "C:/Users/kouga/Projects/Unity/fixed-cam-vr" \
  -executeMethod FixedCamVr.EditorTools.BuildVariants.BuildFixedCam \
  -logFile "C:/Users/kouga/Projects/Unity/fixed-cam-vr/logs/build-batch-$(date +%H%M%S).log"
```

- **`-buildTarget` は付けない**。既に Android なら不要で、付けるとターゲット切替の再インポートで
  何倍も時間がかかる（付けるべきなのは desktop ビルドを挟んだ直後だけ）
- メソッド名は `FixedCamVr.EditorTools.BuildVariants.<BuildFixedCam|BuildTableDuo|BuildFixedCamRelease|…>`
- **コンパイルの成否はビルドログで見る**: `Tundra build success` の直後に `error CS` が無いこと。
  `warning CS` は既存分（`ShowRoomProxy.DestroyObject` 等）なので無視してよい
- 完了判定は `Builds/mawarimi.apk` の mtime 更新 + サイズ安定（上のポーリングと同じ）
- **終わったら Editor を開き直す**（ユーザーが使うもの）:
  `nohup "/c/Program Files/Unity/Hub/Editor/2022.3.62f2/Editor/Unity.exe" -projectPath "<repo>" > /dev/null 2>&1 &`

⚠ **Editor が起動したままでは batchmode は使えない**（同じプロジェクトを 2 つ開けない）。
逆に言えば、Editor を閉じる判断がこの手順の唯一のリスクなので、①の未保存確認を飛ばさないこと。

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

- **⚠ APK ビルドは「アクティブプラットフォーム = Android」が前提（2026-07-24 実害・HMD にシーンが出ない事故）**:
  desktop ビルド（2ed040a 以降）は active target を **Standalone のまま残す**。その状態で APK メニューを
  撃つと、クロスターゲット一発ビルド（BuildPlayer 内部切替）では **Oculus XR プラグインのマニフェスト注入が
  実行されず、`com.oculus.intent.category.VR` / `focusaware` の無い APK** が焼ける → Quest がアプリを
  **2D パネルとして起動**し、HMD にシーンが出ない（NGO 接続は正常に成立するのが紛らわしい）。
  BuildVariant にガード実装済み（Android 以外なら切替を開始して中断 → 完了後にメニュー再実行）。
  検品は `aapt dump xmltree <apk> AndroidManifest.xml | grep category` に
  `com.oculus.intent.category.VR` があること。ビルド後は active が Android のまま残るので通常は連続ビルド可。
  desktop ビルドを挟んだ直後だけこのガードに当たる（1 回目=切替開始・2 回目=本ビルド、の 2 段になる）
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
- [quest-capture](../quest-capture/SKILL.md) — **入れた後、実機の画を録って見た目を確かめる**（HMD 不要）
- `python tools/quest-fleet.py sync` — Quest が複数あるとき、mtime を比べて古い機だけへ配る
  （[quest_fleet_two_devices](../../memory/quest_fleet_two_devices.md)）
- [streamer-android-build](../streamer-android-build/SKILL.md) — 配信側スマホアプリのビルド（同じ adb の罠）
- [unity-status](../unity-status/SKILL.md) — ビルド前の Editor 状態確認
