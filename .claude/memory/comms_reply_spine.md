---
name: comms-reply-spine
description: スイの文面を通している 4 つの語（違和感→異常・遮断・廻る・観測カメラ）と、それを守るテスト。解析の 1 行・頼まれずに出す警告・3 周目の赤い嘘の出す条件と罠
metadata:
  node_type: memory
  type: project
  modified: 2026-11-14T02:51:08.543Z
  originSessionId: 9b348fe0-870c-4b5d-a739-2c09a2857306
---

2026-11-14 に、スイの文面を「作品だけを見て」作り直した（`canon/LEDGER.md` 0351〜0354）。
文そのものと捨てた案は `canon/OPEN.md`「スイの言葉を、作品だけを見て作り直す」（シュビーの案・未判定）。
何も知らない読み手 12 人に 6 回読ませた記録は `canon/ROUNDS.md`。ここは、コードが守っている約束と罠だけ。

## 4 つの語（`CommsNoticeTextTests` が守る）

- **違和感 → 異常。** 体験者が報告するのは「違和感」（博士の語）、スイが判定するのは「異常」。「異変」は使わない。
  `TheVisitorReportsUnease_AndSuiNamesAnomalies`
- **遮断。** 約束 `TutorialReminder` → 返事 `MarkLogged` → `MarkStuck` / `ForeignFeed`「遮断できません」→ 乗っ取りの警告
  「遮断を執行／遮断失敗」（`UnauthorizedAccessEffect`）→ ③b `Prompt` → ④ `MarkCleared`。en は block、fr は bloquer。
  `ThePromiseToBlock_UsesOneVerbFromTheTutorialToTheEnd` / `SuiBlocks_WithTheSameWordAsTheTakeoverWarning`
  ⚠ 警告の文言を変えたら、スイの動詞も見る。同じ語だから「遮断失敗」が読める
- **廻る。** 開始 `Begin` → `SecondLap` → 赤い嘘 `ThirdLapLieText` → ③a `Halt` → ③b `Prompt`。en は circle、fr は tourner。
  `Circling_IsOneVerb_FromTheStartToTheWarning` / `TheWalkingOrders_StartContinueStop`
  ⚠ 途中で「歩く」「周を重ねる」に替えない。替えた版では、締めの「止まって」が何を止めるのか読まれなかった
- **観測カメラ。** 到着 `Arrived`「観測カメラに接続します。／目に見えない異常も映ります。」→ `ForeignFeed`「観測カメラ以外の映像が、」→ `MarkStuck`。
  `TheCameraIsNamedOnArrival_AndDeniedInTheDollView` / `TheUnaskedWarning_SaysWhatTheStuckReplySays` / `TheObservationCamera_ShowsWhatTheEyeCannot`
  ⚠ 「室内カメラ」と呼ばない（0360）。ただの室内カメラでは、なぜ自分の目でなくテレビの中の自分を見て歩くのかに答えが無い。
  3 行目で、異常は部屋に実在して目に見えないだけ、と決まる（終幕の写真の人形・遮断の意味がこれに乗る）。
  博士の「室内のカメラ」は名前ではなく説明なので録り直していない。en は observation cameras、fr は caméras d'observation

## ほかの約束

- **空振りの返事と赤い嘘は同じ文。** `MarkNothing` と `TakeoverLieText` は 3 言語とも 1 字も違わない。`TheHonestMissAndTheLie_AreTheSameSentence`
- **乗っ取りの読ませる段は、当たりの返事の 1 行目そのもの。** `TakeoverShowsTheTruthfulDetectionFirst`
- **周の節目は 3 回、同じ型**（一周目です。／二周目です。／三周目です。）。`EveryLap_IsAnnouncedInTheSameForm`
- **⓪a が職掌（調査を支援する）を言う。** 遮断の権限を渡すのはここだけ。`Greeting_SaysWhatSuiIsThereToDo`
- **③a は何を止めるのかを言い、短い**（日本語 14 字まで）。装置を外すなとは言わない。`TheStopCall_SaysWhatToStop_AndIsShort`
  画に居るのは 1.8 秒ほど（`CommsPanelLogic.FadeHoldSec`）。博士の教えた中断の手順（立ち止まる → 手すりを離す → 装置を外す）と
  取り違えさせないために、止める対象を「廻る」と言う
- **③b は「あなたも私も」「人形」を言う。** スイの侵食を言葉で言う唯一の場所。`ClosingPrompt_PutsSuiInsideTheDanger`
- **侵食中の返事（`MarkAnalyzing`）も一人称。** 判定の語を入れない。`MissAtOrAbovePossessedLevel_*`
- **⑤は引用の 1 行だけ。** スイの地の文（解析・声・あの子・言っています）を付けない。引用は「また、」を含む。
  `FarewellFinding_QuotesWhatTheDollSaysAsItLeaves`。地の文を 5 通り試して 5 通りとも読み手に嫌われた
