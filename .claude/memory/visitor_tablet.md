---
name: visitor-tablet
description: タブレット 2 台（クエストα用・β用）で体験前に言語とホラー軽減を選ばせる仕組み（0185/0186/0187）。Quest 自身が HTTP の口（:8090）と面を持ち、卓も PC も経由しない／正は Quest／戻す→載せるの順／確かめ方（代役の stub）
metadata: 
  node_type: memory
  type: project
  originSessionId: 94dc6571-7165-4a32-9334-39ae2473a8c9
  modified: 2026-09-11T10:37:25.755Z
---

# タブレットの設定（visitor.html）を触る前に

2026-09-11・`canon/LEDGER.md` 0185（タブレット 2 台）/ 0186（最初の画面は注意事項と設定だけ・反映を見て取れる）/
**0187（PC は開かない。卓を経由せず Quest とタブレットを直接つなぐ）**。

⚠⚠ **卓も PC も経由しない。** 0185 の初版は「タブレット → 卓 → long-poll → Quest」だったが、
0187 で作り直した。**Quest 自身が HTTP の口を持ち、面も Quest が配る。**

| 何 | どこ |
|---|---|
| タブレットが開く URL | `http://192.168.10.31:8090/`（α）／ `http://192.168.10.32:8090/`（β）— 現地の静的割当（`docs/onsite/network-setup.md`） |
| 面 | [`Assets/Resources/Visitor/visitor.html`](../../Assets/Resources/Visitor/visitor.html)（TextAsset。**APK に入る**） |
| 口 | [`VisitorPortal`](../../Assets/Scripts/Streaming/VisitorPortal.cs)（TcpListener :8090・`ShowControlClient.EnsureVisitorPortal` が起動時に生成。シーンには焼かない） |
| 判断 | [`VisitorPortalLogic`](../../Assets/Scripts/Streaming/VisitorPortalLogic.cs)（純ロジック・テスト 11 本） |
| 箱 | [`VisitorPrefs`](../../Assets/Scripts/Streaming/VisitorPrefs.cs)（static・テスト 10 本） |
| 書く | `TitleScreen.BeginTitle`（戻す → 載せる）と `TitleScreen.Update`（Wait の段で届いたら即） |
| 代役 | [`tools/visitor-portal-stub.py`](../../tools/visitor-portal-stub.py)（Quest 無しで面を机上で通す） |
| 観測 | `ev=sum visitor=<口が開いているか>/<受けた累計>/<枠の受理番号>/<書いた受理番号>/<書いた回数>`／解析器「## タブレットの口」／logcat `[VisitorPortal]` |

## 口は 4 つだけ

| | |
|---|---|
| `GET /` | 面 |
| `GET /status` | この機の実値: `lang` / `relief` / `titleStage` / `phase` / `pending{lang,relief,seq}` / `appliedSeq` / `applyCount` / `received` / `model` / `ip` / `port` |
| `POST /set` `{"lang":"en","relief":true}` | 枠へ入れる。`{"ok":true,"seq":N}`。lang が ja/en/fr 以外は 400 |
| `POST /clear` | 枠を空にする（スタッフ） |

役の結び付けは**どの URL を開くか**で決まる。卓に台帳は無い（0185 初版の `control.visitorDevices` は消した）。

## 正は Quest の中。持ち越しは Quest が断つ

`VisitorPrefs` は枠（受理番号・言語・軽減）と、書いた受理番号を持つだけ。**永続化しない**（再起動でまっさら）。
体験者が A を押して注意書きを閉じた瞬間（`TitleStage.Wait` を出た縁）に `Consume()` が枠を空にする ＝
次の人は既定から始まる。本編中に届いた枠は次の `BeginTitle` で載る。

## ⚠⚠ 戻す → 載せる の順（`TitleScreen.BeginTitle`）

`ShowLanguage.Reset()` / `HorrorRelief.Reset()` の**直後**に `VisitorPrefs.ApplyAtTitle()`。
同じ受理番号でも**もう一度書く**。逆順にすると、スタッフがランをやり直した瞬間にタブレットの設定が消える。
`memory/show_language.md` の「戻すのは 1 か所」は生きている — 載せる場所が同じ行の隣に足されただけ。

## ⚠ 書くのは注意書きの段だけ

`POST /set` は箱に入れるだけ（背景スレッド → キュー → `Update` で `VisitorPrefs.Set`）。
書くのは `TitleScreen` が `Stage == Wait` のとき。本編の途中で言語が変わると連絡の面が途中から別の言語になる。
`Select` で書くので `ShowLanguage.ChangeCount` / `HorrorRelief.ChangeCount` は動かない（`langN` / `relief` の 2 つ目は「押した回数」のまま）。

## 「反映されたか」は `GET /status` の実値で出す（0186）

面は送った後、**選んだ値 → ヘッドセットが返している値**を 2 行で並べ、一致して `appliedSeq >= seq` なら緑の ✓。
送った値を写して「反映済み」と出すことはしない。本編中（phase RUN/END）は「次の開始時」、
`/status` が返らなければ赤の「繋がっていません」。

## ⚠ 面を直したら APK を焼き直す

面は `Resources` の TextAsset。卓の静的配信と違い、ファイルを置き換えても実機には届かない。
注意書きの 3 言語は HMD の `TitleNotice` と同じ文（片方だけ直すと受付とヘッドセットで食い違う）。

## 確かめ方

- 机上: `py -3.11 tools/visitor-portal-stub.py --stage Done --phase RUN` → `http://127.0.0.1:8090/` を開く →
  送る（橙「体験中」）→ `curl -X POST :8090/_phase -d INTRO` `curl -X POST :8090/_stage -d Wait` → 緑 ✓
  （2026-09-11 に通した）。⚠ 代役は面の検証用。実機の bind・スレッド・IL2CPP はここでは確かめられない
- EditMode: `VisitorPortalLogicTests`（要求の読み・道・応答の形）/ `VisitorPrefsTests`
- 実機: 走行の `visitor=1/…`（1 つ目が 0 なら :8090 を開けていない）。logcat `[VisitorPortal] タブレットの口を開けた: http://…`。
  タブレットで押したのに 2 つ目（受けた累計）が増えないなら届いていない（同じ Wi-Fi か・URL の IP か）
- ⚠ **画にも音にも出ない**ので `lang` / `relief` / `visitor` が唯一の証拠

## 卓に残っているもの

heartbeat の `deviceId` / `deviceModel` / `localIp` / `titleStage` / `visitorPort` / `visitorReceived` / `visitorPending` /
`lang` / `relief`。卓は機ごとに持ち分けて `GET /unity/devices`（`tools/web-compositor/unity_devices.py`）で出す。
**スタッフが眺めるためのもので、タブレットはここを読まない。**

## まだ決めていないこと（`canon/OPEN.md`）

- 導入の文をどこへ置くか（0186 で最初の画面からは外した）
- HMD 側の単押し（言語）と長押し（軽減）を残すか（いまは残っている。両方効く）
- 当日の手順書への追記（用途が決まってから）

関連: [[show_language]] / [[onsite_day_ops]] / [[quest_fleet_two_devices]]
