---
name: quest-build
description: 廻リ視 / TableDuo / MyCobotHand の APK を Unity CLI で焼いて Quest に入れる。「ビルドして」「実機に入れて」「APK 作って」で呼ぶ。3 アプリを別パッケージで焼き分ける仕組み、Android マニフェスト注入の罠、adb インストールと起動の罠。
---

# Quest へのビルド & インストール

廻リ視の現行 Player は事前同梱のみ（2026-09-22）。この文書中の古いライブ反映・設定キャッシュの記録は現行 Player に適用しない。
export は使用中の素材を検証し manifest を作る。Unity のビルド前検査は本文と素材のハッシュ不一致で停止する。
成功したビルドの `Builds/mawarimi.apk.content.json` も残す。運用卓は APK 自体のハッシュと buildGuid を使って導入版を照合する。
記録は `BuildVariants` の `BuildPlayer` 成功確定後に作る。`IPostprocessBuildWithReport` 内では `result` が未確定のため作らない。
素材を更新したら再ビルドして両 Quest に入れる。ビルド後の設定変更は博士 UI の言語とホラー軽減だけ。

**`tools\unity.ps1` が Unity CLI を叩く唯一の場所。** ここ以外から `unity.exe` を呼ばない。

```powershell
.\tools\unity.ps1 doctor                    # 前提（Editor / Android モジュール / py / adb）
.\tools\unity.ps1 build fixedcam            # 廻リ視 → Builds/mawarimi.apk
.\tools\unity.ps1 build fixedcam -Release   # 提出用（Development なし）
.\tools\unity.ps1 build tableduo
.\tools\unity.ps1 build mycobothand
.\tools\unity.ps1 build tableduo-desktop    # 実機ゼロの L0 検証用 Standalone
```

**焼き直し待ちは build が落とす**（2026-08-09〜。`fixedcam` のみ）。演出は**シーンに焼かれた
GameObject**、HMD の日本語は**静的ベイクのアトラス**なので、コードを書いただけでは APK に入らない。
どちらも実害があり、どちらも「ルールに書いてあるのに誰も見ていない」形で起きたので、見る側を機械へ移した。

```
✗ 焼き直していないものがある。このまま焼いても APK には入らない
    .\tools\unity.ps1 menu scene
```

言われたコマンドを打てばよい（どちらも冪等）。判定は `unity.ps1` の `$Menus` の `Src`（入力の .cs）と
`Out`（焼いたもの）の mtime 比較。**`menu scene` は毎回 2000 行規模の差分を出す**が、
fileID の振り直しで、このリポジトリでは以前からそうなっている。

古いまま焼くと決めたときだけ `-Force`（`git checkout` 直後は mtime が揃うので誤検知しうる）。

Unity の**手動 Build Settings は使わない**。手動だと 3 アプリが同名・同パッケージ ID になり、
Quest 上で共存できなくなる。

### 描画メニューで出力先を変える場合

`menu comms-revision -Set out=...` は実行先だけを変える。wrapperの更新確認先は
`$Menus.Out`の固定パスのままなので、本体exit 0でも「出力が更新されていない」となる。
別の出力先へ描画するときは次を使い、指定先のCSVと画像を検証する。

```powershell
.\tools\unity.ps1 menu raw:FixedCamVr.Streaming.EditorTools.CommsRevisionPreview.Run -Set out=Logs/comms-check
py -3.11 tools/render-comms-possession-preview.py --render Logs/comms-check
```

既定先を使う場合は従来の`menu comms-revision`のままでよい。
固定先のmtimeだけを更新して判定を通さない。（2026-09-21 実測）

## なぜ CLI なのか（2026-08-08 に切り替え）

旧経路は MCP の `execute_menu_item` で、**構造的に不利だった**。

| | 旧（MCP + GUI Editor） | 新（Unity CLI） |
|---|---|---|
| 実行 | ほぼ確実に Timeout。失敗と区別できない | 同期。exit code が返る |
| 完了判定 | APK の mtime を 240 回ポーリング | 出力の更新を 1 回見る |
| 1 回目 | **必ず中断**（下記ガード）→ メニューを 2 回撃つ | 1 回で通る |
| ダイアログ | モーダルで無言停止（35 分溶かした実害） | `-batchmode` なので出ない |
| 失敗の理由 | Editor.log を掘る | `Logs/build-<target>-*.log` |

**1 回目で通る理由**: `BuildVariant` は「アクティブプラットフォームが Android でなければ、
切替を開始して中断する」（[BuildVariants.cs:128](../../../Assets/Editor/BuildVariants.cs)）。
GUI からだと切替待ちで 1 回落ちるので 2 回撃つ必要があった。
CLI の `unity build --target Android` は **`-buildTarget Android` を張ってから `-executeMethod` を呼ぶ**ので、
このガードを 1 回目で通過する。

## 3 アプリ分離の仕組み

`BuildVariants.BuildVariant()` がビルド時だけ差し替える（終了後 `finally` で必ず復元 →
ProjectSettings に差分を残さない）。

