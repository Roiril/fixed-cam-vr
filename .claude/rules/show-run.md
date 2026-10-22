---
name: show-run
description: 体験の骨格（導入→3 周→最後の A→終幕）・周と区間・take・cue・引き返し・BGM の切替の現行の契約。経緯と旧い記述は reference/archive/ の凍結版
paths:
  - "Assets/Scripts/Streaming/ShowRun*.cs"
  - "Assets/Scripts/Streaming/ShowTake*.cs"
  - "Assets/Scripts/Streaming/Take*.cs"
  - "Assets/Scripts/Streaming/Timeline*.cs"
  - "Assets/Scripts/Streaming/Zone*.cs"
  - "Assets/Scripts/Streaming/LineCross*.cs"
  - "Assets/Scripts/Streaming/StartSpot*.cs"
  - "Assets/Scripts/Streaming/Approach*.cs"
  - "Assets/Scripts/Streaming/Overlay*.cs"
  - "Assets/Scripts/Streaming/ShowAnomaly*.cs"
  - "Assets/Scripts/Streaming/CameraSwitch*.cs"
  - "Assets/Scripts/Streaming/Bgm*.cs"
  - "Assets/Scripts/Streaming/Ending*.cs"
  - "Assets/Scripts/Streaming/ShowClock.cs"
  - "Assets/Scripts/Streaming/CueScheduler.cs"
  - "Assets/Scripts/Streaming/SegmentDwellLog.cs"
  - "Assets/Scripts/Streaming/ScreenOverlayController.cs"
  - "Assets/Scripts/Streaming/SwitchAudioCue.cs"
  - "Assets/Scripts/Streaming/Recording/Recorded*.cs"
  - "Assets/Scripts/Tracking/LapCounter.cs"
  - "Assets/Tests/Streaming/ShowRun*Tests.cs"
  - "Assets/Tests/Streaming/Take*Tests.cs"
  - "Assets/Tests/Streaming/Timeline*Tests.cs"
  - "Assets/Tests/Streaming/CueScheduler*Tests.cs"
  - "Assets/Tests/Streaming/Zone*Tests.cs"
  - "Assets/Tests/Streaming/SegmentDwell*Tests.cs"
  - "Assets/Tests/Streaming/LineCross*Tests.cs"
  - "Assets/Tests/Streaming/StartSpot*Tests.cs"
  - "Assets/Tests/Streaming/Approach*Tests.cs"
  - "Assets/Tests/Streaming/Overlay*Tests.cs"
  - "Assets/Tests/Streaming/ShowAnomaly*Tests.cs"
  - "Assets/Tests/Streaming/Bgm*Tests.cs"
  - "Assets/Tests/Streaming/SwitchAudioCue*Tests.cs"
  - "Assets/Tests/Streaming/Ending*Tests.cs"
  - "Assets/Tests/Tracking/LapCounterTests.cs"
  - "Assets/Tests/Tracking/SwitchWiringTests.cs"
---

# 体験の骨格と演出スケジュール

**現行の契約だけを書く。** 変わったら行を書き換える（日付の節を足さない・`rules/doc-sync.md`）。
体験者に見えることは `canon/NOW.md`、経緯は `reference/archive/streaming-2026-10-22.md`（S:）・`reference/archive/show-design-2026-10-22.md`（D:）。出典の行番号はその凍結版のもの。