- **句点。** 話しかける連絡は句点あり、返事（`MarkLogged` / `MarkStuck` / `MarkNothing` / `Takeover`・解析の 1 行）は句点なし。
  `Notices_EndWithAFullStop_AndLogRepliesDoNot`
- **キー名（X／Y）を出すのは練習だけ。** `OnlyTheRehearsal_NamesTheButtons`

## 解析の 1 行（`CommsPanel.FindingLine`）

- 当たりの返事 `MarkLogged` の 1 行目と 2 行目のあいだに挟む。報告が乗った演出の id は `ShowControlClient.LastMarkTakeId`
  （報告で段が進む前に凍らせた値）。面は `Deliver` で受け取り、差し替えで文面を組むのが遅れても変わらない
- 対応は `L1C1#0`（手形）・`L2C0#0`（シミ）・`L2C0#1`（ついてくる人形）・`L2C1#0`（2 周目 B）。id が台本から無くなれば元の 2 行へ戻る
- ⚠ 文を足したら `CommsPanel.FindingTakeIds` にも足す。テストとはみ出しの検査はこの一覧を回す
- ⚠ 画を見れば分かることを繰り返さない・主語を言う・行の中に句点を置かない

## ForeignFeed（観測カメラ以外の映像が、混ざっています。遮断できません。）

- 出る条件: 開始の合図の後、人形の視点が 1 度でも出て（`CommsCueInput.dollViewStarted` ＝ 侵食度 > 0）、面が空き、
  乗っ取りがまだで、侵食度 < 1、締めの前。ラン 1 回に 1 度。面が塞がっていれば消費せずに待つ
- 先に `MarkStuck` を返していれば出さない（同じ理由を返事で言った）
- 逆に、警告の後や 2 度目の `MarkStuck` は 2 行目から先を `StuckFollowUpLine`（映像の出どころが分かりません）へ替える。
  `CommsCueLogic.StuckReasonAlreadySaid` を面が読む。走行ログの id は `MarkStuck` のまま
- ⚠ `dollViewStarted` は侵食度と別に持つ。侵食度だけを入れて回す既存の検証が、警告を拾わないようにする
- 解析器は乗っ取りの前に 1 回かを見る。警告も `MarkStuck` も無いまま乗っ取りが来たら WARN

## ThirdLap（3 周目の頭）

- 出る条件: 区間の周が 3、開始の合図の後、面が空いた縁、締めの前。ラン 1 回に 1 度。押しのけられても消費しない
- 乗っ取りが届いた後は出方が Cursed になり、文は `CommsPanel.CursedBodyFor` が返す専用の嘘（`ThirdLapLieText`）。
  ほかの連絡の Cursed は従来どおり嘘の一文
- 乗っ取りが起きなかった走行では、スイのまま正直な文（Typed）
- ⚠ 嘘の字を変えたら `menu comms-horror-font`（赤い字は別のアトラス）。`ThirdLapLie_ExistsInTheHorrorFont`
- ⚠ 赤い 3 行の座りは `menu comms-preview` の `notice_ThirdLap_cursed.png` でしか見られない
  （一覧の撮り方では、乗っ取りが届く前なので通常のスイで出る）
- 解析器は 3 周目の区間の中で 1 回か、乗っ取りの後なら delivery=Cursed かを見る

## MarkStuck・SecondLap

- `MarkStuck`: 報告時に異常演出が出ていて、解除が通らず、走行中の演出が報告で畳めない。`ShowControlClient.LastMarkStuck`。
  `CommsCueInput.markStuck` は否定形で持つ。返事の種類を足したら `ev=mark` のキーと `analyze-xp-log.py` の `want` を対で直す
- `SecondLap`: 区間の周が 2、開始の合図の後、面が空いた縁に 1 回。人形の視点が始まった後・乗っ取りの後・締めには出さない

## 罠

- 連絡の種類は `CommsNotice` の末尾に足す。`CommsCueLogic.IsOnboarding` は並びの範囲で判定する
- 文面と `CommsPanel.cs` のコメントを変えたら `menu hud-font`。焼き直すまで実機で豆腐になる
- `menu comms-preview` の一覧（`CommsPreview.Notices`）に足さないと、その文面のはみ出しを誰も見ない
- 開始の合図は 3 行になった。「1 行の文面」として使っていたテスト（`CommsPanelSwapTests`）は空振りの返事へ替えた
- プレビューで時間を進めるときは段ごとに刻む。1 回の大きな刻みでは、開く段を抜けた所で止まって字が出ていない絵になる

関連: [[comms_takeover]] / [[hmd_onboarding]] / [[ending_farewell_doll]]
