---
name: comms-reply-spine
description: スイの文面を通している 4 つの語（違和感→異常・遮断・廻る・観測カメラ）と、それを守るテスト。画を説明しない・止められないものはログの行で返す・遮断できた返事の 2 枚・人形の声の別れの出す条件と罠
metadata:
  node_type: memory
  type: project
  modified: 2026-11-14T07:03:53.554Z
  originSessionId: 9b348fe0-870c-4b5d-a739-2c09a2857306
---

2026-11-14 に、スイの文面を「作品だけを見て」作り直した（`canon/LEDGER.md` 0351〜0354）。
同じ日に、ユーザーの赤入れで説明しすぎを削った（0363・0365・0366）。文と捨てた案は `canon/OPEN.md`「スイの言葉」。
何も知らない読み手に読ませた記録は `canon/ROUNDS.md`。ここは、コードが守っている約束と罠だけ。

## 4 つの語（`CommsNoticeTextTests` が守る）

- **違和感 → 異常。** 体験者が報告するのは「違和感」（博士の語）、スイが判定するのは「異常」。「異変」は使わない。
  `TheVisitorReportsUnease_AndSuiNamesAnomalies`
- **遮断。** 約束 `TutorialReminder` → 返事 `MarkLogged`「遮断します」→ `MarkStuck`「遮断不能」→ 乗っ取りの警告
  「遮断を執行／遮断失敗」（`UnauthorizedAccessEffect`）→ ③b `Prompt`「ボタンを押して遮断を…」→ ④ `MarkCleared`「遮断できました」。
  en は block、fr は bloquer。`ThePromiseToBlock_UsesOneVerbFromTheTutorialToTheEnd` / `SuiBlocks_WithTheSameWordAsTheTakeoverWarning`
  ⚠ 警告の文言を変えたら、スイの動詞も見る。同じ語だから「遮断失敗」が読める
- **廻る。** 開始 `Begin` → 赤い嘘 `CursedReplyText`「そのまま、廻ってください」→ ③a `Halt`「もう廻らないで」。en は circle、fr は tourner。
  `Circling_IsOneVerb_FromTheStartToTheWarning` / `TheWalkingOrders_StartContinueStop`
  ⚠ 途中で「歩く」「周を重ねる」に替えない。替えた版では、締めの「止まって」が何を止めるのか読まれなかった
- **観測カメラ。** 到着 `Arrived`「観測カメラに接続します。／目に見えない異常も映ります。」だけが名指す。
  `TheCameraIsNamedOnArrival` / `TheObservationCamera_ShowsWhatTheEyeCannot`
  ⚠ 「室内カメラ」と呼ばない（0360）。en は observation cameras、fr は caméras d'observation

## ほかの約束

- **スイは画を説明しない**（0363「説明しすぎ」）。人形の視点が出たときの `ForeignFeed` は「何かが……」だけ。
  カメラの名前・視点・映像の出どころを言わない。`TheDollView_IsNotExplained`
- **周を名指さない**（0363「周回の表示は無くていい」）。周の連絡（旧 SecondLap / ThirdLap）は enum ごと消した。
  `NoNotice_NamesTheLap` / `CommsCueLogicTests.NoLapIsAnnounced_AfterTheBeginNotice`
- **止められないものはログの行で返す。** `MarkStuck` は「解析不能／原因不明／遮断不能」の 3 行。理由を文で説明しない。
  `ReportOnAnAnomalyThatCannotBeRemoved_AnswersInLogLines`
- **空振りの返事と赤い嘘は 1 行目が同じ。** `MarkNothing`「異常は検出されませんでした」と `TakeoverLieText` は 1 字も違わない。
  乗っ取りの後の返事は `CursedReplyText`（同じ 1 行目＋「そのまま、廻ってください」）。`TheHonestMissAndTheLie_AreTheSameSentence` /
  `AReplyAfterTheTakeover_LiesAndKeepsTheVisitorCircling`
- **乗っ取りの読ませる段は、当たりの返事の 1 行目そのもの。** `TakeoverShowsTheTruthfulDetectionFirst`
- **⓪a が職掌（調査を支援する）を言う。** `Greeting_SaysWhatSuiIsThereToDo`
- **③a はユーザーの文のまま**（「止まってください！／もう廻らないで」・0365）。何を止めるかを言う。日本語は 16 字まで
  （ユーザーの文が 16 字なので 14 字の上限を緩めた）。画に居るのは 1.8 秒ほど（`CommsPanelLogic.FadeHoldSec`）なので、
  読み切れるかは実機で見る。`TheStopCall_SaysWhatToStop_AndIsShort`
