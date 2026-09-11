---
name: visitor-tablet
description: タブレット 2 台（クエストα用・β用）で体験前に言語とホラー軽減を選ばせる仕組み（0185）。枠は卓が持ち、Quest は世代番号だけ／端末 ID で役を結ぶ／戻す→載せるの順／確かめ方
metadata: 
  node_type: memory
  type: project
  originSessionId: 94dc6571-7165-4a32-9334-39ae2473a8c9
  modified: 2026-09-11T10:09:27.363Z
---

# タブレットの設定（visitor.html）を触る前に

2026-09-11・`canon/LEDGER.md` 0185。ユーザー逐語「各タブレットを、クエストα、β用に割り当てて、
各クエストには体験前にタブレットで設定した値が適応される」。**仕組みだけ先に作った**（用途と文は後）。

| 何 | どこ |
|---|---|
| タブレットの面 | [`tools/web-compositor/visitor.html`](../../tools/web-compositor/visitor.html)（`http://192.168.10.10:8099/visitor.html`） |
| 卓の判断 | [`visitor_prefs.py`](../../tools/web-compositor/visitor_prefs.py)（純関数・`test_visitor_prefs.py` 14 本） |
| 卓の口 | `POST /command setVisitor` / `bindVisitorDevice`、`GET /unity/devices` |
| Quest の箱 | [`VisitorPrefs`](../../Assets/Scripts/Streaming/VisitorPrefs.cs)（static・`VisitorPrefsTests` 10 本） |
| 受け取る | `ShowControlClient.ApplyVisitorSlot`（long-poll の 1.69） |
| 書く | `TitleScreen.BeginTitle`（戻す → 載せる）と `TitleScreen.Update`（Wait の段で届いたら即） |
| 観測 | `ev=boot id=`（端末 ID の頭 6 桁）／ `ev=sum visitor=<役>/<卓>/<書>/<始>/<回数>`／解析器「## タブレットの設定」 |

## 正は卓が持つ。Quest は世代番号を 3 つ持つだけ

`control.visitor.<役> = {lang, relief, epoch}`。タブレットが書くたびに epoch +1。
Quest は `pending`（卓が言った世代）/ `applied`（書いた世代）/ `consumed`（体験者が始めた世代）を
持つが、**どれも再起動で 0 へ戻ってよい**（キャッシュには載せない）。

**持ち越しを断つのは卓。** 体験者が A を押して注意書きを閉じた瞬間（`TitleStage.Wait` を出た縁）に
`VisitorPrefs.Consume()` が世代を記録し、heartbeat の `visitorConsumedEpoch` で卓へ返す。
卓は**同じ世代が枠に残っていて、しかも既定でなければ**枠を既定（ja・軽減なし）へ戻して epoch +1。
Quest はそれを「次のタイトルで載せる値」として受け取る ＝ 次の人は既定から始まる。

- ⚠ 世代が一致しないと戻さない（送った後にタブレットで次の人が選び直した分を消さない）
- ⚠ 既定の枠は戻さない（long-poll を無駄に起こさない）
- ⚠ 再起動直後の Quest は consumed 0 を返す。0 は何もしない

## ⚠⚠ 戻す → 載せる の順（`TitleScreen.BeginTitle`）

`ShowLanguage.Reset()` / `HorrorRelief.Reset()` の**直後**に `VisitorPrefs.ApplyAtTitle()`。
これは同じ世代でも**もう一度書く**（`AppliedEpoch` を 0 に落としてから書く）。
逆順にすると、スタッフがランをやり直した瞬間にタブレットの設定が消える。
`memory/show_language.md` の「戻すのは 1 か所」は生きている — 戻す場所は増えていない。**載せる場所が同じ行の隣に足された**だけ。

## ⚠ 書くのは注意書きの段だけ

long-poll で届いても `Offer` は箱に入れるだけ。書くのは `TitleScreen` が `Stage == Wait` のとき
（体験者が手元で巡らせるのと同じ縁）。本編の途中で言語が変わると連絡の面が途中から別の言語になる。
本編中に届いた枠は次の `BeginTitle` で載る（タブレットは「次の体験の開始時に反映」と出す）。

`Select` で書くので `ShowLanguage.ChangeCount` / `HorrorRelief.ChangeCount` は動かない
（`langN` / `relief` の 2 つ目は「体験者が押した回数」のまま。解析器の門も変わらない）。

## 役と Quest を結ぶのは端末 ID

heartbeat に `deviceId`（`SystemInfo.deviceUniqueIdentifier`）と `deviceModel` を足した。
卓は機ごとに heartbeat を持ち分ける（`_unity_devices`）— **2 台が 1 スロットを交互に上書きする**
問題（`onsite_day_ops.md` §3）の受け皿。ただし `/unity/status` は従来どおり最後の 1 台。

結ぶのは `visitor.html` のスタッフ欄（`bindVisitorDevice`）。`control.visitorDevices = {alpha: id, beta: id}`
に残る。1 台 1 役（別の役に居たらそちらは解ける）。**どの機がどれか**は `adb logcat` の
`[XP] ev=boot … id=` の頭 6 桁か、片方だけ電源を入れて見分ける。
⚠ 端末 ID は APK の署名鍵と端末で決まる。同じ鍵で焼き直す限り変わらないが、**変わったら結び直す**
（結ばれていない機は `visitor=-/…` と出て、タブレットで何を選んでも届かない）。

## 「このタブレットはどっちか」は卓に無い

`visitor.html` は役を `localStorage` に持つ（スタッフ欄で α/β を押す）。卓にはタブレットの識別が無い。
2 台目のタブレットに同じ役を持たせても卓は止めない（同じ枠を 2 台が書くだけ）。

## 確かめ方

- 卓だけ: `py -3.11 -m unittest discover -s tools/web-compositor -p "test_*.py"`
- Quest 無しで通し: 偽の heartbeat を curl で送る（下）。`visitor.html` のスタッフ欄に機が出て、結んで、送って、
  消費の世代を返すと枠が既定へ戻る（2026-09-11 に実施済み。rev 1151→1157）
  ```bash
  curl.exe -s -X POST http://127.0.0.1:8099/unity/heartbeat -H "Content-Type: application/json" \
    -d '{"deviceId":"FAKEA1xxxxxx","deviceModel":"Quest 3","phase":"INTRO","lang":"ja","relief":false}'
  ```
- 実機: `quest-record.py --walk` の解析器「## タブレットの設定」。**画にも音にも出ない**ので `lang` / `relief` / `visitor` が唯一の証拠
- ⚠ 卓を触った後は `capture-server.py` を入れ直す（コードはプロセスに読まれたまま）。
  `onsite-autostart.cmd` は `:8099` が開いていれば何もしないので、kill → 同じコマンドで起動

## まだ決めていないこと（`canon/OPEN.md`）

- タブレットに出す説明の文（いまは仮。注意書きは HMD の `TitleNotice` と同じ文）
- HMD 側の単押し（言語）と長押し（軽減）を残すか（いまは残っている。両方効く）
- 当日の手順書（`docs/onsite-checklist.md`）への追記（用途が決まってから）

関連: [[show_language]] / [[onsite_day_ops]] / [[show_json_is_live_config]] / [[quest_fleet_two_devices]]
