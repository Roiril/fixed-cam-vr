---
name: comms-reply-spine
description: スイの文面が 1 本の筋になっている所（約束 → 実行 → 失敗 → 言い直し → 果たす）と、それを守るテスト。MarkStuck と SecondLap の出す条件と罠
metadata:
  node_type: memory
  type: project
  modified: 2026-11-11T18:04:34.590Z
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
- `MarkLogged` と `MarkStuck` の 1 行目は同じ（0054 の逐語「異常を検出しました」）

## MarkStuck（取り除けません）

- 出る条件: 報告時に異常演出が出ていて（`LastMarkDetected`）、解除が通らず（`LastMarkResolved` が false）、
  走行中の演出が報告で畳めない（`TakeRunnerLogic.ActiveRemovable` が false）。`ShowControlClient.LastMarkStuck`
- 消している最中の演出（2-A の幕がたなびく応答の終端待ち）への押し直しは `MarkLogged`。演出は消せるものなので
- 乗っ取りの後は出方が Cursed になり、文は赤い嘘に替わる。画に「取り除けません」が出るのは乗っ取りの前だけ
  （2-C の人形視点の最中の報告と、乗っ取りが起きないまま 3 周目に入った走行）
- ⚠ `CommsCueInput.markStuck` は否定形で持つ。既定（false）は従来どおり `MarkLogged`
- ⚠ 返事の種類を足したら、`ev=mark` のキー（`det` / `stuck`）と `analyze-xp-log.py` の `want` を対で直す。
  古いログには `stuck` が無く、解析器は無ければ `MarkLogged` を期待する

## SecondLap（二周目です。反応が、近づいています。）

- 区間の周が 2 に居て（`TimelineDirector.SegmentLap == 2`）、開始の合図の後、面が空いた縁に 1 回
- 押しのけられても消費しない（報告の返事を読ませ終わってから出す）
- 人形視点が始まった後（侵食度 > 0）・乗っ取りの後・締めには出さない。乗っ取りの因果（0245）へ割り込まない
- 解析器は `ev=comms id=SecondLap` を 2 周目の区間の中で 1 回と数える。2 周目に入ったのに出なければ WARN

## 罠

- 連絡の種類は `CommsNotice` の末尾に足す。`CommsCueLogic.IsOnboarding` は並びの範囲で判定する
- 文面を変えたら `menu hud-font`。`Élimination` の É のような新しい字は、焼き直すまで実機で豆腐になる
- `menu comms-preview` の一覧（`CommsPreview.Notices`）に足さないと、その文面のはみ出しを誰も見ない

関連: [[comms_takeover]] / [[hmd_onboarding]] / [[ending_farewell_doll]]