- **③b はスイが遮断できないので、体験者に押させる**（0363）。「私が」「I will」「Je la」を言わず、「…」で終わる。
  `ClosingPrompt_UsesTheApprovedWording` / `ClosingPrompt_HandsTheBlockToTheVisitor`
- **④は 2 枚**（0366「2回順番でスクリーンをだして。入れ替えで。」）。`MarkCleared`「遮断できました」→ 同じ面の差し替えで
  `MarkClearedClose`「データは十分です／お疲れ様でした」。`TheClosingReply_ComesInTwoPanels`
- **⑤は人形の声の 1 行だけ**（0363・0365）。`FarewellFinding`「また、あそぼ」。引用符を付けない（人形が自分で言う）。
  `FarewellFinding_IsTheDollsOwnVoice`
- **句点。** 話しかける連絡は句点あり。返事（`MarkLogged` / `MarkStuck` / `MarkNothing` / `Takeover` / `MarkCleared` /
  `MarkClearedClose`）は句点なし。言いさし（`Halt` / `Prompt` / `ForeignFeed` / `FarewellFinding`）は例外。
  `Notices_EndWithAFullStop_AndLogRepliesDoNot`
- **キー名（X／Y）を出すのは練習だけ。** `OnlyTheRehearsal_NamesTheButtons`

## ForeignFeed（何かが……）

- 出る条件: 開始の合図の後、人形の視点が 1 度でも出て（`CommsCueInput.dollViewStarted` ＝ 侵食度 > 0）、面が空き、
  乗っ取りがまだで、侵食度 < 1、締めの前。ラン 1 回に 1 度。面が塞がっていれば消費せずに待つ
- `MarkStuck` とは中身が違うので、先に `MarkStuck` を返していても出す（旧版は「同じことを 2 度言う」として止めていた）
- ⚠ `dollViewStarted` は侵食度と別に持つ。侵食度だけを入れて回す既存の検証が、反応を拾わないようにする
- 解析器は乗っ取りの前に 1 回かを見る。反応も `MarkStuck` も無いまま乗っ取りが来たら WARN

## 遮断できた返事の 2 枚（MarkCleared → MarkClearedClose）

- `MarkCleared` は `CommsPanel.Deliver` が解除の成立した報告で `MarkLogged` から差し替える（侵食度 1 でも Typed）。
  面が `NotifyDelivered(MarkCleared)` を返すと `CommsCueLogic` が覚え、読ませ終わった縁に `MarkClearedClose` を 1 回出す
- `MarkCleared` の後の報告には返事をしない（`Tick` が `None` を返す）。解析器も②の照合から外す
- ⚠ 2 枚のあいだに 1 フレーム面が空く。別れの人形が面待ちをそこで抜けないよう、`CommsPanel` は
  `ClearedClosePending` の間も `EndingNoticeOpen` を立てる。上限 `EndingFarewellLogic.NoticeWaitMaxSec` は 8 秒（2 枚で約 6 秒）

## FarewellFinding（また、あそぼ）

- 出る条件: 解除済み・別れの人形が Turn（か Retreat）に入った・遮断できた返事の 2 枚を読ませ終わった。ラン 1 回
- 出方は `Cursed`。`_cursedFromStart` で顔が最初から人形、`CursedBodyFor` が自分の文を返す（ほかの返事の Cursed は赤い嘘）。
  `CommsPanel.Deliver` の「乗っ取り前は Cursed を Typed へ落とす」から外してある
- 持続表示ではない。HoldSec + OutSec で自分で引き、引いた縁で `_farewellFindingShown` を落とす
- ⚠ 赤い字を変えたら `menu comms-horror-font`（赤い字は別のアトラス）。`TheRedTexts_ExistInTheHorrorFont`

## MarkStuck

- 報告時に異常演出が出ていて、解除が通らず、走行中の演出が報告で畳めない。`ShowControlClient.LastMarkStuck`。
  `CommsCueInput.markStuck` は否定形で持つ。返事の種類を足したら `ev=mark` のキーと `analyze-xp-log.py` の `want` を対で直す

## 罠

- 連絡の種類は `CommsNotice` の末尾に足す。`CommsCueLogic.IsOnboarding` は並びの範囲で判定する
- 文面と `CommsPanel.cs` のコメントを変えたら `menu hud-font`。焼き直すまで実機で豆腐になる
- `menu comms-preview` の一覧（`CommsPreview.Notices`）に足さないと、その文面のはみ出しを誰も見ない。
  赤い嘘の 2 行は `notice_MarkNothing_cursed.png` でしか見られない
- プレビューで時間を進めるときは段ごとに刻む。1 回の大きな刻みでは、開く段を抜けた所で止まって字が出ていない絵になる

関連: [[comms_takeover]] / [[hmd_onboarding]] / [[ending_farewell_doll]] / [[closing_line_halt]]
