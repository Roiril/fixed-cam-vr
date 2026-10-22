---
name: screen-cg
description: CG 人形の合成（較正・影・部屋プロキシ・光だまり）の現行の契約。経緯と旧い記述は reference/archive/ の凍結版
paths:
  - "Assets/Scripts/Streaming/Cg/**"
  - "Assets/Art/Shaders/Cg/**"
  - "Assets/Scripts/Streaming/Editor/ShowCompositePreview.cs"
  - "Assets/Tests/Streaming/CgProjectionTests.cs"
  - "Assets/Tests/Streaming/ShowCompositePreviewPlanTests.cs"
  - "Assets/Tests/Streaming/ShowActorPrefabSizeTests.cs"
---

# 3. CG 人形の合成

**現行の契約だけを書く。** 変わったら行を書き換える（日付の節を足さない・`rules/doc-sync.md`）。
体験者に見えることは `canon/NOW.md`、経緯は `reference/archive/streaming-2026-10-22.md`（S:）・`reference/archive/show-design-2026-10-22.md`（D:）。出典の行番号はその凍結版のもの。

## 契約
- 人形は実カメラ姿勢の双子カメラが ShowCg レイヤだけを RT に描き、post の前に合成する — `ShowCgLayer` / `_CgTex` 〔出典: S:2663-2664, S:1256-1264〕
- CG はライブと同じ contain-fit 枠に収める — `_CgScale` ← `MjpegScreen.ContainScale` 〔出典: S:2777〕
- 合成は premultiplied over `bg*(1-a*s)+rgb*s`。影は rgb=0 / a=濃さの断片 — `ScreenComposite.frag` 〔出典: S:2780, S:3031-3033〕
- RT は映像の実寸・depth 24・MSAA 4x — `ShowCgLayer`（`new RenderTexture(w,h,24)` / `antiAliasing=4`） 〔出典: S:2778, S:3046-3047, S:3085〕
- 画角は水平 `hfovDeg` が正 — `ShowCgLayer.HorizontalToVerticalFovDeg` 〔出典: S:2779〕
- 内部行列は `Camera.projectionMatrix` へ直接。卓と実機の投影は両側にハードコードして突き合わせる — `ShowCgLayer.ApplyCameraCalib` / `CgProjectionTests.Projection_MatchesPinholeFormula` 〔出典: S:2874-2879〕
- 較正は解像度・レンズに従属し、食い違えば概算姿勢へ落として警告する — `ShowCameraCalibDef.MatchesSource` / `ShowCgLayer.ResolveCalib` 〔出典: S:2788, S:2924-2925〕
- 歪みは除算モデル `r_u = r_d/(1+k1 r_d²)` — `_CgLens` 〔出典: S:2920-2922〕
- 未登録なら人形を出さない — `ShowControlClient.CourseRegisteredProvider` 〔出典: S:2781〕
- 光の向きは course 相対。シーンのライトを参照しない — `ShowCgLayer.CourseLightDirToWorld` 〔出典: S:2782, S:3040-3044〕
- 影は平面投影（`ZTest LEqual`・ステンシル 1 回）、接地影も `LEqual`、オクルーダは深度だけ。Queue は -200 / -100 / -99 / Geometry — `ShowShadowProjector` / `ShowGroundBlob` / `ShowOccluder` / `ShowRoomProxy` 〔出典: S:3029-3050, S:3150-3153〕
- `layout.room` が無くても人形と影は出す（床は y=0） 〔出典: S:3048-3049〕
- 接地影の半径は背丈に比例（下限 0.03m） — `ShowCgLayer.BlobRadiusPerHeight` `BlobRadiusMinM` 〔出典: S:3076-3078〕
- 人形は実写の刻みで描く（手動 `Render()`、止まっている間は 15fps） — `ShowCgLayer.RenderIfDue` / `CgFallbackIntervalSec` 〔出典: S:3069-3075〕
- 身体入力は 0.15 秒前を補間して読む。手の有効フラグは補間しない — `BodyInputHistory` / `ShowCgLayer.VideoLatencySec` 〔出典: S:3054-3059〕
- CG だけを鈍らせる（tent 0.7 テクセル）。色差だけ 2.2 テクセル均して 0.55 倍 — `ShowCgLayer.CgSoftenTexels` `CgChromaBlurTexels` `CgChromaGain` 〔出典: S:3060-3062, S:3139-3148〕
- 明るさは立つ場所の局所輝度（4×4）を自動露出の後で合わせる。基準 0.20・下限 0.15 — `SourceLumaMap.Tiles` / `ShowCgLayer.LumaGainFor` `ReferenceLuma` `LumaGainMin` 〔出典: S:3124-3137〕
- 影側も `albedo·_LightColor·(_Ambient+_ShadeColor)`。リムも `_LightColor` に比例 — `ShowActor.shader` / `ShowCgLayer.ApplyLight` 〔出典: S:3111-3122〕〔未照合: シェーダ式〕
- 色温度の式は卓と C# で同一 — `ShowCgLayer.KelvinToLinearColor` / `room-model.js kelvinToRgb` 〔出典: S:3211-3212〕
- 人形に付き従う劣化（`aura`）の中心と半径は人形の投影から毎フレーム — `ShowCgLayer.WriteActorFocus` / `_ActorFocus` 〔出典: S:1108-1110〕
- 夜間モード中は人形を描いている間だけ頭のあたりに光だまり（1.5EV×`_Mono`、人形自身は半分）。代役には書かない — `ShowCgLayer.WriteActorLight` / `ActorLightEv` / `ACTOR_LIGHT_*` 〔出典: S:1466-1469、memory/screen_decay.md、LEDGER 0287〕
- 同じ条件で人形の上だけ赤を 0.6 残す。画面全体には残さない — `ShowCgLayer.DollRedKeep` / `_RedKeep.y` 〔出典: S:1470、LEDGER 0289〕
- 腕は肩→手を腕長比で写し、上下にだけ振る（90°まで）。左だけコントローラで代える — `ActorArmLogic.LimitToVerticalSwing` / `ShowActorRig.MaxSwingDeg` / `OvrHandTrackingBridge` 〔出典: S:2682-2709、LEDGER 0051〕
- 人形の向きは頭ヨーを半減期 0.35s で追い、首のねじれ 50° で引く — `ActorArmLogic.SmoothYawDeg` 〔出典: S:2689-2692〕〔未照合: 定数値〕
- 立ち位置も腕・向きと同じ時刻を読む — `CourseTrack` 〔出典: S:2693-2694〕
- 人形の寸法はテストが固定する — `ShowActorPrefabSizeTests` 〔出典: S:2732-2735〕

