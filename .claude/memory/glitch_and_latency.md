---
name: glitch-and-latency
description: 乱れ演出と遅延計測を触る前に：_Glitch と _SignalLost は別系統／post は 4 箇所同時／絶対 E2E は測っていない（/clock が要る）
metadata: 
  node_type: memory
  type: project
  originSessionId: 9c9e9742-4d99-47bc-8b46-e7eb4d64ed2b
  modified: 2026-07-29T03:29:11.526Z
---

2026-07-29 に企画書（学会論文版）の要求へ合わせて足したもの。契約は `.claude/rules/streaming.md`。

**映像の乱れ（グリッチ）**

- uniform は `_Glitch` / `_GlitchSeed`。障害表示の `_SignalLost` とは**別系統**で、書き手は `GlitchFx` だけ。
  シェーダの順序は 乱れ → 障害表示 なので、実際に信号が切れたら障害表示が勝つ。
- 位置ずれは**全層のサンプル前の uv** に掛ける（ライブ・素材・マスク・CG が一緒にずれる ＝ 差し替えの継ぎ目も隠れる）。
  ここを層ごとに掛けると継ぎ目だけ無傷で残り、企画書の狙いが消える。
- 出口は 4 つ：カットの遷移 `glitch` / カット頭の `steps[].glitch` / `control.switchGlitch` / 卓の `⚡`（`glitchEpoch`）。
  単発と持続は **max 合成**（加算だと飽和して遷移の形が潰れる）。

**post が 12 項目になった**

`aberration` / `pixelate` / `scanlineCount` を追加。**足すときは 4 箇所を同時に直す** —
`ScreenComposite.shader` / `shaders.js` の `FS_POST` / `common.js` の `FX` 表 / `pipeline.js` の uniform 受け渡し。
機械テストが無く、片方だけだと沈黙して食い違う（走査線の本数とグレインの座標が実際にそうなっていた）。

**遅延**

**絶対の end-to-end は測っていない。** `X-Capture-Ns` は配信端末の monotonic、Unity は自前 Stopwatch で
基準が違い、引き算すると端末間の時計のずれがそのまま遅延として出る。出しているのは
到着の揺らぎ（窓内最小からの超過）・展開・提示・配信側の鮮度だけ。
**絶対値が要るなら配信アプリ（fixed-cam-streamer）に「`X-Capture-Ns` と同じ基準の現在時刻」を返す口が要る。**
入れば `LatencyEstimatorLogic.ObserveArrival` のオフセット推定に差し替えるだけで済む形にしてある。

`/health` の熱フィールドを読むようになり、**降格中は lag 判定を抑止**する。
旧実装は熱で落ちた fps を経路の詰まりと誤認して 5 秒ごとに張り直し、黒 / 砂嵐を出しながら悪化させていた。

関連: [[show_run_skeleton]]
