// Graphics.Blit 経由で呼ぶ、古典的フルスクリーン RGB ずらしシェーダ。
// 入力 Texture を中心からの距離に応じて RGB チャネル別にオフセットサンプリング。
Shader "FixedCamVr/Fx/ChromaticAberration"
{
    Properties
    {
        _MainTex ("Source", 2D) = "white" {}
        _Strength ("Strength", Range(0, 0.05)) = 0.012
        _Falloff ("Falloff", Range(0, 4)) = 1.5
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        ZWrite Off Cull Off ZTest Always

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
            float _Strength;
            float _Falloff;

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = v.uv;
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                float2 uv = i.uv;
                float2 c = uv - 0.5;
                float r = pow(saturate(length(c) * 2.0), _Falloff);
                float2 dir = normalize(c + 1e-5) * _Strength * r;

                half rC = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv + dir).r;
                half gC = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv).g;
                half bC = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv - dir).b;
                return half4(rC, gC, bC, 1);
            }
            ENDHLSL
        }
    }
}
