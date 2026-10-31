Shader "Hidden/FixedCamVr/Editor/CrtTuningAdjustment"
{
    Properties
    {
        _MainTex("Canonical processed linear signal", 2D) = "black" {}
        _CenterEv("Center exposure EV", Float) = 0
        _CenterRadius("Center radius", Float) = .8
        _CenterFalloff("Center falloff", Float) = 2
        _TintStrength("Tint strength", Float) = 0
        _TintCoolness("Neutral to cool", Float) = .55
        _Aperture("Image aperture xywh", Vector) = (0, 0, 1, 1)
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Name "CrtTuningAdjustment"
            Tags { "LightMode"="CrtTuningAdjustment" }
            Cull Off ZWrite Off ZTest Always
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert_img
            #pragma fragment Adjust
            #include "UnityCG.cginc"
            Texture2D<float4> _MainTex;
            float4 _MainTex_TexelSize, _Aperture;
            float _CenterEv, _CenterRadius, _CenterFalloff, _TintStrength, _TintCoolness;
            float4 Adjust(v2f_img input) : SV_Target
            {
                int2 pixel = int2(input.pos.xy);
                float4 source = _MainTex.Load(int3(pixel, 0));
                // Exact texel load and early return preserve the zero-adjustment control.
                if (_CenterEv == 0 && _TintStrength == 0) return source;
                float2 uv = (float2(pixel) + .5) * _MainTex_TexelSize.xy;
                float2 imageUv = (uv - _Aperture.xy) / _Aperture.zw;
                float distanceFromCenter = length((imageUv - .5) * 2);
                float radial = pow(1 - smoothstep(0, _CenterRadius, distanceFromCenter), _CenterFalloff);
                float3 rgb = source.rgb * exp2(_CenterEv * radial);
                // A low-saturation multiplicative cast preserves black and never adds white.
                float3 tint = lerp(float3(.98, .98, .98), float3(.85, .94, 1), _TintCoolness);
                rgb *= lerp(float3(1, 1, 1), tint, _TintStrength);
                return float4(rgb, source.a);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