## 罠（破ると壊れる）
- RT の depth を 16 にすると影が二重暗化の斑になる 〔出典: S:3045-3047〕
- `ShowCgLayer.ApplyLight` で ambient にも倍率を掛けない（二重） 〔出典: S:3119-3120〕
- 人形のマテリアルは 1 枚へまとめる（影は `AttachShadowMaterial` が末尾に 1 枚足す） 〔出典: S:2748-2750〕
- `ShowActorRig` の可動域は const。SerializeField にすると既存プレハブで 0 になる 〔出典: S:2729-2731〕
- 卓は CG の陰影・素材を描かない。合成の見た目の正は Unity の `menu composite` 〔出典: S:2795-2797, S:2825-2828〕
- 浮き具合の 1 行（luma / sat / grain）と絵を並べて見る。`FEET OUT OF FRAME` は立ち位置の問題 〔出典: S:3155-3163〕
- rec / plate カットでも人形の刻み・明るさ・較正はライブ基準（未着手の既知の穴） 〔出典: S:3176-3179〕〔未照合〕

## 深く読むとき
- 合成の像空間・較正 → S:2768-3027 / memory/cg_compositing.md
- 影・なじませ → S:3029-3182
- 人形の腕・寸法・レイヤ → S:2655-2766 / memory/cg_actor_and_layer_traps.md
- 夜間モードでの人形の見え → memory/screen_decay.md「人形について回る光だまり」
