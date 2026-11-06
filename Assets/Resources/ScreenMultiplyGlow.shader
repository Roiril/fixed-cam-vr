// 増えるスクリーンの周りの光の暈（参考 mul.jpg の部屋の霞）。
//
// 面の少し奥の quad に塗る。面とメインスクリーンが先に深度を書くので、塗られるのは周りの環だけ。
// ⚠ 暗い階調を薄く足すので帯が出やすい。1 階調ぶんの揺らぎを混ぜる。
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

            CBUFFER_START(UnityPerMaterial)
                float _WallBig;
                float _WallField;
                float _WallSpan;
                float _WallTime;
                float _WallGlowGain;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 uv0 : TEXCOORD0;
                float4 uv1 : TEXCOORD1;
                float4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 uv : TEXCOORD0;      // xy -1..1 / z 面に対する暈の倍率
                float2 seat : TEXCOORD1;    // x 開き / y 席の乱数
                float3 tint : TEXCOORD2;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes v)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float span = max(0.005, _WallSpan);
                float open = v.uv1.w > 0.5 ? _WallBig : saturate((_WallField * (1.0 + span) - v.uv1.x) / span);
                o.positionCS = open > 0.001 ? TransformObjectToHClip(v.positionOS.xyz) : float4(0, 0, -1, 1);
                o.uv = v.uv0.xyz;
                o.seat = float2(open, v.uv1.z);
                o.tint = v.color.rgb;
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
                float fall = (1.0 - dist) * (1.0 - dist);
                fall *= fall;

                float flutter = 1.0 + 0.06 * sin(_WallTime * (3.0 + i.seat.y * 9.0) + i.seat.y * 40.0);
                float lamp = lerp(0.5, 1.05, i.seat.y);   // ScreenMultiply の lamp と同じ式
                float3 col = i.tint * fall * _WallGlowGain * lamp * saturate(i.seat.x * 1.5) * flutter;

                // 画面座標の揺らぎで帯を割る（Interleaved Gradient Noise）。
                float n = frac(52.9829189 * frac(dot(i.positionCS.xy, float2(0.06711056, 0.00583715))));
                col += (n - 0.5) / 255.0 * step(0.0005, fall);
                return half4(max(col, 0.0), 0.0);
            }
            ENDHLSL
        }
    }
}
