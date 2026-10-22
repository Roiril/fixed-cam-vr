---
name: screen-swap-eyes
description: 乱れ・すり替わり（swap）・闇の目・目の視界ジャックの現行の契約。経緯と旧い記述は reference/archive/ の凍結版
paths:
  - "Assets/Scripts/Streaming/Glitch*.cs"
  - "Assets/Scripts/Streaming/Swap*.cs"
  - "Assets/Scripts/Streaming/AnomalyEyes*.cs"
  - "Assets/Scripts/Streaming/EyesCueLogic.cs"
  - "Assets/Scripts/Streaming/EyeJack*.cs"
  - "Assets/Art/Shaders/Streaming/AnomalyEyes.shader"
  - "Assets/Art/Shaders/Streaming/EyeJack.shader"
  - "Assets/Tests/Streaming/Glitch*Tests.cs"
  - "Assets/Tests/Streaming/Swap*Tests.cs"
  - "Assets/Tests/Streaming/AnomalyEyes*Tests.cs"
  - "Assets/Tests/Streaming/EyesCueLogicTests.cs"
  - "Assets/Tests/Streaming/EyeJackLogicTests.cs"
---

# 2. 乱れ・すり替わり・闇の目

**現行の契約だけを書く。** 変わったら行を書き換える（日付の節を足さない・`rules/doc-sync.md`）。
体験者に見えることは `canon/NOW.md`、経緯は `reference/archive/streaming-2026-10-22.md`（S:）・`reference/archive/show-design-2026-10-22.md`（D:）。出典の行番号はその凍結版のもの。

## 契約
- 乱れは `_Glitch` / `_GlitchSeed`。書くのは `GlitchFx` だけ。信号断の砂が後に掛かるので信号断が勝つ — `GlitchFx` 〔出典: S:1030-1032〕
- 乱れは 2 段。全層のサンプル前 uv をずらし、post の後に砂を掛ける — `GlitchUv` / `ScreenComposite.frag` 〔出典: S:1033-1034〕
- 乱れの砂は信号断と同じ乗算・同じ定数。混ぜ具合は `g×1.5`。黒帯の中だけ地を 0.027 へ上げる — `SAND_*` / `ContainUv` 〔出典: S:1035-1040〕
- 持続と単発は max で合成 — `GlitchEnvelopeLogic` 〔出典: S:1047-1048〕
- 乱れは起きた回数の 3 乗で育つ。12 回で頭打ち、強さの上限 0.60、尺の上限 0.50s。倍率と頭打ちは別 — `GlitchEscalationLogic.Exponent` `SaturateAtCount` `MaxLevel` `MaxHoldSec` `LevelAtMax` 〔出典: S:1050-1074、LEDGER 0055 / 0057〕
- 数えるのは `GlitchFx` 1 か所。`SetSustain` は立ち上がりだけ数え、報告で消す乱れは数えない — `GlitchFx.SetSustain(level, count)` 〔出典: S:1078-1080、S:2541-2544〕
- ラン単位で落とすのは `GlitchFx.ResetAll` 1 か所 〔出典: S:1080〕
- すり替わり（`transition:"swap"`）を使うのは 4-A（人形 → 人）だけ。3-A は全画面の乱れ — LEDGER 0119 〔出典: S:2255-2259〕
- `_Swap*` と `_SwapMaskTex` を書くのは `SwapMorphFx` だけ。段は 32% / 42% / 26% — `SwapMorphLogic.RiseFrac` `MorphFrac` 〔出典: S:2265-2274〕
- 覆いは黒。芯（無人プレートとの差分、無ければ CG の形）∪ 棒（48 行の横棒） — `SwapDiffAt` / `_SwapSmear.x` 〔出典: S:2283-2306、LEDGER 0091〕
- 差分を引くのは人 → 人形のほどける段だけ 〔出典: S:2308-2313、LEDGER 0100〕
- 画面の差し替えは `justSwapScreen` の 1 フレーム。人 → 人形はほどけ切り、人形 → 人は育ち切り — `SwapMorphLogic`（`justSwapScreen`） 〔出典: S:2433-2442〕
- 4-A で 1 層目を畳んだ跡は無人プレートを出す（静止画のみ・マスク等は触らない） — `TakeRunner.CoverWithPlate` / `ScreenOverlayController.ReplaceStillSource` 〔出典: S:2443-2457、LEDGER 0155〕
- 向きはデータで指さない。前後で人形の有無が同じなら乱れへ倒す 〔出典: S:2458-2460〕
- 差分のしきい値と mip は 1 か所（lo 0.06 / hi 0.20 / lod 0.6）。ほどけ切りの全画面乱れは 0.55 × 0.20s — `SwapMorphLogic.MaskDiffLo` `MaskDiffHi` `MaskDiffLod` `VeilLevel` `VeilSec` 〔出典: S:2411, S:2466-2467〕
- 黒い波の時計と段の値は C#、形の寸法はシェーダ。体の入力は見た目だけを変え、総尺と縁を動かさない — `SwapWaveLogic` / `SwapEnergyLogic` / `SwapWaveLogicTests` 〔出典: S:2349-2364〕
- 縦は機械（行の高さを揺らさない）、黒だけで組む、乱数を使わない 〔出典: S:2330-2336, S:2364〕
- 持続の覆い（`swapHold`）は機構だけ残り、著作は 0 本 — `SwapMorphFx.BeginHold` 〔出典: S:2075-2083、LEDGER 0119〕
- 左右分割の反転はライブと凍結だけ。軸は枠 UV の 0.5。第 2 層は静止画専用・フェードしない・色合わせは 1 層目だけ — `_SplitFlipLeft` / `CameraFeelFx.SetSplit` / `_Overlay2Tex` 〔出典: S:2045-2073、LEDGER 0050〕
- 闇の目はスクリーンの外だけ（Background+100・前乗算）。段は兆し 1.42 / 凝視 0.5 / 開眼 3.0 / 閉じ 1.6 秒で著作させない — `AnomalyEyesLogic.HintSec` `StareSec` `SwarmSec` `CloseSec` 〔出典: S:1801-1815, S:1902-1911、LEDGER 0076 / 0094 / 0138〕
- 兆しの開きは焼いた `sfx_eye_big` の形に合わせる — `AnomalyEyesLogic.HintOpenKnots` 〔出典: S:1915-1919、LEDGER 0138〕
- 目は壊れた画像データの版を引く。版を瞼で切らない・滑らせない・縦に伸縮させない — `Resources/Eyes/EyeGlitch.png` / `tools/make-eye-glitch.py` 〔出典: S:1817-1835、LEDGER 0237-0240〕
- 開始と閉じ始めは居場所が決める。宣言した区間でしか始まらない — `EyesCueLogic.Declare` / `ReEnteringTheZone_StartsAgain_WhenTheCutDeclaresAgain` 〔出典: S:1854-1898、LEDGER 0093〕
- 中止系は 1 フレームで消す — `AnomalyEyes.Abort` 〔出典: S:1863〕
- 目どうしは重ねない（楕円の当たり・置けない席は採らない） — `AnomalyEyesMesh` 〔出典: S:1939-1944、LEDGER 0077〕
- 視界ジャックは全開＋0.4s か区間の半分の早い方で発火。総尺 2.4s ÷ 枚数（0.2〜0.5s）。上限 6s — `EyeJackLogic.TotalSec` `HoldBeatSec` `MaxActiveSec` / `EyesCueLogic.HalfReached` 〔出典: S:1994-2024、LEDGER 0099〕
- 写真の正規化（縮小・EXIF・減光）はサーバが焼く — `EYEJACK_BRIGHTNESS` 〔出典: S:1987-1992〕〔未照合〕

