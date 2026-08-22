---
name: approach-shoot-console
description: 当日の素材撮り（撮影コンソール・streamer の端末内録画・動画カットの先読み）を触る前に読む実装の罠
metadata: 
  node_type: memory
  type: project
  originSessionId: 8d1af606-b13a-4a88-9317-a501fb6c906f
  modified: 2026-08-22T06:54:52.713Z
---

`canon/LEDGER.md` 0102 の接近を「当日ミスなく撮る」ための環境（2026-08-22）。
設計書は [reports/2026-08-22_approach-shoot-console.html](../../reports/2026-08-22_approach-shoot-console.html)、
契約は `rules/streaming.md`（動画素材の先読み）、卓の説明は
[tools/web-compositor/README.md](../../tools/web-compositor/README.md) の「撮影コンソール」節。
ここは**その 3 つに書けない罠**だけ。

## 1. 停滞は `ev=clip` にしか出ない（`ev=step played=1` は「画面を取った」しか言わない）

カットの尺は**発火時刻**から数える（`TakeRunnerLogic.StepEndTime`）ので、動画の
ダウンロード + Prepare がそのまま「画に出ない時間」になる。実測（testassets 24KB・走行 2 回）:

| | 落とす | 用意 | 合計 | 0.5s カットの |
|---|---|---|---|---|
| 先読みなし | 0〜152ms | 128〜167ms | 280ms | 56% |
| 先読みあり | **0ms** | 120〜166ms | 166ms | **33%** |

- **用意（Prepare）は先読みでも消えない。** `_player.Stop()` → url 代入 → `Prepare()` を
  毎カット通り、**同じファイルでも短絡しない**。消すには VideoPlayer を 2 本持つしかない（未実装）
- ⚠ `ev=clip` の `dl` / `prep` / `tot` は**ミリ秒**、著作の `durSec` は**秒**。
  混ぜて 56000% と出した（`analyze-xp-log.py`）

## 2. 8 カットの連続再生そのものは成立している（測って確認済み）

間隔は著作値と一致（1.21 / 0.71 / 1.00 / 0.71 / 0.81 / 0.61 / 0.51 / 1.20 秒）、
画も毎カット切り替わり、偽ライブの trim も進んでいた（C の再生位置 0.5 → 2.0 → 2.3 → 3.6 → 3.9）。
⇒ **素材は mp4 のままでよい**（フレーム列化も 1 本焼きも不要）。

⚠ **画から停滞は測れない。** `xp-evidence.py` のオフセットは ±0.5 秒ずれるので、
カットの頭を切り出すと 1 カット先の絵が写る。**停滞の数値はログ、切り替わりの有無は画**。

## 3. ショットの定義を cue に足すと、次の cue 保存で消える

`cue-editor.js` の保存はフィールドの白名簿でオブジェクトを組み直す。だから
`shoot-model.js`（コード）に持つ。**要求尺だけは timeline から導出**するので著作に追従する。

## 4. 採用ファイル名を毎回一意にしないと「撮り直したのに変わらない」

Quest は URL → ローカル DL のキャッシュを持つ（`ScreenOverlayController._videoFileCache`）。
同名で差し替えるとアプリ再起動まで古い版が再生される。

## 5. 採用に `POST /state` を使わない

`/state` は cues 配列を丸ごと差し替えるので、卓の別タブが編集中の cue を消す。
`POST /shoot/adopt` が `_mutate_show` で 1 件だけ触る。

## 6. 頭合わせはファイル側を切る（`trimStartSec` は著作の値）

`-ss` + `-c copy` で無劣化・正確（端末内録画は**全フレームが I フレーム**）。
カットの `trimStartSec`（偽ライブの 0 / 1.9 / 3.6 / 5.0）へ撮影都合を書くと、
素材を差し替えたとき古い頭合わせが別素材に効く。

⚠ ブラウザ ⏺ 由来の素材はキーフレーム間隔が長いので `-c copy` だと指定より手前へ寄る（注記で言う）。

## 6b. ffmpeg の画素形式は括弧の中にカンマを入れる（2026-08-22 実機で踏んだ）

`Video: h264 (High) (avc1 / …), yuv420p(tv, smpte170m, progressive), 480x640` の形なので、
`[^,]*` で読み飛ばすと**解像度の手前で外れて符号化も寸法も黙って None になる**。
台帳が空になるだけでエラーは出ないので、当日「Quest で再生できる形式か」を判定できない。
`(?:\([^)]*\))?` で括弧を明示的に食う。**括弧なしの出力でも通ることを対で確かめる。**

## 6c. 卓サーバが遅く見えたら `localhost` の IPv6 フォールバックを疑う

`http://localhost:8099` は **2.0 秒**、`http://127.0.0.1:8099` は **0.00 秒**（実測）。
`::1` で待ってから IPv4 へ落ちている。**Python のスクリプトから叩くときだけ出る**
（ブラウザは Happy Eyeballs で並行試行するので出ない）。
サーバ側を疑う前にここを見る — 2026-08-22 に「録画中のポーリングが遅い」と誤診しかけた。

## 7. streamer は v0.10.0 が要る。旧アプリは黙って失敗しない

`/record/start?shot=&maxSec=&countdownSec=` と `/record/list` `/record/file` `/record/delete` は
v0.10.0 で追加。v0.9.0 は `shot=` を**無視して録画だけ始める**ので、
「撮れたのに回収できない」になる。⇒ コンソールは `/record/status` に `takeCount` があるかで
新旧を判定し、**端末の欄に「旧アプリ（要 v0.10.0）」と赤で出す**（`canList`）。

⚠ **配信スマホの APK 更新は USB が要る**（無線 adb は無効・`adb connect …:5555` は拒否される）。
⚠ **インストール直後はアプリが止まっている** — `adb shell monkey -p com.fixedcamvr.streamer
-c android.intent.category.LAUNCHER 1` で起こす（起こさないと `/info` が返らず「壊れた」に見える）。

**2026-08-22 に実機で通した**（Pixel 7a ×3・A は横向き / B・C は地面向き）:
構えて 3 → 2 → 1 → 撮影 → 指定の 3 秒で自動停止 → 自動回収 → 3.1 秒 / h264 / yuv420p / **640x480**。
採番 t01 → t02 → t03（上書きしない）・端末側の削除・頭の切り出し（2.10 → 1.60 秒・再圧縮なし）。

## 8. 卓サーバを再起動しても show.json は消えない

`_mutate_show` がファイルへ書いているので、再起動時に読み直される（実測 rev 942 が保たれた）。
⚠ ただし**卓とブラウザの long-poll は切れる**ので、ユーザーが操作中でないことを確かめてから。

## 9. B の端末を POV に借りるなら、B を取り付ける前に撮る

取り付けた後に外して戻すと較正が壊れる（それを検出するために傾き × 較正の照合がある）。
順序で回避できるので、`docs/onsite-checklist.md` §0 にその順で書いてある。

関連: [[approach_and_veil_hold]] / [[take_continuity]] / [[web_compositor]] /
[[show_json_is_live_config]] / [[camera_fleet]]
