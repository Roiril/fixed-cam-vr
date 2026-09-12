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

0198により `scene.image === 'doctor.jpg'` の場面は博士を中央に表示する。章名は左上だけに表示。設定へ戻る際は `is-doctor-only` を解除する。

0197によりタイトルと設定の背景は右側に日本人形一体の生成画像へ変更。`entrance-doll-v1.png.bytes` を共有する。博士の画像・動画は `.stage.is-briefing` のときだけ表示する。メディア停止処理が hidden を解除しても設定画面へ博士が戻らないようCSSで制限する。

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

## 見た目（2026-09-12 全面再設計）

博士を右に置く構図と黒・生成り・朱赤を維持した。
左は注意事項から言語3択、ホラー軽減2択へ読む順番で並べる。
矢印の循環選択は廃止した。全候補を直接押すネイティブのラジオ入力になった。
縦持ちと狭幅では縦に並べる。注意事項を低い画面で隠す旧CSSは廃止した。
RECと時計は置かない。接続表示はヘッドセットの応答に基づく。
詳細と検証手順は [`tools/visitor-ui/README.md`](../../tools/visitor-ui/README.md)。

2026-09-12 に設定後の博士の説明を追加した（0194）。
実値への反映を確認すると、人形の背景を保った確認画面で止まる。「Questに反映しました」と実値を表示し、「画面をタップして次へ」と案内する。画面タップで説明を一度だけ始める。自動では進めず、説明開始専用のボタンも置かない。0199で短縮後、0201と0202で文脈を補い4場面9文。日本語の仮の尺は35秒。最終文に到達した時点で「次の文」を隠し、同じ字幕を終了状態でもう一度見せない。
2026-09-12 の追加依頼（0196）でタイトルを追加した。タップすると設定へ移る。
字幕は一文の全文を即時表示する。通常は手動送り。オートをオンにしたときだけ自動進行する。
前後移動も文単位。章を跨ぐときだけ左上の章見出しを演出する。最後は装置の装着依頼で留まる。
次の来場者はタイトルとオートオフから開始する。説明から設定へ戻るときはタイトルを挟まない。
説明内容は `Resources/Visitor/briefing-v1.json.bytes`。画像は同じ場所の `briefing-*-v1.png.bytes`。
`/asset/` に JSON の MIME を追加した。音声と動画は未制作で任意の差し替え欄だけがある。
台本と SRT は `tools/visitor-ui/export-briefing.py` で JSON から再生成する。
台詞と画像の採否は `canon/OPEN.md`。機械の検証済みを世界観の採用へ読み替えない。

博士の画は `GET /asset/doctor.jpg`（`Resources/Visitor/doctor.jpg.bytes`）。
元は `tools/visitor-ui/doctor_v1.png`。女性版は未採用の候補。
博士の説明に動画を使うときは JSON の各場面の `video` へ言語別のファイル名を指定する。
手動送りでは各文の終端で媒体を止める。文送りで次の文の開始位置へ移る。
開始位置は同じ場面の先行する文の `durationMs` の合計。サーバーは Range/206 対応済み。

字幕の一字ずつの表示は廃止した。設定と博士の説明の両方で全文を即時表示する。
言語を変えるとその言語の文へ切り替わる。反映待ちでは設定を尋ねる字幕から待機の字幕へ変える。
動きを減らす設定ではタイトルと章の遷移も即時にする。軽減の説明は「あり」の場合だけ出す。
「設定を変える」は選択を保って戻る。「はじめから」は置かない。

送信時の設定を固定してから送る。通信には期限を設ける。
失敗画面には再送と編集への復帰を用意した。確認要求を重複させない。
カウンターの減少で検出できる再起動では古い送信を破棄する。
本番の `?demo=` による偽の成功画面は廃止した。状態の検証は代役サーバーで行う。
ブラウザ検証は明示した viewport の寸法と実際の `innerWidth/innerHeight` を照合する。

## 口は 5 つ

| | |
|---|---|
| `GET /` | 面 |
| `GET /status` | この機の実値: `lang` / `relief` / `titleStage` / `phase` / `pending{lang,relief,seq}` / `appliedSeq` / `applyCount` / `received` / `model` / `ip` / `port` |
| `POST /set` `{"lang":"en","relief":true}` | 枠へ入れる。`{"ok":true,"seq":N}`。lang が ja/en/fr 以外は 400 |
| `POST /clear` | 枠を空にする（スタッフ） |
| `GET /asset/<name>` | 面が使う画像・動画。`Resources/Visitor/<name>.bytes`。Range（206）対応・10 分キャッシュ |

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

面は送った後、**選んだ値 → ヘッドセットが返している値**を 2 行で並べ、一致して `appliedSeq >= seq > 0` なら反映済み。
送った値を写して「反映済み」と出すことはしない。本編中（phase RUN/END）は「次の開始時」、
`/status` が返らなければ確認不能を表示する。古い実値は「—」に戻す。

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
