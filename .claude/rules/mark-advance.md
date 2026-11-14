---
name: mark-advance
description: 報告でカットを 1 つ進める（step の markAdvance）の現行の契約。演出は畳まず、そのカットだけを終える
paths:
  - "Assets/Scripts/Streaming/TakeRunner.cs"
  - "Assets/Scripts/Streaming/TakeRunnerLogic.cs"
  - "Assets/Scripts/Streaming/ShowTakeSchema.cs"
  - "Assets/Scripts/Streaming/ShowControlClient.VisitorRun.cs"
  - "Assets/Tests/Streaming/TakeMarkAdvanceTests.cs"
  - "tools/test_mark_advance_log.py"
  - "Assets/Tests/Streaming/TakeStepUntilBeyondTests.cs"
---

# 報告でカットを進める（markAdvance）

**現行の契約だけを書く。** 変わったら行を書き換える（`rules/doc-sync.md`）。報告と演出の骨格は [show-run.md](show-run.md)。

## 契約

- step の `markAdvance`（既定 false・キーが無ければ false）は、そのカットで報告が成立したとき演出を畳まず、
  そのカットだけを今で終える（`SetCurrentStepEnd(now)`）。次の Tick で次のカットへ進み、最後のカットなら `Completed` で終わる
  — `TakeRunnerLogic.NotifyMarkPressed` が `MarkResult.Advanced` を返す
- 見る順は ⓪ 報告済みの印 → ① 締めのカット（`untilMark` = Released）→ ①' markAdvance → ② `dismissible`（Dismissed）。
  同じ演出が `dismissible` でも、旗のカットでの報告は進める意味になる
- 最初の数秒の受け付けない時間（`IsMarkTooEarly` / `MarkGraceSec`）は変わらない
- `ShowControlClient` は Advanced を Dismissed と同じく報告成立として数える（集計とスイの返事も同じ）。呪いを解くのは Released だけ
- 旗のカットは報告が効くカットとして扱う（`TakeRunnerLogic.ActiveRemovable`）
- 押し始めの応答（`markStartCueId`）が出ていたら、応答の状態だけ落とす。待機の画は戻さない（次のカットが画を出し直す）
  — `TakeRunner.DropMarkResponseForAdvance`
- 側待ちのカット（`untilBeyond`）の `beyondMinSec`（既定 0）は、線の向こうに既に居ても始まってからその秒までは畳まない。報告（markAdvance）と watchdog は待たない — `TakeRunnerLogic.EndStepIfBeyond` / `TakeRunner.BuildStepBeyondMinSec`。卓の白名簿は `timeline-model.js` の `beyondMinSec`
- 旗の配列は台本から作り、1 本も立っていなければ null — `TakeRunner.BuildStepMarkAdvance`。
  卓の白名簿（`timeline-model.js` の `markAdvance: !!s.markAdvance`）と名前を揃える

## 観測（`ShowTelemetryHost` と `analyze-xp-log.py` を対で直す）

- `ev=markadv take=<id> step=<進める前のカット> n=<累計>`（`TakeRunner.MarkAdvanceCount`）
- `mark_advance_findings` は、3 秒（`MARK_ADVANCE_NEXT_SEC`）以内に `ev=step take=<id> i=<step+1>` が続かなければ FAIL。
  最後のカットなら `ev=take st=end` で OK、`why=done` 以外で畳まれたら WARN
- 機械の門: `TakeMarkAdvanceTests`（C#）・`DollLaughAudioTests.MarkAdvance_ThroughTheRunner_*`・`tools/test_mark_advance_log.py`
