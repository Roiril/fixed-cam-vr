---
name: backtrack-and-replay
description: 体験者が引き返したときの区間キーと演出の再演を触る前に：周は 2 つ／前進は直前のカメラも見る／once は「決着した回数」／録画は上書きしない
metadata: 
  node_type: memory
  type: project
  originSessionId: d878fe09-d0a5-4cbe-8b4c-6f84c4527e41
  modified: 2026-08-17T21:02:17.252Z
---

2026-08-17。契約は `.claude/rules/streaming.md` の「周回数は 2 つある」と
「引き返したら、途中で切れた演出を頭から出し直す」、体験側の要点は `rules/show-design.md`
「体験者は引き返す」。

**周回数は 2 つある。混ぜると片方が必ず壊れる。**
`LapCounterLogic.CurrentLap`（進行・単調増加・終了判定）と `SegmentLap`（区間・逆走で戻る・
演出／録画／post／BGM の区間キー）。`CueScheduler.CameraEntered` を 3 引数にして、
購読者にどちらかを**コンパイラが選ばせる**。`ShowRunDirector.NotifyLap` へ区間の周を渡すと、
帰りの A で 1 区間引き返した瞬間に**体験が終わらなくなる**（出口は `lap > totalLaps` の 1 本だけ）。

**前進の判定に「直前に居たカメラ」が要る。** 進入カメラが `order[pos+1]` と一致するかだけを見ると、
`A(2周目) → C → B` で B が「A の次」に見えて `(2,B)` を先取りする。
前進は `prev == order[pos] && camera == order[pos+1]` のときだけ。これで進行と区間が
いつも一緒に動く（片方だけ進む案を採ると、区間の物語が終了判定より遅れて 3 周目が丸ごと切れる）。

**`once` が数えるのは「始めた回数」ではなく「決着した回数」**（`TakeRunnerLogic.Outcome`）。
未決着で残るのは「体験者が区間を移って打ち切られた（yield）かつ未報告」だけ。
完走・報告・watchdog・卓の介入は全部決着。**再演の経路は作っていない** — 区間キーが戻るので
同じ区間へ入れば普通に武装される。

⚠ **走行中の演出を武装から除く**のを忘れない。旧実装は発火済みが開始時点で立っていたので
この穴が構造的に無かった。`policy:"hold"` は画面を持ったまま引き返せるので、
自分の区間へ戻ると**自分自身を武装して二重に始まる**。

⚠ **報告として数えるのは走行中の押下だけ。** 終わった後に猶予を作ると、連絡の面は
「異常は検出されませんでした」と出したのに機械は「報告済み」になる ＝ 同じ 1 回の押下について
画と機械が別のことを言う（連絡の面が読むのは `TakeRunnerLogic.NotifyMarkPressed` の**戻り値**
＝ `ShowControlClient.LastMarkResolved` で、走行中でなければ false。
⚠ 2026-08-17 まではここが `LastMarkHadTake`（押した瞬間の `ActiveTakeId`）だった —
`canon/LEDGER.md` 0082 で「解除が通ったか」へ変わっている）。

⚠ **一度録れた区間は録り直さない。** `SegmentRecordWriter` は `FileMode.Create` なので、
引き返して同じ区間へ戻ると**1 周目の映像が数秒の断片へ上書きされる**。3 周目に流すのはそれ。

観測は `ev=seg lap= cam= plap=`（食い違い ＝ 引き返し）と `ev=sum reN=`（出し直した回数）。
**この 2 つは別物** — 引き返したのに `reN=0` なら（報告済みでない限り）機構が効いていない。

関連: [[show_run_skeleton]] [[take_continuity]] [[screen_decay]]
