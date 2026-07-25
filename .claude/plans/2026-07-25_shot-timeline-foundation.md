# 演出基盤の根本設計 — ショット・タイムライン（3 層モデル）

status: design-fixed（2026-07-25 設計確定。opus アドバイザーの赤入れを反映済み・実装は未着手）

決定ログは §8。論点 1〜5 は opus アドバイザーが判断し、メインが採否を確定した。

## ユーザー要求（原文）

> 今、インサートショットのUIがわかりずらい。そもそも、カメラごとじゃなくて、１周目A-B-CのタイムラインUIから、インサート-A-インサート-B-Cみたいに直感的に自由にできるのが直感的なのかもしれない。
> また、今回のプロジェクトにおいて難しいのが、カメラの切り替えタイミングが、①体験者が境界をまたいだ時と、②こちらが演出として切り替えたときの２パターンあり、これを両立しなければいけない事。
> 例えば、まだ実装してないけど3周目ではA-Bと来たときに、Bの途中で新しいカメラDに切り替わって、そのあとスクリーン上すべてを事前に用意した映像①にきりかえ流し終わったら→1周目のBでの録画映像にCG人形だけをオーバーレイ→1周目のCでの録画映像にCG人形だけをオーバーレイ みたいな感じな凝った演出をしたい。
> まだこれらの細かい演出の実装はしないで、まずはそのように拡張していくための基盤を根本思想から設計していってほしい。

## 1. 現状の診断（なぜ分かりづらいか）

分かりづらさは UI の見た目ではなく**語彙とデータモデルが実体とズレている**ことに由来する。

| 症状 | 構造的原因 |
|---|---|
| インサートの UI が分からない | **同じことをする概念が 2 つある**。`cue`＝画面の中身を差し替える（カメラは変えない）／`insert`＝カメラを差し替える（cue も出せる）。実体はどちらも「一定時間、画面を横取りする」。別サブシステム（CueScheduler+ScreenOverlayController ⇄ InsertController+CameraSwitchDirector）として並ぶので、オーサリング面でも別物に見える |
| 「A-インサート-B」と書けない | インサートは区間の**属性**（`hasInsert` 単数・`anchor:enter/exit`）であって、タイムライン上の**要素**ではない。区間 1 つに 1 本しか置けず、順番も表現できない |
| 凝った多段演出が書けない | ステップ列という概念がない。「D → 事前映像① → 録画B+人形 → 録画C+人形」は現スキーマでは表現不能（cue を delaySec で手計算して並べる以外に手がなく、破綻する） |
| ①体験者の境界跨ぎと②演出の切替が喧嘩する | 両者が同じ土俵（`CameraSwitchDirector`）で殴り合い、`_cueActive` / `_insertActive` / `_overrideActive` の**凍結フラグで調停**している。凍結が降りない＝自動切替が恒久停止するバグを繰り返し出した（[logic_audit_2026_07_23](../memory/logic_audit_2026_07_23.md) の複数件） |
| インサート復帰で周回がズレた | **ショーの時計（周回・区間）が「画面が切り替わったか」に従属している**。`LapCounter` は `SwitchCommitted(Zone)` を購読するので、インサート復帰時に「実は Zone だった」と偽装発火する必要があった（`InsertController.EndInsert` の `asZone`）。これが構造的な歪みの核 |

つまり **cue / insert / ゾーン切替 は「画面に何を映すか」という 1 つの問題の 3 つの断片**であり、それを 3 つの別機構で解いている。

## 2. 根本思想 — 3 層・一方向

```
  人の層（体験者が決める）        ショーの層（オーサリングが決める）      画面の層（見せ方を決める）
  HMD 位置                        確定ゾーン進行                        映すべき Shot の要求
   → ゾーン判定（重なり/ヒス）  →  → 周回・区間 = ショーの時計       →  → 優先度調停（卓 > 演出 > 既定）
   → dwell（一瞬の通過を捨てる）    → 区間に付いた演出を解決            → 最小ショット長・dip/フェード
   → **確定ゾーン進行**             → 「今映すべき Shot」               → registry / overlay / post へ
```

流れは常に一方向。下流（画面）の状態が上流（時計）を変えることは無い。

### 5 つの不変条件

1. **体験者の位置は「時計」であって「画面」ではない。**
   位置が進めるのは *ショーの進行（何周目のどの区間か）* だけ。画面に何を映すかは、その進行に対してオーサリングされた結果。
   既定の割当がたまたま「その区間のカメラのライブ」なだけで、**ゾーン切替も「演出の一種（既定の演出）」**に格下げされる。
   → ①と②の両立問題が「両立」ではなく「**同じ仕組みの既定値と上書き**」になる。

