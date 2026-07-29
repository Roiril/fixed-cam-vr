---
name: show-run-skeleton
description: 体験の骨格（導入→3周→終了）を触る前に：ゲートは CueScheduler 1 点／終了は次フレーム判定／導入で録画を消さない／凍結ラッチを増やさない
metadata: 
  node_type: memory
  type: project
  originSessionId: 9c9e9742-4d99-47bc-8b46-e7eb4d64ed2b
  modified: 2026-07-29T03:28:50.257Z
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

関連: [[glitch_and_latency]]
