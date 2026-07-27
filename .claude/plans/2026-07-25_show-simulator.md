# 実機なしでショーを検証する — フロアマップ駆動シミュレータ

status: S1–S4 実装済み（2026-07-25）。Web ミラーは golden 一致（23/23 イベント・時刻も厳密一致）、
卓 UI はブラウザ実測済み（記録 → 保存 → 再実行まで通し）。設計の決定（§1 の trace 形・§6）は変更していない。

| 段 | 実体 | 状態 |
|---|---|---|
| S1 | `Assets/Scripts/Tracking/ZonePickLogic.cs` | ✅ |
| S2 | `Assets/Scripts/Tracking/ShowScenarioRunner.cs` + `Assets/Tests/Fixtures/scenario_walk*.json` | ✅ |
| S3 | `tools/web-compositor/scenario-engine.js` + `scenario-engine.test.mjs`（golden 照合） | ✅ |
| S4 | `tools/web-compositor/show-sim.js` / `show-scenario.js` / `zone-layout.js` + 卓の 🕹 パネル | ✅（実機の代わりにならない範囲は UI に明示） |

実装で決めた補足（§1・§2 の運用細部）:
- **JS は時刻計算だけ `Math.fround`** で C# の float 精度に合わせる（そうしないと `t_B_late` の終了が 1 tick ずれる）。
  照合の契約は従来どおり ±1 tick だが、許容の中に drift を隠さないため実装は厳密一致を狙う
- **ゾーン展開は `layout.grid` 専用**（cuts のみの show.json では実行しない）。Unity も grid 優先で、卓の保存は常に grid を書く。
  cuts しか無い show.json はフロアマップの「cuts から自動生成」→ 💾 保存で grid 化してから使う（UI にその案内を出す）
- **`untilClipEnd` の尺**は 2026-07-25 に**素材の実尺をブラウザで実測**するようにした
  （[media-duration.js](../../tools/web-compositor/media-duration.js)。`<video preload=metadata>` で測ってキャッシュ →
  `resolveStepDuration` が trim を適用）。測れないもの（`sa://` 焼き込み URL・静止画）だけ従来の trim 推定 / watchdog に落ち、
  そのときだけ ⚠ を出す
- **旧「▶ 検証（矢印キー）」は削除した**（§5 の「S4 完成時に置き換える」を実施。timeline.js から 214 行を撤去し、
  v2 / v3 とも ▶ 検証 = このシミュレータへ移動する導線に統一）

### S4 の追加実装（2026-07-25 後半）

- **画面プレビューを実画にした**: `composite-view.js`（カメラ列・cue エディタと同じ WebGL 合成器）を
  provider で駆動し、**ライブ映像 + カット素材 + マスク + post（カット > 区間 > カメラ > 全体）** を Quest と同じ式で描く。
  カットが変わった瞬間に素材を `trimStart` へ頭出しし、早送り時は `playbackRate` も合わせる。
  カメラ未接続でも素材・マスク・post は出る（ライブだけ黒）。**色チップとテキストは内訳表示として残した**
- `buildScenarioConfig(state, { getDuration })` で尺の実測値を注入。実測できたカットは警告に出さない

## ユーザーの指摘（出発点・そのまま正しい）

> unity を使わなくてもこの webUI を使って実機検証とほぼ同義のことを作れるよね。だって HMD が
> このコア機能に関与するのはフロアマップ内の位置だけで、映像は映すだけなんだから。

**成立する。** 段 B で「体験者の位置は時計であって画面ではない」を実装した結果、体験の中核は

```
位置(x,z) → ゾーン判定 → dwell → 確定ゾーン → 周回・区間 → 演出解決 → 映すべき Shot
```

という**決定的な関数**になっている。HMD からの入力は `(x, z)` だけ。だからフロアマップ上でドットを
動かせば、実機と同じ判断が再現できる。映像は「映すだけ」で、卓には既に WebGL 合成器がある。

## 1. ただし条件 — シミュレータが嘘をついたら価値はマイナス

「ほぼ同義」を名乗るには、**Web の再現が Unity の実装と同じ答えを出す保証**が要る。
JS で書き直した第二実装は必ず drift する（2026-07-23 の監査で Web と Unity の食い違いを何度も出した）。

そこで**振る舞いの golden 一致**を仕組みにする。データ契約を fixture で固定した
（`show_timeline_v3_canonical.json`）のと同じ手を、今度は**時間発展**に対して打つ:

```
scenario.json（共有 fixture）
  layout(grid / overlap / hysteresis) + course.order + timeline(takes)
  + samples: [{tMs, x, z}, …]        ← 体験者の歩き（course 座標）

  ├─ Unity: ShowScenarioRunner（純 C#・EditMode）  → trace
  └─ Web:   scenario-engine.js                     → trace

  2 つの trace が一致することをテストで固定する（golden）
```

**これが無い簡易シミュレータは作らない。** 「卓で確認したのに実機で違う」は最悪の失敗で、
シミュレータを信じた分だけ被害が大きくなるから。

### trace の形（両側で完全一致させる）

固定 tick（**20ms / 50Hz**）で回し、時刻は**整数 ms**。イベントは発生順の配列:

| event | payload | 意味 |
|---|---|---|
| `zone` | `{t, cam}` | 時計が確定させたゾーン（dwell 通過） |
| `lap` | `{t, lap}` | 周回が進んだ |
| `seg` | `{t, lap, cam}` | 区間へ進入（演出の評価点） |
| `take` | `{t, id}` | 演出開始 |
| `step` | `{t, id, i, src, cam}` | カット開始 |
| `end` | `{t, id, forced}` | 演出終了（forced=watchdog） |
| `screen` | `{t, cam}` | 画面の切替が確定（cooldown / 凍結を通過） |

