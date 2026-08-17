// 歩行誘導の**指定ポイント**（canon/LEDGER.md 0079）。床に置いた同心の弧の輪。
// 参考画像（HUD の同心弧）から採ったのは **半径の違う弧が別々の速さで回る**という形で、
// 色は作品の暖色（0010）。
//
// quad 1 枚（WalkGuide.cs が円の中心へ置く）。uv 0..1 → p = uv*2-1、
// **p の長さ 1 が判定の円の QUAD_K 倍**（下の定数と WalkGuide.QuadK は対で直す）。
//
// ⚠⚠ **描く輪と判定の円は同じ半径。** 見た目だけ大きく／小さくすると、
//    「そこへ立て」という指示と実際に始まる場所が食い違う ＝ 装置が嘘をつく。
//    主の輪（RING_0）が判定の円そのもの。他の弧は飾り。
//
// ⚠⚠ **alpha は 0 を返す**（加算合成で現実に穴を塞がない — WalkGuideArrow と同じ）。
//
// ⚠ **実行時 Shader.Find で引く** → Always Included に登録済み（rules/unity-vr.md）。
Shader "FixedCamVr/WalkGuideRing"
{
    Properties
    {
        _Color("Ink", Color) = (1.0, 0.72, 0.42, 1)
        _Reveal("Ring Draw", Range(0, 1)) = 1
        _Fade("Fade", Range(0, 1)) = 1
        _Spin("Spin Time", Float) = 0
        _Arrive("Arrive", Range(0, 1)) = 0
        _Gain("Gain", Range(0, 4)) = 1.5
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent+461" }
        LOD 100

        Pass
        {
            Name "WalkGuideRing"
            Blend One One
            ZWrite Off
            // ⚠ 床の印なので**深度に従う**（スクリーンの向こう側へ回った所は隠れる）。
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float _Reveal;
                float _Fade;
                float _Spin;
                float _Arrive;
                float _Gain;
            CBUFFER_END

            // quad の半幅 ÷ 判定の円の半径。**WalkGuide.QuadK と同じ値**（片方だけ直すと輪がずれる）。
            #define QUAD_K 1.42
            #define TAU 6.2831853

            // 弧の縁のぼけ（半径方向 / 角度方向）。
            #define R_SOFT 0.012
            #define A_SOFT 0.020

            // ---- 開き方（登場）----
            // ⚠⚠ **中心から広がりながら、少しだけ回って止まる**（2026-08-17・ユーザー赤入れ 4
            //    「円も中心から、少し回転しながらかっこよく徐々に出るようにして」）。
            //    それまでは弧が最初から所定の半径に居て、角度方向へ引かれるだけだった
            //    ＝ 中心から出ないし、出るときに回らない。
            // ⚠ 角度方向の描き出し（旧 `sweep`）は**やめた**。半径の成長と同時にやると
            //    「3 つのことが起きている」になって、どれも読めない。
            // 弧 1 段ぶんの時差。**内側から順に**開く。
            // ⚠ `段の数 - 1`（4）× これ ＋ OPEN_SPAN が 1 を超えると、
            //    いちばん外の弧が開き切らないまま段が終わる。
            #define OPEN_STAGGER 0.085
            #define OPEN_SPAN    0.66   // 1 本が開き切るまで（reveal のうちの割合）
            #define OPEN_W_MIN   0.30   // 生まれた瞬間の帯の細さ（点から出るように見せる）
            #define OPEN_TURN    0.30   // 開くあいだに余分に回る量（回転数）。着地で 0 になる
            // 生まれた瞬間の弧の長さの割合。
            // ⚠ **1 のままだと、中心に集まった 5 本が重なって「輪」ではなく染みに見える**（実測）。
            //   短い弧が回りながら伸びる形にすると、小さいうちも意匠として読めて、
            //   回っていることも同時に読める。
            #define OPEN_DUTY_MIN 0.45

            // 1 本の弧。R = 半径（判定の円 = 1）/ W = 半幅 / N = 切れ目の数 / DUTY = 描く割合 /
            // SPIN = 回る速さ（毎秒何周）/ PH = 位相 / A = 濃さ / ORD = 開く順（内側から 0）
            float arc(float r, float ang, float R, float W, float N, float DUTY,
                      float SPIN, float PH, float A, float ORD, float reveal, float arrive)
            {
                // 開き（0 = 中心の点 / 1 = 所定の位置）。
                float g = saturate((reveal - ORD * OPEN_STAGGER) / OPEN_SPAN);
                if (g <= 0.0) return 0.0;
                g = 1.0 - pow(1.0 - g, 3.0);        // 速く広がって、すっと着地する

                // 半径の帯。生まれたては中心の細い点で、広がりながら太る。
                float rad = R * g;
                float w = W * lerp(OPEN_W_MIN, 1.0, g);
                float band = 1.0 - smoothstep(w - R_SOFT, w + R_SOFT, abs(r - rad));
                if (band <= 0.0) return 0.0;

                // 切れ目。**着いたら埋まって 1 本の輪になる**。
                // 開いている最中は短く（上の OPEN_DUTY_MIN）。
                float duty = lerp(DUTY, 1.0, arrive) * lerp(OPEN_DUTY_MIN, 1.0, g);
                // ⚠ 余分な回転は着地（g=1）で 0 になるので、そのまま定常回転へ繋がる
                //   （止まってから回り出すと、開きと回転が別の出来事に見える）。
                //   向きは定常回転と揃える ＝ 内と外が逆へ回りながら開く。
                float turn = (1.0 - g) * OPEN_TURN * sign(SPIN);
                float seg = frac(ang / TAU * N + PH + SPIN * _Spin + turn * N);
                float on = smoothstep(0.0, A_SOFT * N, seg) *
                           (1.0 - smoothstep(duty - A_SOFT * N, duty, seg));
                if (duty >= 0.999) on = 1.0;

                return band * on * A * g;
            }

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionHCS = TransformObjectToHClip(input.positionOS.xyz);
                o.uv = input.uv;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                float2 p = i.uv * 2.0 - 1.0;
                float r = length(p) * QUAD_K;
                float ang = atan2(p.y, p.x) + 3.14159265;   // 0..TAU

                float rev = saturate(_Reveal);
                float arv = saturate(_Arrive);

                // ⚠ 参考画像（HUD の同心弧）の骨格は **太い塊の弧 ＋ 細い線の弧** の対比。
                //   太さを揃えると「輪が何重にもある」だけになり、装置の意匠に見えない。
                // ⚠⚠ **開く順（末尾から 2 つ目の引数）は半径の順**。中心から外へ波及して見せる。
                //    意匠の重要さで並べると、内と外が入り混じって「中心から」が読めない。
                // 内の太めの弧（ゆっくり）
                float a = arc(r, ang, 0.72, 0.026, 2.0, 0.50, 0.17, 0.62, 0.55, 0.0, rev, arv);
                // 内の細い線（速く逆へ回る）
                a += arc(r, ang, 0.88, 0.008, 5.0, 0.56, -0.14, 0.31, 0.45, 1.0, rev, arv);
                // 主の輪 ＝ 判定の円そのもの（切れ目 3・ほとんど閉じている）
                a += arc(r, ang, 1.00, 0.024, 3.0, 0.88, 0.05, 0.00, 1.00, 2.0, rev, arv);
                // 外の太い塊（参考の外周。短く切れて回る）
                a += arc(r, ang, 1.22, 0.048, 4.0, 0.38, -0.06, 0.17, 0.60, 3.0, rev, arv);
                // 外の細い線（塊のすぐ外を 1 本）
                a += arc(r, ang, 1.34, 0.007, 2.0, 0.58, -0.06, 0.44, 0.40, 4.0, rev, arv);

                // 中の淡い面（「ここ」を面で示す。輪だけだと床の模様に紛れる）。
                // ⚠ **濃くしない。** 0.085 で焼いたら茶色い円盤になって、輪の意匠が消えた（実測）。
                // ⚠ これも中心から広がる（半径を進みで割る）。面だけ先に全面へ出ると、
                //   弧が中心から開いてくるのが読めなくなる。
                float gf = saturate(rev / OPEN_SPAN);
                gf = 1.0 - pow(1.0 - gf, 3.0);
                a += 0.009 * gf * (1.0 - smoothstep(0.05, 0.62, r / max(gf, 1e-3)));

                // 着いた合図: 輪から外へ 1 度だけ広がる波。
                // ⚠⚠ **quad の外へ出さない。** 半径を QUAD_K より大きくすると縁で切られて
                //    **角の丸い四角が光る**（実測でそうなった）。0.40 → 1.12 の内側で走らせる。
                // ⚠ 強くしない。4.0 では輪の意匠が飛んで「光った四角」だけが残った。
                float waveR = 0.40 + 0.72 * arv;
                a += exp(-pow((r - waveR) * 13.0, 2.0)) * arv * (1.0 - arv) * 1.2;

                a *= _Fade;
                // 着いたら少しだけ強くなる（閉じた輪が「確定した」に見える）
                float3 rgb = _Color.rgb * _Gain * a * (1.0 + 0.55 * arv);

                return half4(rgb, 0.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
