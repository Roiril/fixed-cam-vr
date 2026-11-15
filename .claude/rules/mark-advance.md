---
name: mark-advance
description: 報告でカットを 1 つ進める（step の markAdvance）・素材だけ消す・長押しで進める・相手が畳まれたら出さない・区間を出た後も画面を持つ（exitHoldSec）の現行の契約
paths:
  - "Assets/Scripts/Streaming/TakeRunner.cs"
  - "Assets/Scripts/Streaming/TakeRunnerLogic.cs"
  - "Assets/Scripts/Streaming/ShowTakeSchema.cs"
  - "Assets/Scripts/Streaming/ShowControlClient.VisitorRun.cs"
  - "Assets/Tests/Streaming/TakeMarkAdvanceTests.cs"
  - "tools/test_mark_advance_log.py"
  - "Assets/Tests/Streaming/TakeStepUntilBeyondTests.cs"
  - "Assets/Tests/Streaming/TakeExitHoldTests.cs"
  - "tools/test_exit_hold_log.py"
  - "tools/show-apply-stain-right.py"
---

# 報告でカットを進める（markAdvance）・素材だけを消す（markClear）・長押しで進める・相手が畳まれたら出さない

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

## 報告で素材だけを消す（markClear）

- step の `markClear`（既定 false）は、そのカットで報告が成立したとき、そのカットの素材（1 枚目の層）だけを消し、演出もカットも続ける
  — `TakeRunnerLogic.NotifyMarkPressed` が `MarkResult.Cleared` を返す。見る順は ⓪ → ① → **markClear** → ①' markAdvance → ②
- 消すのは `TakeRunner.ClearStepMaterial`: 押し始めの応答と長押しの膨らみを畳み、`StopOverlay(MarkDoneOutSec)`・2 枚目の層を外し、
  乱れ（`GlitchTransitionLevel`・`DismissGlitchMs`）を 1 回打つ
- 消した印（`ActiveCleared`）は演出の終わりまで残る。以後のカットは 2 枚目の層（`overlay2CueId`）と長押しの膨らみを出さない
  — `ApplyStepOverlay2` / `EnsureTakePress` / `SetVisitorMarkProgress` / `NotifyVisitorMarkStarted`
- 消した後の同じカットでの報告は None（何にも効かない）。`ActiveRemovable` も false。次のカットでは従来どおり `dismissible` で畳まれる
- 消しただけは「報告で畳まれた」に数えない（skipIfDismissed の相手にならない）。`ShowControlClient` は None 以外を報告成立として数える

## 長押しの開始でカットを進める（markStartAdvance）

- step の `markStartAdvance`（既定 false）は、そのカットの間に報告の長押しが始まったら、そのカットを今で終える（演出は畳まない）
  — `TakeRunnerLogic.NotifyMarkStarted(now)`。入口は `TakeRunner.NotifyVisitorMarkStarted` の頭
- 押し切ったときの報告は、進んだ先のカットで普通に判定する（2-A では伸び出しのカットで `dismissible` が畳む）

## 相手が報告で畳まれたら出さない（skipIfDismissed）

- take の `skipIfDismissed`（既定 ""）は相手の take id。相手が同じランで `VisitorDismissed` で終わっていたら、この演出は武装しない
  — `TakeRunnerLogic.ArmEnterTakes`・`FindExitCandidate`・`ReportRemainingDrops` が `_takeDismissed[]` を見る。`ResetRun` / `SetDefs` で忘れる
- 報告で畳んだ縁（`TakeRunnerLogic.DismissDecision`）でも、武装済み（Waiting・Ready）の演出のうち相手が畳まれたものを外し、出さなかったに数える。区間を出た後も画面を持つ演出（take の `exitHoldSec`・`rules/show-run.md`）は、持っている間に次の区間の演出を武装しているため
- 台本の id は `TakeRunner.ResolveSkipIfDismissed` が index+1 へ解く（0 = 相手なし。知らない id は警告して相手なし）
- 卓の白名簿は `timeline-model.js` の `markClear` / `markStartAdvance` / `skipIfDismissed`。2-A の台本は `tools/show-apply-stain-right.py`

## 区間を出た後も画面を持つ（exitHoldSec）

- take の `exitHoldSec`（既定 0）を持つ `policy:"yield"` の演出は、前進で区間を出てもその秒までは畳まずに画面（元のカメラ）を持つ — `TakeRunnerLogic.OnZoneCommitted` / `ExitHolding`。`TakeRunner` は yield でない演出の値を 0 にする
- 持っている間もカットは尺どおり進む。`untilZoneChange` のカットは区間移りでは終わらず、持つ秒の終わりで終わる（`NotifyZoneChanged`）
- 持ち終わりは前進の yield（`Yielded`・`yieldedForward`）。`chainNext` は立てない（次の演出は別のカメラなので、画面を返してから始める）
- 引き返し・持っている間の 2 度目の区間移り・報告・watchdog はすぐ畳む。持つのは 1 演出 1 度
- 次の区間の演出は持ち始めた時点で武装され、画面が空くまで Ready で待つ。2 周目 A は `tools/show-apply-stain-right.py` の `EXIT_HOLD_SEC`

## 観測（`ShowTelemetryHost` と `analyze-xp-log.py` を対で直す）

- `ev=markadv take=<id> step=<進める前のカット> n=<累計>`（`TakeRunner.MarkAdvanceCount`）
- `mark_advance_findings` は、3 秒（`MARK_ADVANCE_NEXT_SEC`）以内に `ev=step take=<id> i=<step+1>` が続かなければ FAIL。
  最後のカットなら `ev=take st=end` で OK、`why=done` 以外で畳まれたら WARN
- `ev=markclear take= step= n=` — `mark_clear_findings`。1 秒以内に同じ演出が `why=mark` で畳まれたら FAIL、2 秒以内にそのカットの cue の `ev=cue st=off` が無ければ FAIL
- `ev=markstartadv take= step= n=` — `mark_start_advance_findings`（判定は markadv と同じ）
- `ev=takeskip take= by= n=` — `take_skip_findings`。相手の `why=mark` が先に無い・出さなかった演出が後で始まった・相手が畳まれたのに `takeskip` 無しで始まった、なら FAIL
- `ev=exithold take= sec= n=` — `exit_hold_findings`。持ち始めから sec + 0.5 秒（`EXIT_HOLD_SLACK_SEC`）以内に同じ演出の `ev=take st=end` が無ければ FAIL。台本に `exitHoldSec` が無い・yield でない演出が持ったら FAIL。滞在をはみ出した WARN は持つ秒の内なら出さない
- 機械の門: `TakeExitHoldTests`・`tools/test_exit_hold_log.py`・`TakeMarkAdvanceTests`・`TakeMarkClearTests`（C#）・`DollLaughAudioTests.MarkAdvance_ThroughTheRunner_*`・`tools/test_mark_advance_log.py`・`tools/test_mark_clear_log.py`
