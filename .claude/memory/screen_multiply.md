---
name: screen_multiply
description: 3-C の増えるスクリーン（0317・試作）。目の時計のまま描くものだけ替えた。取り込みの形・席の焼き方・確かめ方
metadata:
  node_type: memory
  type: project
  originSessionId: 6cd725ef-8e93-4852-88d7-17922bbaff01
  modified: 2026-11-05T22:14:56.454Z
---

2026-11-06 ユーザー依頼（0317・逐語は LEDGER）を受けた試作。闇の目の代わりに、メインスクリーンと同じ画を映す面が頭の周りへ増える。参考は `mul.jpg`。

**切替**: `AnomalyEyes.ScreensReplaceEyes`。false にすれば目へ戻る。目のコードと版は消していない。

**時計と出番は目のまま**: `AnomalyEyesLogic`（兆し → 凝視 → 開眼 → 閉じ）と `EyesCueLogic`（0093）を流用。`eyes=` の開いた数は面の枚数になる。

**席**: `tools/make-screen-wall.py` が `ScreenMultiplySeats.cs` を焼く（seed 11・95 席）。先に Python で実フレームを貼って形を決めた。面どうしは重ねない。86% が前方 ±112°。0 番はメインの右上に固定で、点く順は 0 番から近い順に ±12° のゆらぎ。3 段（幅 20〜27° / 13〜19° / 7.5〜12°）で大きいほど手前。全部メイン（2m）より奥なので、メインは常に前に残る。

**画**: `ScreenMultiply` が手動カメラでメインスクリーンを正射影で撮る（10Hz・640x516 → 320x258）。リング 16 枚の `Tex2DArray` に積み、席ごとに 0〜1.5 秒前の画を映す。人形も乱れも外形も付いて来るのはこのため。外形は取り込みの alpha で切る。

**描画**: メッシュは面と箱で 1 draw、暈で 1 draw。`Queue=Geometry+50` なのでメインに隠れた所は深度で捨てる。暈は `Geometry+60` の加算で alpha を変えない。シェーダは `Assets/Resources` に置いて `Resources.Load`（`ProjectSettings` を触らないため）。

**音**: 最初の 1 枚が `ScreenOn`、以降が新しい `SoundCue.ScreenAdd`（`sfx_switch_1..6` の流用）。間引きは目と同じ 0.09 秒で、`sndEye=` に出る。耳では確かめていない。

**計器**: `scrMul=<組めたか>/<点いた枚数>/<取り込みの累計>`。点いたのに取り込みが 0 なら FAIL。

**確かめていないこと**: 実機の fps。別セッションの実測で、10-23 の実機は目が出ている 3-C で既に 40〜55fps（通常 88〜90）だった。専用カメラの Render は 10Hz の周期スパイクになりうる。

関連: [[eye_jack]] [[dark_eyes_slices]] [[twin_verify]]