2. **画面の所有者は常にちょうど 1 人。**
   優先度は `ライブ卓（人間が見ている）> 演出（Take）> 既定（ゾーンのライブ）`。同時所有・暗黙のブレンドを作らない。
   凍結フラグの掛け合わせではなく、**毎フレーム上から順に「誰が持っているか」を決める**（状態ではなく解決）。

3. **演出の所有は必ず有界。**
   全ての演出に終わりの時刻がある。ランタイムが上限（`maxDurationSec`）を強制し、超えたら強制返却＋ログ。
   → 「凍結が降りない＝体験が死ぬ」クラスのバグを個別パッチではなく**仕組みで殺す**。

4. **返し方は「復元」ではなく「再計算」。**
   演出が終わったら「元のカメラ」に戻すのではなく「**いま体験者がいるゾーン**」を映す。
   （`InsertController` が `TryGetPendingZone` / `asZone` で後付けした挙動を第一級のルールに昇格）

5. **時計は演出中も止まらない。**
   画面を横取りしている間もゾーン進行・周回カウントは進む。ただし**横取り中に通過した区間のオーサリング内容は発火しない（捨てる）**。
   → 「演出を見ている間に歩いてしまった」体験者でも周回が壊れない。

## 3. 語彙（データモデル）

| 用語 | UI 表記 | 定義 |
|---|---|---|
| **Segment（区間）** | `A` `B` `C` のブロック | `(lap, camera)`。**伸縮する**＝滞在時間は体験者が決める。ショーの時計の単位 |
| **Take（演出）** | `🎬 演出` ブロック | 区間に付く、開始規則と占有ポリシーを持つ**剛体**（尺が決まっている）。中に Step を順に持つ |
| **Step（カット）** | Take 内の小ブロック | `Shot` ＋ 尺。1 カット = 1 画 |
| **Shot** | — | `source` ＋ `overlay` ＋ `post` ＋ `transition` |

- **source（映すもの）**: `live:<camIdx>`（ライブカメラ）/ `inherit`（今映っているものを保つ）/ `clip:<asset>`（事前映像）/ `still:<asset>`（静止画）
- **overlay**: `cueId`（マスク合成）または無し
- **開始規則**: `enter+t`（区間進入から t 秒。t=0 で進入直後）/ `exit`（この区間から離脱する瞬間＝ dip の黒中）
  ＋ **`ifMissed`**（`enter+t` のみ・**v3 の最初から入れる**）: `fireOnExit`（既定・未発火のまま離脱したら離脱の瞬間に発火＝ exit へ自動降格）/ `skip`（捨てる。通り過ぎてよい環境演出用）
  ```jsonc
  "start": { "at": "enter", "offsetSec": 20, "ifMissed": "fireOnExit" }
  ```
- **尺**: `sec` / `untilClipEnd` / *(将来)* `untilZone(cam, timeoutSec)`
- **占有ポリシー**: `hold`（既定・ゾーン切替を凍結し、終了時に現在地へスナップ）/ `yield`（境界を跨いだら演出を打ち切って返す）/ *(将来)* `wait`（体験者の到達を待つ・タイムアウト必須）
- **transition**: `cut` / `dip(ms)` / `fade(ms)`（今はカメラ切替＝ dip、cue ＝ fade と別系統になっているものを統一）

### 現行機能の写像（＝ 1 語彙に畳める証明）

| 今 | 新モデルでの表現 |
|---|---|
| 区間 cue | `Take{ start: enter+delay, steps:[{ source: inherit, overlay: cue, dur: untilClipEnd }] }` |
| enter インサート | `Take{ start: enter+delay, steps:[{ source: live:D, overlay: cueId?, post?, dur: N }] }` |
| exit インサート | `Take{ start: exit, steps:[{ source: live:D, ..., dur: N }] }` |
| 全面差し替え（マスク無し cue） | `Take{ steps:[{ source: clip:①, dur: untilClipEnd }] }` |
| 要求の凝った演出 | `Take{ start: enter+20s, policy: hold, steps:[ live:D 4s, clip:① 尺, clip:録画B + overlay:人形, clip:録画C + overlay:人形 ] }` |

**cue と insert が 1 語彙になる** → UI から「インサートショット」という概念そのものが消える。

### 命名 — UI 文言とデータキーを分ける（cue は全面撤去しない）

`cue` は 2 つの役を兼ねている。**素材（マスク + 映像のペア）としての cue はモデル上まだ必要**で、消すのは「区間に cue をぶら下げる」構造の方だけ。

