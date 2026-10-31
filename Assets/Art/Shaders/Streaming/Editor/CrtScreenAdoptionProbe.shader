Shader "Hidden/FixedCamVr/Editor/CrtScreenAdoptionProbe"
{
    Properties { _Color("Opaque background", Color) = (0, 1, 0, 1) _MaskTex("Alpha calibration", 2D) = "white" {} _ProbeMask("Read alpha before clipping", Float) = 0 }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }
        Pass
        {
            Cull Off ZWrite On ZTest LEqual
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            float4 _Color;
            TEXTURE2D(_MaskTex); SAMPLER(sampler_MaskTex);
            float _ProbeMask;
            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionHCS : SV_POSITION; float2 uv : TEXCOORD0; };
            Varyings Vert(Attributes i) { Varyings o; o.positionHCS = TransformObjectToHClip(i.positionOS.xyz); o.uv = i.uv; return o; }
            float4 Frag(Varyings i) : SV_Target
            {
                if (_ProbeMask > .5) { float a = SAMPLE_TEXTURE2D_LOD(_MaskTex, sampler_MaskTex, i.uv, 0).a; return float4(a, a, a, 1); }
                return _Color;
            }
            ENDHLSL
        }
    }
}
