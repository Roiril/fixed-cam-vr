// スクリーンの外の闇に開く**目**（canon/LEDGER.md 0072）。
//
// 1 つの目 = 中心（体験者の頭）を向いた quad 1 枚。**形はここで描き、進み方は C# が決める**
//   （AnomalyEyesLogic が `_EyeBig` / `_EyeField` / `_EyeIntensity` を配る。OutroLogic.FlickerPower と同じ流儀）。
//
// ⚠ **加算合成（Blend One One）。** 目は互いに重なるので、半透明で描くと並べ替えが要る。
//    加算なら描く順序に依らず同じ絵になる ＝ 走行ごとに違う画にならない。
//    闇（黒）の上に光を足すだけなので、瞳孔や虹彩の暗い所は「足さない」で表せる。
//
// ⚠ **Queue は Background+100（1100）。** 本編のスクリーン（ScreenComposite = Geometry / 不透明）が
//    後から上書きするので、**目はスクリーンの外にしか出ない**（0072「スクリーンの外の黒い背景を」）。
//    ここを Transparent へ動かすと、目がスクリーンの上に乗って装置の映像を汚す。
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
        _EyeSpan("Swarm Span", Range(0.01, 1)) = 0.28
        _EyeDensity("Density", Range(0, 1)) = 1
        _EyeFade("Fade", Range(0, 1)) = 0
        _EyeIntensity("Intensity", Range(0, 1)) = 0
        _EyeTime("Time", Float) = 0
        _EyeGain("Gain", Range(0, 2)) = 0.85
        _EyeBlink("Blink Amount", Range(0, 1)) = 1
        _EyeAspect("Quad Aspect (h/w)", Float) = 1
        _EyeColor("Color", Color) = (1.0, 0.93, 0.84, 1)
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Background+100" }
        LOD 100

        Pass
        {
            Name "AnomalyEyes"
            Blend One One
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
                float _EyeAspect;
                float4 _EyeColor;
            CBUFFER_END

            // ---- 目の形（すべて quad ローカル -1..1）------------------------------------
            // 上瞼は下瞼より大きく上がる（60:40）。左右対称に開くと「目」ではなく「口」に見える。
            #define LID_UP   0.60
            #define LID_DOWN 0.40
            // 瞼の弧の半径（1 より少し大きい）。**2 つの円弧の差**で描くので、目尻が
            // ⚠ **尖る**（べき乗 pow(1-u²,p) だと目尻の接線が垂直になり、両端が丸い「目玉焼き」になる。
            //    最初そう書いて実際に目玉焼きが焼けた）。
            #define LID_ARC 1.10
            // 縁のゆらぎ（筆で引いた線の粗さ）。**ゆっくりした歪み ＋ 細かい粗さ**の 2 段。
            // ⚠ 1 段で高い周波数にすると縁が**パイの縁飾り**になる（実際に一度そうなった）。
            #define LID_WARP  0.030
            #define LID_GRAIN 0.012
            // 虹彩の半径（quad の**横**半分を 1 とする。縦横比の補正込みで真円になる）。
            // ⚠ 小さいと白目ばかりの目玉焼きになる。参考画像（me1 / me2）の虹彩は目の高さの 7 割ある。
            #define IRIS_R 0.32
            // 瞳孔（虹彩に対する比）。
            #define PUPIL_R 0.44
            // 目そのものが quad の中で占める割合。**残りは暈のための余白**。
            // ⚠ メッシュ側は「目の見かけの大きさ ÷ これ」で quad を張るので、
            //    ここを変えても目の大きさ（sizeDeg）は変わらない。
            #define SHAPE_SCALE 0.74
            // 闇に浮かぶ暈。**闇の中では硬い縁の面は「貼った紙」に見える**。
            // ⚠ 強いと**闇に灯った投光器**に見える（一度そうなった）。輪郭の外 1〜2° に収める。
            #define GLOW_GAIN 0.10
            #define GLOW_FALL 16.0
            // 瞬き（周期の逆数 / 鋭さ）。9 秒に 1 度・0.22 秒。
            #define BLINK_RATE 0.11
            #define BLINK_SHARP 40.0
            // 縁の色ずれの量（強度が最大のとき）。連絡の面の「赤とシアンへ分離」と同じ語彙で、
            // ⚠ 参考画像 me3 のネオンはここまで。**全体を極彩色にはしない**（作品の色は暖色 — LEDGER 0010）。
            #define FRINGE_GAIN 0.16

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
                float4 attr : TEXCOORD1;   // (順位, 大きい目か, 種, 籤)
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 attr : TEXCOORD1;
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
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float rank = i.attr.x;
                float isBig = i.attr.y;
                float seed = i.attr.z;
                float presence = i.attr.w;

                // 籤に外れた目は最初から居ない（大きい目は籤に関係なく必ず出る）。
                if (isBig < 0.5 && presence > _EyeDensity) discard;

                // 開き具合。**AnomalyEyesLogic.EyeOpen と同じ式**（片方だけ直すと、
                // 数えた本数（eyesN）と画が黙って食い違う）。
                float span = max(_EyeSpan, 0.01);
                float open = saturate((_EyeField * (1.0 + span) - rank) / span);
                open = lerp(open, _EyeBig, isBig);

                // 瞬き。目ごとに位相が違うので、群れの中で不規則に見える。
                float bp = frac(_EyeTime * BLINK_RATE + seed);
                open *= 1.0 - _EyeBlink * saturate(1.0 - abs(bp - 0.5) * BLINK_SHARP);
                // 震え。強度が上がったときだけ、開き切った目がわずかに脈打つ。
                open *= 1.0 - 0.05 * _EyeIntensity * (n11(_EyeTime * 5.3 + seed * 31.0) * 2.0 - 1.0);

                if (open <= 0.002 || _EyeFade <= 0.002) discard;

                float2 p = i.uv;

                // 瞼の弧。**2 つの円弧の差**（端で厳密に 0・傾きは有限）なので目尻が尖る。
                float pu = p.x / SHAPE_SCALE;
                float arc = LID_ARC;
                float base = sqrt(max(arc * arc - 1.0, 1e-4));
                float lid = (sqrt(max(arc * arc - pu * pu, 0.0)) - base) / (arc - base);
                lid = saturate(lid);
                // 筆の粗さ。**目尻では揺らさない**（lid を掛ける）ので、尖りは残る。
                lid *= 1.0 + lid * (LID_WARP * (n11(pu * 1.7 + seed * 17.0) * 2.0 - 1.0)
                                    + LID_GRAIN * (n11(pu * 11.0 + seed * 23.0) * 2.0 - 1.0));

                float up = LID_UP * open * lid * SHAPE_SCALE;
                float dn = -LID_DOWN * open * lid * SHAPE_SCALE;
                float aa = max(fwidth(p.y), 1e-4) * 1.2;
                float inside = smoothstep(-aa, aa, up - p.y) * smoothstep(-aa, aa, p.y - dn);
                // ⚠⚠ **画素より細い帯は、その細さのぶんだけ薄くする。**
                //    2 つの smoothstep は上下の縁が重なった所（＝目尻の外・閉じた目）でも 0.25 を返すので、
                //    これが無いと**目尻から左右へ 1 画素の白い線が伸びる**（髭のように見えた）。
                inside *= saturate((up - dn) / max(2.0 * aa, 1e-5));

                // 闇に滲む暈。形の外側だけに乗る（内側は白目そのものが明るい）。
                float mid = 0.5 * (up + dn);
                float dv = max(abs(p.y - mid) - 0.5 * (up - dn), 0.0);
                float du = max(abs(p.x) - SHAPE_SCALE, 0.0);
                float glow = exp(-length(float2(du, dv)) * GLOW_FALL) * (1.0 - inside) * open;
                // ⚠ quad の縁で必ず 0 にする。残すと**面の境目が斜めの線として見える**（一度出た）。
                glow *= saturate((1.0 - max(abs(p.x), abs(p.y))) / 0.14);
                if (inside <= 0.001 && glow <= 0.004) discard;

                // 虹彩と瞳孔。**真円で描く**（quad は横長なので縦を _EyeAspect で伸ばして測る）。
                // 開くほど大きく見える（開きかけは白い隙間 ＝ 闇の中で見つけやすい）。
                float cy = 0.5 * (LID_UP - LID_DOWN) * open * SHAPE_SCALE;
                float ir = IRIS_R * (0.45 + 0.55 * open) * SHAPE_SCALE;
                float2 q = float2(p.x, (p.y - cy) * _EyeAspect) / max(ir, 1e-3);
                float d = length(q);
                float irisMask = 1.0 - smoothstep(0.90, 1.02, d);
                float pupilMask = 1.0 - smoothstep(PUPIL_R - 0.06, PUPIL_R + 0.06, d);
                // 強度が上がると瞳孔が縮む（「凝視されている」の生理）。
                pupilMask *= 1.0 - 0.35 * _EyeIntensity;

                // 虹彩の彫り。**粒が主・輪が従**（参考画像 me1 / me2 の、版画のように掻き取った虹彩）。
                // ⚠ 放射の筋を強く入れると**歯車か花**に見える（一度そうなった）。輪も粒で崩す。
                float grit = h21(floor(q * 17.0 + seed * 7.0));
                float rings = 0.5 + 0.5 * sin(d * 11.0 + seed * 9.0 + grit * 3.0);
                float etch = saturate(grit * 0.75 + rings * 0.45 - 0.22);
                float iris = lerp(0.02, 0.30, etch * etch);
                // 虹彩の縁は必ず暗い（実物の角膜輪。無いと虹彩が「模様」に見える）。
                iris *= 1.0 - smoothstep(0.72, 1.0, d);   // ⚠ smoothstep は edge0 < edge1

                float lum = 1.0;
                // 上瞼の影。白目が真っ平らだと紙に描いた記号に見える。
                // ⚠ smoothstep は edge0 < edge1 でなければ壊れる（up > dn なので引き算で書く）。
                lum *= 1.0 - 0.30 * smoothstep(dn, up, p.y);
                lum = lerp(lum, iris, irisMask);
                lum = lerp(lum, 0.0, pupilMask * irisMask);
                lum *= inside;

                float3 col = _EyeColor.rgb * (lum + glow * GLOW_GAIN);

                // 縁の色ずれ。左が赤・右がシアンへ割れる（信号が壊れている合図）。
                float band = saturate(1.0 - min(up - p.y, p.y - dn) / max(0.10 * open, 1e-3)) * inside;
                float3 fringe = lerp(float3(1.0, 0.25, 0.30), float3(0.25, 0.95, 1.0), step(0.0, p.x));
                col += fringe * band * (_EyeIntensity * FRINGE_GAIN);

                // ⚠ **alpha は 0。** 加算合成では alpha も足されるので、1 を返すと
                //    パススルーが出ている場面（位置合わせ・導入）で現実に穴を塞いでしまう。
                //    闇に光を足すだけなら alpha を触る理由が無い。
                return half4(col * _EyeGain * _EyeFade, 0.0);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