| 面 | 決定 |
|---|---|
| UI 文言（区間上の要素） | **演出 / カット / 映すもの**。「インサートショット」「anchor」「cue を出す」は消す |
| UI 文言（素材ライブラリ） | **素材**（裏の `cue_*` id はそのまま） |
| データ: トップレベル `cues[]` | **改名しない**（素材ライブラリ。既存 show.json / `OverlayCue` / ドキュメント多数が依存） |
| データ: 区間側 | `segments[].takes[]` を**新設**。v2 の `segments[].cues[]` / `insert` は**読み取り互換のみ**、書き出しは v3 の `takes[]` 単独 |
| C# | `ShowTakeDef` / `ShowStepDef` |

書き出しで v2/v3 を併存させない理由: 「どちらが正か」が現場で分岐して事故る（present-flag の幽霊インサート事故と同型）。

### 意図的に先送りする制約

- **同一周に同じカメラのゾーンを 2 回通るコースは表現できない**（キーが `(lap, camera)` のため）。必要になったら `(lap, stopIndex)`（course.order 内の位置）へ移行する。今のコース（A→B→C）では発生しない
- **カメラ ≠ ゾーン**。`camera D` のようにどのゾーンからも選ばれないカメラは、registry に居るだけで良い（演出専用カメラ）。現モデルでも成立するので変更不要

## 4. UI — 「体験者に見えるリボン」

現行（周×カメラのグリッド＋インスペクタ内に埋もれたインサート欄）を、**1 周 = 横 1 本のリボン**へ。

```
1周目  ┃▨ A ▨▨▨▨▨▨┃▨ B ▨▨▨┃🎬 4s┃▨ C ▨▨▨▨┃
3周目  ┃▨ A ▨▨▨▨▨▨┃▨ B ▨▨┃🎬 終盤演出 26s ─ D 4s │ 映像① 8s │ 録画B+人形 7s │ 録画C+人形 7s ┃▨ C ▨┃
```

- **区間は伸縮ブロック**（斜線ハッチで「尺は体験者が決める」ことを表す）。**Take は秒に比例した固定幅**。この対比が「①体験者が決める時間 / ②こちらが決める時間」の視覚的な答えになる
- 「1周目: A|B|C」が「A|演出|B|演出|C」と**そのまま読める** → 要求の「インサート-A-インサート-B-C」
- `anchor:enter/exit` は UI から消え、**リボン上の位置**になる。ただし **連続ドラッグにはしない**（下記）
- Take をクリック → ステップ列を展開。ステップを足す / 並べ替える / 端を掴んで尺を変える
- 空の区間 = 伸縮ブロックだけ = 「そのままライブが映る」と読める（現行の `—` より意味が明確）
- 語彙の統一に合わせて UI 文言も改める：「cue」「インサートショット」→ **「演出」「カット」「映すもの」**

### 位置が持てる意味の限界（`exit` を連続ドラッグにしない理由）

`enter+t` は区間進入からの**決定的な時刻**、`exit` は**いつ来るか分からない事象**で、両者は同じ数直線上にない。連続ドラッグは「右へ寄せるほど遅い」という嘘の連続性を教えてしまう。

- ドラッグは**スナップ 2 種のみ**。区間ブロック内に置けば `enter+t`（t は px から算出）、**区間の右境界線に磁石で吸着**させると `離脱時`。吸着なしに滑らかに移り変わる遷移は作らない
- `exit` の演出は**区間ブロックの外**に、境界線をまたいで描く（区間内には置かない）。「区間内の時刻ではない」ことを形で示す
- 演出ヘッダに**常時テキスト**で `進入 +20s` / `離脱時` を出す。位置だけに意味を持たせない
- 区間ブロックに**実測の平均滞在時間**（実機 heartbeat のログがあれば `実測 平均 12s`）を出す。`enter+20s` が発火しない危険（§8 論点 5）を作者に見せる唯一の手段

## 5. 実装の段取り（今回は実装しない・順序と不変条件だけ確定）

**順序は `0 → B → C → A → D`**（当初案 A 先行から反転。理由は §8 論点 4）。

