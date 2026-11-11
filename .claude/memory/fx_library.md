---
name: fx_library
description: 演出ライブラリー（tools/fx-library）。ツインの目線の動画から演出ごとに山場 1 枚と動画を切って起きる順に並べる。撮影が内蔵 GPU で描いてカクついていた罠
metadata:
  node_type: memory
  type: project
  originSessionId: 087f4247-3d8b-4247-8d54-581fc30a9915
  modified: 2026-11-11T07:57:51.116Z
---

2026-11-11 ユーザー依頼「この作品の中で使われている演出ライブラリーを作ってほしい。時系列順に並べるデザインスキルを使用して、デジタルツインを用いた体験者視点のもので、一番その演出の特性を写す1枚の写真と、それに至るアニメーションを全部。細かいことは任せる。今後の演出改善に生かすためのライブラリーです。」

**形**: `tools/fx-library/catalog.json`（1 演出 1 行・起点の出来事と秒）→ `build.py` → Drive `reports/fx-library/<時刻>/index.html`。
年表 `poster.html` は timeline スキルの検査（`timeline.mjs both`）を通す。回し方は `tools/fx-library/README.md`。

**走行は 2 本**: 本線 `--calib twin --marks 1A,1B,2A,3B --keep-rec --tag fxlib-main`（帰還 End）と枝 `--calib twin --nomark --keep-rec --tag fxlib-doll`（人形 End）。
`--marks` はこのとき足した（[[twin_verify]]）。既定の走行では報告で起きる演出が 1 度も画に出ないため。

**⚠ 撮影が Intel の内蔵 GPU で描いていた（11-11）**: ユーザー「アニメーションがかっくかくで、フレームレートが低すぎて見れたものじゃない。」
- `XpReplayCapture` は実時間で進み、遅れたコマは前の画を繰り返す。1080p で 1 コマ 200ms・描けたのは 283 秒で 775 コマ（毎秒 2.7）。540p にしても 2027 コマ（129ms）で、固定費が残った
- 原因は解像度ではなく GPU。batchmode の Unity のログ `Renderer: Intel(R) UHD Graphics`。この PC は RTX 4090 Laptop も積んだハイブリッド
- **Windows の Editor で効くのは `-force-device-index 1`**（`-gpu 1` は効かない。どちらも起動して `Renderer:` 行で確認）。`unity.ps1` の `Get-UnityGpuArgs` が、内蔵と独立を両方見つけたら replay / twin-run / menu / crt に付ける。別の PC は `FCV_UNITY_GPU`。規約は `~/.claude/rules/windows-env.md` §14
- 撮影の開始行に `gpu=` を出す（`[Replay] 撮影を始めた ... gpu=...`）。カクついたら最初にここを見る
- 時間を止めて 1 コマずつ描く形は取らない。本番は実時間の時計を約 140 か所で読む（`XpReplayDriver` の冒頭・10-23 実測）

**山場の秒は走行ごとにずれる**: 歩く速さ・報告の有無で変わる。目録の `hero` は `--sheet` の `look/` で選び直し、`fx/<id>/hero.jpg` をフル解像度で見て決める。
導入の前半はツインの部屋が黒い（パススルーが無い）ので暗い画になる。そう注記して出す。

関連: [[twin_verify]] / [[experience_replay]]