## 契約
- 相は Intro → Run → Finished — `ShowRunLogic.Tick` / `ShowRunDirector.Update` 〔出典: streaming:741-752〕
- 本編へ入る条件は「導入演出が終わった ∧ スタート区間 ∧ 中止でない ∧ introMinSec 経過」。スタッフの明示操作は常に通す — `ShowRunLogic.Tick(introCompleted, introAborted)` / `IntroDirector.Completed` 〔出典: show-design:409-427, 963-981〕
- 導入・終了の間、区間進行を止めるのは 1 点だけ。カメラ切替は止めない — `CueScheduler.SetShowGate` 〔出典: streaming:804-806〕
- 導入 → 本編は録画を消さない。新しい体験者の号令元は卓と現地の 2 つで、体験 1 回ぶんの状態はどちらからも `BeginRun` で落とす — `ShowControlClient.BeginMainRun` / `TriggerRunReset` / `BeginNewVisitorRunLocal` / `ShowRunDirector.BeginRun` 〔出典: memory/show_run_skeleton.md〕
- 周は 2 つ。進行の周は単調増加で終了判定に使う。区間の周は引き返すと戻り、演出・録画・post・BGM の区間キーになる — `LapCounterLogic.CurrentLap` / `SegmentLap` / `CueScheduler.CameraEntered(camera, segmentLap, progressLap)` 〔出典: streaming:443-457〕
- 前進は `prev == order[pos] && cam == order[pos+1]` のときだけ。`order[0]` へ戻ると lap++。区間の周は進行の周を超えない — `LapCounterLogic.Feed` / `LapCounterTests.SegmentLap_NeverExceedsProgressLap` 〔出典: streaming:460-466〕
- 周回の時計は dwell 0.5s で確定し、画面の凍結・dip・卓の固定に左右されない — `ZoneProgressionLogic.DefaultDwellSec` / `CameraSwitchDirector.ZoneCommitted` 〔出典: streaming:436-441〕
- 到達可能な区間は `lap <= totalLaps || (lap == totalLaps+1 && cam == order[0])` — `ShowRunReach.IsSegmentReachable` / `ShowRunReachTests` 〔出典: streaming:758-767〕
- 周を走り切ったら 3s は必ず待ち、走行中の演出は 60s まで見せ切る。締めの武装中も待つ。時間切れ 300s は待たない。判定は次フレームの Tick — `ShowRunLogic.Tick(deferNaturalEnd)` / `ShowRunDefaults.EndGraceSec` / `EndHoldMaxSec` 〔出典: streaming:769-800〕
- 出口は 5 つ。終幕の合図（`afterTakeId` の演出が走り終えた）／締めの判定（Trapped）／周回／時間切れ／卓。合図を落とすのは `BeginRun` だけ — `EndingCueLogic` / `ShowRunDirector.TickEndingDecision` 〔出典: show-design:930-962〕
- 体験の時計は休止を数えない — `ShowClock.MaxStepSec` 〔出典: memory/unexpected_event_recovery.md〕
- 区間に take が 0..N 本。実行体は 1 つ。v2 は読込時に変換する — `TimelineMigration.EnsureTakes` / `TimelineDirector.SetTimeline` 〔出典: streaming:598-605〕
- 開始規則は enter+t・exit・line の 3 つ。line は担当カメラが一致したときだけ due になる。横断の猶予は 0.6s。使えない線は `lineIndex=-1` — `TakeRunnerLogic.IsDue` / `LineCrossLogic.CrossLatchSec` / `TakeWiringTests.LineTake_WithoutLineId_*` 〔出典: streaming:1583-1607〕
- 画面が塞がっているだけなら区間内で Ready のまま待つ。捨てたら報告する — `TakeRunnerLogic.TakeDropped` 〔出典: streaming:611-620〕
- `wait:"chain"` は塞がれていた演出だけ持ち越す。上限は 2 本と 60s — `TakeRunnerLogic.MaxCarrySlots` / `CarryMaxWaitSec` 〔出典: streaming:674-690〕
- `yield` は区間を移ると畳み、離脱時の演出も出さない。`hold` は区間をまたぐ — `TakeRunnerLogic.OnZoneCommitted` 〔出典: streaming:1784-1788〕
- watchdog は演出の頭から数え、既定は 45s。戻り先は時計の確定ゾーン — `TakeSchema.DefaultMaxDurationSec` / `CameraSwitchDirector.TryGetCurrentZoneCamera` 〔出典: streaming:622-624〕
- 尺の待ち相手は負値で表す。clip 終端・次の区間・線・報告の 4 つ。線はカット開始後の横断だけを数え、線が無ければ既定尺。報告は締め専用で、開始 4s は受けない — `TakeRunnerLogic.WaitClipEnd`〜`WaitMark` / `EndStepIfLineCrossed` / `MarkGraceSec` 〔出典: streaming:1775-1777, 2226-2249, 2495-2514〕
- `once` は決着回数で数える。未決着は「yield で切れた ∧ 未報告」だけで、その区間へ戻れば頭から再演する（2 回まで）。走行中の自分は武装しない — `TakeRunnerLogic.Outcome` / `ArmEnterTakes` / `TakeReplayOnBacktrackTests` 〔出典: streaming:2586-2610〕
- `dismissible`（既定 false）は報告で畳まれ、乱れ 420ms で現実へ戻る。Ready と持ち越しは捨てる。締めのカットと `afterTakeId` は消せない — `TakeRunnerLogic.NotifyMarkPressed` / `TakeRunner.SetTakes` / `TakeSchema.DismissGlitchMs` 〔出典: streaming:2515-2558〕
- 報告に数えるのは走行中の押下だけ — `TakeRunnerLogic.NotifyMarkPressed` 〔出典: streaming:2599-2604〕
- 次の演出が控えていれば復帰の暗転を打たない。安全網は次フレーム以降で判定する — `TakeRunner._chainPendingFrame` 〔出典: streaming:691-707〕
- 継ぎ目の遷移は割り込む側が持つ — `CameraSwitchDirector.HandoffDip` 〔出典: streaming:709-725〕
- 卓の介入と現地リセットは走行中の演出を畳む — `TimelineDirector.SetSuppressed` / `ShowControlClient.ReleaseLiveHolds` 〔出典: streaming:642-646〕
- カットの分割・凍結は、正常終了・中止・ラン開始のどれでも畳む — `TakeRunner.ReleaseStepState` / `TakeSplitLayerTests` 〔出典: streaming:2206-2211〕
- `switchSfx` は素材カットだけで、画を差し替える行から鳴らす — `CameraSwitchDirector.PlaySwitchSfx` / `SwitchAudioCueTests` 〔出典: streaming:2196-2225〕
- BGM は指示の無い区間で継続する。take の bgm は占有で、終われば今のレーンへ戻る。step の bgm はレーンを書き換え、戻らない — `BgmPlanLogic.Decide` / `DecideRestore` / `TakeRunner.BeginStepBgm` 〔出典: streaming:3222-3262〕
- 録った区間は録り直さない。3 周目は区間を出たら録画の途中でも畳む — `SegmentRecorder.SegmentAlreadyRecorded` / `TakeRunnerLogicTests.Yield_CutsStillPlayingRecording_AndNextSegmentStartsFromItsHead` 〔出典: streaming:467, 1742-1747〕
- present-flag は「宣言 ∧ 実体あり」 — `TimelinePresentFlags.Reconcile` 〔出典: streaming:575-589〕