| 段 | 内容 | リスク | 得られるもの |
|---|---|---|---|
| **0. v3 スキーマを紙で確定** | `takes[]` の形（`start.ifMissed` 込み）を確定する。コードは書かない | なし | 以降の全段が同じ契約を見る |
| **B. 時計と画面の分離** ✅ **完了（2026-07-25）** | `ZoneProgressionLogic` を独立の純ロジックとして抽出（dwell 通過後のゾーン確定を、画面が追従したかに関係なく発火）。`LapCounter` は新イベント `CameraSwitchDirector.ZoneCommitted` を購読。**dwell（人の層）と cooldown＝最小ショット長（画面の層）を分離**。`asZone` 偽装を廃止 | 中（LapCounter / Director を触る） | 既存バグの根治。**出展の安定に単独で効く** |
| **C. TakeRunner** ✅ **完了（2026-07-25）** | 多段 Take の実行体（`InsertController` の後継）。有界・watchdog・返しは再計算。v3 の show.json のときだけ動き、旧経路は空にして所有者を 1 人に保つ。検証は **JSON fixture + EditMode テスト**（UI 不要） | 中〜高 | 凝った演出が実機で本当に動く |
| **A. リボン UI** ✅ **完了（2026-07-25）** | A-1: JS の v3 モデル + node テスト 20 本（fixture 往復で Unity と機械照合）／A-2: `ribbon.js` でリボン描画・スナップドラッグ・演出/カットのインスペクタ。**版で分岐し v2 グリッドは無傷** | 低 | オーサリングが分かりやすくなる |
| **D. 新ソース種別** | `clip` / `still` を第一級の source に（今は「マスク無し cue」で代用）。録画 + CG 合成の素材パイプライン | 低〜中 | 要求の演出が素直に書ける |

### 段 B 実装メモ（2026-07-25 完了・EditMode 578/578 pass・実機未検証）

- **配線変更ゼロで実施**（opus アドバイザー判断・§8 参照）。`ZoneProgressionLogic` は新 MonoBehaviour にせず
  `CameraSwitchDirector` が 2 つの純ロジックを持つ形にした。シーン / prefab の差分なし＝実機事故リスクを負わない
- **⚠ 負債（段 C で返す）**: `ZoneProgressionLogic` は「人の層」なのに画面層のコンポーネントに同居している。
  `ShotDirector` 新設時に人の層へ移す（クラスの XML doc にも明記済み）
- `SwitchCommitted` / `SwitchSource` は**残した**（画面切替の唯一の観測点として段 C の `ShotDirector` が使う）。
  ただし**もう時計は駆動しない**。production の購読者は現時点でゼロ（テストのみ）
- **`Update` の順序が契約**: ①時計 Tick → ②画面（dip 進行 / commit）→ ③`ZoneCommitted` 通知、の順。
  ③を①の直後に出すと exit インサートが `_insertActive` を立てて②の dip 自体を止め、黒中差し替えの予約が
  消化されずに画面が固まる。コード中にも同じ注記を置いた
- **`InsertExitRedirect` に dip 状態ガードを追加**: 予約（`_blackRedirect`）を立てるのは dip が `Down`（まだ黒に
  達していない）ときだけ。それ以外は通常 dip で切り替える。旧実装は「必ず黒の瞬間に呼ばれる」前提だったが、
  時計と画面が非同期になったため成立しなくなった（消化されない予約が次の dip へ持ち越されると
  「戻るはずが insert カメラへ飛ぶ」事故になる）
- **override 解除時に既定映し先を貼り直す**: 時計は遷移でしか発火せず、tracker の再 Pick も同一ゾーンなら
  何も要求しないため、解除時に `_logic.SetAmbient(_progress.Current)` を明示的に呼ぶ（`SwitchWiringTests` T5/T6）

### 段 C 実装メモ（2026-07-25 完了・EditMode 632/632 pass・実機未検証）

- **v2/v3 は show.json の版で分岐する**（`ShowControlClient.PushCueSource`）。**v3 でなければ挙動は完全に不変**＝
  既存の show.json を戻すだけで全部元通りになる退避路。`ShotDirector` への完全統合（cue / insert / ambient を
  1 クラスに畳む）は**やっていない** — 二重経路を作らない目的は「版で切り替える」形で達成し、
  クラス統合は締切後の整理に回す（負債として明示）
- **構成**: [`ShowTakeSchema`](../../Assets/Scripts/Streaming/ShowTakeSchema.cs)（型 + 判別子 + 既定）/
  [`TimelineMigration`](../../Assets/Scripts/Streaming/TimelineMigration.cs)（v2→v3）/
  [`TakeRunnerLogic`](../../Assets/Scripts/Streaming/TakeRunnerLogic.cs)（純ロジック）/
  [`TakeRunner`](../../Assets/Scripts/Streaming/TakeRunner.cs)（実行体）
- **配線は自己修復**: `TimelineDirector.Awake` が `TakeRunner` を持っていなければ自分で `AddComponent` し、
  `TakeRunner.Awake` が Director / overlay / showControl を GetComponent → FindObjectOfType で解決する。
  `Setup Main Demo Scene` にも正規配線を追加済み（**シーン未再生成でも v3 が動く**＝ prefab SerializeField
  欠落で機能全死した過去の事故を繰り返さない）
