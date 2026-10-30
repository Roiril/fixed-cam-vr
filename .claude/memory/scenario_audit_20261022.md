---
name: scenario-audit-20261022
description: 2026-10-22 の現行シナリオ監査（正規・逸脱・運用の 3 視点）。未修正の指摘 17 件。最重要は 4-A で締めの線が消え、③a より先に人形 End が成立しうること
metadata:
  node_type: memory
  type: project
  originSessionId: 4380d259-bb76-4f0d-ad6c-2a7779d07767
  modified: 2026-10-22T04:41:47.748Z
---

2026-10-22 に配布中の show.json rev 1283 とコード（8a25a895）でシナリオ監査をした。
本文は [reports/2026-10-22_scenario-audit.html](../../reports/2026-10-22_scenario-audit.html)。**どの指摘もまだ直していない。**

- **最重要**: 3-A を位置合わせ版（0274）にして `splitFreeze` のカットが消えたので、
  `TakeSchema.ResolveClosingLineId` が空を返す。4-A の③a は 3 秒の時計だけで出る。
  人形 End（左半分）は催促を待たないので、警告より先に成立しうる（0233 / 0247 / 0178 と矛盾）
- 中: 乗っ取りを見ていない人にも③a の復帰表示が出る／乗っ取り後の砂嵐が 3-A の頭を覆う／
  hold＋untilZoneChange の演出（1-B 手・2-A 染み）は引き返しても再演されない／
  2-B の 3 カット目が camera=1 固定で、A に戻った人に B が映る／1 周目の逆走で 3 周目 B・C の録画が逆向き／
  4-A の左コントローラ断で帰還不能／結果画面の分母（0247）が外れた発話が LEDGER に無い／
  リセット完了がタイトルの段に依存／タブレットがリセットの中間状態を失敗と読む／台車の失敗が次の人へ持ち越し（10-30 に台車ごと廃止）
- 文書のずれ: show-design 冒頭の 3-A・解除後の返事、streaming.md の OSD「最後」、hmd_onboarding.md の順番

**Why:** 修正は別途。次に 4-A・3-A・リセットを触るセッションが同じ穴を踏まないため。
**How to apply:** 直したら本文の該当番号に済みを付け、ここからも外す。関連 [[ending_result]] [[comms_takeover]] [[staff_setup]]
