---
name: screen_multiply
description: 3-C の増えるスクリーン（0317・試作）。目の時計のまま描くものだけ替えた。帯の並び・点く順・色・回転・閉じ・床の映り込み・確かめ方
metadata:
  node_type: memory
  type: project
  originSessionId: 6cd725ef-8e93-4852-88d7-17922bbaff01
  modified: 2026-11-12T23:09:10.326Z
---

2026-11-06 ユーザー依頼（0317・逐語は LEDGER）を受けた試作。闇の目の代わりに、メインスクリーンと同じ画を映す面が頭の周りへ増える。参考は `mul.jpg`（列に並んだ光の走馬灯）。

**切替**: `AnomalyEyes.ScreensReplaceEyes`。false にすれば目へ戻る。目のコードと版は消していない。

**時計と出番は目のまま**: `AnomalyEyesLogic`（兆し → 凝視 → 開眼 → 閉じ）と `EyesCueLogic`（0093）を流用し、どちらの時計も触らない。`eyes=` の開いた数は面の枚数になる。

**席**: `tools/make-screen-wall.py` が `ScreenMultiplySeats.cs` を焼く（seed 11・104 席。手で直さない）。席は帯番号を持つ（`ScreenSeat.band`。0 目の高さ / 1 上 / 2 天頂 / 3 霧の奥）。
- 全部の面が頭を向く（tilt 0・roll ±1.2°）。上の帯は仰角 27°・3.3m、目の高さの帯は 2°・3.9m で、メインの後ろも空けない（空けた所は帯の回転でメインの横へ流れて穴に見えた。後ろを通る面はメインに隠れる）。どちらも 360° 巻き、背面（|方位| > 130°）は 3 枚に 1 枚抜く
- 幅は 11 / 15.5 / 22° の 3 種で、同じ幅を 3 つ続けない（抜いた後も見る。上の帯は 0 番を含めて環で見る）。隙間 0.8〜1.6°。大きい面だけ仰角 ±2.5°・距離 ±0.25m ずらす。中・小は帯の中心線に乗る
- 天頂の帯は仰角 46°・4.6m・幅 8〜10°・方位 ±110° に 10〜14 枚。詰まらないときは枚数を 1 ずつ減らして引き直す（隙間 2.0°）
- 霧の奥の段（6.8〜10.5m・幅 3.6〜6.4°・56 枚）は帯の間の仰角に置く。奥の段だけ手前の面へ 1.6° まで隠れてよい
- 0 番はメインの右上（方位 13°・仰角 24.5°・幅 22°・上の帯）に固定。全部メイン（2m）より奥なので、メインは常に前に残る
- 点く順 `rank` は 0 番からの方位差 ÷ 180 ＋ 帯の遅れ（上 0 → 目 0.06 → 天頂 0.12 → 奥 0.18）± 0.03。取り込みの遅れ `delay` は方位差に比例（180° で 15 コマ）
- 帯番号は頂点の uv3.x で渡す。uv3.y は箱の陰（以前は頂点色にあった）

**画**: `ScreenMultiply` が手動カメラでメインスクリーンを正射影で撮る（10Hz・640x516 → 320x258）。リング 16 枚の `Tex2DArray` に積み、席ごとに `delay` コマ前の画を映す。人形も乱れも外形も付いて来るのはこのため。
- 枠はメインのテレビの筐体そのもの（0324）。取り込みは筐体ごと撮る（`_CrtTvCase` を触らない）。面は自前の枠を描かない。管と筐体の境はメインと同じ輪郭のマスク（`_CrtScreenMaskTex` → 面の `_WallTubeMask`）で切る
- 色味・乱れ・電源の入り切りは管の中だけに掛ける。筐体は染めない
- ⚠ 0319〜0323 の間は管の形だけ取り込み自前の四角い枠を描いていた。メインと違う枠だと指摘されて（0324）やめた

**色**: 席の色は `ScreenMultiplyLayout.Tints` の 2 色（寒色・淡い寒色。淡いのは席の 25%）。点いたばかりの面は橙 `WarmTint`（1, 0.60, 0.32）で、`WarmSettleSec` 1.3 秒かけて席の色へ落ちる。式は C# の `TintAt` と `ScreenMultiplyCommon.hlsl` の `WallTint` が同じ（smoothstep）。波の先頭だけが橙に見える。

**帯の回転**: `ScreenMultiplyLayout.DriftDeg(band, sec)` 1 か所。目 +0.9°/s・上 −1.2°/s・天頂 +0.7°/s・奥 0。隣の帯は逆向き。`HorrorRelief.Enabled` のときは 0。シェーダへは帯ごとのラジアンを float4 `_WallDrift` で渡す。`AnomalyEyes` の 0 番の向き・`NewestOpen` の音の向きは `ScreenMultiply.SeatCenter` で回った後の位置を引く。

