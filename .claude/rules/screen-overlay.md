---
name: screen-overlay
description: 差し替え素材の層（cue）の合成のしかた。掛け算の比の画・滲んで現れる順の画・長押しで膨らむ press の現行の契約（0349）
paths:
  - "Assets/Scripts/Streaming/ScreenOverlayController.cs"
  - "Assets/Scripts/Streaming/Overlay*.cs"
  - "Assets/Scripts/Streaming/TakeRunner.cs"
  - "Assets/Art/Shaders/Streaming/CrtCompositeShared.hlsl"
  - "Assets/Art/Shaders/Streaming/ScreenComposite.shader"
  - "Assets/Tests/Streaming/Overlay*Tests.cs"
  - "Assets/Tests/Streaming/TakeSplitLayerTests.cs"
  - "tools/gen-plate/bleed_*.py"
  - "tools/gen-plate/mask-recipes/bleed-*.json"
  - "tools/show-apply-bleed.py"
  - "tools/test_bleed_log.py"
---

# 差し替え素材の層 — 掛け算・滲み・膨らみ

**現行の契約だけを書く。** 変わったら行を書き換える（`rules/doc-sync.md`）。
経緯・実測・試し描きの所見は [memory/material_flow.md](../memory/material_flow.md) の「滲みと膨らみ」。

## 契約

- `_RevealTex` / `_OverlayRevealOn` / `_OverlayReveal` / `_OverlayRevealSoft` / `_OverlayMul` / `_PressTex` / `_PressMaskTex` /
  `_PressScale` / `_PressCenter` / `_OverlayPress` を書くのは `ScreenOverlayController` だけ。既定は全部 0 で、従来と同じ画になる
- `blend:"multiply"` の cue は `sourceUrl` を**比の画**として読む。比の画は線形の比「素材 ÷ 無人の写真」を
  sRGB で符号化した PNG で、1 は実写のまま。画は `live * lerp(1, ratio, a)` — `OverlayRevealLogic.IsMultiplyBlend` / `CrtCompositeShared.hlsl`
- `revealUrl` を持つ cue は、枠空間 640x360 の灰色の順の画（0 が最初・1 が最後・外は 1）で滲んで出入りする。
  `revealDelaySec` 待って `revealSec` で 0→1（曲線 `1-(1-t)^2.2`）、出たあとは息をする。
  引くときは `strength` ではなく順の画を逆に戻す（`revealOutSec`）— `OverlayRevealLogic` / `ScreenOverlayController.StopOverlay(float)`
- 順の画と press マスクは既存のサンプラーを借りる（`sampler_MaskTex`・`sampler_OverlayTex`）。読むのは `[branch]` の中だけ
  — `ShaderSourceLintTests.BleedTextures_*`
- 膨らみが起きるのは、take の `markStartCueId` が名指しした cue が `pressMaskUrl` を持つときだけ。
  その take が始まると `BeginPress` で貼り、cue は差し替えない（それまでの画が残る）— `TakeRunner.EnsureTakePress`
- 長押しの進み具合は `OvrControllerBridge` → `ShowControlClient.SetVisitorMarkProgress` → `TimelineDirector` → `TakeRunner` →
  `ScreenOverlayController.SetPress` と毎フレーム届く。受けない時（早すぎる・塞がれている・終幕）は 0 を送る。
  ツインと再生は同じ入力を通るので同じ値が届く
- 膨らみはばね（`OverlayPressLogic`・2.4Hz・ζ0.38・−0.15..1.1・1/120 秒刻み）。離すと 0 より下へ揺れて戻る
- 報告が成立したら、膨らみを 0.12 秒で畳み、素材を `OverlayRevealLogic.MarkDoneOutSec` で引き、
  `markDoneCueId` を 2 枚目の層に `markDoneSec` 出す — `TakeRunner.BeginPressMarkDone`
- `revealUrl` と `pressMaskUrl` は焼き込みに同梱する（`export_build.CUE_URL_KEYS`）。卓の cue 編集は
  `CUE_CARRY_KEYS` を消さずに持ち越す — `common.js carryCueFields`
- 観測は `ev=cue rev=` / `revMax=`・`ev=take pressMax=`・`ev=sum ovlRev= ovlPress=`。読み手は
  `analyze-xp-log.py` の `bleed_findings`（`REVEAL_MIN_MAX` / `PRESS_MIN_MAX`）。対で直す
- 試し描き（`tools/gen-plate/bleed_preview.py`）の定数は C# とシェーダの写し。`tools/test_bleed_log.py` が突き合わせる

## 罠

- 比の画・順の画・press マスクは sRGB で読まれ mip を持つ。線形の値として書いた PNG は暗く読まれる。道具は sRGB で書く
- 順の画の無い cue では `StopOverlay(float)` の秒は使われず、cue の `fadeOut` で薄まる
- 演出のカットは cue を写して出す（`TakeRunner.BuildStepOverlay`）。掛け算と滲みを継がないと比の画が差し替えの画として出る — `TakeSplitLayerTests.StepOverlay_InheritsMultiplyAndReveal_FromTheCue`
- ツインの通しは比の画・順の画・press マスクもツインの部屋のものへ差し替える（`tools/twin/assets.json` の `reveal` / `pressMask`。作るのは `mask-recipes/bleed-twin-*.json`）。本番の素材を替えたらツイン側も作り直す
- 卓のプレビュー（`shaders.js` / `pipeline.js`）は掛け算・滲み・膨らみを描かない。見た目は `bleed_preview.py` で見る
- 台本へ当てるのは `tools/show-apply-bleed.py`。素材を `static-inputs/` へ写してから `--apply`。卓を止めてから流す
