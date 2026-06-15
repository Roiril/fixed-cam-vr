---
name: quest_adb_auth
description: Quest が adb で unauthorized のまま許可ダイアログが出ない時の切り分け（中古機=別アカウント=初期化が原因だった実例）
metadata: 
  node_type: memory
  type: reference
  originSessionId: 5d891bfb-921e-4220-a39c-18714e9babd1
---

Quest を adb 接続して `unauthorized` のまま「USB デバッグを許可」ダイアログが**一切出ない**時の切り分け。2026-06-15 に2台目 Quest（中古・前オーナーのアカウント）で延々ハマった実例。

## 結論（この時の真因）
**2台目が前オーナーの Meta アカウントのままだった。** adb 認証はアカウントが「認証済み開発者組織」に属している必要があり、別アカウントだと dev モードのトグルは押せても**ダイアログ自体が出ない**。→ **ファクトリーリセット（電源OFF→電源+音量(−)長押し→Factory Reset→Yes）して自分のアカウントで初期設定**したら一発で通った。

## 切り分け順（PC からできること）
1. `adb devices` が `unauthorized` → ダイアログ承認待ち。ヘッドセット被って Home で承認（ダイアログは**被ってる時しか見えない**・通知に出ることも）
2. 出ない時の PC 側手当て（全部やったが効かず＝アカウント問題だった）:
   - `adb kill-server; adb start-server` で要求を投げ直す / `adb -s <serial> shell echo` で能動的に叩く
   - PC の鍵リセット: `%USERPROFILE%\.android\adbkey(.pub)` をリネーム→ kill/start-server（新規鍵で完全初見化）
   - adb 一本化: Unity 同梱 adb と SideQuest 同梱 adb の**バージョン違いがサーバを奪い合う**と不安定。`Get-Process adb` で確認し全 kill→1本で start
3. Quest 側（公式・[developers.meta.com](https://developers.meta.com/horizon/documentation/native/android/mobile-device-setup/)）:
   - 開発者モード ON は**ヘッドセット内では不可**。スマホ Meta Horizon アプリ→デバイス→**該当ヘッドセット**→設定→開発者モード ON（Wi-Fi 接続必須・フラグ同期）
   - ヘッドセット 設定→開発者 に「USB デバッグ」トグルは**無い**のが正常。`MTP Notification` ON ＋ 接続時ポップアップ承認のみ
   - dev ON 直後は**再起動**で adbd が dev で上がり直す

## 付随の罠
- Quest Link が接続画面で掴んで Home に戻れないループ → PC の `OVRServer_x64`/`OculusDash` を kill（OVRService 再起動は要管理者）or ヘッドセットで Quest Link OFF。今回の作業（APK インストール）に **Link は不要**
- PowerShell の `adb ... > file.png` は**バイナリ破損**（UTF-16化）。device 保存→`adb pull` で取る
- git-bash 経由の PowerShell ワンライナーは `$_` が壊れる → PowerShell ツールで直接実行