**閉じ**: 目の時計の閉じのまま、点く順の逆（rank の高い背面から、0 番が最後）。面ごとに CRT の電源断（横線 → 点）をして、`AfterglowSec` 0.5 秒の残光を残す。`_closedAt` は残光の間 openAt を保ち、点き直した席は時刻を上書きする。
- 閉じの半ば（closing ≥ 0.5）から取り込みを止め、面は最後のコマを映したまま消える
- 電源の落ちた面（open ≤ 0.001・`WallAfterglow` と同じ境）は残光の点だけを描き、筐体と箱は clip する。黒い筐体のまま深度を書くと、後ろの面の暈が四角く欠けた（`17_closing_front` の 0 番）

**録画の終わりの暗転で一斉に消える（OPEN の指摘）**: 走行ログでは目は正規の閉じ（Release か区間の半ば）を通っていて、Abort ではない。閉じの場は 0.38 × 1.6 ≈ 0.6 秒で 0 になり、録画 → プレートの切替（`ev=step src=plate` / `recplay v=close`）と重なる。面はメインを映すので、暗くなったメインに合わせて一斉に黒くなっていた。取り込みを止めたので暗転は面に映らない。閉じの尺を暗転に合わせて縮めるには `AnomalyEyesLogic` の時計を変える必要があり、`ScreenMultiply` からは暗転の閉じと区間の半ばの閉じを区別できないので、そこはしていない。

**床の映り込み**: `ScreenMultiply.FloorReflection`（既定 true）。面と暈を床で鏡写しにした 2 枚目の描画（`ScreenMultiplySurfacesFloor` / `ScreenMultiplyGlowFloor`）。明るさ 0.18・mip +2・揺れ 0.4°（5 秒周期）・水平 6m で消える。床の高さは `AnomalyEyes.FloorWorldY`（`ShowCgLayer` と同じ `CourseToWorldProvider`、無ければ 0）。メインスクリーン自体は映らない（ScreenComposite の管轄）。

**uniform**: 席ごとの点いた時刻・閉じた時刻は float4 に 4 席ずつ詰める（`_WallOpenAt[40]` / `_WallClosedAt[40]`・`WallLane`）。160 席の float 配列 2 本では定数レジスタが足りない。共通の式は `Resources/ScreenMultiplyCommon.hlsl`。

**描画**: 面と箱で 1 draw、暈で 1 draw（床の映り込みでそれぞれもう 1 draw）。`Queue=Geometry+50` なのでメインに隠れた所は深度で捨てる。暈は `Geometry+60` の加算で alpha を変えない。シェーダは `Assets/Resources` に置いて `Resources.Load`（`ProjectSettings` を触らないため）。

**音**: 最初の 1 枚が `ScreenOn`、以降が `SoundCue.ScreenAdd`。間引きは目と同じ 0.09 秒で、`sndEye=` に出る。耳では確かめていない。

**計器**: `scrMul=<組めたか>/<点いた枚数>/<取り込みの累計>`、`scrEmg=<寄っている途中の枚数>/<席より奥に居た最大 m>`。意味は変えていない。残光の枚数は観測に出していない。

**霧と奥行き（0318）**: 式は `ScreenFogLogic`（濃さ .38/m・2.2m まで無し）と `Resources/ScreenFog.hlsl`。面は距離で暗くなり mip でぼける。点いた面は席より `EmergeM` 奥に現れ `ArriveSec` で寄る（時計は席ごとの点いた時刻）。暈の揺らぎは表示の 1 階調ぶん。にじみを強めると闇が灰色に寄る。

**見るのは `.\tools\unity.ps1 menu screen-wall`**。fog と flat を同じ時刻で 19 枚ずつ焼く（1〜9 は従来の向き、10・11 は波の先頭、12 は帯の回転（閉じない）、13・14 は床の映り込み、15〜19 は閉じと残光）。メインスクリーンは本番と同じ面で組み、面は本番の取り込みを通す。目の高さ 1.55m から床の高さを決める。
- ⚠ 最初の 2 版は 16:9 の板に写真を貼った代用品をメインにして 2 回指摘された（0319 / 0320）。新しいプレビューも本番の面を組む
- `report.tsv` の手前と奥の明るさは、flat に奥の段が無いので前後の比較に使えない
- 前後の比較は `Logs/screen-wall-compare/`（2026-11-13 の作り直し。前は `Logs/screen-wall-before/`）

**確かめていないこと**: 実機と HMD で見ていない。実機の fps（10-23 の実機は目が出ている 3-C で既に 40〜55fps）。床の映り込みで draw が 2 本増えた分も実機では測っていない。

関連: [[eye_jack]] [[dark_eyes_slices]] [[twin_verify]]