- **時刻源を差し替え可能にした**（`TakeRunner.SetTimeSource`）。EditMode は `Time.time` が進まないため、
  配線レベル（`TakeWiringTests`）で多段カット・復帰・watchdog を実測できるようにする seam
- **テスト**: `TakeRunnerLogicTests` 23 / `TimelineMigrationTests` 16 / `TakeWiringTests` 9 /
  `TakeFixtureContractTests` 6。fixture `Assets/Tests/Fixtures/show_timeline_v3_canonical.json` は
  **ユーザー要求の 4 カット演出そのもの**を含み、JsonUtility 往復でも壊れないことを固定している
- **未着手（段 D へ）**: `policy: yield`（境界を跨いだら打ち切り）はスキーマにあるが実行はまだ `hold` 相当。
  `transition` / `transitionMs` も解決関数はあるが dip/fade の実適用は Director の既定値のまま

### 段 A-2（リボン UI 本体）— ✅ **実装完了（2026-07-25）・実運用未使用**

`tools/web-compositor/ribbon.js`（新規 989 行）。`timeline.js` への変更は版分岐と変換ボタンのみで、
**v2 の描画・インスペクタ・検証エンジンは無傷**（差分の削除行は import 1 行と `isDirty`/`destroy` の 2 行だけ）。
親が git diff とブラウザ実操作で検証済み（v2 グリッドは従来どおり・コンソールエラー 0・
変換 → リボン描画 → 演出追加 → インスペクタ編集まで実動）。

**既知の未対応（次にやるならここ）**:

- **▶ 検証（矢印キー）は v3 で無効**（disabled + 明示）。v3 セマンティクスへの移植が未了
- **区間の「実測 平均滞在時間」は未実装** — show.json / capture-server に滞在時間の集計が無く読む先が無い。
  §4 が「作者が『山場が出ない』危険に気づく唯一の手段」と位置づけたものなので、**heartbeat に滞在時間を
  足すところから**必要（`ifMissed` があるので事故は防げるが、気づけないままではある）
- BGM は数値入力のみ移植（v2 の試聴プレイヤーは未移植）
- ドラッグは pointer イベントでデスクトップのみ検証（タッチ・キーボード操作は未対応。
  開始位置はインスペクタの数値入力で完全に編集できる）
- `untilClipEnd` のカット幅は推定表示（`≈` 付き）
- `index.html` の節見出しヒント文は v2 の語彙のまま

### 段 A-2 の実装方針（上記の実装が従った制約）

卓（`tools/web-compositor`）は**本番運用ツール**なので、半分移行した状態で放置しないことを最優先にする。

- **モードは「ユーザーが選ぶトグル」ではなく「データの版」で決める**（`isV3(timeline)`）。
  v3 → リボン編集 / v2 → 現行グリッド編集（**一切変更しない**）+ 「v3 に変換」ボタン（片道・明示）。
  こうすると書き出し経路が常に 1 本で、「どちらが正か」が現場で分岐しない（§8 論点 2 と同じ理由）
- **新モジュール `ribbon.js`** に閉じ込め、`timeline.js` からは版で切り替えて呼ぶだけにする。
  途中で止まっても現行エディタは無傷（本番ツールを壊さない）
- 保存は `serializeTimelineV3` / 読み込みは `normalizeTimelineV3`（**実装済み・テスト済み**）。
  `app.js` の `saveTimeline(tl)` はそのまま使える（`postState({timeline, schedule:{rev++, entries:[]}})`）
- **描画**（アドバイザー §8 論点 1 の確定仕様）:
  区間 = 伸縮ブロック（斜線ハッチ）/ 演出 = 尺に比例した固定幅 / ドラッグは**スナップ 2 種のみ**
  （区間内 = `enter+t` / 右境界に磁石 = `離脱時`）/ exit の演出は**区間ブロックの外**に境界をまたいで描く /
  ヘッダに常時テキスト `進入 +20s` `離脱時` / 区間に実測平均滞在時間（heartbeat のログがあれば）
- **インスペクタ**: 演出（開始規則 / policy / once / maxDurationSec）→ カット列（source・素材・cueId・
  尺・遷移・post）。素材ライブラリ（現 cue エディタ）は「素材」として再利用する
- **▶ 検証（矢印キー）は v3 セマンティクスへの移植が要る**。移植までは v3 モードで無効化し、
  その旨を UI に出す（黙って古い挙動を再現しない）
- 参考実装のたたき台: [`timeline-mock.html`](../../tools/web-compositor/timeline-mock.html)（描画のみのモック）

**A が締切のカット可能点**。UI が間に合わなければ現行グリッド UI + JSON 手書きで出展できる（退避経路が残る）。A を先にやると、カット可能点が「ランタイム」側に来て退避できなくなる。

