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
                float4 _LiveTex_TexelSize; // 同上。**ソースの実寸**が要る（粒をソース画素で刻むため）
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
                float _SrcFrame;
                float _ChromaKill;
                float _Glare;
                float _Mono;
                float _CrtRound;
                float _CrtEdge;
                float _CrtEdgeWidth;
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
                float liveIn;
                float2 uvL = ContainUv(RotateUvSteps(uv, _UvRotSteps), _LiveScale.xy, liveIn);
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
                if (_Echo > 0.001)
                {
                    half3 echo = SAMPLE_TEXTURE2D_LOD(_EchoTex, sampler_EchoTex, uvL, lod).rgb * liveIn;
                    live = lerp(live, echo, saturate(_Echo));
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
                return lerp(live, overlay, saturate(mask * _OverlayStrength));
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
                    // 人形にも**映像と同じ伝送の痩せ**を掛ける（装置を通して見えている以上、同じだけ落ちる）。
                    half4 cg = SampleCgSoft(uvC, lod);
                    cg.rgb = CgChromaMatched(cg, uvC, lod);
                    // premultiplied over（Porter-Duff 1984）。straight alpha の lerp から変えたのは、
                    // **影が「乗算」だから** — 影を rgb=0 / a=濃さ の断片として同じ RT に描けば、
                    // この式が自動的に背景を (1-a) 倍する。不透明な人形（a=1）に対しては lerp と同値。
                    float s = saturate(_CgStrength) * cgIn;
                    col = col * (1.0 - saturate(cg.a) * s) + cg.rgb * s;
                }

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
