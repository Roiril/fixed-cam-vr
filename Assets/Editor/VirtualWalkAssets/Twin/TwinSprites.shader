// 視線へ向く小さな板（加算）。_Mode 0 = 舞う埃（電球の光と影が当たった粒だけ光る）、1 = 光のにじみ（電球・録画灯の暈）。
// メッシュは 1 粒 4 頂点。位置 = 粒の中心、uv0 = 角（-1..1）、uv1 = (大きさ m, 乱数)。
Shader "FixedCamVr/Twin/Sprites"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        _Mode ("Mode (0 dust / 1 glow)", Float) = 0
        _Drift ("Drift amplitude m", Float) = 0.04
        _Falloff ("Glow falloff", Float) = 3
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent+100" "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Name "Sprites"
            Tags { "LightMode"="UniversalForward" }
            Blend One One
            ZWrite Off ZTest LEqual Cull Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "TwinCommon.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float _Mode, _Drift, _Falloff;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float2 corner : TEXCOORD0; float2 sizeSeed : TEXCOORD1; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 corner : TEXCOORD0; float3 centerWS : TEXCOORD1; float seed : TEXCOORD2; };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                float3 c = TransformObjectToWorld(v.positionOS.xyz);
                float seed = v.sizeSeed.y;
                if (_Mode < 0.5)
                {
                    // 埃はゆっくり沈みながら揺れる（体験の時刻で決まるので同じ時刻は同じ絵）
                    float t = _TwinTime;
                    c += _Drift * float3(sin(t * 0.37 + seed * 41.0), sin(t * 0.23 + seed * 17.0) - 0.4 * frac(t * 0.011 + seed), cos(t * 0.31 + seed * 29.0));
                }
                float3 viewC = TransformWorldToView(c);
                float size = v.sizeSeed.x;
                // レンズの直前の粒は大きなボケ玉になる。1m より手前では消す
                if (_Mode < 0.5) size *= smoothstep(0.8, 1.5, distance(c, _WorldSpaceCameraPos));
                viewC.xy += v.corner * size;
                o.positionCS = TransformWViewToHClip(viewC);
                o.corner = v.corner;
                o.centerWS = c;
                o.seed = seed;
                return o;
            }

            float4 Frag(Varyings i) : SV_Target
            {
                float r = length(i.corner);
                if (_Mode < 0.5)
                {
                    float disc = saturate(1.0 - r);
                    disc *= disc;
                    float3 acc = 0;
                    uint count = GetAdditionalLightsCount();
                    for (uint k = 0u; k < count; k++)
                    {
                        Light l = GetAdditionalLight(k, i.centerWS, half4(1, 1, 1, 1));
                        acc += l.color * min(l.distanceAttenuation, 2.5) * l.shadowAttenuation;
                    }
                    float twinkle = 0.55 + 0.45 * sin(_TwinTime * (1.3 + i.seed * 2.0) + i.seed * 60.0);
                    float3 c = acc * disc * twinkle * _Color.rgb * TwinKeyCone(i.centerWS);
                    c *= exp(-_TwinFogDensity * distance(i.centerWS, _WorldSpaceCameraPos));
                    return float4(c, 0);
                }
                float glow = exp(-r * r * _Falloff) - exp(-_Falloff);
                return float4(_Color.rgb * max(glow, 0.0), 0);
            }
            ENDHLSL
        }
    }
}
