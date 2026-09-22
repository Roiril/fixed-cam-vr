---
name: onsite-day-ops
description: 当日運用の道具（onsite.py / 当日パネル / 自動起動）と、点検が実際に暴いた 4 つ。PC を触らない経路の設計
metadata: 
  node_type: memory
  type: project
  originSessionId: aa629b9d-978a-41e5-b9f1-d22062359145
  modified: 2026-09-04T13:55:01.706Z
---

# 当日の運用を触る前に

## 現行の導線（2026-09-22）

演出と接続は別々に確認する。`/ops/content` は準備素材 → StreamingAssets → APK → 各 Quest を照合する。
`rev` の一致は証拠にしない。本文と素材の SHA256、成功した APK の buildGuid を使う。
Player は演出のライブ更新と旧キャッシュを受けない。変更時は素材をそろえて再ビルドし、両 Quest に入れる。
通常サーバは演出変更の POST を拒否。事前編集のときだけ `FIXEDCAM_AUTHORING=1` を使う。
博士タブレットは Quest の `:8090` に直接接続。卓は設定を中継しない。
`/ops/status.tablets` で待受・ページの応答・送信した設定の実値反映を確認する。
未送信、30 秒以上応答なし、複数ページ、別ページの送信、古い Quest 応答は確認済みにしない。
`onsite.py check` は Wi-Fi 設定を変更しない。切断時の自動切り替えを点検に混ぜない。

2026-09-22 検証: Streaming EditMode 1531 件、ビルドガード 12 件、Python 61+17 件、Node 10+10 件が通過。
代役 Quest で博士 UI の送信 1 / 反映 1 と実値一致を確認。実機タブレットの往復は未確認。
`build fixedcam -Force` の検証 APK は素材 20/20 件のハッシュと receipt / boot.config の build-guid が一致。
通常ビルドは既存の hud-font / comms-horror-font / OSD の mtime 検査が未通過。既存の dirty フォントは保全した。
この APK は実機へ配布していない。push は通常の事前チェックが通るまで保留。

`tools/web-compositor/serve.ps1` で新しい `capture-server.py` を起動してから
`http://192.168.10.10:8099/` を開く。`index.html` は「設営と点検」。旧 `onsite.html` は転送入口。
演出編集は `authoring.html`。以下の 2026-09-04 の記録にある旧画面の配置と操作は歴史的経緯であり、
現行の当日画面の案内には使わない。新サーバを起動していなければ `/ops/status` と `/ops/restart` は無い。

当日画面はカメラ A/B/C と Quest α/β を固定行で見る。登録は
`tools/web-compositor/operations-fleet.json`（A/B/C=`192.168.10.21/.22/.23:8080`、
Quest α/β=`192.168.10.31/.32`）。カメラ UUID は 8 月の観測値なので、新設 Android 登録との一致は要確認。
PC の `autoFollow=false`、Quest の `discoveryEnabled=false`、A/B/C の `pinned=true` を投影する。
通常面で演出・ID・IP は編集しない。`show.json` の host などローカル値はコミットしない。

`GET /ops/status` は端末 ID・UUID と MJPEG の実フレーム 2 枚以上の連番進行、温度・電池、
Quest ごとの 6 秒以内の heartbeat を見る。単一の `/unity/status` では両機を判定できない。
`POST /ops/restart` は登録 UUID が一致するカメラ 1 台だけを起こし直す。90 秒間は再試行せず、
復旧後にフレームを再観測する。歩く場所と構図、Quest 2 台の映像・音・入力は人が確認する。
確認は 1 時間で失効し、切断または設定変更でも解除される。
`onsite.py check` と詳細点検は従来の機器情報・素材も見る補助。既存の音と素材は変更しない。
スマホ 3 台が未接続のため、配信アプリの実機更新と動作は未検証。

---

## 2026-09-04 の運用記録

2026-09-04 に作った。会期は **9/6・9/7**。要求は「当日 PC を触りたくない・人形視点を撮った後は
シュビーに頼むだけで反映されてほしい・人間が関与する不確実性を無くしたい」。

入口は `.claude/skills/onsite/SKILL.md`。

## 1. 層を 3 つに分けた理由

**シュビーを運用の層に置かない。** Claude は Anthropic への回線が要るので、
**会場の外向き回線が死んだ瞬間に消える**。だから当日の操作は LAN だけで閉じる層に置き、
シュビーは「判断が要るときの修理役」に退けた。

| 層 | 実体 | 落ちる条件 |
|---|---|---|
| 機械が勝手に | `onsite.py watch`（監視と復旧）／ログオン自動起動 | PC が落ちる |
| 人がスマホから | `http://192.168.10.10:8099/onsite.html` | LAN が落ちる |
| シュビー | `py -3.11 tools/onsite.py <cmd>` | **外向き回線が落ちる** |

⚠ 3 つとも**同じ HTTP API を叩く**ので、どこから操作しても結果は同じ。ここを分けると
「卓では直したのにパネルに出ない」が生まれる。

## 2. ⚠ 卓の「✅ 本番前チェック」と `onsite.py check` は別物（統合しない）

卓のはブラウザの中にあり、**ブラウザが見えるものしか見えない**（`show.json` ＋ heartbeat ＋ `/diag`）。
`onsite.py check` が見るのは**その外側**で、重なりが無い:

- 端末に入っている APK が焼いたものと同じか（古い機は「実装が効いていない」の顔で出る）
- 端末キャッシュ・電池・熱・Wi-Fi の相手
- **無線 adb が開いているか**（閉じていると当日 1 手も打てない）
- 露出 / AWB ロック・レンズの画角・名乗った cameraId
- 傾きと較正の差
- **卓が 2 つ立っていないか**

