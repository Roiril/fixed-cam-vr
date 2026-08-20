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
        // 映像そのものの出方（導入の段 4）。**管が点くこととは別**。
        // ⚠ 分けていないと「管が点き切った瞬間に映像も出る」ことになり、重み `live` が
        //   0 のままなのに画には映像が出る ＝ 重みと画が食い違う（2026-08-13 に絵で見つけた）。
        // ⚠ 既定 **1**。0 を既定にすると、この uniform を書かない場面で画がまるごと消える。
        _IntroLive("Intro Live (0=dark tube, 1=image)", Range(0, 1)) = 1
        // 終幕。**装置に届いている電力**（canon/LEDGER.md 0048「電池が切れかけみたいな感じで
        //   だんだんとちかちかしながら消えていき」）。画の**いちばん最後**に掛ける ＝
        //   映像も砂嵐も管の縁も一緒に落ちる（電池が切れるのは画の一部ではなく装置そのもの）。
        // ⚠ ちらつきの形はここに持たせない。`OutroLogic.FlickerPower` が数値で出すので、
        //   実際に書いた値をテレメトリに出せて、EditMode テストで固定でき、毎回同じ絵になる。
        // ⚠ 既定 **1**（点いている）。0 を既定にすると、この uniform を書かない場面
        //   （本編・導入・卓のプレビュー・Editor の合成プレビュー）で画がまるごと消える。
        _ScreenPower("Screen Power (1=on, 0=dead)", Range(0, 1)) = 1
        // 暗部の色を殺す量。安い ISP はノイズリダクションで**暗い所の色差から捨てる**ので、
        // 一様な脱色ではなく「明るい所に色が残り、暗がりが無彩へ落ちる」形になる。
        _ChromaKill("Dark Chroma Kill (ISP noise reduction)", Range(0, 1)) = 0
        // 周を重ねるごとに落ちていく解像度（canon/LEDGER.md 0012）。**枠を横切るブロック数**をそのまま受ける。
        //   0 = 量子化しない（今までと 1 ビットも変わらない画）
        // 0..1 → ブロック数の対応表は **C# の ScreenDecayLogic にしかない**。ここにも式を置くと、
        // テレメトリが読む値と画が黙って食い違う（この codebase が何度も踏んだ型）。
        _CoarseBlocks("Coarse (blocks across frame, 0=off)", Float) = 0
        _ExposureBias("Exposure Bias (EV, AGC lag)", Range(-2, 2)) = 0
        // 人形に付き従う劣化。(中心 u, 中心 v, 半径, 強さ)。ShowCgLayer が人形の投影から供給する。
        // 対象に紐づく非一様な乱れ＝機材のせいにできない＝原因が世界の側にあることになる。
        _ActorFocus("Actor Focus (cx, cy, radius, amount)", Vector) = (0.5, 0.5, 0.2, 0)
        // 焼き付き / ホールド。指定した瞬間の画を 1 枚だけ保持して混ぜる（フレーム履歴は持たない
        // ＝決定的で、同じ show.json は同じ絵になる）。1.0 で完全に止まって見える。
        // ソースはライブ映像と同じなので contain-fit も _LiveScale を共用する。
        _EchoTex("Echo (frozen frame)", 2D) = "black" {}
        _Echo("Echo Mix", Range(0, 1)) = 0
        [Header(Switch and Signal FX (out of FS_POST parity))]
        // ↓ これらは web-compositor の FS_POST 一致規約の対象外（別系統 uniform）。
        //   dip-to-black（切替演出）と信号ロスト（配信断のフェイルソフト＝砂嵐）を post FX の後段にかける。
        _SwitchDim("Switch Dip Dim", Range(0, 1)) = 0
        _SignalLost("Signal Lost (static)", Range(0, 1)) = 0
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
        _SwapMask("Swap Mask (on, lo, hi, lod)", Vector) = (0, 0.035, 0.13, 3.0)
        _SwapCover("Swap Cover", Range(0, 1)) = 0
        _SwapKnot("Swap Knot", Range(0, 1)) = 0
        _SwapThread("Swap Thread", Range(0, 1)) = 0
        _SwapReal("Swap Real", Range(0, 1)) = 0
        _SwapFromTop("Swap Unravels From Top", Range(0, 1)) = 1
        // ⚠ **線の色を作るプロパティは置かない**（2026-08-19 に `_SwapInk` を削除）。
        //   参考画像の線の明暗はその画素が元々どこの色だったかで決まっていて、乱数ではない。
        //   濃淡を振るのは「元の映像を捨てて縞を描き足す」のと同じことだった。
        // 帯の刻み。x = **枠の縦**に対する行数 / y = 帯ごとの横幅のばらつき（指数のスケール）/
        // z = 帯ごとの中心のずれ（人型の半高が 1 の座標。**「少しずつ」なので小さく**）/
        // w = 帯の組み替えの速さ (Hz)。
        // ⚠ 行数は**映像の粗さと独立**でよい（走査線は装置が走査した跡で、映像の内容ではない）。
        //   参考画像の線は 1〜2 画素で、180 行（1 行 4 画素）ではブラインドの羽根に見えた。
        _SwapSmear("Swap Smear (rows, width, center shift, hz)", Vector) = (320, 0.20, 0.42, 14)
        // 黒い線（行あたり 3 本まで）。x = 中心寄りの長い線の半幅の基準 /
        // y = 外の短い線をどれだけ離すか / z = 長い線の中心のばらつき（すべて人型の半高が 1 の座標）/
        // w = **先に掛けるぼかしの量**（mip の段。顔も服の模様もここで消す）。
        // ⚠ w が 0 だと「引き伸ばした写真」にしか見えない。参考画像の線に細部が無いのはぼかしのため。
        // ⚠ x を人型の半幅（およそ 0.22）より小さくすると、輪郭を作る線が体に届かず細切れになる。
        _SwapLine("Swap Line (long half, gap, center jitter, blur lod)", Vector) = (0.22, 0.12, 0.26, 0.8)
        // 糸のうねりの時計（秒）。**_Time を使わない** — Editor のプレビューは 1 エディタフレームの中で
        // 何コマも描くので、_Time だと連番 PNG の糸が全コマ同じになる（_GlitchSeed と同じ理由）。
        _SwapSeed("Swap Seed (seconds)", Float) = 0
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
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_LiveTex);    SAMPLER(sampler_LiveTex);
            TEXTURE2D(_SwapMaskTex); SAMPLER(sampler_SwapMaskTex);
            TEXTURE2D(_OverlayTex); SAMPLER(sampler_OverlayTex);
            TEXTURE2D(_MaskTex);    SAMPLER(sampler_MaskTex);
            // CG レイヤ（実カメラの双子の仮想カメラが描く人形）。スクリーン空間・アルファ = 被覆率。
            TEXTURE2D(_CgTex);      SAMPLER(sampler_CgTex);
            // 焼き付き / ホールド用に凍らせた 1 枚（CameraFeelFx が Graphics.CopyTexture で作る）。
            TEXTURE2D(_EchoTex);    SAMPLER(sampler_EchoTex);
            // 第 2 の差し替え層（左右分割で片側だけ別の素材を出す）。
            TEXTURE2D(_Overlay2Tex); SAMPLER(sampler_Overlay2Tex);
            TEXTURE2D(_Mask2Tex);    SAMPLER(sampler_Mask2Tex);

            CBUFFER_START(UnityPerMaterial)
                float4 _LiveScale;
                float4 _OverlayScale;
                float4 _CgScale;
                float4 _CgLens;
                float4 _CgFocalN;
                float4 _CgTex_TexelSize;   // Unity が自動で埋める (1/w, 1/h, w, h)
                float4 _LiveTex_TexelSize; // 同上。**ソースの実寸**が要る（粒をソース画素で刻むため）
                float4 _OverlayGain;
                float4 _OverlayOffset;
                float4 _Overlay2Scale;
                float _Overlay2Strength;
                float4 _SwapRect;
                float4 _SwapRect0;
                float4 _SwapMask;
                float4 _SwapSmear;
                float4 _SwapLine;
                float _SwapCover;
                float _SwapKnot;
                float _SwapThread;
                float _SwapReal;
                float _SwapFromTop;
                float _SwapSeed;
                float _SplitX;
                float _SplitFlipLeft;
                float _SplitFreezeLeft;
                float _FrameAspect;
                float _CgSoften;
                float _CgChromaBlur;
                float _CgChromaGain;
                float _UvRotSteps;
                float _OverlayStrength;
                float _CgStrength;
                float _Exposure;
                float _Contrast;
                float _Saturation;
                float _Temperature;
                float _Vignette;
                float _Grain;
                float _Scanline;
                float _ScanlineCount;
                float _Lift;
                float _Tint;
                float _Aberration;
                float _Pixelate;
                // FS_POST 一致規約の対象外（別系統）。CameraSwitchDirector / SignalLostFx / GlitchFx が駆動。
                float _SwitchDim;
                float _SignalLost;
                float _Glitch;
                float _GlitchSeed;
                // 同じく別系統。CameraFeelFx（撮像の質と残像）と ShowCgLayer（_ActorFocus）が駆動。
                float4 _ActorFocus;
                float _NoiseDark;
                float _NoiseFixed;
                float _SrcFrame;
                float _ChromaKill;
                float _Glare;
                float _Mono;
                float _CrtRound;
                float _CrtEdge;
                float _CrtEdgeWidth;
                float _CrtIgnite;
                float _IntroLive;
                float _ScreenPower;
                float _ExposureBias;
                float _Echo;
                float _CoarseBlocks;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings vert(Attributes input)
            {
                Varyings o;
                o.positionHCS = TransformObjectToHClip(input.positionOS.xyz);
                o.uv = input.uv;
                return o;
            }

            // 90 度単位の UV 回転（中心まわり）。streamer が正立フレームを送る前提なので
            // 通常 0。緊急時の手動補正用。
            float2 RotateUvSteps(float2 uv, float steps)
            {
                float2 p = uv - 0.5;
                int s = (int)round(steps) & 3;
                if (s == 1) p = float2(-p.y, p.x);
                else if (s == 2) p = -p;
                else if (s == 3) p = float2(p.y, -p.x);
                return p + 0.5;
            }

            // contain-fit: scale<1 の軸を縮めてソース全体を枠内に収める。
            // 枠外 (uv が [0,1] を出る) は inside=0 → 黒 letterbox。
            float2 ContainUv(float2 uv, float2 scale, out float inside)
            {
                float2 s = max(scale, 1e-4);
                float2 p = (uv - 0.5) / s + 0.5;
                float2 ok = step(0.0, p) * step(p, 1.0);
                inside = ok.x * ok.y;
                return saturate(p);
            }

            // CG レイヤを実映像の粗さへ寄せる 9-tap tent ぼかし。
            //
            // CG はピンホールで完璧に鮮鋭だが、実映像は JPEG q40 で高周波が落ちている。RT を映像実寸まで
            // 下げたうえで、さらに少しだけ鈍らせないと**輪郭のクッキリ度が食い違って「貼り付けた絵」に見える**。
            // 合成物が偽物に見える最大の要因のひとつが「camera / lens settings の不整合」で、その一番安い対処。
            //
            // ⚠ premultiplied なので **rgb と a を同じ重みでぼかす**。別々にぼかすと rgb > a の画素ができて
            //    人形の縁に明るい滲みが出る（over 合成の前提が壊れる）。
            half4 SampleCgTent(float2 uv, float texels)
            {
                half4 c = SAMPLE_TEXTURE2D(_CgTex, sampler_CgTex, uv);
                if (texels <= 0.001) return c;
                float2 t = _CgTex_TexelSize.xy * texels;
                half4 s = c * 4.0;
                s += SAMPLE_TEXTURE2D(_CgTex, sampler_CgTex, uv + float2( t.x, 0.0)) * 2.0;
                s += SAMPLE_TEXTURE2D(_CgTex, sampler_CgTex, uv + float2(-t.x, 0.0)) * 2.0;
                s += SAMPLE_TEXTURE2D(_CgTex, sampler_CgTex, uv + float2(0.0,  t.y)) * 2.0;
                s += SAMPLE_TEXTURE2D(_CgTex, sampler_CgTex, uv + float2(0.0, -t.y)) * 2.0;
                s += SAMPLE_TEXTURE2D(_CgTex, sampler_CgTex, uv + float2( t.x,  t.y));
                s += SAMPLE_TEXTURE2D(_CgTex, sampler_CgTex, uv + float2(-t.x, -t.y));
                s += SAMPLE_TEXTURE2D(_CgTex, sampler_CgTex, uv + float2( t.x, -t.y));
                s += SAMPLE_TEXTURE2D(_CgTex, sampler_CgTex, uv + float2(-t.x,  t.y));
                return s * (1.0 / 16.0);
            }

            /// 人形にも**映像と同じ伝送の痩せ**を掛ける。装置を通して見えている以上、同じだけ落ちる。
            ///
            /// ⚠⚠ **人形の RT は mip を使えない**（MSAA 4x で描いており、手動 Render の経路では
            ///   自動生成が走らず、`GenerateMips()` も「自動生成に任せろ」と拒否される）。
            ///   だから映像側の LOD をぼかし幅へ翻訳する — LOD n は 2^n テクセルの箱平均に相当し、
            ///   その実効半径は 2^n / 2。9-tap tent の幅をそこへ合わせれば同じだけ鈍る。
            ///   **タップ数は増えない**（オフセットが広がるだけ）。
            /// ⚠ 渡し忘れると**人形だけ鮮明**になり、そこだけ別の層に見える（2026-08-12 の実害）。
            float CgSoftenTexels(float lod)
            {
                return _CgSoften + max(exp2(lod) - 1.0, 0.0) * 0.5;
            }

            half4 SampleCgSoft(float2 uv, float lod) { return SampleCgTent(uv, CgSoftenTexels(lod)); }

            /// 枠 UV → CG レイヤの UV。**contain-fit ＋ 実レンズの歪み**を通す。
            ///
            /// 実レンズの歪みを CG にも掛ける。CG はピンホール、実映像は樽型に歪んでいるので、
            /// 掛けないと画面の端で必ずずれる（広角ほど大きい）。
            /// **除算モデル**（Fitzgibbon）: r_u = r_d / (1 + k1 r_d^2)。多項式モデルと違って
            /// 順・逆の両方に解析解があるので、卓の順投影（較正・ワイヤー重畳）とこの逆変換が
            /// **厳密に一致する**。互いに近似の逆だと画面端で食い違い、較正の検証が成立しない。
            ///
            /// ⚠ 入れ替わりのスメアも**この関数で人型マスクを引く**。写経した変換を別に持つと、
            ///   歪みや contain-fit を直したときに片方だけ取り残されて黙って食い違う。
            float2 CgUvOf(float2 uv, out float inside)
            {
                float2 uvC = ContainUv(uv, _CgScale.xy, inside);
                if (abs(_CgLens.x) > 1e-5)
                {
                    float2 n = (uvC - _CgLens.yz) / max(_CgFocalN.xy, 1e-4);
                    uvC = _CgLens.yz + n / (1.0 + _CgLens.x * dot(n, n)) * _CgFocalN.xy;
                }
                return uvC;
            }

            // 実写だけが受けている「色の粗さ」を CG にも掛ける。
            //
            // 実写は JPEG 4:2:0 で**色差が半解像度**、そのうえ q40 の粗い量子化と暗所のカラーノイズ抑制で
            // 色がにじみ、彩度そのものも落ちている。CG はどれも受けていないので、同じ絵の中で
            // 人形だけ色が鮮鋭で濃い。実測（2026-08-07・無人プレート 3 台）で人形の胴の彩度は
            // 周囲の **3〜4 倍**あった。post の彩度は乗算なので比が保存され、**原理的に埋まらない**。
            //
            // 輝度は鮮鋭なまま、色差だけ広く均して倍率を掛ける。premultiplied のまま平均して a で割ると
            // 「a で重み付けした平均色」になるので、透明な周囲の色に引っ張られない。
            half3 CgChromaMatched(half4 cg, float2 uv, float lod)
            {
                if (cg.a <= 0.002) return cg.rgb;
                half3 sharp = cg.rgb / max(cg.a, 1e-4);
                half yS = dot(sharp, half3(0.299, 0.587, 0.114));
                half3 chroma = sharp - yS;
                if (_CgChromaBlur > 0.001)
                {
                    half4 wide = SampleCgTent(uv, _CgChromaBlur + CgSoftenTexels(lod));
                    half3 blur = wide.rgb / max(wide.a, 1e-4);
                    chroma = blur - dot(blur, half3(0.299, 0.587, 0.114));
                }
                return max(yS + chroma * _CgChromaGain, 0.0) * cg.a;
            }

            /// ブラウン管の面。**角丸矩形の符号付き距離**（負 = 内側 / 0 = 縁）。
            /// 枠のアスペクトを掛けて、角の丸みが縦横で同じ半径になるようにする
            /// （掛けないと 16:9 では横に伸びた楕円の角になる）。
            float CrtSdf(float2 uv)
            {
                float2 p = (uv - 0.5) * 2.0;                 // 中心 0 / 縁 ±1
                p.x *= max(_FrameAspect, 1e-3);
                float2 half = float2(max(_FrameAspect, 1e-3), 1.0);
                float r = _CrtRound * min(half.x, half.y);
                float2 q = abs(p) - (half - r);
                return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
            }

            float Hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            /// なだらかな雑音（格子の間を補間する）。**燐光の焼けムラ**に使う。
            /// ⚠ `Hash21` を直接使うと格子のブロックが見える（それは「わざと加工した感」の側）。
            float ValueNoise21(float2 p)
            {
                float2 i = floor(p), f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                float a = Hash21(i);
                float b = Hash21(i + float2(1.0, 0.0));
                float c = Hash21(i + float2(0.0, 1.0));
                float d = Hash21(i + float2(1.0, 1.0));
                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            /// 管が点いていく途中の「電子が当たっている量」。
            ///
            /// ⚠ **チャンネルごとに少しずらして呼ぶ**と、実物の CRT と同じ収束のずれ
            /// （縁で色が割れる）になる。単色の板に見えないための仕掛けのひとつ
            /// （`canon/LEDGER.md` 0030「単色なのもなんか怖くないというかリアルじゃない」）。
            float CrtIgniteEnergy(float2 uv, float t)
            {
                float d = CrtSdf(uv);
                float2 p = (uv - 0.5) * 2.0;

                // 1) 縁に高電圧が乗る（0.00〜0.30 で立ち、0.65 までに引く）
                float rim = saturate(t / 0.30) * (1.0 - saturate((t - 0.30) / 0.35));
                float e = exp(-abs(d) * 22.0) * rim;

                // 2) 走査が中央から水平に開く（0.25〜0.78）。**ゆっくり開く** —
                //    速いと「光った」になり、管が立ち上がる過程が読めない
                float open = saturate((t - 0.25) / 0.53);
                float band = 1.0 - smoothstep(0.0, max(open * open, 1e-3), abs(p.y));

                // 3) 面が満ちるにつれて走査の帯は消える（映像が来る前の間を持たせる）
                float fill = saturate((t - 0.55) / 0.35);
                return e + band * open * (1.0 - fill) * 0.42;
            }

            /// 燐光の色。**強さで色が変わる。** 電子が届き切っていない所は深い赤、
            /// 強く当たっている所だけ白へ寄る（実物の燐光と同じ）。
            /// ⚠ ここを 1 色で書くと「明るい単色の板」になる。
            half3 PhosphorColor(float e)
            {
                return lerp(half3(0.52, 0.115, 0.038), half3(1.0, 0.84, 0.62), saturate(e));
            }

            /// 粗さの出どころは 2 つあり、**粗い方だけ**を掛ける（2 つの格子が干渉すると縞が出る）。
            ///   _Pixelate     著作した値（post 12 項目。卓の FS_POST と同式・硬い格子のまま）
            ///   _CoarseBlocks 周を重ねるごとに痩せる伝送（別系統。C# の ScreenDecayLogic が解いたブロック数）
            /// 返すのは「_CoarseBlocks 側を使うか」。使うならブロック数を <paramref name="blocks"/> へ。
            bool PickCoarse(out float blocks)
            {
                blocks = 0.0;
                float pix = _Pixelate > 0.001 ? max(6.0, floor(lerp(400.0, 18.0, saturate(_Pixelate)))) : 0.0;
                float cb  = _CoarseBlocks > 0.5 ? max(6.0, floor(_CoarseBlocks)) : 0.0;
                if (cb <= 0.0) return false;
                if (pix > 0.0 && pix <= cb) return false;   // 著作した方が粗いならそちらに譲る
                blocks = cb;
                return true;
            }

            // 著作した低解像度化。**サンプル位置の量子化**（硬い格子）。卓の FS_POST と同じ絵にする側。
            float2 PixelateUv(float2 uv)
            {
                if (_Pixelate <= 0.001) return uv;
                float bx = max(6.0, floor(lerp(400.0, 18.0, saturate(_Pixelate))));
                float2 b = float2(bx, max(4.0, floor(bx / max(_FrameAspect, 1e-3))));
                return (floor(uv * b) + 0.5) / b;
            }

            /// 周回で痩せる伝送を **mip で作る**。
            ///
            /// ⚠⚠ 旧実装はサンプル位置を量子化していた（点サンプル）。それは低域通過でも符号化でもなく
            ///    **周期的な停止と局所拡大を持つ座標変形**で、ブロック内は 1 テクセルを引き伸ばし、
            ///    境目だけが元画像を数倍速で走査する。実測（2026-08-12）で格子の境目に元の **2.9 倍**の
            ///    段差が乗っていた（実際の JPEG 劣化も解像度低下もすべて 1.0 前後）。
            ///    これが「現実でこんな粗くなり方はしない」の正体（`canon/LEDGER.md` 0018）。
            ///
            /// 実物の順序は **帯域を落としてから間引く**。mip はまさにそれ（面積平均のピラミッド）で、
            /// trilinear が中間の LOD を補間するので「低い解像度で送られてきた画」そのものになる。
            /// **1 タップのまま**なので Quest の予算も動かない。
            /// ⚠ ソーステクスチャが mipChain を持っていないと**何も起きない**（LOD が無視される）。
            ///   CameraStream / MjpegScreen / RecordedFramePlayer の Texture2D は mipChain:true で作る。
            float CoarseLod()
            {
                float blocks;
                if (!PickCoarse(blocks)) return 0.0;
                // 枠を横切るブロック数 → 映像を横切るブロック数 → 1 ブロックのソース画素数
                float acrossImage = max(blocks * max(_LiveScale.x, 1e-3), 1.0);
                float srcW = max(_LiveTex_TexelSize.z, 2.0);
                return max(log2(srcW / acrossImage), 0.0);
            }

            /// 色差は輝度より先に捨てられる（4:2:0 とクロマの粗い量子化）。輝度の LOD へ足す分。
            float ChromaLodBias()
            {
                float blocks;
                return PickCoarse(blocks) ? 1.0 : 0.0;
            }

            // ---- 入れ替わりのほどけ（canon/LEDGER.md 0089 / 0090）-------------------------
            //
            // 全画面の砂嵐は「機材が壊れた」で説明が付くので、その最中に何が入れ替わっても
            // **入れ替わったことにならない**。だから乱れは**人型に紐づいて**起こす。
            //
            // ⚠⚠ **描き足した線ではなく、その人の画素を引き伸ばす**
            //   （2026-08-19・ユーザーが参考画像を 3 枚渡して 2 度目の作り直し）。
            //   参考画像は人型が**水平の細い線**に分解され、線ごとに横へずれて伸びている絵で、
            //   **線の中身はその人自身の画素**。だから新しい絵を上に描くのではなく、
            //   **下の映像を行ごとに水平へ引き伸ばして置き換える**。
            //
            //   1 度目（等高線の渦）を捨てた理由は焼いた絵に出ている:
            //     ① 人型が読めなくなる（画面の広い範囲へ木目が広がり、どこが人だったか分からない）
            //     ② 中身が体験者の画素でないので「本人がほどけた」に 1 ビットも見えない
            //
            // ⚠ **マスク（どこをほどくか）は当面 CG の人型シルエット。** 中身は既に本人の画素なので、
            //   人体検知（MediaPipe 等）へ差し替えるときに直すのは `silSmear` / `sil` の 2 行で済む。

            /// 人型を原点、身長の半分を 1 とする座標。線の密度と広がりが figure に付いてくるので、
            /// 縮んでいく間も「同じ糸が集まっている」に見える（画素で刻むと縮小＝別物になる）。
            float2 SwapFigureSpace(float2 uv)
            {
                float h = max(_SwapRect.z, 1e-3);
                return float2((uv.x - _SwapRect.x) * _FrameAspect, uv.y - _SwapRect.y) / h;
            }

            /// figure 空間 → 枠 UV（<see cref="SwapFigureSpace"/> の逆）。
            float2 SwapFrameUv(float2 p)
            {
                float h = max(_SwapRect.z, 1e-3);
                return float2(_SwapRect.x + p.x * h / max(_FrameAspect, 1e-3),
                              _SwapRect.y + p.y * h);
            }

            /// 走査線スメアの読み出し先。**この uv で映像も人型マスクも引く**（別々に取ると、
            /// 線が体の外へ流れた所でマスクが付いてこず、線が体の縁でぶつ切りになる）。
            ///
            /// ⚠⚠ **作り方はユーザーが言葉で示した手順に従う**（2026-08-19。それまで 4 巡
            ///   「線を 1 本ずつ描く」方向で外し続けた）:
            ///
            ///   > 人間の上にぼやけるフィルターをかけ、それをもとに横に割って、
            ///   > その横の中差をランダムにし、中心を少しずつずらす
            ///
            ///   > 輪郭を白色で表現するんじゃなくて、あくまで黒い線で表現するんだけど、
            ///   > それが不ぞろいだからぼやけて見える
            ///
            ///   ① **ぼかす**（`_SwapLine.w`）— 顔も服の模様も先に消す。これが無いと
            ///      「引き伸ばした写真」にしか見えない。参考画像の線に細部が無いのはこれが理由
            ///   ② **横に割る** — 走査線の帯に切る（行の中心 1 本を行じゅうが読む）
            ///   ③ **帯ごとに横幅をランダムにする**（`_SwapSmear.y`）— 伸びる帯と縮む帯を混ぜる
            ///   ④ **帯ごとに中心を少しずつずらす**（`_SwapSmear.z`）— ずれは小さい。輪郭が波打つ
            ///   ⑤ 線は <see cref="SwapLineHit"/> が**黒だけ**で描く（明るい線は作らない）
            ///
            /// ⚠ **行の乱数は時間で組み替える。** 固定すると 2.6 秒ずっと同じ縞が貼り付いて、
            ///   ほどけている最中なのに絵が止まって見える。
            /// ⚠⚠ **行は枠の座標で刻む。figure 空間で刻んではいけない**（2026-08-19 に 1 度やった）。
            ///   人型は縮む段で 1.65m → 0.40m になるので、figure に紐づけると行の高さも 1/4 になり、
            ///   **縮み切る前に線が 1 画素を割って消える**（実際そうなり、縮む段が無地になった）。
            ///   走査線は装置が持つものなので、画面に固定されているのが物理的にも正しい。
            float2 SwapSmearUv(float2 uv, float reach, float t, out float rowR, out float rowS)
            {
                float rows = max(_SwapSmear.x, 4.0);
                float row = floor(uv.y * rows);
                float tick = floor(t * max(_SwapSmear.w, 1.0));
                rowR = Hash21(float2(row, tick));
                rowS = Hash21(float2(row + 137.0, tick + 41.0));

                // 行の中心 1 本を行じゅうが読む（＝縦解像度が落ちて走査線になる）。
                // 水平の動きだけは人型の中心を基準にするので figure 空間で解く。
                float2 p = SwapFigureSpace(float2(uv.x, (row + 0.5) / rows));

                // ③ 帯ごとの横幅（伸び / 縮みを対称に扱うので指数）と、④ 中心のずれ（小さい）。
                float scale = exp((rowR - 0.5) * 2.0 * reach * _SwapSmear.y);
                float shift = (rowS - 0.5) * 2.0 * reach * _SwapSmear.z;
                p.x = (p.x - shift) / scale;
                return SwapFrameUv(p);
            }

            /// 線の黒さ。0 で真っ黒、1 で地のまま。**単色の黒を置かない** — 監視カメラの画は
            /// 場所で明るさが桁違いなので、固定色だと暗い所で線が 1 本も見えない。
            static const float SwapInkDark = 0.30;

            /// その画素が黒い線の上か 0..1。**線は行あたり 3 本まで。**
            ///
            /// ⚠⚠ **明るい線を作らない**（2026-08-19 ユーザー指摘「輪郭を白色で表現するんじゃなくて、
            ///   あくまで黒い線で表現する」）。白く見えるのは**地**（帯ごとに歪めたぼかし画）で、
            ///   線ではない。3 巡目まで「明るい線と暗い線が混ざる」と読んでいたのが誤り。
            ///
            /// ⚠⚠ **本数を決め打つ**（2026-08-19 ユーザー指定）:
            ///   > 各横軸から見て、中心寄りに長めのを 1 本で、それで大体の崩れた輪郭を形成し、
            ///   > それより外には 1〜2 本、それも短いのくらいしかつけちゃダメ
            ///   > 線の細さはちょうどいいけど長さが短すぎて細切れ
            ///
            ///   暗さから線の密度を作る（ディザ）と 1 行に何十本もの断片ができて**細切れ**になる。
            ///   そうではなく、**長い 1 本が輪郭を作り、外の 1〜2 本が短くほつれる**。
            ///   行ごとに長い線の幅と中心が違うので、その端がそのまま崩れた輪郭になる。
            /// その画素を黒く覆うか 0..1。**行ごとの矩形波**で、黒の割合（デューティ）を
            /// 人型の重みで決める。
            ///
            /// ⚠⚠ **人型の中をベタで塗らない**（2026-08-19 の 11 巡目・参考画像と並べて測って直した）。
            ///   塊で潰していた頃は**隣り合う行の輝度差が 7〜8** しかなく（参考画像は 23〜28）、
            ///   「黒いベタの人型」であって「線でできた人型」に見えていなかった。
            ///   参考画像は人型の中まで線で、**間に細い隙間が残っている**。
            /// ⚠ 隙間は figure 0.012（実機で 3〜4 画素）ほどしか空かないので、
            ///   そこから下が覗いても**体としては読めない**（0091 の「直接は見られない」は保たれる）。
            /// ⚠ **周期と位相を行ごとに散らす。** 揃えると縦の縞になる。
            float SwapLineHit(float px, float row, float tick, float reach, float duty)
            {
                float r0 = Hash21(float2(row, tick));
                float r1 = Hash21(float2(row + 11.0, tick + 3.0));
                float r2 = Hash21(float2(row + 31.0, tick + 7.0));
                float period = _SwapLine.x * (0.90 + r0 * 2.30) * lerp(1.0, 1.55, saturate(reach));
                // ⚠⚠ **粗い行と細かい行を混ぜる**（2026-08-19 の 11 巡目）。1 つの周期だけだと
                //   その行の線がぜんぶ同じ長さになる。参考画像の線は**中央値 13px に対し最大 257px**
                //   ＝ 短い線が多く、稀に非常に長い線が走る。
                float fine = period * 0.30;
                float coarse = period;
                float pick = step(0.46, r2);          // 半分弱の行が長い線
                float len = lerp(fine, coarse, pick);
                float u = frac((px + r1 * len) / max(len, 1e-4));
                // ⚠⚠⚠ **芯は完全に塗る。散らすのは縁だけ**（2026-08-20・ユーザー指定
                //   「人は完全に黒色で塗りつぶされ、人は 1 ミリも映像内に映らないようにしてほしい。
                //   ただ、塗りつぶしで人の輪郭を鮮明にするのではなく、線でぼやけさせたい」）。
                //   11 巡目は中まで 5% 抜いていたので、**そこから体が透けていた**。
                //   輪郭がぼやけるのは縁の散らしが担うので、芯を埋めても硬いシルエットにはならない。
                float jitter = lerp(0.72 + r2 * 0.56, 1.0, smoothstep(0.72, 0.97, duty));
                float d = saturate(duty * jitter);
                return step(u, d);
            }

            /// 糸が届く範囲（人型を包む楕円）。<paramref name="spread"/> で外へ広がる。
            ///
            /// ⚠⚠ **これが無いと画面全体が横線になる**（2026-08-19 に 1 度そうなった）。
            ///   引き伸ばした uv が体の中を指す画素は**体から遠くてもいくらでも出る**ので、
            ///   「どこまで糸が届くか」は別に決めなければならない。全域が乱れた瞬間に
            ///   「機材が壊れた」へ戻り、体験者だけが襲われているという読みが消える（0089 の核心）。
            /// ⚠ 落とし方は 3 乗で、しかも**すぐ落ち始める**。緩やかだと体の外まで一様に濃い縞が
            ///   立って人型が読めなくなる（参考画像は芯が濃く、外へ流れる線ほど疎）。
            /// ⚠⚠⚠ **縦と横を分ける**（2026-08-19 の 10 巡目・絵で直した）。楕円 1 つで書いていた頃は
            ///   縦にも 3 乗で落ちるので、**頭と足元で 0 に近づき、そこだけ覆いが掛からずに
            ///   元の体が見えていた**（実測: 頭の高さで 0.0003）。人型の縦の形を持っているのは
            ///   `silSmear` の方なので、ここで縦を切る理由が無い — 切ってよいのは
            ///   **体より上下へ大きく外れた所**（糸が垂れる範囲の外）だけ。
            float SwapReachField(float2 p, float spread)
            {
                float rx = 0.34 + spread * 0.40;
                float kx = 1.0 - smoothstep(0.22, 1.00, abs(p.x) / max(rx, 1e-3));
                float ky = 1.0 - smoothstep(1.02, 1.22, abs(p.y));
                return kx * kx * kx * ky;
            }

            /// その行がどれだけ糸で塞がれているか 0..1。
            ///
            /// ⚠ <paramref name="knot"/> が 1 のときは**全部の行**が塞がる。差し替えの 1 フレームが
            ///   そこなので、隙間が残ると下の映像が入れ替わる瞬間が見えてしまう。
            ///   緩むほど行が抜けて、隙間から下が覗く（参考画像も線の間に背景が見えている）。
            float SwapRowInk(float rowR, float front, float knot)
            {
                float gate = smoothstep(0.72, 0.16, rowR);   // 半分ほどの行が濃く残る
                float k = saturate(knot);
                // ⚠⚠⚠ **締まるときは薄めない**（2026-08-20・ユーザー指定「人は完全に黒色で
                //   塗りつぶされ、人は 1 ミリも映像内に映らないようにしてほしい」）。
                //   `knot` は**ほどける段で 0 → 1**・晴れる段で 1 → 0 と両方向に動くので、
                //   値だけで薄めると**ほどけている最中がずっと半透明**になり、体が透けて見える。
                //   ほどけ切ったか（`_SwapCover`）で向きを分ける。
                // ⚠⚠ **緩むときは行を抜くだけでなく全体も薄める**（2026-08-19 の 9 巡目）。
                //   抜くだけだと、残った行と抜けた行が 1 行おきに並んで**ブラインドの羽根**になる。
                float loose = step(0.999, _SwapCover) * (1.0 - k);
                return saturate(front * lerp(1.0, gate * 0.35, loose));
            }

            /// ほどけの前線。0 = まだ / 1 = ほどけた。
            /// **行き先の方向へほどける** — 人 → 人形は頭から（人形は足元に現れる）、
            /// 人形 → 人は足元から（人は上へ育つ）。
            float SwapFront(float2 uv)
            {
                float h = max(_SwapRect.z, 1e-3);
                float v = saturate((uv.y - (_SwapRect.y - h)) / (h * 2.0));   // 足元 0 → 頭 1
                float x = _SwapFromTop > 0.5 ? 1.0 - v : v;
                // ⚠ **広げない**（2026-08-19 の 10 巡目に 0.50 → 0.30）。覆いは前線の後ろでしか
                //   濃くならないので、ぼけ幅が体の半分もあると**ほどけ切る直前まで体が透けて見える**。
                const float band = 0.30;  // 前線のぼけ幅（体の高さに対する割合）
                return saturate((_SwapCover * (1.0 + band) - x) / band);
            }

            /// 走査線の色。**素材は引き伸ばした下の映像そのもの**（新しい色を作らない）。
            /// 行ごとに濃さを振って、参考画像の「黒く潰れた線」と「背景と同じ明るさの線」を混ぜる。
            ///
            /// ⚠ **単色で塗らない。** 塗ると黒い穴になって線に見えないし、
            ///   その画素が「誰だったか」の手掛かりが 1 ビットも残らない。
            // ⚠⚠ **線の色を作る関数は置かない**（2026-08-19・ユーザー指摘
            //   「今、ノイズはランダムに白黒じゃん。けど、元画像はそうじゃない」）。
            //   参考画像の線の明暗は**その画素が元々どこの色だったか**で決まっている —
            //   明るい壁から来た画素は白い線、暗い体から来た画素は黒い線。乱数ではない。
            //   一度は行ごとの乱数で濃い / 淡いへ振っていたが、それは
            //   **元の映像を捨てて縞を描き足している**のと同じで、
            //   「線の中身は本人の画素」（`canon/LEDGER.md` 0090）と正面から衝突する。
            //
            //   ⇒ 引き伸ばした画素を**そのまま**出す。線に見えるのは
            //     「隣り合う行が別の場所を読んでいる」ことだけで足りる。
            //     線と地の差は**ずれの大きさ**で作る（`lineAmt` が色ではなく距離に掛かる）。

                        // 演出としての「映像の乱れ」の位置ずれ成分。帯（走査線ブロック）の一部だけを水平に飛ばし、
            // 強いときは垂直の同期ずれも足す。**全層のサンプル前**に掛けるので、ライブ・差し替え素材・
            // マスク・CG が一緒にずれる = 差し替えの継ぎ目も一緒に乱れて隠れる（企画書 2.3）。
            // 枠外へ出た分は ContainUv が黒に落とすので、伝送のブロック落ちに見える。
            float2 GlitchUv(float2 uv)
            {
                if (_Glitch <= 0.001) return uv;
                float g = saturate(_Glitch);
                float t = floor(_GlitchSeed * 18.0);       // 約 18Hz で組み替わる
                float band = floor(uv.y * 24.0);
                float pick = Hash21(float2(band, t));
                float amt = Hash21(float2(band + 37.0, t + 11.0));
                float active = step(1.0 - 0.6 * g, pick);  // 強いほど多くの帯が飛ぶ
                uv.x += (amt - 0.5) * 0.22 * g * active;
                uv.y += (Hash21(float2(t, 3.7)) - 0.5) * 0.06 * g * g;
                return uv;
            }

            // ライブ × 差し替え素材の合成だけを 1 点で評価する。色収差はこれを RGB 別の uv で 3 回引く。
            // CG（人形）を含めないのは、3 回引くと 9-tap のぼかしが 27 サンプルに膨らむため。
            // 人形は色収差の後に中心 uv で 1 回だけ重ねる。
            /// 輝度は <paramref name="lod"/>、色差は <c>lod + chromaBias</c> で引く。
            /// 実物の符号化は**色差を先に捨てる**（4:2:0 とクロマの粗い量子化）ので、輝度と色差が
            /// 同じ大きさの格子で落ちるのは起こりえない。bias=0 のときは 1 タップのまま。
            half3 SplitChroma(half3 sharp, half3 wide)
            {
                half y = dot(sharp, half3(0.299, 0.587, 0.114));
                return max(y + (wide - dot(wide, half3(0.299, 0.587, 0.114))), 0.0);
            }

            /// レンズの内面反射（ベイリンググレア）。明るい所の光が画全体へ薄く回り、
            /// **暗い所のコントラストを奪う**。安いレンズほど強く、監視カメラの絵では必ず出る。
            /// 閾値を高く取るので、光源が無い場面では何も起きない（無い所に滲みを作ると即座に嘘になる）。
            /// ⚠ **ぼけた値が元より明るい所にだけ足す**。物理的にもそれが正しい（暗い画素の隣に
            ///   光源があるときだけ光が回り込む）が、実装上の安全網でもある —
            ///   ソースが mipChain を持たないと LOD 指定は無視されて <c>wide == sharp</c> が返り、
            ///   その場合この式は**恒等に 0** になる。閾値方式だと明るい画素が二重加算されて飛ぶ。
            half3 Glare(half3 sharp, half3 wide)
            {
                return max(wide - max(sharp, 0.45), 0.0) * (_Glare * 2.2);
            }

            half3 SampleBase(float2 uv, float lod, float chromaBias)
            {
                // 左右分割（canon/LEDGER.md 0050）。分割位置より左は**左右反転して**ライブを読む。
                // ⚠ 反転が効くのは live と凍結だけ。差し替え素材（録画・生成画像）は反転しない —
                //   反転したまま録画を流すと映像の中の自分が逆走し、画面中央の境目へ入って消える。
                // ⚠ 環境が左右対称なカメラでしか成立しない（このプロジェクトではカメラ A だけ）。
                float onLeft = (_SplitX > 0.0001 && uv.x < _SplitX) ? 1.0 : 0.0;
                float2 uvSrc = uv;
                if (onLeft > 0.5 && _SplitFlipLeft > 0.5) uvSrc.x = 1.0 - uvSrc.x;

                float liveIn;
                float2 uvL = ContainUv(RotateUvSteps(uvSrc, _UvRotSteps), _LiveScale.xy, liveIn);
                half3 live = SAMPLE_TEXTURE2D_LOD(_LiveTex, sampler_LiveTex, uvL, lod).rgb;
                if (chromaBias > 0.001)
                    live = SplitChroma(live, SAMPLE_TEXTURE2D_LOD(_LiveTex, sampler_LiveTex,
                                                                  uvL, lod + chromaBias).rgb);
                if (_Glare > 0.001)
                    live += Glare(live, SAMPLE_TEXTURE2D_LOD(_LiveTex, sampler_LiveTex, uvL, lod + 4.5).rgb);
                live *= liveIn;

                // 凍らせた 1 枚を混ぜる。1.0 = 完全に止まって見える（ホールド）、
                // 小さい値 = 少し前の姿がそこに薄く残る（焼き付き）。
                // 動いていない画素は同じ値なので何も起きず、**動いたものの跡だけが残る**。
                // 全画面の焼き付き（_Echo）と、左半分だけの凍結（_SplitFreezeLeft）の**大きい方**。
                // 左半分の凍結は 3 周目 A で「凍った自分」を残すためのもので、
                // 凍らせた 1 枚は反転した uv で読むので**反転したまま止まる**（そこが狙い）。
                float echoMix = max(_Echo, onLeft * _SplitFreezeLeft);
                if (echoMix > 0.001)
                {
                    half3 echo = SAMPLE_TEXTURE2D_LOD(_EchoTex, sampler_EchoTex, uvL, lod).rgb * liveIn;
                    live = lerp(live, echo, saturate(echoMix));
                }

                float ovIn;
                float2 uvO = ContainUv(uv, _OverlayScale.xy, ovIn);
                // 差し替え素材（録画・プレート）にも**同じだけ**掛ける。片方だけ鮮明だと、
                // 3 周目に録画へ切り替わった瞬間に画の素性が変わって切替がばれる。
                half3 overlay = SAMPLE_TEXTURE2D_LOD(_OverlayTex, sampler_OverlayTex, uvO, lod).rgb;
                if (chromaBias > 0.001)
                    overlay = SplitChroma(overlay, SAMPLE_TEXTURE2D_LOD(_OverlayTex, sampler_OverlayTex,
                                                                        uvO, lod + chromaBias).rgb);
                if (_Glare > 0.001)
                    overlay += Glare(overlay, SAMPLE_TEXTURE2D_LOD(_OverlayTex, sampler_OverlayTex,
                                                                   uvO, lod + 4.5).rgb);
                // 色統計マッチング（卓が焼いた per-channel の gain/offset）。恒等なら何も起きない。
                overlay = saturate(overlay * _OverlayGain.rgb + _OverlayOffset.rgb) * ovIn;

                // マスクも同じだけ鈍らせる。継ぎ目だけが鮮明に残ると、粗い画の中でそこだけ浮く。
                half mask = SAMPLE_TEXTURE2D_LOD(_MaskTex, sampler_MaskTex, uv, lod).r;
                half3 col = lerp(live, overlay, saturate(mask * _OverlayStrength));

                // 第 2 の差し替え層。**左と右へ別の素材を同時に置く**ときだけ効く
                // （3 周目 A: 左＝録画 / 右＝環境＋人形、4 周目 A: 左＝大量の人形 / 右＝体験者人形）。
                // ⚠ 色統計マッチング（_OverlayGain/_Offset）は 1 層目にしか掛からない。
                //   第 2 層の素材は生成のパイプラインが post を抜いた素の色で入る（tools/gen-tone.py）。
                if (_Overlay2Strength > 0.001)
                {
                    float ov2In;
                    float2 uvO2 = ContainUv(uv, _Overlay2Scale.xy, ov2In);
                    half3 ov2 = SAMPLE_TEXTURE2D_LOD(_Overlay2Tex, sampler_Overlay2Tex, uvO2, lod).rgb;
                    if (chromaBias > 0.001)
                        ov2 = SplitChroma(ov2, SAMPLE_TEXTURE2D_LOD(_Overlay2Tex, sampler_Overlay2Tex,
                                                                    uvO2, lod + chromaBias).rgb);
                    ov2 *= ov2In;
                    half mask2 = SAMPLE_TEXTURE2D_LOD(_Mask2Tex, sampler_Mask2Tex, uv, lod).r;
                    col = lerp(col, ov2, saturate(mask2 * _Overlay2Strength));
                }
                return col;
            }

            /// <b>映像の中に写っている人</b>を、無人プレートとの差で拾う。−1 = 使わない。
            ///
            /// ⚠⚠ **入れ替わりの対象は映像の中の人であって、CG の人形ではない**
            ///   （3 周目なら 1 周目の録画の中の自分、帰りの A ならライブの自分）。
            ///   CG のシルエットで覆うと、**いまの体験者の立ち位置**に黒が出る —
            ///   3 周目の映像は過去の自分なので、まったく別の場所を覆うことになる。
            /// ⚠ 固定視点カメラなので**背景差分で足りる**（人体検知の推論を実機で回さない）。
            ///   無人プレートは卓が既に撮っていて（`plate_<ID>_<時刻>.jpg`）、素材としても配っている。
            /// ⚠⚠ **マスクは「ほどけ始めた瞬間の枠」で引く**（`_SwapRect0`）。映像の中の人は
            ///   縮まないので、縮んだ `_SwapRect` で引くと縮む段でマスクが人から外れる。
            /// ⚠ **粗い mip で取る**（`_SwapMask.w`）。等倍だと粒と符号化のゆらぎを人として拾う。
            float SwapDiffSil(float2 uv, float lod)
            {
                if (_SwapMask.x < 0.5) return -1.0;

                // いまの人型枠 → ほどけ始めた瞬間の枠（映像の中の人はそこに写っている）。
                float2 p = SwapFigureSpace(uv);
                float h0 = max(_SwapRect0.z, 1e-3);
                float2 uv0 = float2(_SwapRect0.x + p.x * h0 / max(_FrameAspect, 1e-3),
                                    _SwapRect0.y + p.y * h0);
                if (uv0.x < 0.0 || uv0.x > 1.0 || uv0.y < 0.0 || uv0.y > 1.0) return 0.0;

                float plateIn;
                float2 uvP = ContainUv(RotateUvSteps(uv0, _UvRotSteps), _LiveScale.xy, plateIn);

                // ⚠⚠ **粗さの違う 2 段で取って大きい方を採る**（2026-08-20）。
                //   粗い段は**人の中の穴を埋める**（服と背景の明るさがたまたま近い所で
                //   マスクが抜け、そこだけ体が透ける）。細かい段は**縁を保つ**。
                //   片方だけだと「穴が空く」か「輪郭が膨らむ」のどちらかになる。
                float lodA = lod + _SwapMask.w;
                float lodB = lod + max(_SwapMask.w - 2.0, 0.0);
                half3 plA = SAMPLE_TEXTURE2D_LOD(_SwapMaskTex, sampler_SwapMaskTex, uvP, lodA).rgb;
                half3 plB = SAMPLE_TEXTURE2D_LOD(_SwapMaskTex, sampler_SwapMaskTex, uvP, lodB).rgb;
                half3 curA = SampleBase(uv0, lodA, 0.0);
                half3 curB = SampleBase(uv0, lodB, 0.0);
                float dA = max(abs(curA.r - plA.r), max(abs(curA.g - plA.g), abs(curA.b - plA.b)));
                float dB = max(abs(curB.r - plB.r), max(abs(curB.g - plB.g), abs(curB.b - plB.b)));
                float d = max(dA, dB);
                return plateIn * smoothstep(_SwapMask.y, _SwapMask.z, d);
            }


            half4 frag(Varyings input) : SV_Target
            {
                // 枠の座標（post FX の空間。卓の FS_POST と一致させる側）と、
                // テクスチャを引く座標（低解像度化 → 乱れ の順に劣化させた側）を分ける。
                float2 screenUv = input.uv;
                float2 sampleUv = GlitchUv(PixelateUv(screenUv));

                // 伝送が痩せたぶん（mip）＋ **周辺の解像度低下**（像面湾曲。実レンズは角ほど像がゆるい）。
                // 角だけぼけるのは静的なので怖さには効かないが、これが無いと「中心も端も等しく鮮明」
                // というレンズの存在しない絵になる。
                float2 dir = screenUv - 0.5;
                float r2 = saturate(dot(dir, dir) * 4.0);
                float lod = CoarseLod() + saturate(r2 - 0.45) * 0.7;
                float chromaBias = ChromaLodBias();

                // 1-2) ライブ × 差し替え素材。色収差は放射方向に RGB をずらす（端ほど強い）。
                half3 col;
                if (_Aberration > 0.001)
                {
                    float2 c = sampleUv - 0.5;
                    float r = saturate(length(c) * 2.0);
                    float2 off = c * (r * r) * _Aberration * 0.02;
                    col.r = SampleBase(sampleUv + off, lod, chromaBias).r;
                    col.g = SampleBase(sampleUv, lod, chromaBias).g;
                    col.b = SampleBase(sampleUv - off, lod, chromaBias).b;
                }
                else col = SampleBase(sampleUv, lod, chromaBias);

                // 2.5) CG レイヤ（人形）。**ポスト FX の前**に重ねるのが要点 —
                //      映像と同じ露出・彩度・ヴィネット・走査線・グレインを浴びて初めて
                //      「映像の中に居る」ように見える（後段に足すと必ず浮く）。
                //
                //      **ライブ映像と同じ contain-fit 枠に収める**（_CgScale = MjpegScreen.ContainScale）。
                //      旧実装は生の screenUv で枠いっぱいに描いており、4:3 の映像が 16:9 の枠へ
                //      レターボックスされている分（_LiveScale.x=0.75）だけ人形が水平 1.33 倍外側へずれていた
                //      ＝ 姿勢を完璧に測っても絶対に合わない（2026-07-27 監査 CRITICAL 1）。
                //      letterbox 帯に人形が出ないのは正しい挙動（映像の外に人形は居ない）。
                // ⚠⚠⚠ **ほどけは CG が居なくても走る**（2026-08-20）。覆う相手は
                //   **映像の中に写っている人**なので、CG（人形）が出ているかとは無関係。
                //   `_CgStrength` の内側に置いていたころは、**人形を出さないカットでは
                //   入れ替わりが 1 画素も出なかった**（プレビューで踏んだ）。
                float cgIn = 0.0;
                half4 cg = half4(0.0, 0.0, 0.0, 0.0);
                half aCg = 0.0;
                half3 rgbCg = half3(0.0, 0.0, 0.0);
                if (_CgStrength > 0.001)
                {
                    // 乱れ・低解像度化は人形にも同じだけ掛ける（映像だけが壊れて人形が無傷だと必ず浮く）。
                    float2 uvC = CgUvOf(sampleUv, cgIn);
                    // 人形にも**映像と同じ伝送の痩せ**を掛ける（装置を通して見えている以上、同じだけ落ちる）。
                    cg = SampleCgSoft(uvC, lod);
                    cg.rgb = CgChromaMatched(cg, uvC, lod);
                    // ⚠ premultiplied を崩さない — アルファに掛けた分は rgb にも掛ける。
                    aCg = saturate(cg.a);
                    rgbCg = cg.rgb;
                }
                // ⚠⚠ **入れ替わりの絵は CG を重ねた後に混ぜる**（2026-08-19）。先に混ぜると
                //   人形（実機）や代役（Editor）が上書きして、線が乗っていない絵になる。
                half3 swapCol = 0.0;
                half swapMix = 0.0;
                if (_SwapRect.w > 0.5)
                {
                    // 入れ替わりのほどけ。**下の映像そのものを行ごとに水平へ引き伸ばす**ので、
                    // ほどけている最中も「誰がほどけているか」が画素に残っている。
                    // ⚠ 人型（CG）と同じ `sampleUv` から起こす — 乱れが乗ったフレームで
                    //   糸だけ元の位置に残ると、そこだけ別の層に見える。
                    // その画素の人型。**映像の中の人が居るなら、そちらが正**
                    // （`SwapDiffSil`。負値 = 無人プレートが配られていないので CG の形へ落ちる）。
                    float silCg = saturate(aCg);
                    float silDiff = SwapDiffSil(sampleUv, lod);
                    // ⚠⚠ **ほどけ切る手前で CG の形へ渡す。** 覆い切った縁で画面が差し替わる
                    //   （録画 → 無人プレート）ので、そこから先に映像の中の人はもう居ない。
                    //   渡すのは全面が覆われている最中なので、形が変わっても 1 画素も見えない。
                    float sil = silDiff < 0.0 ? silCg
                              : lerp(silDiff, silCg, smoothstep(0.86, 1.0, _SwapCover));
                    float2 p0 = SwapFigureSpace(sampleUv);
                    float spread = saturate(_SwapThread);
                    float frontHere = SwapFront(sampleUv);        // この画素までほどけが来たか

                    // 早い棄却。糸が届くのは体を包む楕円の中だけ（`SwapReachField`）なので、
                    // 画面の大半はここで抜ける（本編は 1 画素も触らない）。
                    // ⚠ ここでは**いちばん遠くまで届く行**の範囲で判定する（下の `span` の上限）。
                    if (sil > 0.002 || silCg > 0.002
                        || SwapReachField(float2(p0.x / 1.30, p0.y), spread) > 0.002)
                    {
                        float rowR, rowS;
                        float2 sUv = SwapSmearUv(sampleUv, spread, _SwapSeed, rowR, rowS);

                        // ⚠ **行ごとに届く距離を変える。** 全行を同じ幅で切ると、
                        //   縁が垂直な直線になって「バーコード」に見える（2026-08-19 の絵）。
                        //   参考画像は長い線と短い線が混ざり、塊の縁が不揃いにほつれている。
                        float span = 0.40 + rowS * 0.90;
                        float field = SwapReachField(float2(p0.x / span, p0.y), spread);

                        // 帯ごとに歪めた位置の人型。**縁が帯ごとに崩れている**ので、
                        // ここから作る重みには CG の輪郭がそのまま出ない。
                        float sIn;
                        half4 cgS = SampleCgSoft(CgUvOf(sUv, sIn), lod + _SwapLine.w);
                        float smDiff = SwapDiffSil(sUv, lod);
                        float smCg = saturate(cgS.a) * sIn;
                        float silSmear = smDiff < 0.0 ? smCg
                                       : lerp(smDiff, smCg, smoothstep(0.86, 1.0, _SwapCover));

                        // ⚠⚠ **マスクは切り取りではなく「線の濃さの重み」**（2026-08-19
                        //   ユーザー指摘「モデルに張り付ける感じだと、モデルの輪郭が目立って
                        //   しまってあんまりよくなくて、そうじゃなくて 2D の映像にオーバーレイして、
                        //   輪郭は途切れによって作らずに線の感じでぼんやりと見えるように」）。
                        //   歪めていないマスク（`sil`）を混ぜると CG の縁がそのまま輪郭として出る。
                        //   下限（0.28）を残すのは、人の外にも線を薄く散らして縁を曖昧にするため。
                        // ⚠⚠ **覆いの重みは「ずらした人型」だけで作る**（2026-08-19 の 9 巡目）。
                        //   ここに `field`（楕円）を足していたので、**体の外の画面全体に薄い線が
                        //   立って「全画面が乱れた」に戻っていた**（0089 の核心を自分で壊していた）。
                        //   `silSmear` は行ごとに横へずれた人型なので、**縁はそれだけで崩れる** —
                        //   外へ散らすために別の場を足す必要が無い。
                        //   `field` は「読みに行く先が体から遠すぎる行」を切るためだけに残す。
                        // ⚠⚠⚠ **`field` を重みに掛けない**（2026-08-19 の 10 巡目・絵で直した）。
                        //   楕円は縦にも 3 乗で落ちるので、**頭と足元で 0 に近づき、そこだけ
                        //   覆いが掛からずに元の体が見えていた**（実測: 頭の高さで 0.0003）。
                        //   人型の縦の形を持っているのは `silSmear` の方で、そちらは
                        //   行ごとに横へずれているから縁は勝手に崩れる。
                        //   `field` は早期棄却（読みに行く先が体から遠すぎる行を切る）だけに使う。
                        // ⚠⚠⚠ **芯は歪めていない人型（`sil`）で確実に覆う**（2026-08-19 の 10 巡目）。
                        //   ずらした人型（`silSmear`）だけで作ると、**体の上を外した行がそのまま
                        //   透けて元の体が見える**一方、体から離れた所には線が散る。
                        //   ⇒ **芯 = `sil`（必ず黒くなる）／ ほつれ = `silSmear`（縁を崩す）**。
                        //   7 巡目の「モデルの輪郭が目立つ」は**切り取り**として使ったときの話で、
                        //   いまは黒く覆うのが指示（縁は下の `smoothstep` と行ごとのずれで崩れる）。
                        float w = max(sil, silSmear * 0.85) * saturate(field * 2.0);
                        float rowInk = SwapRowInk(rowR, SwapFront(sUv), _SwapKnot);
                        if (rowInk * w > 0.002)
                        {
                            // ⚠ 線を置く位置は**画面上の座標**（p0）で決める。読む位置（sUv）で
                            //   決めると、帯を動かすたびに線まで一緒に動いて形が定まらない。
                            // 黒の割合。人型の中はほぼ埋まり、縁と外へ向かって疎になる。
                            // ⚠ **裾を切りすぎない**（2026-08-19 の 11 巡目）。参考画像は人型の外にも
                            //   黒い線が散っていて、そこが無いと**黒いベタが 1 つ浮いている**絵になる。
                            float duty = smoothstep(0.025, 0.55, w);
                            float hit = SwapLineHit(p0.x,
                                                    floor(sampleUv.y * max(_SwapSmear.x, 4.0)),
                                                    floor(_SwapSeed * max(_SwapSmear.w, 1.0)),
                                                    spread, duty);
                            // ⚠⚠⚠ **覆いは黒。中身を 1 画素も見せない**（2026-08-19 の 10 巡目・
                            //   ユーザー指摘「人の体が全部黒いもので覆われ、自分の体は直接は
                            //   見られなくなり、黒いものが自分の腕などに合わせて動くことで分かる」）。
                            //   9 巡目までは**引き伸ばした本人の画素**を置いていたので、
                            //   明るい体は明るいまま残り、**人がそのまま見えていた**。
                            //   参考画像の人型も**黒く潰れている**（実測 5 分位 16 ／ 背景の中央値 207）。
                            //   ⇒ **芯は黒い塊で潰し、縁と外は黒い線だけがほつれる。**
                            //   自分だと分かるのは中身ではなく**動く形**（腕が人型に付いてくる）。
                            // ⚠ **芯を別に塗らない**（2026-08-19 の 11 巡目）。`duty` が既に
                            //   人型の中を 93% まで埋めるので、ここで塊を足すと線が消えてベタに戻る。
                            float amt = hit;
                            swapCol = 0.0;
                            swapMix = saturate(rowInk * amt);
                        }
                    }

                    // 糸は CG 層ではなく**映像の側**に描いたので、CG に残るのは実体だけ。
                    // ⚠⚠ **ほどけが来ていない所は実体のまま残す**（2026-08-19 に絵で確かめて直した）。
                    //   `_SwapReal` だけで消すと、**入れ替わりが始まった 1 フレーム目に当人が丸ごと
                    //   消える**（実体は CG 層に居て、糸はまだ前線のぶんしか出ていないため）。
                    //   体験者から見ると「ノイズに覆われて見えなくなる」ではなく「消えてから
                    //   ノイズが出る」になる。実機の 4 周目 A（人形が CG）でも同じ。
                    //   前線の後ろは糸が引き受け、前は実体が立っている。
                    float body = saturate(max(_SwapReal, 1.0 - frontHere));
                    aCg = sil * body;
                    rgbCg = cg.rgb * body;
                }

                // premultiplied over（Porter-Duff 1984）。straight alpha の lerp から変えたのは、
                // **影が「乗算」だから** — 影を rgb=0 / a=濃さ の断片として同じ RT に描けば、
                // この式が自動的に背景を (1-a) 倍する。不透明な人形（a=1）に対しては lerp と同値。
                float s = saturate(_CgStrength) * cgIn;
                col = col * (1.0 - aCg * s) + rgbCg * s;
                // ⚠ **ほどけは最後。** 映像も人形（実機）も代役（Editor）も、まとめてこの下に入る。
                col = lerp(col, swapCol, swapMix);

                // ================= ここから撮像の順（レンズ → センサ → ISP）=================
                // ⚠⚠ 旧実装は **現像 → レンズ → センサ** の逆順だった。だから
                //    「潰れた黒の上に砂が浮く」「角に後から黒い楕円を乗せた」絵になっていた
                //    （`canon/LEDGER.md` 0018 の「わざと加工してる感」）。順序は見た目の飾りではない。
                //    卓の FS_POST（shaders.js / common.js / pipeline.js）も同じ順序に揃えてある。

                // 3) レンズ — 周辺光量の落ち。**現像より前**（届いていない光は後段でも戻らない）。
                //    cos^4 に近い形（r^4）。放射 2 次だと中心付近から効いて「楕円を乗せた」に見える。
                //    夜間モードでは赤外の照射範囲だけが残るので、進みに応じて周辺がさらに落ちる。
                float vig = saturate(_Vignette + saturate(_Mono) * 0.16);
                col *= 1.0 - vig * 0.58 * r2 * r2;

                // 4) センサ — 粒。**トーンカーブの前**。後に置くと、暗部を締めた黒の上に
                //    最大量の砂が浮く（旧実装がそうだった）。
                {
                    // 刻むのは**ソース画素**。枠空間で刻むと、粗くなった画より粒の方が細かくなる
                    // ＝ 符号化は粒を真っ先に捨てるので物理的に起こりえない絵になる。
                    float2 srcUv = (sampleUv - 0.5) / max(_LiveScale.xy, 1e-3) + 0.5;
                    float2 srcPx = srcUv * max(_LiveTex_TexelSize.zw, float2(2.0, 2.0));
                    float grainPx = max(exp2(lod), 1.0);
                    float2 np = floor(srcPx / grainPx);
                    // 時刻は**映像が更新されたときだけ**進む（_Time だと 15fps の映像の上で粒だけ 90Hz）。
                    float t = frac(_SrcFrame * 0.0173) * 97.0;

                    half y = max(dot(col, half3(0.299, 0.587, 0.114)), 0.0);
                    // 光ショットノイズ（√信号）＋ 読み出しノイズ（信号に依らない床 = _NoiseDark）。
                    // 粗い画ほど符号化が粒を捨てるので、ブロックが大きいほど弱める。
                    // 夜間モードは増感で成り立つので、進むほど粒が増える。
                    // ⚠ 粗い画では符号化が粒を捨てるが、**捨て切らない**（pow 0.6）。
                    //   完全に消すと「のっぺりした低解像度」になり、暗視カメラの手触りが無くなる。
                    float amp = (sqrt(y) * 0.030 + _NoiseDark * 0.30)
                              * (1.0 + saturate(_Mono) * 2.6) / max(pow(grainPx, 0.6), 1.0);
                    if (amp > 0.0005)
                    {
                        col += (Hash21(np + t) - 0.5) * amp;
                        // 色ノイズは低周波の塊（デモザイクと NR で広がる）。輝度成分を抜いて色差だけ動かす。
                        float3 cn = float3(Hash21(floor(np * 0.5) + t * 1.7),
                                           Hash21(floor(np * 0.5) + t * 2.9 + 17.0),
                                           Hash21(floor(np * 0.5) + t * 4.1 + 41.0)) - 0.5;
                        cn -= (cn.r + cn.g + cn.b) * (1.0 / 3.0);
                        col += cn * amp * 1.6;
                    }
                    // 固定パターン（画素ごとの感度ばらつき）は**乗算**。明るい所ほど出て、黒には浮かない。
                    if (_NoiseFixed > 0.001)
                    {
                        float f = (Hash21(floor(srcPx)) - 0.5)
                                + (Hash21(floor(srcPx * 0.04)) - 0.5) * 1.8;
                        col *= 1.0 + f * _NoiseFixed;
                    }
                }

                // 5) ISP — 露出 → ホワイトバランス → トーン → 彩度。
                //    _ExposureBias だけは別系統（装置の自動露出が遅れて追いつくぶん）。
                // ⚠ 夜間モードは**増感**で成り立つ（だから粒が増える）。色を抜いて周辺を落とすだけだと
                //    真っ暗になって「装置が壊れた」ではなく「何も映っていない」になる。
                col = max(col, 0.0) * exp2(_Exposure + _ExposureBias + saturate(_Mono) * 0.95);
                col.r *= 1.0 + 0.25 * _Temperature;
                col.b *= 1.0 - 0.25 * _Temperature;
                // 色かぶり（緑↔マゼンタ）: 色温度と直交する軸。安物 CMOS + 蛍光灯の緑寄りを作る。
                col.g *= 1.0 + 0.25 * _Tint;
                col.r *= 1.0 - 0.12 * _Tint;
                col.b *= 1.0 - 0.12 * _Tint;
                // 夜間モードは中間調が減って白と黒に寄る（照らされた所と、届かない闇）。
                col = (col - 0.5) * (_Contrast + saturate(_Mono) * 0.10) + 0.5;
                // 黒浮き: **コントラストの後**に黒の床を上げる（前だと潰されて意味が無い）。
                col = col * (1.0 - _Lift) + _Lift;
                // 夜間モードでは赤外カットフィルタが外れるので**赤いものが明るく写る**。
                // ただの脱色（Rec.601）だと赤い着物が灰色に沈むが、実物の暗視映像では白く浮く。
                half luma = lerp(dot(col, half3(0.299, 0.587, 0.114)),
                                 dot(col, half3(0.52, 0.34, 0.14)), saturate(_Mono));
                // 彩度。_ChromaKill が立っていると**暗い所ほど色が死ぬ** — 安い ISP のノイズリダクションは
                // 暗部の色差から捨てるので、一様な脱色（＝フィルタ）ではなくこの形になる。
                half sat = _Saturation + max(1.0 - _Saturation, 0.0)
                                       * saturate(luma * 3.33) * saturate(_ChromaKill);
                // 周回で夜間モードへ落ちる。**最後は色がまったく無い**（`canon/LEDGER.md` 0019）。
                sat *= 1.0 - saturate(_Mono);
                col = lerp(luma.xxx, col, sat);

                if (_Scanline > 0.001)
                {
                    float s = 0.5 + 0.5 * sin(screenUv.y * _ScanlineCount * 3.14159265);
                    col *= 1.0 - _Scanline * (1.0 - s) * 0.6;
                }

                // 人形に付き従う劣化。**この作品に唯一無かったのが「対象に紐づく非一様な乱れ」**で、
                // 全域一様な加工はすべて機材のせいにできてしまう（＝安全に見える＝怖くない）。
                // 人形が動くと荒れも動くので、原因が機材ではなく世界の側にあることになる。
                float aura = 0.0;
                if (_ActorFocus.w > 0.001)
                {
                    float2 ad = (screenUv - _ActorFocus.xy) * float2(_FrameAspect, 1.0);
                    float ar = max(_ActorFocus.z, 0.01);
                    aura = saturate(_ActorFocus.w) * (1.0 - smoothstep(ar * 0.6, ar * 2.6, length(ad)));
                }
                if (aura > 0.001)
                {
                    half al = dot(col, half3(0.299, 0.587, 0.114));
                    col = lerp(col, al.xxx, aura * 0.6);   // そこだけ色が抜ける
                    col *= 1.0 - aura * 0.18;              // そこだけ沈む
                }

                // 著作した粒（post 12 項目。卓の FS_POST と同じ位置・同じ式）と、人形に付き従う荒れ。
                // **センサの粒はここではない**（上のレンズ→センサ→ISP の段に移した）。
                // _Grain は「装置の素性」ではなく作者が足す粒なので、既定 0 で使わない。
                {
                    float w = _Grain + aura * 0.10;
                    if (w > 0.001)
                        col += (Hash21(screenUv * 480.0 + frac(_SrcFrame * 0.0173)) - 0.5) * w;
                }

                // --- 演出の乱れ（色の成分）。位置ずれは既に sampleUv へ掛かっている ---
                // 帯ごとに濃さを変える。一様に混ぜると「フィルタ」に見えて伝送障害に見えない。
                if (_Glitch > 0.001)
                {
                    float g = saturate(_Glitch);
                    float t = floor(_GlitchSeed * 42.0);
                    float st = Hash21(screenUv * 300.0 + t);
                    float band = floor(screenUv.y * 24.0);
                    float bandPick = Hash21(float2(band + 5.0, t + 2.0));
                    float amt = g * 0.5 * step(1.0 - 0.75 * g, bandPick);
                    col = lerp(col, half3(st, st, st), saturate(amt));
                    col *= 1.0 - 0.22 * g * Hash21(float2(t, 9.3)); // 同期崩れの明滅
                }

                // ブラウン管に電源が入る（導入の段 3）。**映像はまだ無い**ので、ここで作るのは光そのもの。
                //
                // ⚠ 「ホログラムのように」を字義どおり作らない。青い線・走査の束・粒は
                //   `canon/LEDGER.md` 0011 / 0016 / 0018 で**3 回続けて「安っぽい」と判定された語彙**。
                //   代わりに**管が点く**（このスクリーンは既にブラウン管なので、装置の側に理由がある）。
                //
                // 段取りは実物の CRT と同じ順: 高電圧が乗って縁が光る → 走査が中央から上下へ開く →
                // 面が満ちる → 少し行き過ぎて落ち着く。
                // 映像そのものの出方。**管が点くこととは別の段**（段 3 で管が点き、段 4 で映像が出る）。
                // 実物のブラウン管も、電源を入れてから絵が出るまで暖機の時間がある。
                col *= saturate(_IntroLive);

                // ⚠⚠ **2026-08-13 に作り直した**（`canon/LEDGER.md` 0030
                //   「枠のホログラムの色が明るく、単色なのもなんか怖くないというかリアルじゃない」）。
                //   直したのは 3 つで、どれも「1 色の明るい板」を崩すためのもの:
                //     ① **色が強さで変わる**（弱い所は深い赤、強い所だけ白へ寄る＝ 実物の燐光）
                //     ② **収束のずれ**（チャンネルごとに横へずらす＝ 縁で色が割れる。点き始めが最大）
                //     ③ **焼けムラと明滅**（一様に光る面は板に見える／高電圧が安定するまで揺れる）
                //   加えて全体を **0.34 倍**へ落とした（旧 0.55 / 0.22）。
                if (_CrtIgnite < 0.999)
                {
                    float t = saturate(_CrtIgnite);

                    // ② 収束のずれ。**点き始めがいちばん大きく、安定すると揃う**（実物と同じ）。
                    float conv = 0.0065 * (1.0 - t);
                    float3 e = float3(CrtIgniteEnergy(screenUv + float2(conv, 0.0), t),
                                      CrtIgniteEnergy(screenUv, t),
                                      CrtIgniteEnergy(screenUv - float2(conv, 0.0), t));

                    // ③ 燐光の焼けムラ（低い周波数のなだらかな雑音）と、高電圧が落ち着くまでの明滅。
                    float uneven = 0.58 + 0.80 * ValueNoise21(screenUv * float2(4.3, 3.1));
                    float flick = 1.0 - 0.30 * (1.0 - t)
                                * Hash21(float2(floor(_Time.y * 31.0), 7.3));
                    e *= uneven * flick * 0.34;

                    // ⚠ **映像はここで制御しない**（`_IntroLive` の担当）。ここが作るのは光だけ。
                    // ① 色は強さで決まる。**暖色**（`canon/LEDGER.md` 0010）は保つ。
                    col += PhosphorColor(e.g * 3.0) * e;
                }

                // 点いた管の面がぼんやり光っている（映像が来るまでの間）。
                // ⚠ **上のブロックの外に置く**。あちらは「点いていく過程」なので `_CrtIgnite = 1` で
                //   走らなくなり、点き切った瞬間に光が消えて画が真っ黒になる（2026-08-13 に絵で見つけた）。
                //   映像が出るぶんだけ引く — 映像そのものが光になるので、足したままだと白く濁る。
                // ⚠ ここも一様にしない（同じ焼けムラを掛ける）。0.07 → 0.052 へ落とした。
                {
                    float tubeLit = saturate(_CrtIgnite) * (1.0 - saturate(_IntroLive));
                    if (tubeLit > 0.001)
                    {
                        float uneven = 0.58 + 0.80 * ValueNoise21(screenUv * float2(4.3, 3.1));
                        col += PhosphorColor(0.30) * (tubeLit * 0.052 * uneven);
                    }
                }

                // --- 信号ロスト砂嵐（FS_POST 一致規約の対象外・別系統）---
                // 手続き砂嵐へクロスフェード + 減光。強=1.0（配信断）/ 弱（トラッキングロスト）は低い値。
                // 演出の乱れより後に置く: 実際に信号が切れたら、演出が何をしていても障害表示が勝つ。
                //
                // ⚠⚠ **管の形（下のブロック）より前に置く。** 砂嵐は管の中で生まれるものなので、
                //   後ろに置くと**角の丸みも縁の暗さも上書きした四角い砂の板**になる
                //   （2026-08-13 に `menu intro` の絵で見つけた）。
                // ⚠⚠ **導入で管がまだ映像を出していない間は砂嵐も出さない**（`_IntroLive` で切る）。
                //   切らないと `_CrtIgnite` の点灯過程をまるごと上書きする ＝ カメラが繋がっていない
                //   現場では**段 0〜3 のあいだずっとスクリーンが砂嵐**で、闇の中で管が点く導入の山が
                //   1 度も画に出ない。`_IntroLive` は演出の外では 1 なので、本編・終幕は 1 ビットも変わらない。
                //   段 4 では live と同じ進みで砂嵐が入ってくる ＝「映像が出るはずの所に砂嵐が出た」。
                float sl = saturate(_SignalLost) * saturate(_IntroLive);
                if (sl > 0.001)
                {
                    float st = Hash21(screenUv * 320.0 + floor(_Time.y * 60.0)); // ~60Hz でざわつく砂嵐
                    half3 stat = half3(st, st, st);
                    col = lerp(col, stat, sl);
                    col *= 1.0 - 0.30 * sl; // 減光
                }

                // ブラウン管の面。**縁へ向かって落ち、角の外は黒**。
                // ヴィネット（レンズ）とは別のもの — あちらは光が届かない話で、こちらは管の形。
                // だから post の最後（切替の暗転より前）に、枠の座標で掛ける。
                if (_CrtEdge > 0.001 || _CrtRound > 0.001)
                {
                    float d = CrtSdf(screenUv);
                    // 縁の内側 _CrtEdgeWidth のあいだで暗くなる（管のガラスが厚くなる所）
                    float inner = saturate(-d / max(_CrtEdgeWidth, 1e-3));
                    col *= 1.0 - saturate(_CrtEdge) * (1.0 - inner) * (1.0 - inner);
                    // 角の外は黒。1 画素で切ると階段が出るので、画素幅で渡す
                    float aa = fwidth(d) * 1.2 + 1e-4;
                    col *= saturate(-d / aa);
                }

                // dip-to-black: 切替の一瞬だけ黒へ（CCTV の瞬断）。
                col *= 1.0 - saturate(_SwitchDim);

                // 終幕: 装置に届いている電力。**いちばん最後に掛ける** — 電池が切れるのは
                // 画の一部ではなく装置そのものなので、映像も砂嵐も管の縁も一緒に落ちる。
                // ⚠ 演出の外では 1 なので、本編・導入は 1 ビットも変わらない。
                col *= saturate(_ScreenPower);

                // ⚠⚠ **alpha は必ず 1**。パススルーの合成は `アプリの rgb + 現実 × (1 - alpha)` なので、
                //   ここを下げると**枠の中に現実が透ける**（canon/LEDGER.md 0005 が禁じたもの）。
                //   2026-08-13 に `_CrtIgnite` を書いてみたが、段 3（管が点く途中）で
                //   スクリーンの面に部屋が透けて出た。**管が点いていない間スクリーンが黒い矩形として
                //   立っている**問題（箱の面から約 2.0m 以上下がると箱の外へはみ出す）は実在するが、
                //   直すならここではない — 面の存在ごと消す側（Renderer）で解くこと。
                return half4(saturate(col), 1);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
