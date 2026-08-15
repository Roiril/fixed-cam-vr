---
name: show-run-skeleton
description: 体験の骨格（導入→3周→終了）を触る前に：ゲートは CueScheduler 1 点／終了は次フレーム判定／導入で録画を消さない／凍結ラッチを増やさない／終わり方の出口は 4 つ（終幕の合図・周回・時間切れ・卓）
metadata: 
  node_type: memory
  type: project
  originSessionId: 9c9e9742-4d99-47bc-8b46-e7eb4d64ed2b
  modified: 2026-08-15T06:44:53.423Z
---

2026-07-29 に企画書が学会論文版（`PR0490_1.pdf`）へ差し替わり、「3 区間を 3 周・導入を含め 3 分以内」が
体験の骨格として実装された。契約の正本は `.claude/rules/streaming.md` の「体験の骨格」節、
実装記録は `.claude/plans/2026-07-29_proposal-v3-implementation.md`。

触る前に知っておくべき 4 つ。**どれも「そうしないと壊れる」理由がある**：

1. **導入・終了のゲートは `CueScheduler.SetShowGate` 1 点。** ここを閉じると演出の武装・端末内録画・
   区間 post / BGM・実測滞在が全部止まる。下流それぞれに条件を配ると必ず片方を忘れる。
   画面のカメラ切替は Director 側なので止まらない（導入では映像を出したい）。
2. **導入 → 本編は `ShowControlClient.BeginMainRun()`。`TriggerRunReset` ではない。**
   後者は `SegmentRecorder.ResetRun`（端末の録画を全消去）を含むので、ラン開始が複数系統に増えて
   「遅れて届いた runEpoch が 1 周目の録画を消す」経路ができる ＝ 3 周目の素材が黙って消える。
3. **終了の判定は次フレームの `Tick`。** 周回の確定と離脱時演出の発火は同じ同期連鎖の中で起きるので、
   周回の変化を受けたその場で終わらせると 3 周目最後の区間の離脱時演出が始まる前に体験が終わる。
   走行中の演出は見せ切る（上限 12s）。
4. **終了で凍結ラッチを増やさない。** 見えなくなるのは `ShowEndingFader` の黒のおかげで、
   切替は裏で回ったまま。凍結が解けない事故はこの codebase で 4 回起きている。

現地の右グリップ長押しも `ShowControlClient.BeginNewVisitorRunLocal()` を通す（卓の ▶ ラン開始と同じ号令元）。
片方だけに処理を足すと非対称ができる。

## ⚠⚠ 「新しい体験者」で戻すものを足し忘れると、黙って持ち越される（2026-08-15）

号令元は 3 つ（卓の ▶ ＝ `TriggerRunReset` / 現地の右グリップ ＝ `BeginNewVisitorRunLocal` /
導入 → 本編 ＝ `BeginMainRun`）。**`ResetRun` を持つ実行体を足しても、ここへ繋がなければ黙って残る。**
同日に見つけた実例が 2 件ある。

- **`ShowSoundDirector.ResetRun()` の呼び出し元がどこにも無かった。** ＝ 導入の音のラッチが
  1 人目で立ったまま、**2 人目以降は隔離も管の点灯も鈴も終幕も無音**（展示は 1 日数十人）。
  ⇒ 直し方は「号令へ足す」ではなく **`SoundCueLogic` が導入の段 0 の縁で自分で落とす**にした。
  段 0 はランリセットでも中止からの復帰でも必ず通るので、**配る側の実装に依存しない**
- **`CameraFeelFx.ResetAll()` は体験の終了でしか呼ばれていなかった。** 途中で打ち切って交代すると
  凍結・焼き付き・aura が持ち越される ⇒ `ShowRunDirector.BeginRun()` に足した
- **現地リセットは卓が掛けた占有（固定カメラ・手動 cue）を外していなかった** ⇒ `ReleaseLiveHolds()`。
  卓が落ちた現場で「前の映像が残る / 自動切替が凍結したまま」が起きる

**新しく「体験 1 回ぶんの状態」を持つ実行体を足したら、`BeginRun` か段 0 の縁のどちらかへ必ず繋ぐ。**
⚠ **繋がっていないことはテストでは捕まらない** — 既存のテストは `ResetRun` を自分で呼んでおり、
**呼ぶ前提を自分で作っていた**ので、呼び出し元が無い穴を素通しした。

## 終わり方の出口が 4 つになった（2026-08-15）

`canon/LEDGER.md` 0048 で **終幕の合図**（`run.outro.afterTakeId` が指す演出の終了）が入った。
判断は [`EndingCueLogic`](../../Assets/Scripts/Streaming/EndingCueLogic.cs)、
落とすのは `ShowRunDirector.BeginRun()` 1 か所（＝ ユーザーが名指しした「周回リセットで
リセットされるフラグ」）。

| 出口 | いつ |
|---|---|
| **合図**（新） | `afterTakeId` の演出が走って、そして終わった |
| `endGraceSec` / `endHoldMaxSec` | 周を走り切って、走行中の演出を見せ切った |
| `hardLimitSec` | 時間切れ（既定 300 秒） |
| `RequestFinish` | 卓の ⏹ / 導入中の終了 |

⚠ **出口を足しただけで、1 つも外していない。** 合図が指す演出が最後まで走らない現場でも体験は必ず終わる。
⚠ **合図は「走っていない」だけを見ない** — 演出が始まる前も `ActiveTakeId` は空なので、
空だけで撃つと本編に入った瞬間に終わる。

関連: [[glitch_and_latency]]
