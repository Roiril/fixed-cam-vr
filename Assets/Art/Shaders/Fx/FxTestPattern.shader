Shader "FixedCamVr/Fx/TestPattern"
{
    Properties
    {
        _FxTime ("Time", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        ZTest Always Cull Off ZWrite Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings  { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            float _FxTime;

            Varyings Vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = v.uv;
                return o;
            }

            // 動的チェッカーボード + 偽ノイズ + 横スクロールバー
            float hash(float2 p) { return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }

            half4 Frag(Varyings i) : SV_Target
            {
                float2 uv = i.uv;
                float t = _FxTime;

                float2 cell = floor(uv * 16.0 + float2(t * 0.5, 0));
                float c = fmod(cell.x + cell.y, 2.0);
                float3 base = lerp(float3(0.10, 0.12, 0.18), float3(0.55, 0.30, 0.18), c);

                float bar = smoothstep(0.02, 0.0, abs(frac(uv.y - t * 0.2) - 0.5) - 0.02);
                base += float3(0.4, 0.05, 0.05) * bar;

                float n = hash(uv * 480.0 + t * 60.0);
                base += (n - 0.5) * 0.08;

                float vig = smoothstep(0.95, 0.55, distance(uv, 0.5));
                base *= vig;

                return half4(saturate(base), 1.0);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
