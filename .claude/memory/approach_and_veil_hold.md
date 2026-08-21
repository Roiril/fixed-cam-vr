---
name: approach-and-veil-hold
description: 接近の再設計（2周目B/C の当日録画・3周目A の持続の覆い・凍結と録画の起点を画像空間の鏡で合わせる）を触る前に読む実装の罠
metadata: 
  node_type: memory
  type: project
  originSessionId: ca97193f-ba68-4974-bd80-a0916ebe706a
  modified: 2026-08-21T22:00:10.518Z
---

`canon/LEDGER.md` 0102 の実装（2026-08-22）。設計書は
[reports/2026-08-22_approach-redesign.html](../../reports/2026-08-22_approach-redesign.html)、
契約は `rules/streaming.md`（持続の覆い / 切替音 / 録り始めの線）と
`rules/show-design.md`（接近の弧）が正本。ここは**その 2 つに書けない実装の罠**だけ。

## 1. `startCovered` で `_covered` を true にすると差し替えが 1 度も起きない

持続の覆いから入れ替わりへ引き継ぐとき、「もう覆い切っている」を素直に
`_covered = true` で表すと `justCovered` が永久に立たず、
**人 → 人形の `justSwapScreen` が発火しない** ＝ 覆いの下で何も入れ替わらないまま
凍結の画が最後まで残る。**進みを縮む段の頭へ置くだけ**にして、
最初の `Tick` で `cover` が 1 になった 1 フレームに縁を立てる。
`SwapVeilHoldTests.ContinuingFromHold_StillSwapsTheScreen_OnTheFirstFrame` が固定している。

## 2. 保持中のカットで `ShowCgLayer.Hide()` を呼ぶと、入れ替わりの終わりに人形が消える

覆いの形の供給元として人の代役を立てるので `HoldForSwap(true)` が立っている。
その状態で `Hide()` を呼ぶと**保留（`_hideDeferred`）に化け**、入れ替わりが終わって
`HoldForSwap(false)` になった瞬間に走る ＝ 次のカット（無人プレート＋人形）が空になる。

⇒ `TakeRunner` は保持中のカットで Hide を呼ばない（`holdVeil` で分岐）。
代役を畳む責任は `SwapMorphFx.Cancel` が持つ（`_holdAppliedCg`）。

⚠ **`ShowCgLayer.Apply` で `_hideDeferred` を降ろす解き方は駄目。**
「人形 → 人」の入れ替わりは `SwapToHuman` → `RestoreDoll` で 2 回 `Apply` を通るので、
降ろすと**終わった後も人形が立ち続ける**（4 周目 A が壊れる）。

## 3. シェーダの `hand`（覆い切ったら CG の形へ渡す）は「画面が差し替わった」前提

`hand = smoothstep(0.86, 1.0, _SwapCover)` は、**覆い切った縁で画面が無人へ差し替わる**
という前提で書かれている。持続の覆いはまだ差し替えていないので、そのまま渡すと
覆いが**いまの体験者の立ち位置**（CG の形）へ出て、映像の中の人からずれる。

⇒ `_SwapDiffHold` を立てているあいだ `hand` を 0 に潰す（差分を読み続ける）。
引き継いだ瞬間に 0 へ戻して CG の形へ渡す ＝ 実機と同じ縁でハンドオーバーする。

## 4. 覆いの左端（`_SwapMinX`）は早い棄却の箱の中で切る

差分（重い）は箱の中でしか引かないので、`sideOk = step(_SwapMinX, sampleUv.x)` を
**箱の条件と AND** にする。外へ出すと差分を払ってから捨てることになる。
実測（`menu swap -Set plate=white,hold=1`）で**左半分の黒が 0.00%** になっているのが証拠。

## 5. 偽ライブは 1 本の録画を進めながら使う（`trimStartSec`）

毎カット頭から出すと、割り込みのたびに映像の中の人が巻き戻って偽だとばれる。
2 周目 C は 0 → 1.9 → 3.6 → 5.0 と進めてある（POV の尺ぶんも足して連続に見せる）。

⚠ **`trimEndSec` は指定しない**（-1 = cue から継承 = 0 = 最後まで）。
指定すると `ScreenOverlayController` の自動停止（`_player.time >= trimEnd`）とカットの尺が競り、
**カットの途中で画面がライブへ戻る**ことがある。尺はカットの `durSec` が持つ。

## 6. 卓の `serializeStep` はキーの白名簿 — 足し忘れると 💾 で消える

`swapHold` / `swapMinX` / `switchSfx` を `timeline-model.js` の `newStep` と `serializeStep`
**両方**へ足した。`linesFromLayout` の `mirrorOf` も同じ（漏らすと鏡の対が保存で消え、
本番前チェックが「崩れている」を二度と言えない）。
`timeline-model.test.mjs` の 2 本が固定している。

## 7. 画像空間の鏡は `splitX` ではなく**枠の中央 0.5** で反転する

シェーダは `_SplitX` の値に関係なく `uvSrc.x = 1.0 - uvSrc.x`（枠 UV 0.5）で反転する。
3 周目 A は `splitX = 0.5` なので一致するが、**分割位置を動かしても鏡の軸は動かない**。
設計書の「splitX に対して反転」はこの一致に依っている表現。

⚠ `MjpegScreen.uvRotSteps` が 0 でないカメラでは軸が縦になるのでこの計算は成り立たない
（あの値は show.json に無い ＝ 卓からは見えない）。

## 8. show.json は卓サーバのメモリが正

著作は `GET /state` → 変更 → `POST /state`。**ファイルを直接書くと次の mutation で消える**
（`show_json_is_live_config` と同じ）。2026-08-22 の著作は rev 932 で入っている。
退避は `tools/web-compositor/show.json.bak-20260822_preapproach`。

## 9. `cue_monster_A/B/C` は cues 庫に残してある

take から外しただけ（設計 §6）。**素材は消していない**ので、戻したくなったら take を書き直すだけ。

関連: [[take_continuity]] / [[screen_decay]] / [[show_json_is_live_config]] /
[[cg_compositing]] / [[material_flow]]
