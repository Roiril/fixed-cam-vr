---
name: streamer-operator-ui
description: 配信スマホ側の UI（補助線・鏡合わせ・撮影パネル・卓の自動発見）を触る前に読む罠と設計の芯
metadata: 
  node_type: memory
  type: project
  originSessionId: 3f8a1b93-ad92-45db-8404-e8cb45e0f296
  modified: 2026-08-22T17:20:24.978Z
---

配信アプリ（[fixed-cam-streamer](https://github.com/Roiril/fixed-cam-streamer) v0.13.0・2026-08-23）に
**据える／撮る**の面を足した。設計書は
[reports/2026-08-23_streamer-ux.html](../../reports/2026-08-23_streamer-ux.html)、
契約は `rules/streaming.md`「配信アプリの画面」、現場の手順は
[docs/onsite-checklist.md](../../docs/onsite-checklist.md) §0.2b / §0.2d。
ここは**その 3 つに書けない罠**だけ。

リポジトリは別（`C:\Users\kouga\Projects\Mobile\fixed-cam-streamer`）。
ビルドは `streamer-android-build` スキル。

## 0. 芯は「役割で置き場を決める」

据える人も撮る人も見ているのは**スマホの画面**で、卓ではない。
だから据える補助（補助線・鏡合わせ）と撮る仕事（指示・⏺・尺の確認・卓へ渡す）は端末に置く。
卓に残したのは**大きな画面が要る仕事**だけ（頭の窓の試写・選び直し）。

⚠ **この線引きを逆にしない。** 「何を撮るか」は卓が正（台本から導く）で、端末は写しを持たない。
「どう据えるか」は端末が正（台本の事実）で、卓からは配らない —— **卓が落ちていても据えられる**必要がある。

## 1. ⚠⚠ 鏡の軸は `splitX` ではなく「配信画像の横中央」

受け側は**枠 UV の 0.5** で反転する（`ScreenComposite.shader` の `uvSrc.x = 1.0 - uvSrc.x`）。
contain-fit は左右対称なので、これはソース画像の横中央と同じ。
**分割位置を動かしても鏡の軸は動かない**（動くのは「どちらの半分に出るか」だけ）。
同じ注意が `tools/web-compositor/line-mirror.js` の冒頭にもある。

⚠ 補助線は**映像が出ている矩形**へ引く（`GuideSpec.fitCenter`）。画面の中央ではない —
縦持ちで配信が 480x640 になると映像の左右に黒帯が出て、画面中央と映像中央がずれる。

## 2. ⚠⚠ 鏡合わせは「需要」として数えないと画が固まる

鏡合わせは配信されている JPEG を `distributor.awaitNext` で横から読む。
需要駆動 encode（`DemandPolicy`）は `/video` 接続と録画しか数えていなかったので、
**誰も見ていない現場では encode が止まり、据えている最中に画が固まる**。
しかも症状は「カメラが壊れた」の顔で出る。

⇒ `FrameDistributor.localPreview` を足し、`DemandPolicy.hasDemand(clients, recording, localPreview)`
の第 3 項にした。⚠ **`videoClients` を増やして済ませない** — あれは `/health` の `clientCount` として
卓に出るので、「誰かが見ている」と嘘をつくことになる。

⚠ 前面でないとき（`onStop`）・IDLE に落ちたときは自動で切る。
切り忘れると背景で JPEG を復号し続け、需要も立ったままになる。

## 3. ⚠⚠ Kotlin のブロックコメントは**入れ子になる**

KDoc の中に `` `/record/*` `` と書いたら「Unclosed comment」でファイル全体が壊れた。
`/*` が入れ子のコメントを開き、閉じないままファイルが終わる。
**コメントの中でパスをワイルドカードで書かない**（`/record/start` などと具体名で書く）。

## 4. ショット定義の置き場が変わった（`shoot-model.js` → `shots.json`）

読む相手が 3 つになったから —— ブラウザ（`shoot-model.js` が `loadShots()`）・
卓のサーバ（`_shots_def()`）・**配信スマホ**（`GET /shoot/plan` 経由）。
JS の定数のままでは Python から読めず、スマホへ配る指示文が二重管理になる。

⚠ **`SHOTS` は空で始まり `setShots()` で入る。** 読み込む前に判定を呼ぶと空配列になり、
`approachPrecheck` が「この台本は接近を使っていない」＝ 緑を返してしまう。
だから `shotsLoaded()` の門を置いて **❌「shots.json が読めていない」を出す**。
`app.js` / `shoot.js` の起動でそれぞれ `loadShots()` を呼ぶ。

## 5. ⚠ 「頭に何秒要るか」が 2 つの言語にある

`neededHeadSec` / `cutCount` はブラウザ（JS）と卓のサーバ（Python）の両方にある。
移植を「気をつける」で守らないために、期待値を `tools/web-compositor/shoot-fixture.json` に置き、
**node と Python の両方が同じファイルを食う**（`shoot-model.test.mjs` / `test_shoot_plan.py`）。

`GET /shoot/plan` の**欄の名前**も 2 言語にまたがる契約なので、`PlanPayloadContractTest` が
欄の集合ごと固定している。黙って改名すると、スマホは既定値を読んで
**「全部 — のまま」「短いのに緑」**になる（例外は出ない）。

⚠ **`needSec` の null を 0 に潰さない。** 「この素材を使うカットが台本に無い」が
「0 秒でよい」に化け、どれだけ短くても緑になる。Kotlin 側は `o.isNull("needSec")` を先に見る。

## 6. 卓の在り処は**既にブロードキャストされていた**

卓（`capture-server.py` の `_disc_sender`）は 5 秒ごとに `role:"show-server"` の announce を
出している。端末はそれを拾って送信元 IP ＋ `httpPort` を刻むだけでよく、
**プロトコルの変更も、現場での設定作業も、卓の画面を開いておく必要も無い**。

最初は「卓 → 端末の HTTP リクエストに `X-Desk-Url` ヘッダを付ける」を考えたが、
それだと**卓の画面（撮影コンソール）を開いている間しか届かない**。
⚠ 新しい経路を足す前に、**既に流れているものを見る**。

⚠ ブロードキャストは誰でも出せるので、`DeskAddress` が形（IPv4 ＋ ポート・パスなし・http のみ）を
検める。端末が卓へ送るのは撮った動画だけで、送るのは撮った本人が押したときだけ。

## 7. `POST /shoot/collect` は接続元アドレスから取りに来る

端末は**自分の IP を送らない**。テザリング・複数 NIC で「端末が思っている自分の IP」と
「卓から見えるアドレス」は食い違う。卓は `self.client_address[0]` を使う。

⚠ この口を足すために `_shoot_pull` / `_shoot_adopt` を
**中核（辞書を返す `_do_pull` / `_do_adopt`）とハンドラ（`_json` で書く）に割った**。
検分・一意名・台帳・頭送りの作法を 2 つに割らないため。片方だけ直さない。

## 8. テイクの尺は 2 か所にある（役割が違う）

- **端末**: 録画セッションの実測（最後のフレームの presentation time）。SharedPreferences に残すので
  アプリを立て直しても一覧から消えない。⚠ 壁時計ではない —— 配信が途切れた区間はフレームが無いので、
  壁時計だと実際より長く出る
- **卓**: ffprobe の実測。**こちらが正**。`POST /shoot/collect` の返事に入るので、
  撮った本人がその場で「⚠ 尺が足りない（要求 1.4 秒 / 撮れた 1.0 秒）」を読める

⚠ **録画中に「あと N 秒」を出すのが効き目の芯**。止めてから短かったと分かる形にしない。

## 9. 実機で確かめていないもの（2026-08-23 時点）

JVM テスト 142 件・APK ビルドまで。**実機に入れて触ってはいない。**

- 鏡合わせの見え方（8fps・640x480 の復号が体感でどうか、継ぎ目の読み取りやすさ）
- 撮影パネルの文字サイズ・押しやすさ（Pixel 7a の実画面）
- 卓の自動発見が現場の Wi-Fi で何秒で入るか
- ⚠ **卓サーバは再起動しないと新しい口（`/shoot/plan` `/shoot/collect`）を持たない**

関連: [[approach_shoot_console]] / [[camera_fleet]] / [[quest_fleet_two_devices]] /
[[show_json_is_live_config]] / [[web_compositor]]