比較は **イベント列（種別と payload）は完全一致・時刻は ±1 tick 許容**。
浮動小数の境界（`now - since >= dwell`）が C# float と JS double で 1 tick ずれても、
セマンティクスの drift だけを検出できるようにするため。

#### 起動時のシード（先頭の `seg`）— 2026-07-27 修正

トレースの先頭は必ず `seg{t=samples[0].tMs, lap=1, cam=startCamera}`。体験者は最初から
スタート領域に居るので**時計（`ZoneProgressionLogic`）は確定イベントを出さない**。実機はこの穴を
[`LapCounter.SeedCurrentZone`](../../Assets/Scripts/Tracking/LapCounter.cs) が塞いでいる
（現在ゾーンを「初回進入」として CueScheduler → TimelineDirector → TakeRunner へ流す）。

**両ランナーにこのシードが無く、1 周目スタート領域の演出（`at=enter` / `at=line`）が永久に武装されなかった**
（2026-07-27 実害: 1 周目の通過ラインを跨いでも卓では何も起きない。実機では出る＝**シミュレータが嘘をついた**側）。
`hasSeg=false` のままだと離脱時の決着（`ifMissed=fireOnExit`）も働かない。
固定テスト: `ShowScenarioRunnerTests.StartSegmentTake_IsArmed_WithoutLeavingAndComingBack` /
node 側 `スタート区間の演出は、一度出て戻らなくても武装される`。

## 2. 何が置き換えられて、何が置き換えられないか（正直に）

**置き換えられる（＝バグが棲んでいる場所）**

- ゾーン判定（重なり・ヒステリシス）／dwell／周回カウント（進行ポインタ）
- 演出の発火：`enter+t` / `exit` / `ifMissed` / `once` / 同時 1 本 / watchdog / `yield`
- 多段カットの進行、復帰先の再計算、区間 post の 3 段解決、BGM の carry-forward
- 画面のクールダウン・凍結、手動優先
- **「この歩き方をしたら何が起きるか」の全数確認**（実機では 1 回歩くのに数分かかる）

**置き換えられない（＝実機でしか分からない）**

- VR での**スケール感・立体視**、ScreenAnchor の首追従の気持ちよさ
- dip の黒の**体感時間**、フェードの繋がり
- MJPEG の**実レイテンシ・stall・砂嵐**、Wi-Fi 由来の挙動
- 位置合わせ（2 点登録）、OS recenter、コントローラ触覚
- Quest の**性能**（fps・発熱）

→ **シミュレータは「ロジックの検証」、実機は「体感の検証」**と役割を分ける。
UI にもこの但し書きを出し、「卓で通ったから実機で確認しなくてよい」と誤解させない。

## 3. 構成

| 層 | Unity（正） | Web（ミラー） |
|---|---|---|
| ゾーン判定 | **`ZonePickLogic`（段 S1 で新設）** — `PlayerZoneTracker.Pick` の純関数版 | 同ロジックの JS |
| 時計 | `ZoneProgressionLogic` | 同 |
| 周回 | `LapCounterLogic` | 同 |
| 演出 | `TakeRunnerLogic` | 同 |
| 画面 | `SwitchDirectorLogic` | 同 |
| 束ね | **`ShowScenarioRunner`（段 S2 で新設）** — 上記を tick で回して trace を吐く | `scenario-engine.js` |

`ZonePickLogic` の抽出は副産物として重要: 現状 `Pick` は MonoBehaviour の private で、
**チェーンの中で唯一単体テストが無い箇所**だった。

## 4. UI（卓）

フロアマップの既存「シミュレーション（ドットをドラッグ）」を育てる:

- ドットをドラッグ → その位置を毎 tick 供給して**ショーが実時間で進む**
- 同時に表示: **いま画面に映っているもの**（WebGL 合成器で live / 素材 / post を再現）、
  現在の周回・区間、走行中の演出とカット、次に来る演出、イベントログ
- **記録と再生**: ドラッグの軌跡を `samples[]` として保存 → シナリオ化。保存したシナリオは
  ワンクリックで再実行でき、そのまま golden 比較の入力になる（現場で見つけた不具合を
  「この歩き方」として固定できる）
- 早送り（×1 / ×4 / ×16）と一時停止。全数確認のための「よくある歩き方」プリセット
  （速い / 遅い / 引き返す / 立ち止まる / 演出中に歩く）

## 5. 段取り

| 段 | 内容 | 依存 |
|---|---|---|
| **S1** | `ZonePickLogic` を純関数として抽出 + EditMode テスト。`PlayerZoneTracker.Pick` はこれを呼ぶ薄いラッパにする（挙動不変を既存テストで担保） | — |
| **S2** | scenario / trace の形を確定。`ShowScenarioRunner`（純 C#）+ EditMode テストで golden fixture を生成・検証 | S1 |
| **S3** | `scenario-engine.js`（JS ミラー）+ node テストで **同じ golden fixture に一致**することを固定 | S2 |
| **S4** | 卓 UI：フロアマップ駆動の実時間シミュレーション + 画面プレビュー + イベントログ + 記録/再生 | S3 |

S3 が通るまで S4 の UI は「実機と同義」と名乗らない。

## 6. 決めたこと

- **tick は 20ms 固定**（50Hz）。実機は可変 fps だが、判定は時刻差で書かれているので tick を固定しても
  セマンティクスは変わらない。再現性を取る
- **trace 比較は「順序と payload は厳密・時刻は ±1 tick」**（§1）
- **シナリオは show.json とは別ファイル**（`tools/web-compositor/scenarios/*.json`）。
  ショーの設定と検証入力を混ぜない
- **既存の ▶ 検証（矢印キー）は S4 完成時に置き換える**（v3 で無効化したまま二重に持たない）
