---
name: l-wall-geometry
description: L 字パーテーションの 2 辺は等長 0.955m。show.json には壁の表現が 2 つあり、2026-08-05〜09-11 は room.walls が 0.59/1.22 の非対称で、機械で読むと毎回そこから間違えた
metadata: 
  node_type: memory
  type: project
  originSessionId: b9ebf964-42a4-4b4c-b264-b168f431a2f3
  modified: 2026-09-11T09:02:26.477Z
---

# L 字の壁の寸法を機械で読む前に

**実物は 2 辺とも 0.955m の等長**（`tools/walk-guide/build_walk_guide.py` の `WALL_ARM`。`Joint.blend` の実測。
ユーザーの判定は `canon/LEDGER.md` 0164 と 0183 — **同じ指摘を 2 回受けた**）。

## なぜ毎回間違えたか

`show.json` の `layout` に壁の表現が **2 つ**ある。

| 表現 | 値（2026-09-11 現在） | 誰が読む |
|---|---|---|
| `layout.room.walls`（w1 / w2 の x1,z1,x2,z2） | **0.955 / 0.955**（0183 で直した。それまで 8/5 から 0.59 / 1.22） | 卓のフロアマップ・CG の遮蔽（`ShowRoomProxy`）・較正の線・**show.json を parse するサブエージェント全員** |
| `layout.wall`（corner / endX / endZ・旧形式） | 1.0 / 1.0 | 位置合わせの既定点（`calib-session.js`）・`floor-sketch.js` |

8/5 に誰かが `room.walls` を 0.59 / 1.22 に動かし（経緯は不明）、正しい値は**コメントと台帳の文章にしか無かった**。
機械で読む場所には 1 か所も無かったので、`show.json` から幾何を起こしたセッションは全員同じ誤りから始めた。
卓のフロアマップも同じ値を描いていた（画面を撮って確認した）。

**How to apply:**
- 壁の寸法を使う計算は `room.walls` を読んでよい（いまは実物と一致）。ただし**読んだ値が等長でなければ、
  計算より先にこの記録と `WALL_ARM` を疑う**（`fig_route.py` は食い違うと警告を出す）
- `layout.wall` は 1.0 / 1.0 のまま。位置合わせの既定点なので、0.955 へ寄せるかは `OPEN.md` の問（登録の残差が動く）
- `show.json` は卓サーバがメモリで配る。直すときは `POST /state {layout}`（0183 でやった手順）。ファイル直書きは巻き戻る

**Why:** ブリーフに `room.walls` の座標をそのまま書き、証拠エージェントがそこから角までの距離を 0.7m と出し、
「5 秒には角を曲がって 0.5m 先」と誤った診断を台帳とレポートに書いた。等長で計算し直すと角までは 1.1m・5.1 秒で、
「止まってください！」は平均的な人がちょうど角に着く時刻だった（[[show_json_is_live_config]]）。
