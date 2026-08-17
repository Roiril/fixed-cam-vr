// 歩行誘導の**矢印**（canon/LEDGER.md 0079）。床に置いた山形（シェブロン）が
// 起点から円へ向かって順に点き、その上を明るさが流れる。
//
// メッシュは山形 1 つにつき quad 1 枚で、**全部が 1 メッシュ**（WalkGuide.cs が組む）。
//   uv  = quad ローカル 0..1（+v が進行方向 ＝ 円の側）
//   uv2.x = 円からの正規化距離 s（0 = 円のすぐ手前 / 1 = 起点）
//
// ⚠⚠ **alpha は 0 を返す。** 加算合成では alpha も足されるので、1 を返すと
//    パススルーが出ている場面（誘導が出るのは段 0 ＝ 現実が見えている区間だけ）で
//    **現実に穴を塞ぐ**。AnomalyEyes と同じ理由。
//
// ⚠ **縁の色ずれ（赤／シアンの分離）は入れない。** 参考画像（HUD の矢印）はネオンの色収差を
//    持つが、立体視では**輪郭の位置ずれ**に見えて焦点不快感を起こす（canon/LEDGER.md 0075 の
//    判断をそのまま踏襲）。参考から採るのは**形（山形・縞・段の付いた並び）**であって色ではない。
//    色は作品の暖色（0010）。
//
// ⚠ **実行時 Shader.Find で引く。** ProjectSettings/GraphicsSettings.asset の Always Included に
//    登録してある（外すと Editor では出て実機だけ剥がれる — rules/unity-vr.md の 2026-07-31 実害）。
// ⚠ **Queue は 4960。** 覆い（IntroVeil 4900・Blend Zero SrcAlpha）より**後**でなければ
//    rgb ごと 0 に潰される。5000 を超えると URP の透明パスに入らず 1 画素も出ない。
Shader "FixedCamVr/WalkGuideArrow"
{
    Properties
    {
        _Color("Ink", Color) = (1.0, 0.72, 0.42, 1)
        _Reveal("Reveal", Range(0, 1)) = 1
        _Fade("Fade", Range(0, 1)) = 1
        _Flow("Flow Phase", Range(0, 1)) = 0
        // 山形 1 つぶんの `_Reveal` の刻み（= 1 / (山形の数 - 1)）。WalkGuide.cs が書く。
        // ⚠ 既定 1 は「1 個しかない」＝ 全部同時に出る（材質を直接使ったときの安全側）。
        _Step("Reveal Step", Range(0.001, 1)) = 1
        _Gain("Gain", Range(0, 4)) = 1.6
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent+460" }
        LOD 100

        Pass
        {
            Name "WalkGuideArrow"
            Blend One One            // 加算（床へ光を足す）。alpha は 0 のまま
            ZWrite Off
            // ⚠ 床の印なので**深度に従う**（スクリーンの向こう側へ回った所は隠れる）。
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            // ⚠⚠ Single Pass Instanced（Quest の既定）。無いと実機で片眼にしか出ない。
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float _Reveal;
                float _Fade;
                float _Flow;
                float _Step;
                float _Gain;
            CBUFFER_END

            // ---- 山形の形（quad ローカル -1..1）----
            #define TIP   0.62      // 先端の v
            #define WING  0.62      // 翼の端（|u|=1）の v（負側）
            #define THICK 0.21      // 線の半幅
            #define SOFT  0.085     // 縁のぼけ
            #define SIDE_FADE 0.88  // quad の端で切れないように落とし始める |u|
            // 縞（参考画像の「横に切られた」見え）。**進行方向に直交する帯**。
            #define STRIPE_N    5.0
            #define STRIPE_DUTY 0.68
            #define STRIPE_MIN  0.34   // 縞の谷でも完全には消さない（点線に見えてしまう）
            // ---- 点き方（1 つずつ）----
            // ⚠⚠ **なだらかな帯で滑らせない**（2026-08-17・ユーザー赤入れ 4
            //    「矢印が手前の線から 1 つづつ出てくるような感じに」）。それまでは幅 0.22 の
            //    smoothstep を s の上で動かしていたが、山形 7 個の刻みは 0.167 なので
            //    **常に 1.3 個ぶんが同時にぼんやり点いていた** ＝ 1 つずつには見えない。
            // 1 つが立ち上がるのに使う「刻みの何倍か」。**1 未満**でなければ次と重なる。
            #define POP_RISE  0.55
            // 生まれた瞬間の大きさ（uv を中心から拡げると形が縮む）。
            #define POP_SCALE 0.45
            // 出た瞬間だけ少し強く光る量（山は進み 0.5）。
            #define POP_FLASH 0.55
            // 流れ。円へ向かって走る明るさ。
            #define FLOW_SHARP 5.0
            #define FLOW_GAIN  1.35

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv  : TEXCOORD0;
                float2 uv2 : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv  : TEXCOORD0;
                float2 uv2 : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionHCS = TransformObjectToHClip(input.positionOS.xyz);
                o.uv = input.uv;
                o.uv2 = input.uv2;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                float u = i.uv.x * 2.0 - 1.0;
                float v = i.uv.y * 2.0 - 1.0;
                float s = saturate(i.uv2.x);      // 0 = 円のすぐ手前 / 1 = 起点

                // ---- この山形の番が来てからの進み（0 = まだ / 1 = 出切った）----
                // 順は**起点（s=1）から円（s=0）へ**。体験者に近い方が先に出る。
                // ⚠ `_Reveal` を刻み 1 つぶん引き伸ばしてから比べる。引き伸ばさないと、
                //    いちばん円寄りの山形（s=0）は `_Reveal` が 1 に着いた所で
                //    **立ち上がり始めるだけ**になり、最後の 1 つが永久に出ない。
                float rise = max(_Step * POP_RISE, 1e-4);
                float p = saturate((_Reveal * (1.0 + rise) - (1.0 - s)) / rise);
                // 速く出て、すっと止まる。
                float e = 1.0 - pow(1.0 - p, 3.0);
                // 生まれるときは小さい（quad の uv を中心から拡げると、描く形が縮む）。
                float sc = lerp(POP_SCALE, 1.0, e);
                u /= sc;
                v /= sc;

                // 山形 = 折れ線からの距離の帯。勾配で割って太さを一定にする。
                // ⚠ 変数名に `line` を使わない（HLSL の予約語。**頂点シェーダの構文エラー**になり、
                //    材質が既定のマゼンタへ落ちる）。
                float slope = TIP + WING;
                float ridge = TIP - slope * abs(u);
                float d = abs(v - ridge) * rsqrt(1.0 + slope * slope);
                float shape = 1.0 - smoothstep(THICK - SOFT, THICK + SOFT, d);
                shape *= 1.0 - smoothstep(SIDE_FADE, 1.0, abs(u));

                // 縞。折れ線に沿った座標で切ると、山形の 2 辺で縞が繋がる。
                float along = v + slope * abs(u);
                float bar = frac(along * STRIPE_N);
                float stripe = lerp(STRIPE_MIN, 1.0,
                                    smoothstep(0.0, 0.12, bar) * (1.0 - smoothstep(STRIPE_DUTY, STRIPE_DUTY + 0.12, bar)));

                // 流れ: 明るさが起点から円へ走る。
                float f = frac(s + _Flow);
                float pulse = pow(saturate(1.0 - f), FLOW_SHARP) * FLOW_GAIN;

                float a = shape * stripe * e * _Fade;
                // 出た瞬間だけ少し強い（1 つずつ置かれていることが目で追える）。
                float flash = POP_FLASH * p * (1.0 - p) * 4.0;
                float3 rgb = _Color.rgb * _Gain * a * (0.55 + pulse + flash);

                // ⚠ alpha は 0（現実に穴を塞がない）。
                return half4(rgb, 0.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
