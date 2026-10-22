---
name: screen-post
description: スクリーンの表示モデルと post（色・粒・夜間モード・劣化・OSD・心音）の現行の契約。経緯と旧い記述は reference/archive/ の凍結版
paths:
  - "Assets/Scripts/Streaming/ScreenAnchor.cs"
  - "Assets/Art/Shaders/Streaming/ScreenComposite.shader"
  - "Assets/Scripts/Streaming/MjpegScreen.cs"
  - "Assets/Scripts/Streaming/CameraFeel*.cs"
  - "Assets/Scripts/Streaming/ScreenDecayLogic.cs"
  - "Assets/Scripts/Streaming/ScreenOsd.cs"
  - "Assets/Scripts/Streaming/OsdClockLogic.cs"
  - "Assets/Scripts/Streaming/SignalLostFx.cs"
  - "Assets/Scripts/Streaming/PerceptualFade.cs"
  - "Assets/Scripts/Streaming/Heartbeat*.cs"
  - "Assets/Scripts/Streaming/SourceLumaMap.cs"
  - "Assets/Scripts/Streaming/CrtScreenMesh.cs"
  - "Assets/Scripts/Streaming/HeadYawFollow.cs"
  - "Assets/Scripts/Streaming/YawFollowLogic.cs"
  - "Assets/Scripts/Fx/**"
  - "Assets/Tests/Streaming/ScreenDecayLogicTests.cs"
  - "Assets/Tests/Streaming/CameraFeelLogicTests.cs"
  - "Assets/Tests/Streaming/OsdClockLogicTests.cs"
  - "Assets/Tests/Streaming/ScreenFrameAspectTests.cs"
  - "Assets/Tests/Streaming/CrtScreenMeshTests.cs"
  - "Assets/Tests/Streaming/PerceptualFadeTests.cs"
  - "Assets/Tests/Streaming/HeartbeatPulseTests.cs"
  - "Assets/Tests/Streaming/HeartbeatScreenTests.cs"
  - "Assets/Tests/Streaming/SourceLumaMapTests.cs"
  - "Assets/Tests/Streaming/YawFollowLogicTests.cs"
---

# 1. スクリーンと post

**現行の契約だけを書く。** 変わったら行を書き換える（日付の節を足さない・`rules/doc-sync.md`）。
体験者に見えることは `canon/NOW.md`、経緯は `reference/archive/streaming-2026-10-22.md`（S:）・`reference/archive/show-design-2026-10-22.md`（D:）。出典の行番号はその凍結版のもの。