各段で**テストに落として固定する不変条件**：

1. 画面の所有者は常に 1 人（同時所有が起きない）
2. どんな入力列でも演出は必ず終わる（watchdog `maxDurationSec` で強制終了する経路のテスト）
3. 返しは再計算（演出中に移動 → 終了時は移動先が映る）
4. 時計は演出中も止まらない（演出中の周回進行テスト）
5. 横取り中に通過した区間の演出は発火しない
6. **`ifMissed:"fireOnExit"` の演出は、区間滞在が `offsetSec` 未満でも必ず 1 回発火する**
7. **区間を離れた時点で、その区間の未発火演出は必ず決着する（発火 or 破棄）。遅れて別区間で発火してはならない**
   （現行 `InsertLogic` はここが破れている — §8 論点 5 の実コード確認結果）
8. **ラン強制リセット（右グリップ長押し / 卓の ▶ ラン開始）は `hold` 中の演出を必ず割り込んで畳む**（体験者が固まった時のスタッフの唯一の出口）

## 6. show.json v3 スキーマ（段 0 の成果物・**確定**）

以降の全段はこの契約を見る。段 C で fixture `Assets/Tests/Fixtures/show_timeline_v3_canonical.json` を作り、Web（node）と Unity（EditMode）の両側から読んで固定する（v2 の `show_timeline_canonical.json` と同じ手法）。

### 6.1 形

```jsonc
"timeline": {
  "rev": 5,
  "schema": 3,                    // 0 / 未指定 = v2（後方互換）。3 = v3
  "segments": [
    {
      "lap": 3, "camera": 1,      // 区間キー（v2 と同一・変更なし）

      "takes": [                  // v3 の本体。0..N 本
        {
          "id": "t_3B_final",     // 区間内で一意。空なら Unity が "L3C1#0" 形式で補う
          "name": "終盤",          // UI 表示のみ（実行に影響しない）

          "at": "enter",          // "enter" | "exit"
          "offsetSec": 20,        // at=enter のみ。at=exit では無視
          "ifMissed": "fireOnExit", // at=enter のみ。"fireOnExit"(既定) | "skip"

          "policy": "hold",       // "hold"(既定) | "yield"
          "once": true,           // ラン内 1 回
          "maxDurationSec": 0,    // watchdog。0 / 未指定 = コード既定 45

          "steps": [
            { "source": "live",    "camera": 3, "assetUrl": "",
              "cueId": "", "strength": -1, "fadeInSec": -1, "fadeOutSec": -1,
              "trimStartSec": -1, "trimEndSec": -1,
              "durKind": "sec", "durSec": 4,
              "transition": "dip", "transitionMs": 0, "hasPost": false },

            { "source": "clip",    "camera": -1, "assetUrl": "sa://assets/pre_01.mp4",
              "cueId": "", "strength": -1, "fadeInSec": -1, "fadeOutSec": -1,
              "trimStartSec": -1, "trimEndSec": -1,
              "durKind": "untilClipEnd", "durSec": 0,
              "transition": "cut", "transitionMs": 0, "hasPost": false },

            { "source": "clip",    "camera": -1, "assetUrl": "sa://assets/rec_lap1_B.mp4",
              "cueId": "cg_doll_B", "strength": -1, "fadeInSec": -1, "fadeOutSec": -1,
              "trimStartSec": -1, "trimEndSec": -1,
              "durKind": "untilClipEnd", "durSec": 0,
              "transition": "cut", "transitionMs": 0,
              "post": { /* PostParams 7 項目 */ }, "hasPost": true }
          ]
        }
      ],

      "post": { }, "hasPost": false,   // 区間そのものの属性（v2 から継続・変更なし）
      "bgm":  { }, "hasBgm":  false,

      "cues": [ ], "insert": { }, "hasInsert": false   // v2 互換。**v3 の書き出しでは出さない**
    }
  ]
}
```

### 6.2 フィールド規約

**入れ子を極力作らない**。JsonUtility の「null 入れ子を既定値で書く」罠を踏む面を減らすため、`start` / `source` / `overlay` / `dur` はオブジェクトにせず**フラットな文字列判別子 + 値**にする。present-flag が要るのは `post` だけ。

