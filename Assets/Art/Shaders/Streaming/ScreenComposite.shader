// 固定枠スクリーン用の合成シェーダ。web-compositor (tools/web-compositor) のモデルを移植:
//   スクリーン枠 (Quad) は不変、ソースは contain-fit で letterbox、
//   ライブ映像 × 事前撮影オーバーレイをマスクで合成し、ポスト FX はスクリーン内容にのみかける。
// Transform を映像に合わせて変形させる旧 MjpegScreen.ApplyOrient 方式を置き換える。
Shader "FixedCamVr/ScreenComposite"
{
    Properties
    {
        _LiveTex("Live (MJPEG)", 2D) = "black" {}
        _OverlayTex("Overlay (Pre-recorded)", 2D) = "black" {}
        _MaskTex("Overlay Mask (R, screen space)", 2D) = "black" {}
        _CgTex("CG Layer (RGBA, screen space)", 2D) = "black" {}
        _LiveScale("Live Contain Scale (xy)", Vector) = (1, 1, 0, 0)
        _OverlayScale("Overlay Contain Scale (xy)", Vector) = (1, 1, 0, 0)
        _CgScale("CG Contain Scale (xy)", Vector) = (1, 1, 0, 0)
        // 実レンズの歪みを CG にも掛けるための係数（ShowCgLayer が較正から供給）。
        //   _CgLens   = (k1, 主点 cx/W, 主点 cy/H, 未使用)
        //   _CgFocalN = (fx/W, fy/H, 0, 0)
        // k1=0 のときは何もしない（較正が無い＝概算姿勢のとき）。
        _CgLens("CG Lens (k1, cxN, cyN, unused)", Vector) = (0, 0.5, 0.5, 0)
        _CgFocalN("CG Focal normalized (fx/W, fy/H)", Vector) = (1, 1, 0, 0)
        // CG を実映像の粗さへ寄せる弱いぼかし（テクセル単位のオフセット。0 = 無効）。
        _CgSoften("CG Soften (texels)", Range(0, 2)) = 0.7
        // 実写だけが受けている「色の粗さ」を CG にも掛ける（JPEG 4:2:0 + 暗所のカラーノイズ抑制）。
        // ShowCgLayer が const から書く（_CgSoften と同じ流儀。現場調整の対象ではない）。
        _CgChromaBlur("CG Chroma Blur (texels)", Range(0, 8)) = 2.2
        _CgChromaGain("CG Chroma Gain", Range(0, 2)) = 0.55
        _UvRotSteps("Live UV Rotation (90deg steps, 0-3)", Float) = 0
        _OverlayStrength("Overlay Strength", Range(0, 1)) = 0
        // 位置で選ぶ録画の 2 コマ目と混ぜ具合（canon/LEDGER.md 0278）。0 なら 2 コマ目は読まない。
        _OverlayTexB("Overlay B (next recorded frame)", 2D) = "black" {}
        _OverlayBlend("Overlay B Blend", Range(0, 1)) = 0
        // 滲んで現れる・長押しで布が膨らむ（0349）。書くのは ScreenOverlayController だけ。
        // 既定は全部 0（順の画も press も読まない＝従来と同じ画）。
        _RevealTex("Overlay Reveal Order (R, screen space, sRGB-encoded)", 2D) = "white" {}
        _OverlayRevealOn("Overlay Reveal On", Float) = 0
        _OverlayReveal("Overlay Reveal", Range(0, 1)) = 1
        _OverlayRevealSoft("Overlay Reveal Soft", Range(0.001, 0.5)) = 0.12
        _OverlayMul("Overlay Multiply (ratio image)", Float) = 0
        _PressTex("Press Bulge (Pre-generated)", 2D) = "black" {}
        _PressMaskTex("Press Mask (R, screen space, sRGB-encoded)", 2D) = "black" {}
        _PressScale("Press Contain Scale (xy)", Vector) = (1, 1, 0, 0)
        _PressCenter("Press Center (screen uv)", Vector) = (0.5, 0.5, 0, 0)
        _OverlayPress("Overlay Press", Range(-0.15, 1.1)) = 0
        _CgStrength("CG Layer Strength", Range(0, 1)) = 0
        // 差し替え素材を実写へ寄せる色統計マッチング（Reinhard per-channel を gain/offset へ落としたもの）。
        // 卓が cue 保存時に算出して焼く。既定は恒等（gain=1 / offset=0）。
        _OverlayGain("Overlay Color Match Gain (rgb)", Vector) = (1, 1, 1, 0)
        _OverlayOffset("Overlay Color Match Offset (rgb)", Vector) = (0, 0, 0, 0)
        // スクリーン枠の縦横比 (w/h)。低解像度化のブロックを正方に保つために要る（MjpegScreen が供給）。
        _FrameAspect("Screen Frame Aspect (w over h)", Float) = 1.7778
        [Header(Post FX inside screen)]
        _Exposure("Exposure (EV)", Range(-2, 2)) = 0
        _Contrast("Contrast", Range(0.5, 2)) = 1
        _Saturation("Saturation", Range(0, 2)) = 1
        _Temperature("Temperature", Range(-1, 1)) = 0
        _Vignette("Vignette", Range(0, 1)) = 0
        _HeartRipple("Heartbeat Ripple", Range(0, 1)) = 0
        _HeartTime("Seconds Since First Heartbeat", Float) = 0
        _HeartVignette("Heartbeat Vignette", Range(0, 1)) = 0
        _RoomWarp("Room Warp", Range(0, 1)) = 0
        _RoomWarpZoom("Room Warp Zoom", Float) = 1
        _RoomWarpTex("Room Warp Offsets", 2D) = "black" {}
        _Grain("Grain", Range(0, 0.3)) = 0
        _Scanline("Scanline", Range(0, 1)) = 0
        _ScanlineCount("Scanline Count", Float) = 240
        _Lift("Lift (raised black)", Range(0, 0.3)) = 0
        _Tint("Tint (green-magenta)", Range(-1, 1)) = 0
        // 以下 2 つは post 9 項目と同じ「画作り」の仲間だが、色ではなく**サンプル位置**を動かすので
        // 合成の前段（テクスチャを引くとき）に効く。卓の FS_POST も同じ位置・同じ式で実装する。
        _Aberration("Chromatic Aberration", Range(0, 1)) = 0
        _Pixelate("Pixelate (low-res)", Range(0, 1)) = 0
        [Header(Camera feel and echo (out of FS_POST parity))]
        // 「装置らしさ」の系統。post 12 項目とは**別の writer**（CameraFeelFx）が持ち、時間で動く。
        // post を時間の関数にすると shader / FS_POST / common.js / pipeline.js の 4 箇所 × 時間の同期に
        // なり、沈黙した食い違いが必ず出る（機械テストが無い）。だから別系統にしてある。
        //
        // 現行の加工はすべて全域一様で、すべてに物理的な言い訳（機材のせい）が付く。だから安全で、
        // だから怖くない。ここに足すのは「言い訳が破れる」ための道具立て。
        _NoiseDark("Read Noise (sensor floor)", Range(0, 0.5)) = 0
        _NoiseFixed("Fixed Pattern Noise", Range(0, 0.3)) = 0
        // 粒が動く時刻。**VR の 90Hz ではなく映像が更新されたときだけ変わる**（CameraFeelFx が
        // 受信フレーム番号を書く）。_Time で動かすと、カメラが 15fps のときでも粒だけ 90Hz でざわつき、
        // 「映像の上に別の層が乗っている」と読まれる（＝加工者の指紋）。
        _SrcFrame("Source Frame Id (noise clock)", Float) = 0
        // レンズの内面反射（ベイリンググレア）。明るい所の光が画全体へ薄く回り、
        // 暗い所のコントラストを奪う。**安いレンズほど強い**ので、監視カメラの画では
        // 光源のまわりが必ず滲む。mip があるので広いぼかしが 1 タップで得られる。
        _Glare("Veiling Glare (lens flare bloom)", Range(0, 1)) = 0
        // 周を重ねるごとに色が抜けて夜間モードへ落ちる。**解像度の劣化と同じ進み**（0 → 1）で、
        // 3 周目の A で 1 ＝ 完全な無彩（`canon/LEDGER.md` 0019）。
        // 色を抜くだけでなく、赤外の夜間モードらしさ（照らされた範囲だけが残る・増感で粒が増える・
        // 中間調が減って白と黒になる）を同じ 1 本から派生させる。別 uniform に分けると、
        // 片方だけ動いた絵（色は残っているのに粒だけ多い等）が作れてしまう。
        _Mono("Night Mode (0=color, 1=IR monochrome)", Range(0, 1)) = 0
        [Header(CRT tube shape)]
        _CrtReference("Reference casing enabled", Float) = 0
        _CrtFrameTex("Reference casing RGBA", 2D) = "black" {}
        _CrtCaseColorTex("Original reference casing color", 2D) = "black" {}
        _CrtGlassTex("Reference neutral glass transmission", 2D) = "gray" {}
        _CrtNormalTex("Reference glass normal RGB", 2D) = "bump" {}
        _CrtGradeTex("Image-fitted display luminance", 2D) = "gray" {}
        _CrtGradeBypass("Image grade calibration bypass", Float) = 0
        [HideInInspector] _CrtOrderStage("Editor processing order stage", Float) = 0
        [HideInInspector] _CrtOrderGradeFirst("Editor processing order grade first", Float) = 0
        [HideInInspector] _CrtPsfStage("Editor PSF calibration stage", Float) = 0
        [HideInInspector] _CrtPsfSigma("Editor calibrated Gaussian source sigma", Vector) = (0, 0, 0, 0)
        [HideInInspector] _CrtPsfLod("Editor PSF calibration mip level", Vector) = (0, 0, 0, 0)
        [HideInInspector] _CrtPsfMode("Editor PSF mode", Float) = 0
        [HideInInspector] _CrtPsfRespectDecayLod("Editor Gaussian normal decay mip", Float) = 0
        [HideInInspector] _CrtPsfLinearTex("Editor linear PSF source", 2D) = "black" {}
        [HideInInspector] _CrtPsfLinearLod("Editor calibrated linear mip level", Vector) = (0, 0, 0, 0)
        [HideInInspector] _CrtLensWarp("Editor radial lens inverse warp", Float) = 0
        [HideInInspector] _CrtFisheye("Camera fisheye: source focal, screen focal", Vector) = (0, 0, 0, 0)
        [HideInInspector] _CrtStyleProbe("Editor lens calibration probe", Float) = 0
        [HideInInspector] _CrtSignalRuntime("Selected runtime signal profile", Float) = 0
        [HideInInspector] _CrtSignalProfileSelected("Keep selected optical profile during synchronous capture", Float) = 0
        [HideInInspector] _CrtDollProfileBlend("Selected doll profile blend", Float) = 0
        [HideInInspector] _CrtSignalGain("Runtime display RGB gain", Float) = 1
        [HideInInspector] _CrtCenterEv("Selected center exposure EV", Float) = 0
        [HideInInspector] _CrtCenterRadius("Selected center radius", Float) = .8
        [HideInInspector] _CrtCenterFalloff("Selected center falloff", Float) = 2
        [HideInInspector] _CrtTintStrength("Selected thin tint strength", Float) = 0
        [HideInInspector] _CrtTintCoolness("Selected neutral to cool", Float) = .55
        [HideInInspector] _CrtScreenOnly("Clip outside the selected screen contour", Float) = 0
        [HideInInspector] _CrtScreenMaskTex("Selected screen contour", 2D) = "white" {}
        [HideInInspector] _CrtSignalBandwidth("Runtime luma/chroma bandwidth", Float) = 0
        [HideInInspector] _CrtPreGlassEncoded("Pre-glass hands sRGB values to the band filter", Float) = 0
        [HideInInspector] _CrtSignalReady("Processed surface is valid", Float) = 0
        [HideInInspector] _CrtProcessedSignalTex("Canonical processed surface", 2D) = "black" {}
        [HideInInspector] _CrtRuntimePsfTex("Native source Gaussian", 2D) = "black" {}
        [HideInInspector] _CrtRuntimePsfRedTex("Red-sigma full RGB source Gaussian", 2D) = "black" {}
        [HideInInspector] _CrtRuntimePsfBlueTex("Blue-sigma full RGB source Gaussian", 2D) = "black" {}
        [HideInInspector] _CrtRuntimePsfUVShift("Phase-aligned Gaussian texture offset", Vector) = (0, 0, 0, 0)
        [HideInInspector] _CrtRuntimePsfReady("Native source PSF is valid", Float) = 0
        [HideInInspector] _CrtSignalStage("Editor pre-glass signal stage", Float) = 0
        [HideInInspector] _CrtSignalTex("Editor filtered pre-glass signal", 2D) = "black" {}
        _CrtOpticalSpread("Image measured RGB spread in reference pixels", Vector) = (1.04, 0.84, 0.82, 0)
        _CrtOpticalConvergence("Image measured red and blue offset in reference pixels", Vector) = (0.98, 1.79, 0, 0)
        _CrtFieldStrength("Low frequency reference illumination style", Range(0, 1)) = 0
        _CrtDisplayContrast("Reference display contrast", Range(0.5, 2)) = 1
        _CrtReflectanceTex("Reference reflectance", 2D) = "black" {}
        _CrtAperture("Reference aperture xywh", Vector) = (0, 0, 1, 1)
        _CrtLogicalCrop("Logical crop xy", Vector) = (1, 1, 0, 0)
        _CrtImageAdjustment("Reference image exposure contrast saturation temperature", Vector) = (0, 0, 0, 0)
        _CrtView("Local eye direction", Vector) = (0, 0, -1, 0)
        // ブラウン管の見た目。**形（曲面）はメッシュが持つ**（CrtScreenMesh）ので、ここは
        // 面の中で完結するものだけ — 角の丸みと、管の縁が落ちる暗さ。
        _CrtRound("CRT Corner Round", Range(0, 0.5)) = 0
        _CrtEdge("CRT Edge Darkness", Range(0, 1)) = 0
        _CrtEdgeWidth("CRT Edge Width", Range(0.01, 0.5)) = 0.16
        // 管が点く（導入）。**0 = 消えている / 1 = 点いている**。
        // 途中は「縁が光る → 中央から水平に開く → 面が満ちる → 行き過ぎて落ち着く」。
        // ⚠ 既定 **1**（点いている）。0 を既定にすると、この uniform を書かない場面
        //   （本編・終幕・卓のプレビュー・Editor の合成プレビュー）で画がまるごと消える。
        _CrtIgnite("CRT Ignition (0=off, 1=on)", Range(0, 1)) = 1
        // 導入のテレビ（0309）。どちらも 0 で本編と同じ。
        [HideInInspector] _CrtTvCase("TV case light outside the contour (0=clip)", Range(0, 1)) = 0
        [HideInInspector] _CrtTvDark("TV glass and inner case darkness", Range(0, 1)) = 0
        // 映像そのものの出方（導入の段 4）。**管が点くこととは別**。
        // ⚠ 分けていないと「管が点き切った瞬間に映像も出る」ことになり、重み `live` が
        //   0 のままなのに画には映像が出る ＝ 重みと画が食い違う（2026-08-13 に絵で見つけた）。
        // ⚠ 既定 **1**。0 を既定にすると、この uniform を書かない場面で画がまるごと消える。
        _IntroLive("Intro Live (0=dark tube, 1=image)", Range(0, 1)) = 1
        // 終幕。**装置に届いている電力**（canon/LEDGER.md 0048「電池が切れかけみたいな感じで
        //   だんだんとちかちかしながら消えていき」）。画の**いちばん最後**に掛ける ＝
        //   映像も砂嵐も管の縁も一緒に落ちる（電池が切れるのは画の一部ではなく装置そのもの）。
        // ⚠ 既定 **1**（点いている）。0 を既定にすると、この uniform を書かない場面
        //   （本編・導入・卓のプレビュー・Editor の合成プレビュー）で画がまるごと消える。
        _ScreenPower("Screen Power (1=on, 0=dead)", Range(0, 1)) = 1
        // 終幕: ブラウン管の電源断の進み（0 = ふつうの画 / 1 = 点が消え切った）。
        // 形（潰れる → 縮む → 消える の割合・線の太さ・明るさの上限）は **このシェーダが持つ**。
        // `OutroLogic.ScreenCollapse` が出すのは進みだけ（数字を 2 か所に書かない）。
        // ⚠ 既定 **0**（恒等）。`_ScreenPower` とは逆向きなので取り違えないこと。
        _ScreenCollapse("Screen Collapse (0=normal, 1=gone)", Range(0, 1)) = 0
        // 終幕: 通信が切れる砂嵐の進み（0 = なし / 1 = 最大。2026-10-21・博士タブレットの消え方）。
        // 強まり方（ざ・ざ・ざ）・帯の裂け・転がる暗い帯は **このシェーダが持つ**（`CutNoiseIntensity`）。
        // `OutroLogic.ScreenNoise` が出すのは進みだけ。⚠ 既定 **0**（恒等）。
        _ScreenNoise("Screen Cut Noise (0=off, 1=full)", Range(0, 1)) = 0
        // 暗部の色を殺す量。安い ISP はノイズリダクションで**暗い所の色差から捨てる**ので、
        // 一様な脱色ではなく「明るい所に色が残り、暗がりが無彩へ落ちる」形になる。
        _ChromaKill("Dark Chroma Kill (ISP noise reduction)", Range(0, 1)) = 0
        // 人形状態に対応する解像度（canon/LEDGER.md 0242）。**枠を横切るブロック数**をそのまま受ける。
        //   0 = 量子化しない（今までと 1 ビットも変わらない画）
        // 0..1 → ブロック数の対応表は **C# の ScreenDecayLogic にしかない**。ここにも式を置くと、
        // テレメトリが読む値と画が黙って食い違う（この codebase が何度も踏んだ型）。
        _CoarseBlocks("Coarse (blocks across frame, 0=off)", Float) = 0
        _ExposureBias("Exposure Bias (EV, AGC lag)", Range(-2, 2)) = 0
        // 人形に付き従う劣化。(中心 u, 中心 v, 半径, 強さ)。ShowCgLayer が人形の投影から供給する。
        // 対象に紐づく非一様な乱れ＝機材のせいにできない＝原因が世界の側にあることになる。
        _ActorFocus("Actor Focus (cx, cy, radius, amount)", Vector) = (0.5, 0.5, 0.2, 0)
        // 人形について回る光だまり（canon/LEDGER.md 0287）。(中心 u, 中心 v, 半径, 強さ EV)。
        // 夜間モードの強さ（_Mono）で掛かる。ShowCgLayer が人形の頭のあたりの投影から供給する。
        _ActorLight("Actor Light (cx, cy, radius, ev)", Vector) = (0.5, 0.5, 0.2, 0)
        // 夜間モードで赤だけ少し残す試し（canon/LEDGER.md 0288・R072）。x = 画面全体 / y = 人形の上だけ。
        // 0 なら従来の完全な無彩。
        _RedKeep("Red Keep (all, doll)", Vector) = (0, 0, 0, 0)
        // 焼き付き / ホールド。指定した瞬間の画を 1 枚だけ保持して混ぜる（フレーム履歴は持たない
        // ＝決定的で、同じ show.json は同じ絵になる）。1.0 で完全に止まって見える。
        // ソースはライブ映像と同じなので contain-fit も _LiveScale を共用する。
        _EchoTex("Echo (frozen frame)", 2D) = "black" {}
        _Echo("Echo Mix", Range(0, 1)) = 0
        [Header(OSD (the device clock))]
        // スクリーン左上の日付と時刻（`canon/LEDGER.md` 0108）。**唯一の writer は ScreenOsd**。
        // 虚構上は**カメラではなく観測装置（画面の側）が打っている時計**なので、
        // 映像に起きること（切替・差し込み・録画・乱れ・劣化・信号断）では 1 ビットも変わらず、
        // 装置に起きること（切替の黒・管の縁・終幕の電力）は一緒に受ける。
        //   _OsdTex     = いまの時刻を敷き直した 1 枚（straight alpha・生成りの字＋暗い縁）
        //   _OsdRect    = 枠 UV の矩形 (x, y, w, h)。**w が 0 なら 1 画素も触らない**
        //                 ＝ この uniform を書かない場面（卓・各プレビュー・旧シーン）は従来と同じ絵
        //   _OsdOpacity = 不透明度
        _OsdTex("OSD Text", 2D) = "black" {}
        _OsdRect("OSD Rect (x, y, w, h in frame uv)", Vector) = (0, 0, 0, 0)
        _OsdOpacity("OSD Opacity", Range(0, 1)) = 1
        [Header(Switch and Signal FX (out of FS_POST parity))]
        // ↓ これらは web-compositor の FS_POST 一致規約の対象外（別系統 uniform）。
        //   dip-to-black（切替演出）と信号ロスト／乗っ取り中の砂嵐を post FX の後段にかける。
        _SwitchDim("Switch Dip Dim", Range(0, 1)) = 0
        _SignalLost("Signal Lost (static)", Range(0, 1)) = 0
        // 2-C 人形視点後の演出用。配信断と違い、砂の下の実写を隠す。
        _NarrativeStatic("Takeover Static (hide source)", Range(0, 1)) = 0
        // 砂の下に**画が 1 枚も無いか**（1 = 無い）。SignalLostFx が唯一の writer。
        // 砂は掛け算で乗るので、下に画が無いと真っ黒に掛かって何も見えなくなる。
        // カメラが 1 台も繋がっていない現場はこの砂だけで体験が流れる（canon/LEDGER.md 0025）ので、
        // そのときだけ地を持ち上げる。⚠ **既定は 1**（書き忘れたら明るい側へ倒す）。
        _SignalFloor("Signal Lost: nothing under the sand", Range(0, 1)) = 1
        // 演出としての「映像の乱れ」。障害表示（_SignalLost）とは所有者も意味も別。
        //   _Glitch     = 強さ (0-1)。GlitchFx が唯一の writer。
        //   _GlitchSeed = 時間シード (秒)。_Time に依らないので Editor プレビューで再現できる。
        _Glitch("Glitch (staged tearing)", Range(0, 1)) = 0
        _GlitchSeed("Glitch Seed (seconds)", Float) = 0
        [Header(Split screen (mirror and dual overlay))]
        // 画面を縦に割って、左右で別のものを出す（canon/LEDGER.md 0050）。
        // 3 周目 A の「左＝左右反転したライブ / 右＝ライブ」と、
        // 4 周目 A の「左＝大量の人形 / 右＝体験者人形＋環境」の両方がこれに乗る。
        //   _SplitX          分割位置（0 = 分割なし）。境目は環境の縦線（カーテンの合わせ目）へ置く
        //   _SplitFlipLeft   左側だけ左右反転して読む。**環境が左右対称なカメラでしか成立しない**
        //   _SplitFreezeLeft 左側だけ _EchoTex へ寄せる（そこだけ画が止まる）
        _SplitX("Split X (0=off)", Range(0, 1)) = 0
        _SplitFlipLeft("Split: mirror left half", Range(0, 1)) = 0
        _SplitFreezeLeft("Split: freeze left half", Range(0, 1)) = 0
        // 第 2 の差し替え層。**左と右へ別の素材を同時に置くために要る**（1 枚では片方しか置けない）。
        _Overlay2Tex("Overlay 2", 2D) = "black" {}
        _Mask2Tex("Overlay 2 Mask (R, screen space)", 2D) = "black" {}
        _Overlay2Scale("Overlay 2 Contain Scale (xy)", Vector) = (1, 1, 0, 0)
        _Overlay2Strength("Overlay 2 Strength", Range(0, 1)) = 0

        [Header(Swap morph (visitor unravels into scanlines))]
        // 入れ替わりのほどけ（canon/LEDGER.md 0089 / 0090）。全画面ではなく
        // **映像の中の体験者だけ**が水平の細い線にほどけ、もつれ、人形へ吸い込まれて晴れる。
        // 線は描き足すのではなく**下の映像を行ごとに水平へ引き伸ばして**作る。
        //   _SwapRect    (cx, cy, halfHeight, active) — 人型の投影中心と見かけの半高（枠 UV 空間）
        //   _SwapCover   ほどけの前線 0..1
        //   _SwapKnot    もつれが塊になっている度合い 0..1（1 で下を完全に隠す）
        //   _SwapThread  体の外へ流れ出た糸の量 0..1
        //   _SwapReal    CG が実体として見えている度合い 0..1
        //   _SwapFromTop 1 = 頭からほどける / 0 = 足元から
        // 唯一の writer は SwapMorphFx（_Glitch を GlitchFx が独占するのと同じ流儀）。
        _SwapRect("Swap Rect (cx, cy, halfH, active)", Vector) = (0.5, 0.5, 0.2, 0)
        // ⚠⚠ **ほどけ始めた瞬間の矩形**。映像の中の人は縮まないので、マスクは**この枠で引く**
        //   （`_SwapRect` は縮む段で小さくなる。そちらで引くと人型が縮むたびにマスクが外れる）。
        _SwapRect0("Swap Rect At Begin (cx, cy, halfH)", Vector) = (0.5, 0.5, 0.2, 0)
        // 映像の中の人を拾う無人プレート（そのカメラで撮った、誰も居ない画）。
        _SwapMaskTex("Swap Mask Plate", 2D) = "black" {}
        // x = 使うか / y,z = 差の下限・上限 / w = 差を取る mip の段（粗くするほどノイズが消える）
        _SwapMask("Swap Mask (on, lo, hi, lod)", Vector) = (0, 0.060, 0.20, 0.6)
        _SwapCover("Swap Cover", Range(0, 1)) = 0
        _SwapKnot("Swap Knot", Range(0, 1)) = 0
        _SwapThread("Swap Thread", Range(0, 1)) = 0
        _SwapReal("Swap Real", Range(0, 1)) = 0
        _SwapFromTop("Swap Unravels From Top", Range(0, 1)) = 1
        // ⚠ **線の色を作るプロパティは置かない**（2026-08-19 に `_SwapInk` を削除）。
        //   参考画像の線の明暗はその画素が元々どこの色だったかで決まっていて、乱数ではない。
        //   濃淡を振るのは「元の映像を捨てて縞を描き足す」のと同じことだった。
        // 帯の刻み。x = **枠の縦**に対する行数（＝ 1 帯が何画素か。48 行 ＝ 実機で 15〜20 画素）。
        // ⚠⚠ **細くしない**（2026-08-20・ユーザーが参考画像を送ってきた）。320 行（3 画素）は
        //   走査線であって、参考画像の**太い筆で引いた横棒**にはならない。
        //   ⚠ 行数を変えたら `SwapWaveExt` の周波数も対で直す（帯あたりの周期が変わる）。
        // **y / z / w はもう読んでいない**（2026-08-21 に棒を「体の実範囲 ＋ 端の波」へ
        // 作り直し、中心を泳がせる方式を廃止した — 泳いだ分だけ体が覆いから外れていた）。
        // ⚠ 行数は**映像の粗さと独立**でよい（走査線は装置が走査した跡で、映像の内容ではない）。
        _SwapSmear("Swap Smear (rows, unused, unused, unused)", Vector) = (48, 0.0, 0.26, 0)
        // ⚠ **もう読んでいない**（2026-08-20 に線の矩形波を、2026-08-21 にぼかし読みも廃止した。
        //   マスクのぼかしは `_SwapMask.w` が持つ）。既存マテリアルの値を壊さないため宣言だけ残す。
        _SwapLine("Swap Line (unused)", Vector) = (0.22, 0.12, 0.26, 0.8)
        // 糸のうねりの時計（秒）。**_Time を使わない** — Editor のプレビューは 1 エディタフレームの中で
        // 何コマも描くので、_Time だと連番 PNG の糸が全コマ同じになる（_GlitchSeed と同じ理由）。
        _SwapSeed("Swap Seed (seconds)", Float) = 0
        // 黒い波（`reports/2026-08-21_swap-wave-design.html`）。**唯一の writer は SwapMorphFx**、
        // 値の正本は `SwapWaveLogic` / `SwapEnergyLogic` の const（数値を 2 か所に置かない）。
        //   x = 走る山の中心（figure y。-1 足元 / +1 頭）
        //   y = 山の振幅（figure 単位）／ z = 針の強さ ／ w = 稀に跳ぶ帯の強さ
        // ⚠ すべて 0 で**従来の絵に戻る**（層を切って焼く `-Set layers=` はこれで効く）。
        _SwapWave("Swap Wave (crestY, crestAmp, needle, spike)", Vector) = (0, 0, 0, 0)
        //   x = 入れ替わりが始まってからの秒 ／ y = キメの一拍 1..0 ／ z = 晴れる段の進み 0..1
        //   w = 基本の波へ掛ける倍率（全身のエネルギー。**既定 1**）
        _SwapWave2("Swap Wave 2 (elapsed, beat, clear, ampMul)", Vector) = (0, 0, 0, 1)
        // 手のホットスポット（設計 F）。位置は**実寸の人の figure 空間**（`_SwapRect0` 側 = `pM`）。
        //   xy = 中心 ／ z = 半径 ／ w = 結合済みの強さ（0 = 使わない）
        _SwapHotA("Swap Hot A (px, py, r, amount)", Vector) = (0, 0, 0, 0)
        _SwapHotB("Swap Hot B (px, py, r, amount)", Vector) = (0, 0, 0, 0)
        // 持続の覆い（`canon/LEDGER.md` 0102）。**唯一の writer は SwapMorphFx**。
        //   _SwapMinX     覆いを効かせる左端（枠 UV・0 = 制限しない）。左右分割の右側だけに効かせる
        //   _SwapDiffHold 1 = 覆い切っていても差分を読み続ける（映像にまだ当人が写っている）
        _SwapMinX("Swap Min X (0=off)", Range(0, 1)) = 0
        _SwapDiffHold("Swap Diff Hold", Range(0, 1)) = 0
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }
        LOD 100

        Pass
        {
            Name "ScreenComposite"
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_local _ CRT_SIGNAL_RUNTIME
            #pragma shader_feature_local _ CRT_ORDER_DIAGNOSTIC
            #pragma shader_feature_local _ CRT_PSF_DIAGNOSTIC
            #pragma shader_feature_local _ CRT_STYLE_DIAGNOSTIC
            #pragma shader_feature_local _ CRT_SIGNAL_DIAGNOSTIC
            #include "CrtCompositeShared.hlsl"
            ENDHLSL
        }
        Pass
        {
            Name "CanonicalPreGlass"
            Tags { "LightMode"="CrtCanonicalPreGlass" }
            Cull Off ZWrite Off ZTest Always
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #define CRT_RUNTIME_PREGLASS 1
            #include "CrtCompositeShared.hlsl"
            ENDHLSL
        }
    }
    FallBack Off
}
