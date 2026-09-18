---
name: closing-line-halt
description: 4-A の③a「止まってください！」は時計ではなく「締めの線（3-A の凍結点）を踏んだ瞬間」に出る（0233）。線は台本から導く・入り際 0.6 秒の猶予・自動走行は線を踏みに行く・時計 5 秒は線が無いときの退避路
metadata: 
  node_type: memory
  type: project
  originSessionId: c1e3e152-4a70-44a5-b034-7cd0e5030226
  modified: 2026-09-18T15:46:26.426Z
---

# 4-A の「止まってください！」は場所で出る（2026-09-19・`canon/LEDGER.md` 0233）

ユーザー逐語「時間指定で 4s ではなく、場所指定にし、その場所を、左右反転の演出のときのフリーズされる位置に」。

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
- **`CommsCueLogic` は線があれば線だけ、無ければ時計（`HaltAfterClosingSec` 5 秒）**。
  `closingLineDefined` は「layout に実体がある」で、実体の無い id は `TakeRunner.ApplyLinesFromLayout` が -1 に倒す
  （実体の無い線は決して横切られず、③a が黙って消える方が危ない）
- **自動走行は経路がたまたま線をまたぐことに頼らない** — `ShowWalkDebugDriver.CrossClosingLine` が締めのカットの
  始まりを待ってから線を踏みに行く（導入の `line_1` と同じ形）。9/5 の走行では入り際の横断が締めの 0.56 秒前で、
  猶予 0.6 秒の内か外かは dwell とフレームの都合で走行ごとに変わっていた
- **観測**: `ev=closingLine id= sec=`（踏んだ縁）・`ev=comms closing= cline=`（Halt の行は cline=1 のはず・
  cline=0 なら退避路）・`ev=config closingLine=`。解析器は 5 通り（線→Halt / 退避路 / 踏んだのに無し /
  踏まず（人=WARN・走行=FAIL）/ 線の無い台本）で校正済み
- 言われていないことは `OPEN.md` Q15〜Q17（踏まない人には出さない・報告後でも出す・猶予前の③b はそのまま）

関連: [[comms_takeover]]（出方の決め所は `DeliveryOf` 1 か所）・[[l_wall_geometry]]（角までの距離と時計のずれ）・
[[onsite_experience_test]]（走行と解析器の対）
