// 増えるスクリーンの周りの光の暈（参考 mul.jpg の部屋の霞）。
//
// 面の少し奥の quad に塗る。面とメインスクリーンが先に深度を書くので、塗られるのは周りの環だけ。
// ⚠ 暗い階調を薄く足すので帯が出やすい。1 階調ぶんの揺らぎを混ぜる。
//
// 霧（ScreenFog.hlsl）の中では光がにじむ。遠い面ほど暈は面より暗くなりにくく、広く残る。
// 奥の面は中身が読めず、青い光のにじみとして見える。
// ⚠ Resources に置いてある（ScreenMultiply が Resources.Load で引く）。
Shader "FixedCamVr/ScreenMultiplyGlow"
{
    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry+60" }
        LOD 100

        Pass
        {
            Name "ScreenMultiplyGlow"
            Blend One One, Zero One
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "ScreenFog.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _WallBig;
                float _WallField;
                float _WallSpan;
                float _WallTime;
                float _WallGlowGain;
                float _WallNow;
                float _WallArriveSec;
                float _WallEmergeM;
                float _WallFogDensity;
                float _WallFogClearM;
                float _WallFogBlur;
                float _WallFarShow;
            CBUFFER_END
            // 席ごとの点いた時刻。大きさは ScreenMultiplyLayout.MaxSeats と対。
            float _WallOpenAt[160];

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 uv0 : TEXCOORD0;
                float4 uv1 : TEXCOORD1;
                float4 uv2 : TEXCOORD2;     // xyz 席の中心 / w 席の番号
                float4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 uv : TEXCOORD0;      // xy -1..1 / z 面に対する暈の倍率
                float2 seat : TEXCOORD1;    // x 開き / y 席の乱数
                float3 tint : TEXCOORD2;
                float fog : TEXCOORD3;      // 霧を抜けて届く光（1 = そのまま）
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes v)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float span = max(0.005, _WallSpan);
                float open = v.uv1.w > 0.5 ? _WallBig : saturate((_WallField * (1.0 + span) - v.uv1.x) / span);

                // 点いた面は霧の奥から席まで寄る。時計は席ごとの「点いた時刻」（ScreenMultiply が配る）。
                int seatIndex = (int)(v.uv2.w + 0.5);
                float arrive = saturate((_WallNow - _WallOpenAt[seatIndex]) / max(0.05, _WallArriveSec));
                float left = (1.0 - arrive) * (1.0 - arrive) * (1.0 - arrive);
                float3 pos = v.positionOS.xyz
                           + normalize(v.uv2.xyz) * (left * _WallEmergeM * (0.75 + 0.5 * v.uv1.z));
                float dist = max(0.001, length(pos));
                float fog = ScreenFogTransmit(dist, _WallFogDensity, _WallFogClearM,
                                              ScreenFogWisp(pos / dist, _WallNow));
                // 霧の奥の段は color.a = 0。プレビューの比較で消せる。
                float shown = step(0.001, open) * step(0.5, max(v.color.a, _WallFarShow));

                o.positionCS = shown > 0.5 ? TransformObjectToHClip(pos) : float4(0, 0, -1, 1);
                o.uv = v.uv0.xyz;
                o.seat = float2(open, v.uv1.z);
                o.tint = v.color.rgb;
                o.fog = fog;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                // 面の縁で最大、quad の縁で 0。
                float2 q = abs(i.uv.xy) * i.uv.z;
                float2 outside = max(q - 1.0, 0.0);
                float reach = max(i.uv.z - 1.0, 0.001);
                float dist = saturate(length(outside) / reach);
                // 霧の中ほど裾が広い（4 乗 → 2.2 乗）。
                float fall = pow(max(1.0 - dist, 0.0), lerp(2.2, 4.0, i.fog));
                // にじんだ光は面そのものより霧に強い。
                fall *= pow(max(i.fog, 0.0001), 0.55) * (1.0 + 1.2 * (1.0 - i.fog));

                float flutter = 1.0 + 0.06 * sin(_WallTime * (3.0 + i.seat.y * 9.0) + i.seat.y * 40.0);
                float lamp = lerp(0.5, 1.05, i.seat.y);   // ScreenMultiply の lamp と同じ式
                float3 col = i.tint * fall * _WallGlowGain * lamp * saturate(i.seat.x * 1.5) * flutter;

                // 画面座標の揺らぎで帯を割る（Interleaved Gradient Noise）。
                float n = frac(52.9829189 * frac(dot(i.positionCS.xy, float2(0.06711056, 0.00583715))));
                // ⚠ 揺らぎは表示の 1 階調ぶん。linear の 1/255 をそのまま足すと、黒の近くでは 6 階調になり織り目が見える。
                float lum = dot(col, float3(0.2126, 0.7152, 0.0722));
                float stepLinear = max(1.0 / 12.92, 2.275 * pow(max(lum, 1e-6), 0.583)) / 255.0;
                col += (n - 0.5) * stepLinear * step(0.0005, fall);
                return half4(max(col, 0.0), 0.0);
            }
            ENDHLSL
        }
    }
}