| 規約 | 内容 |
|---|---|
| **`-1` = 継承** | step の `strength` / `fadeInSec` / `fadeOutSec` / `trimStartSec` / `trimEndSec` は `-1` で「`cueId` の素材定義の値をそのまま使う」。v2 の `override` + `hasOverride` は**廃止**（`ShowBgmDef` の -1 継承と同じ流儀） |
| **present-flag は `hasPost` のみ** | `TimelinePresentFlags.Reconcile` の AND 規約（宣言 bool && object != null）を踏襲。Web は `hasPost:false` のとき `post` キー自体を出さない |
| **`0` = コード既定** | `maxDurationSec` / `transitionMs`。`SwitchDirectorLogic.ResolveTiming` と同じ「>0 で上書き」流儀 |
| **未知の文字列は既定へ倒す** | `source` / `durKind` / `transition` / `policy` / `ifMissed` / `at` が未知値なら既定（`live` / `sec` / `dip` / `hold` / `fireOnExit` / `enter`）+ 警告ログ。例外にしない |

### 6.3 実行セマンティクス（ここが契約の本体）

1. **同時に走る take は 1 本**。走行中に別 take の開始条件が成立したら、その take は**待たない** — `ifMissed=fireOnExit` なら離脱時へ持ち越し、`skip` なら破棄。離脱時にもまだ走行中なら**破棄 + 警告ログ**（キューに溜めない＝不変条件 5）
2. **同一区間の take の順序**: `at:enter` は `offsetSec` 昇順 → 同値なら配列順。`at:exit` は配列順
3. **`ifMissed:"fireOnExit"`** — `offsetSec` に達する前に区間を離脱したら、その離脱の瞬間（dip の黒中）に発火する＝ exit へ自動降格（不変条件 6）
4. **区間離脱時に未発火 take は必ず決着する**（発火 or 破棄）。**遅れて別区間で発火してはならない**（不変条件 7・現行 `InsertLogic` が破っている点）
5. **`once`** はラン内 1 回。ランリセット（`control.runEpoch` 変化 / 右グリップ 2 秒長押し）で全クリア。**リセットは走行中の take を必ず畳む**（不変条件 8）
6. **ライブ卓が最優先**: `control.activeCue` 非空 / `control.cameraOverride` 非 null の間は take を発火しない（既存挙動を維持）
7. **watchdog**: `maxDurationSec`（既定 45）を超えた take は強制終了 + `[Take] forced end` ログ（不変条件 2）
8. **終了時は「いま体験者がいるゾーン」へ**（復元でなく再計算・不変条件 3）

### 6.4 不正値の扱い（例外にしない・既存流儀）

- `steps` が空の take → 無視
- `source:"live"` で `camera` が registry 範囲外 → その step を飛ばす（警告）
- `source:"clip"/"still"` で `assetUrl` 空 → その step を飛ばす（警告）
- `durKind:"untilClipEnd"` だが動画でない（静止画 / live）→ `durSec>0` があればそれ、無ければ 4s
- 全 step が飛ばされた take → 発火しない（警告）

### 6.5 v2 読み取り互換（決定的変換・両側で同一）

v3 で書き出す時に v2 キーは出さないが、**読む時は必ず変換する**（既存 APK の端末キャッシュ・焼き込み show.json のため）。変換は純関数 1 箇所に置き、fixture で両側を突き合わせる:

- JS: `timeline-model.js` の `migrateV2Segment(seg)`
- C#: `TimelineMigration.FromV2(ShowTimelineSegmentDef)`

| v2 | v3 |
|---|---|
| `cues[i]` | `take{ at:"enter", offsetSec:cues[i].delaySec, ifMissed:"fireOnExit", once, policy:"hold", steps:[{ source:"inherit", cueId, strength/fade/trim = override があればその値・無ければ -1, durKind:"untilClipEnd" }] }` |
| `insert`（`anchor:"enter"`） | `take{ at:"enter", offsetSec:delaySec, once, steps:[{ source:"live", camera, cueId, durKind:"sec", durSec:durationSec, post/hasPost }] }` |
| `insert`（`anchor:"exit"`） | 同上で `at:"exit"` |
| 変換後の take の並び | `cues[]` の順 → 最後に `insert`（v2 は 1 区間 1 insert なので決定的） |

**v2 の `ifMissed` は `fireOnExit` に倒す**（v2 の実挙動は「遅れて誤爆」だったが、それはバグであって仕様ではない。移行で挙動が変わることを 8.1 に明記する）。

## 7. 決めた既定値と、現場で覆しうるもの

設計を止めないため既定を決めておく。違う意図があれば覆す：

- **占有ポリシーの既定 = `hold`**（演出中は歩いても画面は演出のまま。終わったら現在地へスナップ）。理由: 凝った演出は「見せ切る」意図で作るものだから。歩行を邪魔したくない演出だけ `yield` を選ぶ
- **`maxDurationSec` の既定 = 45 秒**（watchdog の強制終了閾値）。ステップ尺の合計がこれを超える演出は Web 卓が保存時に警告する。
  「有界」を原則で言うだけでは閾値が実装者裁量になり、不変条件 2 が骨抜きになるため**数字で決める**
