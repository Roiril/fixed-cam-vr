// ツインの注記（床の格子・カメラの視野・体半径）を、部屋の光とは別の層として加算で重ねる。
// 遠いほど霧に沈み、頂点色の a で端を消せる。
Shader "FixedCamVr/Twin/Line"
{
    Properties
    {
        _Color ("Color", Color) = (0.3,0.9,1,1)
        _Intensity ("Intensity", Float) = 1
        _ConeFade ("Fade outside the bulb cone", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent+20" "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Name "Line"
            Tags { "LightMode"="UniversalForward" }
            Blend One One
            ZWrite Off ZTest LEqual Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "TwinCommon.hlsl"
            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float _Intensity, _ConeFade;
            CBUFFER_END
            struct Attributes { float4 positionOS : POSITION; float4 color : COLOR; };
            struct Varyings { float4 positionCS : SV_POSITION; float4 color : TEXCOORD0; float3 positionWS : TEXCOORD1; };
            Varyings Vert(Attributes v)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.color = v.color;
                return o;
            }
            float4 Frag(Varyings i) : SV_Target
            {
                float fog = exp(-_TwinFogDensity * distance(i.positionWS, _WorldSpaceCameraPos));
                float cone = lerp(1.0, TwinKeyCone(i.positionWS), _ConeFade);
                return float4(_Color.rgb * i.color.rgb * i.color.a * _Intensity * fog * cone, 0);
            }
            ENDHLSL
        }
    }
}