| | 廻リ視 (FixedCam) | TableDuo | MyCobotHand |
|---|---|---|---|
| productName | 廻リ視 | TableDuo | ロボットハンド操作VR |
| パッケージ ID | `com.roiril.mawarimi` | `com.roiril.tableduo` | `com.mycobot.handteleop` |
| シーン | `Assets/Scenes/Main.unity` | `Assets/TableDuo/Scenes/TableDuoMain.unity` | `Assets/MyCobotHand/Scenes/HandTeleop.unity` |
| 出力 | `Builds/mawarimi.apk` | `Builds/tableduo.apk` | `Builds/mycobothand-dev.apk` |

- `BuildPlayerOptions.scenes` に**そのアプリのシーンだけ**渡すので、もう片方は APK に入らない
- パッケージ ID が別 = Quest 上で独立したアプリとして並存する（同 ID だと上書き）
- ⚠ **MyCobotHand だけ C# 側の命名が非対称**（`BuildMyCobotHand` が Development なし、
  `BuildMyCobotHandDev` があり）。`unity.ps1` が `-Release` の意味を揃えるため入れ替えている

⚠ **2 ビルド同時起動は厳禁**（productName / ID を一時 swap するので、片方が他方の ID で焼ける）。
`unity.ps1` は Editor がプロジェクトを開いていたら止まる（`Temp/UnityLockfile`）。

## 事前確認（オペレータ卓に繋ぐ場合のみ）

Quest 単体起動で卓（cue 発火・カメラ固定・ポスト FX）を効かせるなら、ビルド前に
`Assets/Settings/ShowServer.asset` の `host` を **PC の LAN IP** にする
（Editor+Link なら `127.0.0.1` で可だが、Quest 単体は PC を見つけられない）。

```powershell
(Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -like "192.168.*" }).IPAddress
```

`host` はローカル値なので**コミットしない**（`rules/git-workflow.md`）。

## インストール（adb）

adb が PATH に無くても **Android モジュール同梱のものが使える**（`doctor` が場所を出す）。

```
adb devices                          # unauthorized は HMD 内で許可
adb -s <serial> install -r --no-streaming "Builds\mawarimi.apk"
adb -s <serial> shell dumpsys package com.roiril.mawarimi | Select-String "lastUpdateTime"
```

- **`--no-streaming` は必須級**: Quest / Pixel は streaming install が固まることがある
- 複数台あるときは `-s <serial>` で明示。配るのは `py -3.11 tools/quest-fleet.py sync`
  （mtime を比べて古い機だけへ配る）
- `lastUpdateTime` が今の時刻 = 新ビルドが入った証拠
- ⚠ **用が済んだら寝かせる**: `py -3.11 tools/quest-fleet.py sleep <serial>`。
  Quest は起きたまま置くと何もしなくても減る（2026-08-01 ユーザー指摘）

## 落とし穴（CLI でも消えない）

- **⚠ APK は「アクティブ = Android」でしか正しく焼けない（2026-07-24 実害・HMD にシーンが出ない）**:
  Standalone アクティブのままのクロスターゲット一発ビルドは **Oculus XR プラグインのマニフェスト注入が
  走らず、`com.oculus.intent.category.VR` / `focusaware` の無い APK** が焼ける →
  Quest が **2D パネルとして起動**する（NGO 接続は正常に成立するので紛らわしい）。
  検品:
  ```
  aapt dump xmltree Builds\mawarimi.apk AndroidManifest.xml | Select-String category
  ```
  `com.oculus.intent.category.VR` があること。**desktop ビルドを挟んだ直後は特に確かめる**
- **単一ファイルの mtime で完成を判定しない**: Standalone の差分ビルドはランチャー stub
  （`TableDuo.exe`）を書き換えない（2026-07-24 偽 TIMEOUT の実害）。
  `unity.ps1` は**出力フォルダ全体の最新**で見ている
- **ビルド失敗の診断は `Logs/build-<target>-*.log`**。`errors=0` の失敗はコンパイル競合かシーン欠落で、
  リトライではなく原因の除去が要る。`Tundra build success` の直後に `error CS` が無いことを見る
  （`warning CS` は既存分なので無視してよい）
- `Builds/` は gitignore 対象

## インストール後の起動の罠

- **スリープ中だと `am start` が黙って失敗**（エラーなし・pid 立たず）→
  `adb shell input keyevent KEYCODE_WAKEUP` か HMD を被る
- **USB 接続中の Quest Link ダイアログが起動をブロック** → `adb shell am force-stop com.oculus.systemux`
- install が「0 files pushed」で無言失敗することがある → リトライで通る
- TableDuo の起動フロー一式は `tools/tableduo-pc-host.ps1`（wake・ダイアログ潰し内包）

## 関連

- `.\tools\unity.ps1 menu` — **ビルド以外の Editor 機能はすべてここから**（引数なしで一覧）
- [adb-logcat](../adb-logcat/SKILL.md) — 実機ログ
- [quest-capture](../quest-capture/SKILL.md) — **入れた後、画を録って見た目を確かめる**（HMD 不要）
- [work-round](../work-round/SKILL.md) — 廻リ視の作りこみ 1 周（この skill はその中の 1 手）
- `rules/parallel-projects.md` — 3 アプリ同居の干渉防止
