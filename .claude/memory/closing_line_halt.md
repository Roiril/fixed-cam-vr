---
name: closing-line-halt
description: 4-A の③a「止まってください！」は「締めの線を踏む OR 最後の A に入って 3 秒」（0233・0247）。⚠ いまの台本には締めの線が無く、3 秒の時計だけで出ている。線の導き方・入り際 0.6 秒の猶予・自動走行の作法
metadata: 
  node_type: memory
  type: project
  originSessionId: c1e3e152-4a70-44a5-b034-7cd0e5030226
  modified: 2026-11-11T01:51:35.959Z
---

# 4-A の「止まってください！」は場所で出る（2026-09-19・`canon/LEDGER.md` 0233）

ユーザー逐語「時間指定で 4s ではなく、場所指定にし、その場所を、左右反転の演出のときのフリーズされる位置に」。

⚠⚠ **いまの状態（2026-11-10 にコードと show.json で確認）: 締めの線は空で、③a は 3 秒の時計だけで出ている。**
3-A を位置合わせ版（0274）にしたとき `splitFreeze` のカットと `untilLine` の段が台本から消えた
（`show.json` の `splitFreeze` は 16 個すべて false）。`TakeSchema.ResolveClosingLineId` は空を返す。
時計は 0247 で「線 OR 3 秒」になり、`CommsCueLogic.HaltAfterClosingSec` は 3（下の「5 秒」は 0233 当時の値）。
下の仕組みはコードに残っていて、台本に線が戻れば働く。未修正の扱いは [[scenario_audit_20261022]] と `canon/NOW.md` の食い違い 1。

**Why:** 0182 の観察で、時計 5 秒の③a は歩く速さで角の前後にずれていた（速い人は曲がった後・遅い人は手前）。
「止まれ」の場所を 3 周目 A で鏡像が凍った場所と同じにすると、戻ってきた瞬間にそこで声が掛かる。

**How to apply:**

- **締めの線は show.json に新しいキーを作らず台本から導く** — `TakeSchema.ResolveClosingLineId`:
  `splitFreeze` のカットの直前まで待っていた `untilLine` の線（いまは `line_freeze`）。
  卓で凍結線を据え直せば「止まれ」の場所も一緒に動く。2 か所で指すとずれる。
  解析器の `closing_line_of()` が同じ規則で、`ev=config closingLine=` で突き合わせる（片方だけ変えない）
- **記録は `TakeRunnerLogic.ClosingLineCrossed`。締めのカット（`untilMark` の段を持つ take）の中だけ**。
  ⚠ **カットの線待ち（`EndStepIfLineCrossed`）と違って、締めに入る直前 0.6 秒（`CrossLatchSec`）の横断も数える。**
  線は区間の入口寄りにあり、ゾーン確定の dwell（0.5 秒）のあいだに踏む人が居る。線待ちが直前を捨てるのは
  「同じ区間に線が 2 本ある」（3-A の左右）ためで、締めにその問題は無い。
  ⚠ 記録は **take が始まった同じ Tick でも拾う**（`StartTake` の直後に `LatchClosingLine`）。
  始まる前に拾って `StartTake` でリセットされる順だと、テストでは 1 Tick で立たない
- **`CommsCueLogic` は「線を踏んだ OR 時計（`HaltAfterClosingSec` 3 秒）」**（0247。線があっても踏まない人には時計で出る）。
  `closingLineDefined` は「layout に実体がある」で、実体の無い id は `TakeRunner.ApplyLinesFromLayout` が -1 に倒す
  （実体の無い線は決して横切られず、③a が黙って消える方が危ない）
- **自動走行は経路がたまたま線をまたぐことに頼らない** — `ShowWalkDebugDriver.CrossClosingLine` が締めのカットの
  始まりを待ってから線を踏みに行く（導入の `line_1` と同じ形）。9/5 の走行では入り際の横断が締めの 0.56 秒前で、
  猶予 0.6 秒の内か外かは dwell とフレームの都合で走行ごとに変わっていた
- **観測**: `ev=closingLine id= sec=`（踏んだ縁）・`ev=comms closing= cline=`（Halt の行は cline=1 のはず・
  cline=0 なら退避路）・`ev=config closingLine=`。解析器は 5 通り（線→Halt / 退避路 / 踏んだのに無し /
  踏まず（人=WARN・走行=FAIL）/ 線の無い台本）で校正済み
- 言われていないことは `OPEN.md` Q15〜Q17（踏まない人には出さない・報告後でも出す・猶予前の③b はそのまま）

## ③b は出したまま（2026-11-11・シュビーの実装判断・未判定）

ツインの無報告走行で、③b「異変があなたを取り込もうとしています。排除してください。」が全文表示から
約 2 秒（Typed の `HoldSecFor`）で自分で引っ込み、人形 End まで約 12 秒、文字の無い時間があった。
原因は③b が普通の文面と同じ非持続で出ていたこと（Halt の Out → Swap → `CommsPanelLogic.Tick` の Hold 満了で Out）。

- **出し方**: `CommsPanel.Update` が Prompt だけ `Deliver(..., persistent: true, ...)`。Swap で差し替わっても
  `_pendingPersistent` が引き継ぐ。Hold は時間で抜けない
- **消え方は 2 つだけ**: 報告で `ScreenDecayReleased` → MarkCleared（非持続・Typed）が上書きして普通に引く。
  人形 End・中断で本編を出た縁 → `CommsPanel.ObserveClosingPromptEnd(inRun=false)` が `Disable` して即座に隠す
  （終幕の暗転と同じフレーム。`Finished` では `CommsCueLogic.Tick` が何も返さないので、これが無いと黒の上に残る）
- **長押し**: Hold から Guide へ（従来どおり）。持続の文面で離すと Out ではなく Hold へ戻し、
  `RestoreSec`（In + FadeIn）で面・丈・字を全文へ戻す。`CommsPanelLogic.Restoring` の間は打鍵音を鳴らさない
- **影響しないもの**: `promptReady`（0178）は Hold へ入った最初の 1 回で立つ（持続でも同じ）。
  `_cue.NotifyDelivered` は Takeover 以外で何もしない。復帰の時計（`commsRecover`）は面が Off へ畳まれるまで回るので、
  ③b の間は `SettleEndSec` 後の静止した斑のまま。`st=end` は MarkCleared が引いた後か本編を出た後に出る
- **解析器**: 人形 End の走行で、③b の後・終幕の 0.1 秒前までの `ev=sum comms=` に `off` があれば FAIL
  （`tools/test-ending-log.py` の `test_prompt_*`）
- 2 人目: `OnRunRestarted` / `OnDisable` が `_closingPromptShown` を落とす。`Disable` が `_persistent` と `_restoring` を戻す

関連: [[comms_takeover]]（出方の決め所は `DeliveryOf` 1 か所）・[[l_wall_geometry]]（角までの距離と時計のずれ）・
[[onsite_experience_test]]（走行と解析器の対）