⚠ `preflightRows()` は `app.js` の 434 行・グローバル 9 個に依存している。**本番直前に共通化しない。**
壊すと卓 UI が丸ごと死ぬ。分けたまま両方を回すのが正しい。

## 3. ⚠⚠ 古い heartbeat で判定させない（作っている最中に踏んだ）

卓の `/unity/status` は**最後に受け取った heartbeat を何時間でも返す**。最初の実装は
Quest が 1 台も起動していないのに「音 OK / コントローラ NG」を出した — **4 時間前の値**だった。
⇒ `ageSec <= 6.0` のサンプルだけ採る。0 件なら「見ていない」と言う（`skip`）。

⭐ 同じ理由で、**2 台のうち片方だけ欠けている状態は 1 回読んでも分からない**。
heartbeat に端末 ID が無く（`ShowControlClient.cs` の `Hb` に無い）、`_unity_status` は
1 スロットを 2 台が交互に上書きする。⇒ **20 秒の窓の最悪値**で見る（欠けている機の番が来た瞬間に落ちる）。
⭐ **2026-09-11 から heartbeat は `deviceId` を名乗る**（0185・[[visitor_tablet]]）。卓は `_unity_devices` に
機ごとに持ち分け、`GET /unity/devices` で出す（`localIp` / `titleStage` / タブレットの口が開いているか も載る）。
`/unity/status` と `onsite.py check` は従来のまま（1 スロット・20 秒の窓）なので、機ごとに見たいなら `/unity/devices` を読む。
⚠ タブレットの設定そのものは卓を通らない（0187。Quest 自身の :8090 へ直接）。

## 4. 点検が初回に暴いたもの（2026-09-04 22:38）

道具を作った日にそのまま出た。**どれも卓の画面にも実機の画にも出ていなかった。**

1. **カメラ C の較正が +51.3° / -21.9° ずれている** — カメラを動かしたあと取り直していない。
   CG 人形が C の映像で盛大にずれる。⚠ 2026-08-16 にも同じ C だけがずれていた（[[camera_fleet]]）
2. **無線 adb が 3 台とも閉じていた**（`:5555` が connection refused）。
   ドキュメントは「A と B は開いた」と書いてあるが、**端末を再起動すると閉じる**
3. **露出 / AWB が 3 台とも自動のまま** — 素材を撮った時と本番で色が変わる
4. **カメラ B が熱段 2**（40.3℃・充電中）

## 5. 目の写真の経路（3 本あり、機械が探す）

1. **スマホのブラウザ → `POST /eyejack/upload`**（既定・LAN だけで閉じる）。
   `onsite.html` の 1 押しで upload → `/eyejack/apply` → `/export-build` まで通る
2. **`G:\マイドライブ\shubie\eyejack-drop`**（Drive 経由）。⚠ **外向き回線が要る**ので退避路
3. 手で `tools/web-compositor/eyejack/` へ置く（従来）

`onsite.py eyejack` は 1→2→3 の順に探し、**見つけた順に番号を打ち直してから**取り込む
（流れる順はファイル名順なので、番号を打たないと撮った順が保たれない）。

⚠ サーバに**ファイルアップロードの口は無かった**ので作った。`cgi` は 3.13 で消えるので使わず、
境界で割って `filename=` を拾うだけの最小実装（`_split_multipart`）。

## 6. ログオン自動起動（2026-09-04 に入れた）

`tools/onsite-autostart.cmd` のショートカットを `shell:startup` へ置いた（`mawarimi-desk.lnk`）。
**外すのはそのショートカットを消すだけ**で、他は何も書き換えていない。

- **冪等が必須**。`onsite.py serve` は `:8099` が開いていれば何もしない。
  ⚠ 卓が 2 つ立つと互いの `show.json` を巻き戻し合う（[[show_json_is_live_config]]）
- ⚠⚠ **`.cmd` は ASCII だけで書く。** cmd.exe は ANSI コードページ（cp932）で読むので、
  UTF-8 の日本語を `rem` に書くと**化けた断片がコマンドとして実行される**（実際に踏んだ）。
  説明は `.md` 側に置く
- ⚠ **発火するのはログオン時だけ。** この機体は `AutoAdminLogon=0`（自動ログオン off・
  **変えていない**）なので、無人再起動でサインイン画面に止まると卓は戻らない。誰かが 1 度サインインする

## 7. ⚠ 自動復旧で Wi-Fi には触らない

`watch` が打つ手は 2 つだけ — **画面を起こす**（`KEYCODE_WAKEUP` → `dismiss-keyguard`）と
**配信アプリを起こし直す**（`force-stop` → `am start`）。順番が要る（Doze 中に `am start` だけ
すると Activity が resume されず、**フレーム 0 のまま HTTP だけ生きる**）。

⚠⚠ **Wi-Fi の入れ直しは 1 度もやらない。** 保存済みの別 AP が選ばれて全台が移る
（2026-08-30 に実演済み）。しかも画面には何も出ない。`onsite.py` はこの操作を持っていない。

⚠ 1 台につき **90 秒に 1 回まで**。落ちている時にだけ動く（再起動ループを作らない）。

関連: [[quest_fleet_two_devices]] / [[camera_fleet]] / [[show_json_is_live_config]] /
[[eye_jack]] / [[onsite_experience_test]] / [[visitor_sound_reset]]
