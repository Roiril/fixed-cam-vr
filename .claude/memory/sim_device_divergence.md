---
name: sim-device-divergence
description: 卓のショーシミュレータが実機と食い違った 3 件（2026-07-27）。「卓で沈黙／卓だけ再生」を疑う時の最初の 3 点
metadata: 
  node_type: memory
  type: project
  originSessionId: 0f53f117-16cc-435a-99a9-0074659dfcbe
  modified: 2026-07-27T03:20:39.655Z
---

# シミュレータ ⇄ 実機の食い違い（2026-07-27 修正済み・再発の型）

ユーザー報告「**1周目チュートリアルラインを跨いでもイベントが発動しない**」の調査で 3 件見つけた。
どれも「卓が嘘をつく」型＝[シミュレータの価値がマイナスになる](../plans/2026-07-25_show-simulator.md) 失敗。

| # | 症状 | 真因 | 修正 |
|---|---|---|---|
| 1 | **1 周目スタート区間の演出が卓で永久に出ない**（実機では出る） | 体験者は最初からスタート領域に居るので時計が確定イベントを出さない。実機は `LapCounter.SeedCurrentZone` が塞いでいたが、**両ランナー（`ShowScenarioRunner.Run` / `scenario-engine.js createShowRunner`）にシードが無かった**。`hasSeg=false` のままなので `ifMissed=fireOnExit` の離脱決着も死ぬ | 両ランナーの先頭で `seg(lap=1, startCamera)` をシード |
| 2 | 使えないラインの演出が**進入した瞬間に出る**（卓は「発火しません」と警告しながら再生。実機も空 lineId で同じ） | `onLine` を false に落として時刻トリガーへ化けさせていた（`offsetSec`=0 なので即 due） | `onLine` は保ち `lineIndex=-1`（決して due にならない枠）で表す |
| 3 | ×4 / ×16 の早送りで**横断が数えられない** | 1 フレーム分の tick 全部に同じドラッグ位置を配る＝テレポート。1 tick 1m 超は `LINE_MAX_STEP_M` でテレポート扱いになり横断が無効 | `show-sim.js advance()` で前回供給位置→今の位置を tick 数へ等速分配 |

## 次に同じ報告が来たら

1. **卓と実機のどちらが正しいかを先に決める**。実機側の駆動点は `LapCounter → CueScheduler.CameraEntered → TimelineDirector → TakeRunner`、卓側は `createShowRunner.step`。**片方にしかない初期化**（シード・リセット・購読）が最初の容疑者
2. 再現は Unity を開かずに **node で show.json を直接食わせる**のが速い（`buildScenarioConfig` → `runScenario` に歩きサンプルを渡すだけ）。ブラウザ操作より桁違いに速く、証拠も正確
3. 契約の正本は [plans/2026-07-27_position-trigger.md](../plans/2026-07-27_position-trigger.md) §3 と [rules/streaming.md](../rules/streaming.md)。**実装が契約と逆になっていないか**を条文単位で照合する（#2 はまさにそれ）

## 併せて見つけた「バグではないが刺さる」こと

- **ラインの両端が床の内側にあると回り込める**（＝踏まずに通過できる）。壁の外まで伸ばすのが正しい引き方
- **演出が走っている間に別区間へ入ると、その区間の演出は `ifMissed` に関係なく破棄される**（同時 1 本の原則）。
  尺の合計が区間の実滞在を超える構成だと後続が連鎖的に消える → 卓の「実測 平均滞在」を見ること