## 契約
- スクリーン Quad の Transform は不変。フィット・合成・post はすべて `ScreenComposite` の UV 空間で行う — `MjpegScreen.ContainScale` → `_LiveScale` 〔出典: S:336-344〕
- ソースは contain-fit、外は黒帯。枠は 16:9 固定 — `ScreenFrameAspectTests` 〔出典: S:341, S:360-362〕
- マスクだけは contain-fit を通らず、枠空間（16:9）の生 uv で読む。`_MaskScale` は無い — `ScreenComposite.SampleBase` / `ScreenFrameAspectTests` 〔出典: S:351-364〕
- frag の順序: 電源断の uv → 終幕の裂け → 心音の波紋 → 低解像度化 → 乱れ uv → 素材（mip）→ CG → ほどけ → レンズ（周辺光量・心音ビネット）→ センサ（粒・色ノイズ・固定パターン）→ ISP（光だまり・露出・色温度・色かぶり・コントラスト・黒浮き・彩度・赤残し）→ 明暗曲線 → 走査線 → aura → 著作の粒 → 乱れの砂 → 管の点灯 → 信号断の砂 → 終幕の砂 → OSD → 電源断の線 → 管の縁・角 → `_SwitchDim` → `_ScreenPower` — `ScreenComposite.frag` 〔出典: S:1192-1211, S:1330-1334〕
- 戻す alpha は必ず 1 — `ScreenComposite.frag` の `return half4(saturate(col), 1)` 〔出典: S:1858-1864〕
- post は linear 空間で計算される（`contrast` の中心は linear 0.5 ＝ sRGB 0.73） — `ScreenComposite.frag` 〔出典: S:1160-1186〕
- post 12 項目は Unity と卓の FS_POST で同じ式。項を足すときは shader / `shaders.js` / `common.js` / `pipeline.js` の 4 か所を同時に直す — `FS_POST`（tools/web-compositor） 〔出典: S:1507-1515〕
- 卓に無い「装置の挙動」（`_NoiseDark` `_NoiseFixed` `_ExposureBias` `_SrcFrame` `_ChromaKill` `_Glare` `_Mono` `_CoarseBlocks` `_Echo` `_HeartRipple` `_HeartTime` `_HeartVignette` `_Crt*`）は post の外の別 uniform。書くのは `CameraFeelFx` だけ — `CameraFeelFx` 〔出典: S:1095-1098, S:1229-1235〕
- `feel` はキーが無くても既定で効く。既定は C# / 卓 / サーバの 3 者で揃える — `CameraFeelFx.Configure` / `FEEL_DEFAULT` / `capture-server.py _default_show` 〔出典: S:1111-1112〕
- 粒はソース画素で刻み、時計は受信フレーム番号 — `_SrcFrame` ← `MjpegScreen.SourceFrameId` 〔出典: S:1213-1222〕
- 色ノイズは 10 フレームに 1 度引き直して補間し、強さは輝度の粒の 0.55 倍 — `ScreenComposite.frag`（`cn *= rsqrt(...)` / `amp * 0.55`） 〔出典: S:1116-1130〕
- 固定パターンは乗算 — `_NoiseFixed` 〔出典: S:1222〕
- ホールドは必ず秒で明け、中止・終了で `ResetAll`。ホールド中は素材の時計も止める — `CameraFeelLogic` / `CameraFeelFx.ResetAll` / `ScreenOverlayController.SetFrozen` 〔出典: S:1113-1114, S:1135-1136〕
- 通常の画面加工 `Shown` は確定区間で決まる。1-A=0、1-B=1/6、1-C=1/3、2-A 以後=0.5。経過秒と歩行速度は使わない — `ScreenDecayLogic.NormalTargetFor` / `NormalMax` / `ScreenDecayLogicTests.通常加工は滞在秒数に依存せず位置確定時に切り替わる` 〔出典: memory/screen_decay.md、LEDGER 0243〕
- 2-C の人形視点は表示中だけ 1。3-A の人形化は乱れの実進捗で 1 へ上がり、4-A の報告まで保持する — `ScreenDecayLogic`（`ScreenDecayShownMode.DollPov/DollMorph/DollHold`） 〔出典: memory/screen_decay.md、LEDGER 0242〕
- 4-A の締めが報告で進むと `Shown` は 0.5 秒で 0 へ戻る。終了相へ移っても進む — `ScreenDecayLogic.Release` / `ReleaseSec`=0.5 〔出典: memory/screen_decay.md、LEDGER 0083 / 0242〕
- `Progress` は単調で戻らない（音が読む）。画は `Shown` だけを読む — `ScreenDecayLogic.Progress` / `Shown` 〔出典: S:1419-1430〕
- 解像度と夜間モードは同じ 1 本で書く — `CameraFeelFx.SetScreenDecay(blocks, progress)` 〔出典: S:1444-1461〕
- 伝送の痩せは mip で作る。ブロック数は C# が出す（800 → 267）。色差は +1 段 — `ScreenDecayLogic.BlocksFor` / `CoarseLod` / `ChromaLodBias` 〔出典: S:1473-1499、memory/screen_decay.md〕
- 著作の `_Pixelate` は硬い格子のまま、`_CoarseBlocks` と粗い方だけ掛ける — `PixelateUv` 〔出典: S:1504-1505〕
- 夜間モードは `_Mono` 1 本から +0.95EV・粒 ×2.6・周辺 +0.16・コントラスト +0.10・輝度重み (0.52,0.34,0.14) を派生させる — `ScreenComposite.frag` 〔出典: S:1442-1464〕
- 明暗曲線は表示 sRGB で `t=saturate((y-0.10)/0.60)`、`target=0.90·smoothstep(t)+0.10·y` を `_Mono` で補間 — `ScreenComposite.frag` 〔出典: memory/screen_decay.md〕
- 管の形: 曲面はメッシュ（膨らみ 0.055）、角丸と縁の暗さはシェーダ（0.07 / 0.34 / 0.17） — `CrtScreenMesh.bulge` / `CameraFeelFx.CrtRound` `CrtEdge` `CrtEdgeWidth` 〔出典: S:1241-1249〕
- 信号断の砂は平均 1.0 の乗数。`_SignalFloor`=1 は下に画が 1 枚も無いときだけ地を持ち上げる — `SignalLostFx` / `SAND_LO` `SAND_ADD` `SAND_FLOOR_LIVE` `SAND_FLOOR_DEAD` 〔出典: S:276-295、LEDGER 0131〕
- 2-C の追いつき後、乗っ取りの面が消えるまで主画面は物語の砂嵐で実写を隠す — `CameraFeelFx.BeginTakeoverStatic` → `SignalLostFx.SetNarrativeStatic` / `_NarrativeStatic` 〔出典: D:1641〕
- 心音の波紋とビネットは乗っ取り警告の自然完了で始まり、3-A の凍結でそのラン中は戻らない。波紋は `bed_heart` の再生位置で引く — `HeartbeatScreenLogic.ShouldApply` / `HeartbeatPulseLogic.Evaluate` / `CameraFeelFx.HeartVignetteBase` 〔出典: D:1641、HeartbeatScreenTests〕〔未照合: LEDGER の該当番号〕
- 時計（OSD）は映像の左上に装置が打つ。時刻は端末の実時刻で、録画にも焼かない — `ScreenOsd`（`_OsdTex` `_OsdRect` `_OsdOpacity` の唯一の writer） 〔出典: S:1270-1356、LEDGER 0108〕
- 周回は区間の周で出す。走り切った先は「終了 / END / FIN」。異世界は時刻ごと `?` — `OsdClockLogic.LapLabel` / `MaskedTime` / `TakeRunner.OtherworldActive` 〔出典: S:1289-1347、LEDGER 0167 / 0247〕
- 暗転・明けは見た目で曲げる。シェーダの掛け算は線形のまま — `PerceptualFade.Darken` / `Reveal` → `CameraSwitchDirector`（`_SwitchDim`） 〔出典: S:324-329〕
- スクリーンの追従は頭の正面で止まる（deadzone 0.5°・trail 0・非シリアライズ const） — `ScreenAnchor.YawDeadzoneDeg` / `YawFollowLogic.Step` 〔出典: S:236-248〕
- 読ませる面は頭のヨーだけを追い、出る縁で頭の正面へ置き直す — `HeadYawFollow`（0.30s・110°/s） 〔出典: D:1073-1100〕
- `Assets/Scripts/Fx/**` は別 Renderer（`FxRenderer.asset`）用の試作。本編の画には効かない — `FxCrtPostFxController` 〔出典: コードのコメント〕

