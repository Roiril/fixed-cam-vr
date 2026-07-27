# 位置トリガー — 「体験者がこの位置に来たら演出」

status: implemented（2026-07-27。**EditMode 724/724 / node 80/80** 通過・卓はブラウザ実操作で確認・**Quest 実機未検証**）

契約の親は [2026-07-25_shot-timeline-foundation.md](2026-07-25_shot-timeline-foundation.md) §6（v3 スキーマ）。
本ファイルはそこへの**追加のみ**を記述する（既存 show.json はそのまま読める）。

## ユーザー要求（原文）

> 固定カメラプロジェクトについて、演出のトリガーに、「体験者がこの位置X」みたいなのを、
> フロアマップで位置Xを置くみたいなことをして作れそうだよね。設計から実装までお願い

## 1. 何を足したか（1 行）

**演出（Take）の開始規則に 3 つ目「位置」を足した。** 進入 +t 秒 / 離脱時 に並ぶ第一級の開始条件で、
発火点はフロアマップに置いた円（course 空間）＝ **位置トリガー**。

```
開始規則:  進入 +t 秒（時刻）  |  離脱時（事象）  |  この位置に来たら（場所） ← 追加
```

## 2. 設計の核 — 位置は「区間の中の開始条件」にする

位置トリガーを**区間 (lap, camera) の外に置かない**。演出は今までどおり区間に属し、
変わるのは「武装した演出がいつ due になるか」だけ（時刻 → 位置）。

理由:

| 選択肢 | 判断 |
|---|---|
| **A. 区間内の開始条件にする（採用）** | `once` / `ifMissed` / 「同時 1 本」/ 離脱時の決着 / ラン リセット / リボン UI の置き場が**全部そのまま効く**。`lap` が要る（「3 周目にここへ来たら」）ので、どうせ周の次元は必要 |
| B. lap だけに紐づく独立トリガー表 | 武装ライフサイクル・once 管理・UI の置き場が二重になる。得るものは「ゾーン確定を待たずに発火できる」だけ |

**B を捨てた代わりに残る制約（作者に見せる）**: 位置トリガーは
**その区間に居ると時計が確定しているあいだ**しか発火しない。位置円をゾーン境界の
すぐ内側に置くと、dwell（既定 0.5s）ぶんの確定待ちで取り逃す。
→ 卓は「その円がどのカメラのゾーンにあるか」を判定し、演出の区間と食い違えば警告する。

## 3. スキーマ（追加分）

### 3.1 `layout.spots[]`（フロアマップで著作）

```jsonc
"layout": {
  "spots": [ { "id": "spot_1", "x": -0.62, "z": 0.15, "rM": 0.25, "label": "人形の前" } ]
}
```

- `id` は take から参照するキー（卓が `spot_1`… で採番。UI では `label` を表示）
- `rM` = 円の半径 (m)。既定 **0.25**（回廊幅 0.45m に対して歩いて確実に踏める大きさ）
- 判定に使うのは **HMD の course 空間 XZ**（`ShowControlClient.HeadCourseXZProvider`／登録済み座標）
- `layout` の一部なので **端末キャッシュ・APK 焼き込み・卓の shallow 置換保存**に自動で乗る
  （`regPoints` と同じ扱い。サーバ側の変更は不要）

### 3.2 演出（Take）の開始規則

```jsonc
{ "at": "spot", "spotId": "spot_1", "holdSec": 0, "ifMissed": "skip", "offsetSec": 0 }
```

| フィールド | 意味 |
|---|---|
| `at: "spot"` | 3 つ目の判別子（未知値は従来どおり `enter` へ倒す） |
| `spotId` | `layout.spots[].id`。空 / 未定義 id は**発火しない**（卓が警告・実機はログ 1 回） |
| `holdSec` | 円の中に**連続で**この秒数居たら発火（既定 0 = 入った瞬間）。通り抜けでは出したくない演出用 |
| `offsetSec` | **at=spot では無視**（卓は 0 で書き出す）。「入ってから N 秒後」は holdSec で表す。円を出た後に発火するのは事故なので採らない |
| `ifMissed` | そのまま効く（この区間に居るあいだに来なかった時）。`skip`=出さない（**卓の既定**）/ `fireOnExit`=離脱の瞬間に出す |
| `once` / `policy` / `maxDurationSec` / `steps` / `bgm` | 変更なし |

**発火は 1 区間滞在につき最大 1 回**（武装は区間進入時のみ・発火で武装から外れる）。
`once=true` ならラン内 1 回（従来と同じ）。

### 3.3 判定の細部（純ロジック `SpotTriggerLogic`）

- 入る = `dist <= rM` / 出る = `dist > rM + 0.08`（**出のヒステリシス 8cm・コード定数**）。
  円の縁でのトラッキング揺れで `holdSec` の計時が 0 に戻り続けるのを防ぐ
- `holdSec` は**連続滞在**（出たら 0 に戻る）
- 走行中 / ライブ卓の抑止中に条件が成立した場合は、時刻トリガーと同じ規則で決着させる
  （`skip`=破棄 / `fireOnExit`=離脱時へ持ち越し）。**待たせない**（親計画 不変条件 5）
- 位置が取れない（`HeadCourseXZProvider` 未注入・未登録）間は発火しない（警告ログ 1 回）

## 4. 実装（差分）

