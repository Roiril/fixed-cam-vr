---
name: quest-build-and-camera-ip
description: Quest 実機体験の2大ハマり — ビルドメニューの「即success=未実行」罠 と Phone*.asset host の DHCP ズレ
metadata: 
  node_type: memory
  type: project
  originSessionId: 90e260bb-ab39-4fcf-9bf8-3f96e926304c
---

廻リ視（FixedCam）を Quest 実機で体験させる時に 2026-06-16 に踏んだ罠2つ。両方再発する。

## 1. ビルドメニューの戻り値で実行有無を判定する

`mcp__UnityMCP__execute_menu_item "Tools/FixedCamVr/Build FixedCam APK（廻リ視）"` の戻り：

- **即 `success:true` が返ったら → ビルドは走っていない**（Editor がメニューを叩いただけ／前回ビルドの `[BuildVariants] OK` ログが残っているのを誤読しがち）。APK の mtime は変わらない。
- **`Timeout receiving Unity response` が返ったら → 実際にビルドが走っている**（数分ブロックするため必ずタイムアウトする）。これが正常系。

確実化の手順：外部でアセット/コードを変えたら必ず `refresh_unity(mode=force, scope=assets, wait_for_ready=true)` で取り込ませてからビルド。完成判定は `Builds/mawarimi.apk` の **mtime が増えた AND サイズ安定**で見る（サイズは偶然同じこともあるので mtime で見る）。コンソールの "OK ... 1064MB" の MB はビルド前サイズ表記で実 APK（圧縮後 ≈84MB）と一致しないので当てにしない。

## 2. Phone*.asset の host は DHCP で毎回ズレる

Quest が接続するカメラ IP は **show.json でも卓でもなく `Assets/Settings/Cameras/Phone0X.asset` の host**（APK に焼き込まれる）。スマホの DHCP IP が変わると古い host へ繋ぎに行き、logcat に
`[MJPEG] disconnected: mono-io-layer-error (113). retry in 30s`（errno 113 = EHOSTUNREACH）。**Web 卓は PC 経由proxyなので全台映るが Quest は直 pull なので不達**＝「Web では見えるが Quest で黒」の典型。

直し方：卓 state（`curl http://<PC>:8099/state?rev=-1`）の `cameras[].host` が現在の実機 IP の真値。`Phone0X.asset` の host をそれに合わせて編集 → refresh → 再ビルド → 再インストール。**host はローカル値＝コミットしない**（git-workflow.md）。恒久対策は streamer 側を固定IP化 or DHCP 予約。

関連: [[web_compositor]]（卓サーバ /command stopCue・setPost）、quest-build スキル、rules/streaming.md