## 罠（破ると壊れる）
- ソースに `mipChain` が無いと LOD が黙って無視され、そこだけ鮮明になる。`CameraStream` / `MjpegScreen` / `RecordedFramePlayer` / `ScreenOverlayController` / プレビューのプレートは全部 mipChain + Trilinear 〔出典: S:1500-1503〕
- `_Mono` を色・粒・露出で別 uniform に分けない。順方向で作れない画が出る 〔出典: S:1459-1461〕
- 判断は Unity が出した PNG で行う。Python で再現するなら linear で計算して最後に sRGB へ戻す 〔出典: S:1183-1186〕
- 暗さを変えたら粒も測り直す（粒は linear 加算） 〔出典: S:1179-1182〕
- 管の縁と角は `rawUv` で引く。潰れた uv を渡すと線が消える — `CrtSdf(rawUv)` 〔出典: D:858-870〕
- 曲面メッシュの 4 隅・4 辺は動かさない — `CrtScreenMeshTests` 〔出典: S:1254-1256〕
- メッシュは Awake でだけ組む。シーンへ焼かない 〔出典: S:1258-1261〕
- OSD の v を反転しない／矩形の内外を分岐にしない／版は 1 セルずつ描く 〔出典: S:1366-1386〕
- OSD のセル並びは `make-osd-font.py` と対 — `OsdClockLogicTests.Glyphs_MatchTheBakingScript` 〔出典: S:1388-1391〕
- OSD はシーンに焼かないと出ない（`menu scene`）。観測 `osd=-` 〔出典: S:1402-1403〕
- 合成プレビューは `CameraFeelFx.WriteUniforms` と `CameraFeelLogic.SteadyBiasFor` を通す。独自の既定を持つと実機より綺麗な絵になる 〔出典: S:3097-3109〕
- 観測項目は `ShowTelemetryHost` と `analyze-xp-log.py` を対で直す（`coarse` / `coarseShown` / `coarsePos` / `cbx` / `coarseMat` / `osd` / `osdLap`） 〔出典: S:1427-1430、memory/screen_decay.md〕

## 深く読むとき
- post の値・明るさを決める → S:1160-1239 / memory/camera_feel_flicker.md
- 画面の劣化と人形状態 → memory/screen_decay.md / S:1405-1505
- 時計の版・書式 → S:1270-1403
- 管の形・点灯 → S:1241-1268
- 暖色の置き場 → memory/warm_palette.md
- 信号断・追従 → S:231-330

---