## 罠（破ると壊れる）
- `startCovered` でも `_covered` を true にしない — `SwapVeilHoldTests.ContinuingFromHold_StillSwapsTheScreen_OnTheFirstFrame` 〔出典: S:2105-2108〕
- 「人形か」は `ShowCgLayer.DollVisible` で訊く（代役を人形と読まない） 〔出典: S:2121-2129〕
- 保持中に `ShowCgLayer.Hide()` を呼ばない 〔出典: S:2130-2132〕
- 持続の覆いのプレートはカットのカメラで選ぶ（`ActiveIndex` は前の区間） — `CameraSwitchDirector.TakeVeilHoldBegin` 〔出典: S:2396-2408〕
- `_SwapWave2.w` の既定は 1。0 を書くと基本の波まで消える 〔出典: S:2353-2354〕
- 走査線は画面の座標で刻む（figure 空間にしない） 〔出典: S:2426-2428〕
- 人 → 人形でほどけている最中の CG を画に混ぜない（二重の人） 〔出典: S:2318-2322〕
- 目の明るさは `AnomalyEyes.DefaultGain`（0.7）とシーン `[Eyes]` の `gain` が対。`menu scene` はシーン値を上書きしない 〔出典: S:1837-1840〕
- プレビューは本番が書く uniform を全部書く — `AnomalyEyesPreviewParityTests` 〔出典: S:1844-1850〕
- 対で直す値: `RiftExtend` ⇔ `RIFT_EXTEND` / `--ratio` ⇔ `TEX_RATIO` 〔出典: S:1833〕
- `AnomalyEyesLogic.EyeOpen` とシェーダの `open` は同じ式 〔出典: S:1967-1968〕〔未照合〕
- AnomalyEyes / EyeJack は実行時 `Shader.Find`。Always Included から外さない 〔出典: S:1969, S:2031〕
- EyeJack は queue 4940・`Blend One Zero`・alpha 1。`DriveJack` は目の `Stage==Off` の早期 return より前 〔出典: S:2026-2034〕
- cover-fit は `AnomalyEyes.CoverUv` 1 か所 〔出典: S:2034〕
- 観測 `glN` `glE` `swap=` `wrap=` `eyes=` `jack=` は `ShowTelemetryHost` と `analyze-xp-log.py` を対で直す 〔出典: S:1084-1087, S:2137-2141, S:2384-2387〕

## 深く読むとき
- 乱れの育ち → S:1026-1087 / memory/glitch_and_latency.md
- すり替わりの覆いと波 → S:2250-2474 / memory/swap_veil_mask.md
- 持続の覆い → S:2075-2141
- 左右分割 → S:2045-2073
- 闇の目 → S:1801-1970 / memory/dark_eyes_slices.md
- 視界ジャック → S:1972-2043 / memory/eye_jack.md
- 差し替え素材のマスク → memory/composite_mask_quality.md

---