- **「録画映像 + CG 人形」は 1 本の動画に焼き込む**前提（`clip` 1 枚）。マスク合成で人形だけ別レイヤーにするのは、実時間の合成品質が読めないため後回し。Web 卓の合成タブは素材づくり（焼き込み前の確認）に使う
- **演出中に通過した区間の演出は捨てる**（キューに溜めて後で流さない）。時間に紐づいた演出を遅れて出すと文脈が壊れるため

## 8. 決定ログ — opus アドバイザーの赤入れ（2026-07-25）

ユーザー指示により、論点の判断を opus サブエージェントに委ねた。以下はその判断とメインの採否。

| 論点 | アドバイザー判断 | 採否 |
|---|---|---|
| 1. リボンの読み方 | 修正して採用。`enter+t` と `exit` は同じ数直線に無い → 連続ドラッグ禁止・スナップ 2 種・exit は区間外に描く・常時テキスト併記・実測滞在時間を出す | **全面採用**（§4 に反映） |
| 2. 語彙 | 修正して採用。cue の全面撤去は行き過ぎ。UI 文言だけ変え、素材ライブラリ `cues[]` は改名しない。区間側は `takes[]` 新設・v2 は読み取り互換のみ | **全面採用**（§3 に反映） |
| 3. 既定値 3 つ | 採用 + `maxDurationSec=45` を数字で決める / ラン強制リセットは hold を割り込む | **全面採用**（§6・不変条件 8 に反映） |
| 4. 段取り | **A→B→C→D を B→C→A→D へ反転**。A 先行は「UI では書けるのに実機では先頭ステップしか出ない」最悪の失敗モードを作り、アダプタは C で丸ごと捨てる廃棄コスト。A をカット可能点にする | **全面採用**（§5 を書き換え） |
| 5. 最大のリスク | **山場の演出が体験者の歩速で発火しない**。`enter+20s` は B に 20 秒滞在する前提だが、緊張した体験者は 8 秒で抜ける。リボンは「そこに演出がある」と描き続けるので作者は気づけない。`(lap,camera)+時間オフセット` というキー設計の限界＝**スキーマなので後から覆しにくい** → `ifMissed` を v3 の最初から入れる | **全面採用**（§3・不変条件 6 に反映） |

### 8.1 v2 → v3 移行で意図的に変わる挙動

既存 show.json / 端末キャッシュを v3 として読むと、**1 点だけ実挙動が変わる**（バグ修正なので変える）:

- 旧: 区間進入 + `delaySec` 待ちの間に体験者が離脱すると、その cue / enter インサートは**離脱後に別の区間で遅れて発火**していた（`InsertLogic.Tick` が現在ゾーンを見ない）。さらに**進入先の区間の insert は武装されず落ちて**いた（`OnZoneCommitted` の `_phase != Idle` 早期 return）
- 新: `ifMissed:"fireOnExit"` により**離脱の瞬間に発火**する（別区間へは絶対に漏れない）。`skip` を選べば従来より素直に「出ない」

### 論点 5 の事実訂正（メインが実コードで確認）

アドバイザーは「現行 `InsertLogic` も `Phase.EnterDelay` 中にゾーンが変わると enter が**静かに消える**」としたが、[`InsertLogic.cs`](../../Assets/Scripts/Streaming/InsertLogic.cs) の実挙動は違う。消えるのではなく **2 つの別々の穴**がある:

1. `OnZoneCommitted` 冒頭の `if (_phase != Phase.Idle) return default;` → **進入先の区間の insert が武装されずに落ちる**
2. `Tick` の `EnterDelay` は現在ゾーンを見ずに `_timerEnd` で発火 → **体験者が区間を出た後に、別の区間で遅れて誤爆する**

指摘の骨子（キー設計の限界）はむしろ補強される。この訂正を受けて**不変条件 7**（区間離脱時に未発火演出は必ず決着する / 遅れて別区間で発火しない）を追加した。

## 9. 参照

- 現行スキーマ v2 と実装: [2026-07-19_webui-timeline-authoring.md](2026-07-19_webui-timeline-authoring.md)
- 切替の時間軸ガード・追従・フェイルソフト: [2026-07-19_viewer-ux.md](2026-07-19_viewer-ux.md)
- 凍結由来バグの実例（この設計が殺したい failure mode）: [logic_audit_2026_07_23](../memory/logic_audit_2026_07_23.md)
- 契約の正本: [.claude/rules/streaming.md](../rules/streaming.md)
