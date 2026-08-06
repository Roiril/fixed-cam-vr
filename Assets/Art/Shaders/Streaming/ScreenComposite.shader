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
        _NoiseDark("Dark Noise (luma dependent)", Range(0, 0.5)) = 0
        _NoiseFixed("Fixed Pattern Noise", Range(0, 0.3)) = 0
        _ExposureBias("Exposure Bias (EV, AGC lag)", Range(-2, 2)) = 0
        _VignetteBias("Vignette Bias", Range(-0.5, 0.5)) = 0
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
            TEXTURE2D(_OverlayTex); SAMPLER(sampler_OverlayTex);
            TEXTURE2D(_MaskTex);    SAMPLER(sampler_MaskTex);
            // CG レイヤ（実カメラの双子の仮想カメラが描く人形）。スクリーン空間・アルファ = 被覆率。
            TEXTURE2D(_CgTex);      SAMPLER(sampler_CgTex);
            // 焼き付き / ホールド用に凍らせた 1 枚（CameraFeelFx が Graphics.CopyTexture で作る）。
            TEXTURE2D(_EchoTex);    SAMPLER(sampler_EchoTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _LiveScale;
                float4 _OverlayScale;
                float4 _CgScale;
                float4 _CgLens;
                float4 _CgFocalN;
                float4 _CgTex_TexelSize;   // Unity が自動で埋める (1/w, 1/h, w, h)
                float4 _OverlayGain;
                float4 _OverlayOffset;
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
                float _ExposureBias;
                float _VignetteBias;
                float _Echo;
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

            half4 SampleCgSoft(float2 uv) { return SampleCgTent(uv, _CgSoften); }

            // 実写だけが受けている「色の粗さ」を CG にも掛ける。
            //
            // 実写は JPEG 4:2:0 で**色差が半解像度**、そのうえ q40 の粗い量子化と暗所のカラーノイズ抑制で
            // 色がにじみ、彩度そのものも落ちている。CG はどれも受けていないので、同じ絵の中で
            // 人形だけ色が鮮鋭で濃い。実測（2026-08-07・無人プレート 3 台）で人形の胴の彩度は
            // 周囲の **3〜4 倍**あった。post の彩度は乗算なので比が保存され、**原理的に埋まらない**。
            //
            // 輝度は鮮鋭なまま、色差だけ広く均して倍率を掛ける。premultiplied のまま平均して a で割ると
            // 「a で重み付けした平均色」になるので、透明な周囲の色に引っ張られない。
            half3 CgChromaMatched(half4 cg, float2 uv)
            {
                if (cg.a <= 0.002) return cg.rgb;
                half3 sharp = cg.rgb / max(cg.a, 1e-4);
                half yS = dot(sharp, half3(0.299, 0.587, 0.114));
                half3 chroma = sharp - yS;
                if (_CgChromaBlur > 0.001)
                {
                    half4 wide = SampleCgTent(uv, _CgChromaBlur);
                    half3 blur = wide.rgb / max(wide.a, 1e-4);
                    chroma = blur - dot(blur, half3(0.299, 0.587, 0.114));
                }
                return max(yS + chroma * _CgChromaGain, 0.0) * cg.a;
            }

            float Hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            // 低解像度化。**撮像側が粗い**という表現なので、色ではなくサンプル位置を量子化する。
            // ブロックが正方に見えるよう縦は枠アスペクトで割る（枠は 16:9、映像は 4:3）。
            float2 PixelateUv(float2 uv)
            {
                if (_Pixelate <= 0.001) return uv;
                float bx = max(6.0, floor(lerp(400.0, 18.0, saturate(_Pixelate))));
                float by = max(4.0, floor(bx / max(_FrameAspect, 1e-3)));
                float2 b = float2(bx, by);
                return (floor(uv * b) + 0.5) / b;
            }

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
            half3 SampleBase(float2 uv)
            {
                float liveIn;
                float2 uvL = ContainUv(RotateUvSteps(uv, _UvRotSteps), _LiveScale.xy, liveIn);
                half3 live = SAMPLE_TEXTURE2D(_LiveTex, sampler_LiveTex, uvL).rgb * liveIn;

                // 凍らせた 1 枚を混ぜる。1.0 = 完全に止まって見える（ホールド）、
                // 小さい値 = 少し前の姿がそこに薄く残る（焼き付き）。
                // 動いていない画素は同じ値なので何も起きず、**動いたものの跡だけが残る**。
                if (_Echo > 0.001)
                {
                    half3 echo = SAMPLE_TEXTURE2D(_EchoTex, sampler_EchoTex, uvL).rgb * liveIn;
                    live = lerp(live, echo, saturate(_Echo));
                }

                float ovIn;
                float2 uvO = ContainUv(uv, _OverlayScale.xy, ovIn);
                half3 overlay = SAMPLE_TEXTURE2D(_OverlayTex, sampler_OverlayTex, uvO).rgb;
                // 色統計マッチング（卓が焼いた per-channel の gain/offset）。恒等なら何も起きない。
                overlay = saturate(overlay * _OverlayGain.rgb + _OverlayOffset.rgb) * ovIn;

                half mask = SAMPLE_TEXTURE2D(_MaskTex, sampler_MaskTex, uv).r;
                return lerp(live, overlay, saturate(mask * _OverlayStrength));
            }

            half4 frag(Varyings input) : SV_Target
            {
                // 枠の座標（post FX の空間。卓の FS_POST と一致させる側）と、
                // テクスチャを引く座標（低解像度化 → 乱れ の順に劣化させた側）を分ける。
                float2 screenUv = input.uv;
                float2 sampleUv = GlitchUv(PixelateUv(screenUv));

                // 1-2) ライブ × 差し替え素材。色収差は放射方向に RGB をずらす（端ほど強い）。
                half3 col;
                if (_Aberration > 0.001)
                {
                    float2 c = sampleUv - 0.5;
                    float r = saturate(length(c) * 2.0);
                    float2 off = c * (r * r) * _Aberration * 0.02;
                    col.r = SampleBase(sampleUv + off).r;
                    col.g = SampleBase(sampleUv).g;
                    col.b = SampleBase(sampleUv - off).b;
                }
                else col = SampleBase(sampleUv);

                // 2.5) CG レイヤ（人形）。**ポスト FX の前**に重ねるのが要点 —
                //      映像と同じ露出・彩度・ヴィネット・走査線・グレインを浴びて初めて
                //      「映像の中に居る」ように見える（後段に足すと必ず浮く）。
                //
                //      **ライブ映像と同じ contain-fit 枠に収める**（_CgScale = MjpegScreen.ContainScale）。
                //      旧実装は生の screenUv で枠いっぱいに描いており、4:3 の映像が 16:9 の枠へ
                //      レターボックスされている分（_LiveScale.x=0.75）だけ人形が水平 1.33 倍外側へずれていた
                //      ＝ 姿勢を完璧に測っても絶対に合わない（2026-07-27 監査 CRITICAL 1）。
                //      letterbox 帯に人形が出ないのは正しい挙動（映像の外に人形は居ない）。
                if (_CgStrength > 0.001)
                {
                    // 乱れ・低解像度化は人形にも同じだけ掛ける（映像だけが壊れて人形が無傷だと必ず浮く）。
                    float cgIn;
                    float2 uvC = ContainUv(sampleUv, _CgScale.xy, cgIn);
                    // 実レンズの歪みを CG にも掛ける。CG はピンホール、実映像は樽型に歪んでいるので、
                    // 掛けないと画面の端で必ずずれる（広角ほど大きい）。
                    // **除算モデル**（Fitzgibbon）: r_u = r_d / (1 + k1 r_d^2)。多項式モデルと違って
                    // 順・逆の両方に解析解があるので、卓の順投影（較正・ワイヤー重畳）とこの逆変換が
                    // **厳密に一致する**。互いに近似の逆だと画面端で食い違い、較正の検証が成立しない。
                    if (abs(_CgLens.x) > 1e-5)
                    {
                        float2 n = (uvC - _CgLens.yz) / max(_CgFocalN.xy, 1e-4);
                        uvC = _CgLens.yz + n / (1.0 + _CgLens.x * dot(n, n)) * _CgFocalN.xy;
                    }
                    half4 cg = SampleCgSoft(uvC);
                    cg.rgb = CgChromaMatched(cg, uvC);
                    // premultiplied over（Porter-Duff 1984）。straight alpha の lerp から変えたのは、
                    // **影が「乗算」だから** — 影を rgb=0 / a=濃さ の断片として同じ RT に描けば、
                    // この式が自動的に背景を (1-a) 倍する。不透明な人形（a=1）に対しては lerp と同値。
                    float s = saturate(_CgStrength) * cgIn;
                    col = col * (1.0 - saturate(cg.a) * s) + cg.rgb * s;
                }

                // 3) ポスト FX（web-compositor の FS_POST と数式・順序を一致させる）
                //    _ExposureBias だけは別系統（装置の自動露出が遅れて追いつくぶん）。
                //    卓の FS_POST には無いが、加算なので著作した _Exposure の意味は変わらない。
                col *= exp2(_Exposure + _ExposureBias);
                col.r *= 1.0 + 0.25 * _Temperature;
                col.b *= 1.0 - 0.25 * _Temperature;
                // 色かぶり（緑↔マゼンタ）: 色温度と直交する軸。安物 CMOS + 蛍光灯の緑寄りを作る。
                col.g *= 1.0 + 0.25 * _Tint;
                col.r *= 1.0 - 0.12 * _Tint;
                col.b *= 1.0 - 0.12 * _Tint;
                col = (col - 0.5) * _Contrast + 0.5;
                // 黒浮き: **コントラストの後**に黒の床を上げる（前だと潰されて意味が無い）。
                col = col * (1.0 - _Lift) + _Lift;
                half luma = dot(col, half3(0.299, 0.587, 0.114));
                col = lerp(luma.xxx, col, _Saturation);

                float2 dir = screenUv - 0.5;
                // 周辺光量は著作値 + 装置の追従ぶん（露出が動くと絞りも動く）。
                col *= saturate(1.0 - max(0.0, _Vignette + _VignetteBias) * dot(dir, dir) * 2.2);

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

                // 粒状。**暗部ほど強い**（実センサの SN は暗部で悪い）＝暗がりが物を隠せるようになる。
                // 固定パターンは時間項を持たない＝画面に貼り付いた汚れとして静止し、その中で
                // 動いているものだけが浮く（変化検出は差分で働く）。
                {
                    half gl = dot(col, half3(0.299, 0.587, 0.114));
                    float w = _Grain
                            + _NoiseDark * (1.0 - smoothstep(0.0, 0.5, gl))
                            + aura * 0.10;
                    if (w > 0.001)
                    {
                        float n = Hash21(screenUv * 480.0 + frac(_Time.y));
                        col += (n - 0.5) * w;
                    }
                }
                if (_NoiseFixed > 0.001)
                {
                    // 高周波（センサの画素ばらつき）+ 低周波（レンズの汚れ・ムラ）。
                    // 白色ノイズだけだと砂目にしか見えず「貼り付いた汚れ」にならない。
                    float f = (Hash21(screenUv * 260.0 + 17.0) - 0.5) * 0.6
                            + (Hash21(floor(screenUv * 42.0) + 3.0) - 0.5) * 0.4;
                    col += f * _NoiseFixed;
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

                // --- 切替 dip-to-black + 信号ロスト砂嵐（FS_POST 一致規約の対象外・別系統）---
                // 信号ロスト: 手続き砂嵐へクロスフェード + 減光。強=1.0（配信断）/ 弱（トラッキングロスト）は低い値。
                // 演出の乱れより後に置く: 実際に信号が切れたら、演出が何をしていても障害表示が勝つ。
                if (_SignalLost > 0.001)
                {
                    float st = Hash21(screenUv * 320.0 + floor(_Time.y * 60.0)); // ~60Hz でざわつく砂嵐
                    half3 stat = half3(st, st, st);
                    float sl = saturate(_SignalLost);
                    col = lerp(col, stat, sl);
                    col *= 1.0 - 0.30 * sl; // 減光
                }
                // dip-to-black: 切替の一瞬だけ黒へ（CCTV の瞬断）。
                col *= 1.0 - saturate(_SwitchDim);

                return half4(saturate(col), 1);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