## 罠（破ると壊れる）
- `NotifyLap` に区間の周を渡すと、帰りの A で引き返した人の体験が終わらない 〔streaming:457〕
- 導入 → 本編に `TriggerRunReset` を使うと 1 周目の録画が消える 〔memory/show_run_skeleton.md〕
- `ApplyGate` が `RunBegan` より後だと (1, order[0]) が録られず、3 周目の録画カットが飛ぶ 〔streaming:807-812〕
- `BeginRun` のゲート後の再リセットを外すと、導入中に本編の演出と録画が開く 〔コード ShowRunDirector.cs:284-306〕
- 使えない線で `onLine` を下ろすと進入即発火 〔streaming:1597〕
- chainNext の安全網を同フレームで見ると渡しが常に取り消される。外すと凍結が解けない。凍結ラッチは足さない 〔streaming:700, 801〕
- `HandoffDip` を通るテストは 0 本。触る前に実尺の配線テストを書く 〔memory/take_continuity.md〕
- `dismissible` を否定形で持つと古いデータが全部消せる側へ倒れる 〔streaming:2537〕
- 3 周目や 2-C 接近を `hold` にすると次区間を潰す 〔streaming:1784, show-design:1683〕
- 録画カットに `overlay2CueId` を付けると右半分の録画が消える 〔show-design:1751〕
- `ShowAnomalyCatalog` は take id 直書き。id を変えると異常数の分母がずれる 〔memory/ending_result.md〕
- `ResolveClosingLineId` は `splitFreeze` を要求する。3-A 位置合わせ版では空になり、③a は 3s の時計だけで出る（未修正）〔memory/scenario_audit_20261022.md〕
- 観測（`ev=seg plap` / `ev=take why=` / `reN` / `disN`）は `ShowTelemetryHost` と `analyze-xp-log.py` を対で直す 〔streaming:2575, 2613〕

## 深く読むとき
- 骨格・終了・出口 → streaming:736-818 / show-design:395-408, 930-981 / memory/show_run_skeleton.md
- 周・引き返し・再演 → streaming:443-471, 2577-2621 / show-design:1810-1850 / memory/backtrack_and_replay.md
- take の待ち・連続・継ぎ目 → streaming:598-735 / memory/take_continuity.md
- line と尺 → streaming:1581-1621, 1759-1800, 2226-2249, 2495-2514
- 報告で消える → streaming:2515-2576
- 録画の背景・3-A の位置録画 → streaming:1622-1758 / show-design:1728-1809
- 2-C 接近と 3-A → show-design:1614-1727
- BGM → streaming:3218-3274
- 終幕 → show-design:789-962
- 現行の穴 → memory/scenario_audit_20261022.md
