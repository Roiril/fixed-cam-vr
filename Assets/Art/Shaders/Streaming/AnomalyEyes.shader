// スクリーンの外の闇に開く**目**（canon/LEDGER.md 0075 / 0076）。
//
// 1 つの目 = 中心（体験者の頭）を向いた quad 1 枚。**形はここで描き、進み方は C# が決める**
//   （AnomalyEyesLogic が `_EyeBig` / `_EyeField` / `_EyeIntensity` を配る。OutroLogic.FlickerPower と同じ流儀）。
//
// ⚠⚠ **前乗算アルファ（Blend One OneMinusSrcAlpha）。** 2026-08-17 に加算から変えた（0076）。
//    加算では**重なった 2 つが必ず 1 つの塊に融ける**ので、参考画像のような密度にできない。
//    前乗算なら 1 パスで「体は隠す（アルファ）／細い光の線は足す（加算）」を両方書ける
//    ＝ 手前の目が奥の目を隠し、暈だけが重なる。
//    ⚠ 手前 / 奥は**メッシュの並び順**で決まる（座席表を小さい順に並べてある ＝ 大きい ＝ 近い目が後）。
//
// ⚠ **Queue は Background+100（1100）。** 本編のスクリーン（ScreenComposite = Geometry / 不透明）が
//    後から上書きするので、**目はスクリーンの外にしか出ない**（0075「スクリーンの外の黒い背景を」）。
//
// ⚠ **実行時 Shader.Find で引く。** ProjectSettings/GraphicsSettings.asset の Always Included に
//    登録してある（外すと Editor では出て実機だけ剥がれる — rules/unity-vr.md の 2026-07-31 実害）。
Shader "FixedCamVr/AnomalyEyes"
{
    Properties
    {
        // C# が毎フレーム書く（Inspector の値は Editor プレビューの初期値でしかない）。
        _EyeBig("Big Eye Open", Range(0, 1)) = 0
        _EyeField("Field Open", Range(0, 1)) = 0
        _EyeSpan("Swarm Span", Range(0.005, 1)) = 0.035
        _EyeDensity("Density", Range(0, 1)) = 1
        _EyeFade("Fade", Range(0, 1)) = 0
        _EyeIntensity("Intensity", Range(0, 1)) = 0
        _EyeTime("Time", Float) = 0
        _EyeGain("Gain", Range(0, 2)) = 1
        _EyeBlink("Blink Amount", Range(0, 1)) = 1
        _EyeColor("Sclera", Color) = (1.0, 0.93, 0.84, 1)
        _EyeRim("Rim (outer)", Color) = (1.0, 0.62, 0.30, 1)
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Background+100" }
        LOD 100

        Pass
        {
            Name "AnomalyEyes"
            Blend One OneMinusSrcAlpha     // 前乗算アルファ（体は隠す / 暈は足す）
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            // ⚠⚠ **Single Pass Instanced（Quest の既定）で両眼へ正しく描くために要る。**
            //    無いと Editor では出るのに**実機で片眼にしか出ない / 位置がずれる**。
            //    プレビューは単眼カメラなので、この誤りは絵からは絶対に分からない。
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _EyeBig;
                float _EyeField;
                float _EyeSpan;
                float _EyeDensity;
                float _EyeFade;
                float _EyeIntensity;
                float _EyeTime;
                float _EyeGain;
                float _EyeBlink;
                float4 _EyeColor;
                float4 _EyeRim;
            CBUFFER_END

            // ---- 目の形（すべて quad ローカル -1..1）------------------------------------
            // 目が quad の中で占める割合。残りは暈のための余白。
            #define SHAPE_SCALE 0.74
            // 瞼の弧の半径（1 より少し大きい）。**2 つの円弧の差**で描くので目尻が尖る
            //（べき乗だと接線が垂直になり、両端が丸い「目玉焼き」になる。実際に一度焼けた）。
            #define LID_ARC 1.10
            // 縁のゆらぎ。ゆっくりした歪み ＋ 細かい粗さの 2 段。
            #define LID_WARP  0.045
            #define LID_GRAIN 0.018
            // 粗い粒で縁を欠けさせる（版画・掠れ）。⚠ **細かいノイズにしない** —
            // VR ではちらつきとモアレになる。数画素の塊として量子化する。
            // ⚠ 実機は 1 度あたりの画素がこのプレビューの約 2 倍あるので、**ここで細かく見えるくらいが実機で丁度**。
            #define DROP_CELLS 44.0
            #define DROP_EDGE  0.22     // 縁の帯でどれだけ欠けるか（参考画像は 10〜25%）
            // 縁とみなす帯の幅（**目の中央での高さ**に対する比）。
            // ⚠⚠ **その場の高さで割ってはいけない。** 目尻へ向かって高さが 0 に近づくので、
            //    帯が両端を丸ごと飲み込み、**目が粒に爆散する**（実際にそうなった）。
            #define DROP_BAND  0.26
            // 開きかけは**断片**にする（参考 me1 — 完成した目が現れるのではなく、
            // 弧や点が闇から現れて瞼と虹彩へ繋がる）。
            #define DROP_EARLY 0.80
            // 輪郭の線。内側 生成り → 外側 橙 の 2 段（me3 の多重輪郭を暖色でやる）。
            #define RIM_INNER 0.30
            #define RIM_OUTER 0.12
            // 暈。⚠ 広いと**闇に灯った投光器**に見える。輪郭の外 1° 以内に収める。
            #define GLOW_GAIN 0.055
            #define GLOW_FALL 44.0
            // 瞬き（周期の逆数 / 鋭さ）。9 秒に 1 度・0.22 秒。**大きい目には掛けない**
            //（あちらは C# が段の中で 1 度だけ瞬かせる）。
            #define BLINK_RATE 0.11
            #define BLINK_SHARP 40.0

            float h11(float p) { p = frac(p * 0.1031); p *= p + 33.33; p *= p + p; return frac(p); }
            float h21(float2 p)
            {
                float3 p3 = frac(p.xyx * 0.1031);
                p3 += dot(p3, p3.yzx + 33.33);
                return frac((p3.x + p3.y) * p3.z);
            }
            float n11(float x)
            {
                float i = floor(x), f = frac(x);
                f = f * f * (3.0 - 2.0 * f);
                return lerp(h11(i), h11(i + 1.0), f);
            }

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                float4 attr : TEXCOORD1;    // (順位, 大きい目か, 種, 籤)
                float4 form : TEXCOORD2;    // (縦横比, 上瞼, 下瞼, 虹彩半径)
                float4 form2 : TEXCOORD3;   // (開き切る量, 目尻の傾き, 瞳孔のずれ, 大きさ 0..1)
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 attr : TEXCOORD1;
                float4 form : TEXCOORD2;
                float4 form2 : TEXCOORD3;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionHCS = TransformObjectToHClip(input.positionOS.xyz);
                o.uv = input.uv;
                o.attr = input.attr;
                o.form = input.form;
                o.form2 = input.form2;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                float rank = i.attr.x;
                float isBig = i.attr.y;
                float seed = i.attr.z;
                float presence = i.attr.w;
                float aspect = i.form.x;      // quad の 縦/横
                float lidUp = i.form.y;
                float lidDn = i.form.z;
                float irisR = i.form.w;
                float openMax = i.form2.x;
                float skew = i.form2.y;
                float pupOff = i.form2.z;
                float sizeN = i.form2.w;      // 0 = 小さい目 / 1 = 視界を埋める目

                // 籤に外れた目は最初から居ない（大きい目は籤に関係なく必ず出る）。
                if (isBig < 0.5 && presence > _EyeDensity) discard;

                // 開き具合。**AnomalyEyesLogic.EyeOpen と同じ式**（片方だけ直すと、
                // 数えた本数（eyesN）と画が黙って食い違う）。
                float span = max(_EyeSpan, 0.005);
                // prog = **開く動きの進み**（0..1）。open = 実際の開き（個体差で 1 まで行かない）。
                // ⚠⚠ この 2 つを混ぜない。断片（欠け）は prog で決める —
                //    open で決めると、半開きで止まる目が**永久に虫食いのまま**になる（実際になった）。
                float prog = saturate((_EyeField * (1.0 + span) - rank) / span);
                prog = lerp(prog, _EyeBig, isBig);
                float open = prog * lerp(openMax, 1.0, isBig);

                // 瞬き。⚠ **動かすものは 1 つに絞る**（開眼が主・瞬きは稀・震えは強度が上がったときだけ）。
                if (isBig < 0.5)
                {
                    float bp = frac(_EyeTime * BLINK_RATE + seed);
                    open *= 1.0 - _EyeBlink * saturate(1.0 - abs(bp - 0.5) * BLINK_SHARP);
                }
                open *= 1.0 - 0.05 * _EyeIntensity * (n11(_EyeTime * 5.3 + seed * 31.0) * 2.0 - 1.0);

                if (open <= 0.002 || _EyeFade <= 0.002) discard;

                float2 p = i.uv;

                // 瞼の弧。目尻で厳密に 0・傾きは有限 ＝ 尖る。
                float pu = p.x / SHAPE_SCALE;
                float base = sqrt(max(LID_ARC * LID_ARC - 1.0, 1e-4));
                float lid = (sqrt(max(LID_ARC * LID_ARC - pu * pu, 0.0)) - base) / (LID_ARC - base);
                lid = saturate(lid);
                lid *= 1.0 + lid * (LID_WARP * (n11(pu * 1.7 + seed * 17.0) * 2.0 - 1.0)
                                    + LID_GRAIN * (n11(pu * 11.0 + seed * 23.0) * 2.0 - 1.0));

                // 目尻の高さ違い（片方を吊る）。上下を同じだけずらすので形は崩れない。
                float tilt = skew * pu * lid * SHAPE_SCALE;
                float up = lidUp * open * lid * SHAPE_SCALE + tilt;
                float dn = -lidDn * open * lid * SHAPE_SCALE + tilt;

                float aa = max(fwidth(p.y), 1e-4) * 1.2;
                float cover = smoothstep(-aa, aa, up - p.y) * smoothstep(-aa, aa, p.y - dn);
                // ⚠⚠ **画素より細い帯は、その細さのぶんだけ薄くする。**
                //    これが無いと目尻から左右へ 1 画素の白い線が伸びる（髭のように見えた）。
                cover *= saturate((up - dn) / max(2.0 * aa, 1e-5));

                // 縁からの距離（0 = 縁 / 1 = 帯の内側）。輪郭・欠け・暈の全部がこれを読む。
                // ⚠ 基準は**目の中央での高さ**（その場の高さではない） ＝ 帯の幅が全体で一定になる。
                float half_ = max(0.5 * (up - dn), 1e-5);
                float halfMax = max(0.5 * (lidUp + lidDn) * open * SHAPE_SCALE, 1e-5);
                float dEdge = saturate(min(up - p.y, p.y - dn) / (halfMax * DROP_BAND));

                // 粗い粒で縁を欠けさせる。開きかけは**全体が断片**になる（闇から弧が現れる）。
                // ⚠ 粒の大きさは**画面での大きさ**を揃える（大きい目ほど細かく刻む）。
                //    刻みを一定にすると、視界を埋める目だけ粒が巨大な市松模様になる。
                float cells = DROP_CELLS * lerp(0.6, 2.6, sizeN);
                float2 cell = floor(p * cells / SHAPE_SCALE + seed * 7.0);
                float grain = h21(cell);
                float dropAmt = saturate((1.0 - dEdge) * DROP_EDGE + (1.0 - prog) * DROP_EARLY);
                // ⚠⚠ **目尻では欠けさせない。** 高さが 1 セルを下回る所で欠けさせると、
                //    先細りが階段状の塊に砕ける（実際に 2 度そうなった）。尖りは残す。
                dropAmt *= smoothstep(0.12, 0.42, lid);
                cover *= step(dropAmt, grain);
                if (cover <= 0.002) discard;

                // 虹彩と瞳孔。**真円で描く**（quad は横長なので縦を aspect で伸ばして測る）。
                // 大きい目ほど虹彩が白目を食う ＝ 参考 me3 の「巨大な虹彩と黒い内部」が自動的に出る。
                float cy = 0.5 * (lidUp - lidDn) * open * SHAPE_SCALE;
                float ir = irisR * (0.45 + 0.55 * open) * SHAPE_SCALE;
                float2 q = float2(p.x, (p.y - cy) * aspect) / max(ir, 1e-3);
                float d = length(q);
                float irisMask = 1.0 - smoothstep(0.90, 1.02, d);
                // 瞳孔は真円にしない（縦に潰れ、少しずれる）。
                float2 pq = float2(q.x - pupOff, q.y * (1.25 + 0.5 * sizeN));
                float pupil = 1.0 - smoothstep(0.40, 0.50, length(pq));
                pupil *= 1.0 - 0.35 * _EyeIntensity;    // 凝視されると瞳孔が縮む

                // 虹彩の彫り。粒が主・輪が従（版画のように掻き取った虹彩）。
                float grit = h21(floor(q * lerp(15.0, 36.0, sizeN) + seed * 7.0));
                float rings = 0.5 + 0.5 * sin(d * 11.0 + seed * 9.0 + grit * 3.0);
                float etch = saturate(grit * 0.75 + rings * 0.45 - 0.22);
                float iris = lerp(0.02, 0.30, etch * etch);
                iris *= 1.0 - smoothstep(0.72, 1.0, d);   // ⚠ smoothstep は edge0 < edge1

                // 白目。上瞼の影 ＋ 粗い斑（均一な面は「貼った紙」に見える）。
                float lum = 1.0 - 0.30 * smoothstep(dn, up, p.y);
                lum *= 0.93 + 0.07 * h21(floor(p * lerp(9.0, 26.0, sizeN) - seed * 5.0));
                lum = lerp(lum, iris, irisMask);
                lum = lerp(lum, 0.0, pupil * irisMask);

                // 輪郭。内側 生成り → 外側 橙 の 2 段（me3 の多重輪郭を暖色でやる）。
                float rimIn = 1.0 - smoothstep(0.0, RIM_INNER, dEdge);
                float rimOut = 1.0 - smoothstep(0.0, RIM_OUTER, dEdge);
                float3 rimCol = lerp(_EyeRim.rgb, _EyeColor.rgb, saturate(rimOut * 1.2));

                float3 body = _EyeColor.rgb * lum;
                // ⚠ 目尻では輪郭を混ぜない（細い所を橙で塗ると、両端だけ色が違う目になる）。
                body = lerp(body, rimCol, rimIn * (0.45 + 0.35 * sizeN) * smoothstep(0.10, 0.40, lid));

                // 闇に滲む暈。**形の外側だけ**・輪郭の外 1° 以内。
                float mid = 0.5 * (up + dn);
                float dv = max(abs(p.y - mid) - half_, 0.0);
                float du = max(abs(p.x) - SHAPE_SCALE, 0.0);
                float glow = exp(-length(float2(du, dv)) * GLOW_FALL) * (1.0 - cover) * open;
                // quad の縁で必ず 0 にする（残すと面の境目が斜めの線として見える）。
                glow *= saturate((1.0 - max(abs(p.x), abs(p.y))) / 0.14);

                float a = cover * _EyeFade;
                // 前乗算: 体は a ぶん隠して足す / 暈は隠さずに足すだけ。
                float3 col = body * a + _EyeColor.rgb * (glow * GLOW_GAIN * _EyeFade);
                return half4(col * _EyeGain, a);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
