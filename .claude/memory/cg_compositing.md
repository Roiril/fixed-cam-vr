---
name: cg-compositing
description: CG 人形の合成（較正・影・部屋プロキシ・照明）を触る前に知るべき前提と、実測で決まった運用制約
metadata: 
  node_type: memory
  type: project
  originSessionId: a6c8f7d1-9002-4f6a-a3d9-d11f6afaa276
  modified: 2026-07-27T14:44:48.323Z
---

CG 人形を実写映像へなじませる基盤は 2026-07-27 に作り直した。契約の正本は
[.claude/plans/2026-07-27_cg-compositing-rebuild.md] と [.claude/rules/streaming.md] の「合成の作り直し」節。
ここには**触る前に知らないと事故る前提**だけ書く。

## 触る前に知るべきこと

- **像空間の対応付けが命**。CG は必ずライブ映像と同じ contain-fit 枠（`_CgScale`）でサンプルし、
  RT はソース映像の実寸、画角は**水平**（`hfovDeg`）が正。この 3 つのどれかを崩すと、姿勢を
  完璧に測っても人形は合わない（作り直しの起点がまさにこれ）
- **卓と Unity で同じ数値を出す箇所が 2 つある**。どちらも期待値を両側にハードコードして突き合わせてある:
  - 投影: `calib.test.mjs`「投影は Unity と同じ画素を出す」⇔ `CgProjectionTests.Projection_MatchesPinholeFormula`
  - 色温度: `room-model.test.mjs`「kelvinToRgb は Unity と同じ値」⇔ `CgProjectionTests.Kelvin_MatchesConsoleFormula`
  **片方だけ直すと沈黙して食い違う**（卓では合うのに実機が違う、という最悪の症状になる）
- **卓は CG 人形を描かない**。輪郭プロキシ（半透明の線）だけ。写実に見せた瞬間に
  「著作者が卓を信じて Unity 確認を飛ばす」が始まる
- レンズ歪みは**除算モデル** `r_u = r_d/(1+k1 r_d²)`。順・逆に解析解があるので卓の順投影とシェーダの
  逆変換が厳密に一致する。多項式に変えると画面端で食い違い、較正の検証自体が成立しなくなる

## 実測で決まった運用制約（変えると精度が落ちる）

| 事実 | 数値 | 帰結 |
|---|---|---|
| 焦点距離を既知にすると精度が一桁変わる | クリック誤差 1.5px で、f も推定すると位置 **27cm** / f 固定なら **2cm** | **画角は一度だけ丁寧に測って固定、置き場所は現場で毎回** |
| 歪みを無視すると高さが 4 割狂う | 広角 k1=0.18 でカメラ高 1.5m → 0.91m | k1 を後回しにできない |
| f 未知のまま k1 を解くと縮退する | 5 点で f 430→300 の偽解 | f 未知なら点 6 個以上 / f 固定なら 4 点で可 |

## 壊れやすい所

- **影のステンシルを外すと影が「濃い斑」になる**（腕と胴の投影が重なって二重に暗くなる）。
  そのため RT の depth は **24**（depth24 + stencil8）。16 に戻すと沈黙して化ける
- **premultiplied の rgb/a を別々に触らない**。ぼかしも色収差も rgb > a の画素を作ると縁が壊れる
- **素材カット（clip / still）に人形は出ない**（パースが原理的に合わないため）。`rec` は出る
- **HMD 位置合わせが未完了だと人形を出さない**。course→world が identity に落ちて別の場所に立つため

## 確認のしかた（実機を持ち出す前に）

1. 卓の［🎯 姿勢を合わせる］→ **部屋のワイヤーが実映像に重なるか**（較正が合っているかの唯一の一次証拠）
2. Unity の `Tools/FixedCamVr/Diagnostics/Preview Show Composite` → `Assets/Screenshots/cgviz/` に
   実写プレート × 人形 × 影の合成 PNG。**最終的な見た目の一次証拠はこれ**（出力は gitignore）
