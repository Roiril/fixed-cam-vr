---
name: take-continuity
description: 演出の待ち・連続・継ぎ目を触る前に：持ち越しは因果条件で絞る／chainNext には安全網が要る／遷移は割り込む側が所有する
metadata: 
  node_type: memory
  type: project
  originSessionId: 9c9e9742-4d99-47bc-8b46-e7eb4d64ed2b
  modified: 2026-07-29T05:03:11.740Z
---

2026-07-29（同日 2 回目）。契約は `.claude/reference/archive/streaming-2026-10-22.md` の「演出の連続と、継ぎ目の遷移」節、
経緯は `.claude/plans/2026-07-29_take-continuity.md`。

**待ちの持ち越し（`wait:"chain"`）は因果条件で絞る。**
離脱の瞬間に**実際に画面が塞がっていた**かつ**開始条件は満たしていた**演出だけを持ち越す。
「歩くのが速くて時刻に届かなかった」を持ち越すと、著作者の知らないうちに遅れて出る
（v2 の delay 誤爆と同じ失敗）。上限 2 本 / 60 秒、いま居る区間の演出に劣後、捨てる出口は全部報告。
`at:"exit"` は持ち越せない（離脱時の演出を別の区間で出すと文脈が最も壊れる）。

**`chainNext`（連続の渡し）には必ず安全網を残す。**
「次が控えているから画面を返さない」と決めた後、判定と発火のあいだの 1 フレームで
その「次」が消えると占有が降りない。`TakeRunner._chainPending` が次フレームで必ず決着させる。
**凍結が解けない事故はこの codebase で 4 回起きている。** ここを外さないこと。

**継ぎ目の遷移は「いま画面を取る側」が所有する（`HandoffDip`）。**
進行中の暗転へ割り込むとき、暗さを比率で保って割り込む側の尺・見た目へ乗り換える。
Up 相でも 0 から張り直さず折り返す（旧実装は「黒 → 明るくなりかけ → また黒」だった）。
「瞬時」は進行中の暗転を打ち切らない（打ち切ると明るさが飛んで最も汚い）。

**相乗り分岐にはまだテストが無い。** 配線テストは dip を 0 にして駆動しているので、
`HandoffDip` を通るテストが 1 本も無い。ここを触るなら、dip を実尺で走らせる配線テストを先に書くこと
（`CameraSwitchDirector.SetDeltaSource` が EditMode 用の seam）。

## ⚠⚠ 「出るまでの時間」は演出の質に直結する（2026-08-23）

ユーザー判定 0114「区間に入った瞬間に手形が出ていてほしい。間があるとクオリティが下がる」。

**素材が出るまでの時間は、その cue の種類で決まる。**

| 種類 | 発火 → 画 | なぜ |
|---|---|---|
| 端末内録画（`rec`） | 90ms | ローカル |
| 無人プレート（`plate_*`） | **0ms** | 前から先読みしていた |
| 生成素材の静止画 | **260ms** → 直して 70ms | **先読みの対象外だった** |
| 動画（`clip`） | 166ms | 落とすのは先読み済み・用意（Prepare）は毎回掛かる |

⚠ **`ev=clip` は動画専用。** 静止画の停滞はそこに出ないので、
**`ev=step` と `ev=cue st=on` の時刻差**で見る（テレメトリは毎フレーム＝ 11ms の分解能）。
⚠ **画で確かめるなら `xp-evidence.py` の offset を信じない**（2 回とも 2 秒以上ずれていた）。
録画の中で画面が切り替わった瞬間を自分で探して起点にする。

契約と実測は `reference/archive/streaming-2026-10-22.md`「素材は先に落としておく」。

## 3 周目の録画は 2 周目の歩き方で切り上げる（2026-11-11）

3-B・3-C は「録画（約 8 秒）→ 0.9 秒の暗転 → 無人の画と人形」の 2 カットで、どちらも `yield`。
**速く歩く人は録画が終わる前に区間を出るので、人形のカットを 1 度も見ずに畳まれていた。**

- 対象は形で決める（`TakeRunner.IsRecordedCutBeforeDoll`）。3 周目以降・録画（`recContinue` / `recFollow` でない）の
  すぐ次が `plate`・演出のどこにも位置で選ぶ録画と線待ちが無い。3-A は位置で進むので外れる。take id では判じない
- 尺は `TakeRunnerLogic.ResolveRecordedCutSec`: `min(著作, max(MinRecordedSec=3, d2 - DollShowSec=2.5))`。
  d2 は 2 周目の同じ区間（同じカメラ）の滞在。分からなければ著作のまま。**ゆっくり歩く人は変わらない**
- d2 は `TakeRunnerLogic.OnZoneCommitted` が区間を出た瞬間に閉じる（`CloseCurrentDwell` / `DwellSecOf`）。
  時計は演出の時計（`TakeRunner.Now`）。入りも出も dwell 0.5s の確定を経るので遅れは差で消える。同じ区間は最長を残す。
  `ResetRun` で消え、`SetDefs` では消えない
- 切り上げは**録画が画に出た瞬間**から数える（`PlayStepOverlay` の `SetCurrentStepEnd(Now + cut)`）。
  著作の尺も「出てから終端まで」なので、乱れの遷移の分だけずれない。次のカットへの遷移は録画の終端で畳んだときと同じ経路（plate の dip）
- 観測は `ev=step` の `auth=` / `cut=` / `d2=`（対象のカットだけ）。`analyze-xp-log.py` は対象の演出が走ったのに
  次のカット（plate）の `ev=step played=1` が無ければ WARN（FAIL にしない）。校正は `tools/test_recorded_cut_log.py`
- ⚠ 3 周目の滞在そのものは見積もりでしかない。2 周目より速く歩けば、切り上げても人形は出ない（WARN で見える）

関連: [[show_run_skeleton]] [[glitch_and_latency]]