| 層 | 変更 |
|---|---|
| スキーマ | `ShowSpotDef` + `ShowLayoutDef.spots` / `ShowTakeDef.spotId` `holdSec` / `TakeSchema.AtSpot` |
| 純ロジック（新） | [`SpotTriggerLogic`](../../Assets/Scripts/Streaming/SpotTriggerLogic.cs) — 円の内外・連続滞在秒。UnityEngine 非依存 |
| 純ロジック | [`TakeRunnerLogic`](../../Assets/Scripts/Streaming/TakeRunnerLogic.cs) — `Def.onSpot/spotIndex/holdSec`、due 判定に滞在秒を受ける（`Tick` の任意引数） |
| 実行体 | [`TakeRunner`](../../Assets/Scripts/Streaming/TakeRunner.cs) — layout から円を取り込み（**スロット割当は session 内で不変**＝走行中の layout 更新で once 状態を壊さない）、毎フレーム HMD 位置で Tick |
| 実機なし検証 | [`ShowScenarioRunner`](../../Assets/Scripts/Tracking/ShowScenarioRunner.cs) に `Spots` を追加（歩きの x,z は既にある） |
| 卓（データ） | `timeline-model.js`（at=spot / spotId / holdSec）・`show-scenario.js`（cfg へ変換 + 警告） |
| 卓（UI） | `floormap.js` に **🎯 位置トリガー**モード（置く / 掴んで移動 / 縁をドラッグで半径 / 右クリック削除 / 一覧でラベル・半径・使用中の演出数）。`ribbon.js` の演出インスペクタに開始規則「この位置に来たら」+ 位置選択 + 滞在秒 |
| 卓（ミラー） | `scenario-engine.js` に `SpotTrigger`（C# の移植）。▶ 検証（歩き）で位置トリガーも発火する |

### 卓が出す警告（黙って出ない状態を作らない）

1. `spotId` 未選択 / `layout.spots` に無い id → 「発火しません」
2. 円の中心が**別のカメラのゾーン**の上 → 「この位置はカメラ C のゾーンです（この演出はカメラ B の区間）」
3. 円の中心が**未割当タイル**の上 → 「ゾーン未割当の場所です」
4. 位置トリガーは**リボン上で位置を持たない**（時間軸に乗らない）ので、区間ブロック内の
   専用レーンに 🎯 付き・破線で描き、ヘッダに常時 `位置「人形の前」` と出す
   （親計画 §4「位置だけに意味を持たせない」と同じ理由）

## 5. テスト

| 層 | テスト |
|---|---|
| C# | `SpotTriggerLogicTests`（内外・ヒステリシス・連続滞在・未定義・境界）／`TakeRunnerLogicTests` に位置トリガー 6 本／`TakeFixtureContractTests` に fixture 契約 |
| 共有 fixture | `show_timeline_v3_canonical.json` に位置トリガーの演出を追加（Web の `take-model.test.mjs` と Unity が同じ形を読む） |
| golden トレース | `scenario_spot.json` / `scenario_spot.trace.json` を新設。**C# が生成し JS が照合**（歩いて円に入る / 通り過ぎる / 立ち止まる の 3 パターン） |
| node | `take-model.test.mjs`（往復）・`scenario-engine.test.mjs`（golden + SpotTrigger 単体） |

## 6. 決めたこと（覆すなら理由と一緒に）

- **円（半径）にした**。矩形にすると回転を持ちたくなる（course 空間の向き付き矩形）が、
  発火点は「そこへ立つ」意図なので円で足りる。判定は距離 1 本＝ミラーが壊れにくい
- **`offsetSec` は使わない**（§3.2）。「円を出た後に発火」は作者の意図とほぼ常にズレる
- **卓の既定 `ifMissed` は `skip`**（時刻トリガーの既定 `fireOnExit` と違う）。
  位置指定の演出を「来なかったけど離脱時に出す」のは、場所に意味を持たせた著作意図と矛盾する。
  データ上はどちらも書けるので、現場で覆せる
- **高さ (y) は見ない**。しゃがみ・背の高さで発火が変わると現場で説明できない
- **1 つの円を複数の演出が参照してよい**（周ごとに別の演出を同じ場所に置く＝主な使い方）

## 6.5 ついでに直した既存の flaky テスト（この作業中に発覚）

`TakeWiringTests` / `SwitchWiringTests` は **Editor の実フレーム間隔に依存して run ごとに落ちていた**。
原因は 2026-07-26 に遷移を「カットごとの引数」へ移した時から: カットの `transition` 既定が dip
（= 170ms・落とし 68ms）で、rig が `dipDownSec=0` にしても**カット側の値が上書きする**ため、
`PumpDirector` の 4 フレームで `Time.unscaledDeltaTime`（フォーカス中 7〜16ms / 非フォーカス数百 ms）が
68ms 貯まるかどうかで結果が変わっていた（今回 1 件 → 7 件 → 1 件と run ごとに違う顔で落ちた）。

- [`CameraSwitchDirector.SetDeltaSource`](../../Assets/Scripts/Streaming/CameraSwitchDirector.cs) を追加
  （dip の dt を差し替える **EditMode 用 seam**。`TakeRunner.SetTimeSource` と同じ流儀・本番挙動は不変）
- 両 wiring テストの rig で `SetDeltaSource(() => 1f)`（1 フレーム = 1 秒）にし、
  遷移が検査対象でない fixture は `transition = cut` に落とした（dip の計時は `DipTransition_*` の担当）
- 結果: 同条件（Editor フォーカス中）で **724/724 が 2 回連続 green**

## 7. 残り（実機で確かめること）

- 円の半径 0.25m が「歩いて確実に踏める / 意図せず踏まない」の妥当な既定か
- ゾーン境界の近くに置いた円が dwell 0.5s の確定待ちで取り逃さないか（§2 の制約）
- `holdSec` を使った「立ち止まったら」の体感（0.5〜1.0s あたり）
