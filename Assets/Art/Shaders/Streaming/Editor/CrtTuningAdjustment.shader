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
            #include "../CrtSignalAdjustment.hlsl"
            Texture2D<float4> _MainTex;
            float4 _MainTex_TexelSize, _Aperture;
            float _CenterEv, _CenterRadius, _CenterFalloff, _TintStrength, _TintCoolness;
            float4 Adjust(v2f_img input) : SV_Target
            {
                int2 pixel = int2(input.pos.xy);
                float4 source = _MainTex.Load(int3(pixel, 0));
                return CrtAdjustSignal(source, pixel, _MainTex_TexelSize, _Aperture, _CenterEv, _CenterRadius, _CenterFalloff, _TintStrength, _TintCoolness);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
