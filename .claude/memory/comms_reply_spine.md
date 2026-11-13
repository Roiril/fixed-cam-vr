---
name: comms-reply-spine
description: スイの文面が 1 本の筋になっている所（約束 → 実行 → 失敗 → 言い直し → 果たす）と、それを守るテスト。MarkStuck と SecondLap の出す条件と罠
metadata:
  node_type: memory
  type: project
  modified: 2026-11-13T13:50:10.249Z
  originSessionId: 5820747c-6df2-42ee-8801-19a86c53bc88
---

2026-11-12 ユーザー依頼「ゲームシナリオとしてもっとクオリティを上げてほしい。…ゲームシナリオとしての納得感を。」で、
スイの文面を 1 本の筋に通した。文そのものと狙いは `canon/OPEN.md`「シナリオの組み直し」（シュビーの案・未判定）。
ここは、コードが守っている約束と罠だけ。

## 文面どうしの約束（テストが守る）

- **同じ動詞で通す。** 導入の約束 `TutorialReminder`「私が解析し、取り除きます」→ 返事 `MarkLogged`「取り除きます」→
  `MarkStuck`「取り除けません」→ ③b `Prompt`「報告を。私が取り除きます。」→ ④ `MarkCleared`「異変を取り除きました。」（0284）。
  en は remove、fr は éliminer。`CommsNoticeTextTests.ThePromiseToRemove_UsesOneVerbFromTheTutorialToTheEnd`
- **空振りの返事と赤い嘘は同じ文。** `MarkNothing` と `TakeoverLieText` は 3 言語とも 1 字も違わない
  （0054 と 0244 の逐語は同じ字。コードは 11-12 まで空振りだけ「異状」だった）。`TheHonestMissAndTheLie_AreTheSameSentence`
- **⑤は判定を言わない。** 記録に残った相手の言葉（2-C の声「あーそぼー」）を引用で読み上げるだけ。検出・敵意などの判定の語を入れない。
  `FarewellFinding_ReadsBackTheOnlyWordsTheAnomalyLeft`。11-12 に「敵意は検出されませんでした」を 2 通り試し、体験者役 6 体のうち 5 体が
  「乗っ取られた装置の言うこと」「偽の『異常なし』と同じ型」と疑った。嘘の一文と同じ型で締めると、嘘のほうへ引っ張られる
- **⑤は「声」と言い、数を数えない**（「言葉」でも「一つです」でもない）。同じテスト。何をいつ記録していたのかは体験の中でここでしか分からず、
  「声」なら 2-C で自分の耳で聞いた音と結びつく。博士の「怪異を記録し、解析するための観測装置」も同じ語で回収する
- `MarkLogged` と `MarkStuck` の 1 行目は同じ（0054 の逐語「異常を検出しました」）
- **⓪a が職掌（調査を支援する）を言う。** `Greeting_SaysWhatSuiIsThereToDo`。0124 の逐語で、**「取り除く」の権限を渡すのはここだけ**
  （博士は 0328 以降「装置に搭載されたエージェントが解析します」までしか言わない）。落とすと、次の `TutorialReminder` の
  「取り除きます」がどこから来た権限か、体験の最後まで誰も言わない
- **③b はスイ自身も取り込まれかけていると言う。** `ClosingPrompt_PutsSuiInsideTheDanger`。0124 の逐語「異常があなたと私を」で、
  **スイの侵食を言葉で言う唯一の場所**。2026-09 に「あなたを」へ縮められて 2 か月空白だった
- **侵食中の返事（`MarkAnalyzing`）も一人称。** `MissAtOrAbovePossessedLevel_NeverCallsItselfTheDevice`。入れ物の名（装置）で
  自分を呼ぶと、判定の保留とも故障とも付かない文になる。壊れていることは「終わらない」で示す。判定の語（異常 / anomal）も入れない
- **⓪c は起動を告げ、①は開始を告げる**（`ArrivalBootsTheDevice_AndTheSurveyStartsSeparately`）。同じ語を 2 回聞かせない。
  ⓪c の直後に起きるのはテレビが現れて電源が入ること（0309）
- **キー名（X／Y）を出すのは練習（`Tutorial`）だけ**（`OnlyTheRehearsal_NamesTheButtons`）。装置の面に入力機器の名前が出ると、
  調査の記録ではなくゲームの操作説明に見える。練習は実際に押させるので例外

## MarkStuck（取り除けません）

- 出る条件: 報告時に異常演出が出ていて（`LastMarkDetected`）、解除が通らず（`LastMarkResolved` が false）、
  走行中の演出が報告で畳めない（`TakeRunnerLogic.ActiveRemovable` が false）。`ShowControlClient.LastMarkStuck`
- 消している最中の演出（2-A の幕がたなびく応答の終端待ち）への押し直しは `MarkLogged`。演出は消せるものなので
- 乗っ取りの後は出方が Cursed になり、文は赤い嘘に替わる。画に「取り除けません」が出るのは乗っ取りの前だけ
  （2-C の人形視点の最中の報告と、乗っ取りが起きないまま 3 周目に入った走行）
- ⚠ `CommsCueInput.markStuck` は否定形で持つ。既定（false）は従来どおり `MarkLogged`
- ⚠ 返事の種類を足したら、`ev=mark` のキー（`det` / `stuck`）と `analyze-xp-log.py` の `want` を対で直す。
  古いログには `stuck` が無く、解析器は無ければ `MarkLogged` を期待する

## SecondLap（二周目です。一周目より、対象が近づいています。）

- 区間の周が 2 に居て（`TimelineDirector.SegmentLap == 2`）、開始の合図の後、面が空いた縁に 1 回
- 押しのけられても消費しない（報告の返事を読ませ終わってから出す）
- 人形視点が始まった後（侵食度 > 0）・乗っ取りの後・締めには出さない。乗っ取りの因果（0245）へ割り込まない
- 解析器は `ev=comms id=SecondLap` を 2 周目の区間の中で 1 回と数える。2 周目に入ったのに出なければ WARN
- ⚠ **「廻るほど、近づいてきます」という博士の種は 0328 の書き直しで消えた。** だからこの連絡は体験者にとって初耳で、
  近づいていることは**スイが 1 周目と比べて観測した結果**として出すしかない（「一周目より、」を落とさない）。
  呼び名は博士と同じ「対象 / the subject / l'objet」。2026-11-13 まで「反応」だった — スイの語彙でここ 1 箇所だけの語で、
  何の反応かを体験者が決められなかった

## 罠

- 連絡の種類は `CommsNotice` の末尾に足す。`CommsCueLogic.IsOnboarding` は並びの範囲で判定する
- 文面を変えたら `menu hud-font`。`Élimination` の É のような新しい字は、焼き直すまで実機で豆腐になる
- `menu comms-preview` の一覧（`CommsPreview.Notices`）に足さないと、その文面のはみ出しを誰も見ない

関連: [[comms_takeover]] / [[hmd_onboarding]] / [[ending_farewell_doll]]
