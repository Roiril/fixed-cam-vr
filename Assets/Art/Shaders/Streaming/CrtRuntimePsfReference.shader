Shader "Hidden/FixedCamVr/CrtRuntimePsfReference"
{
    Properties
    {
        _LiveTex("Raw source", 2D) = "black" {}
        _CrtPsfLinearTex("Dense reference linear source", 2D) = "black" {}
        _CrtRuntimePsfTex("Green-sigma RGB source", 2D) = "black" {}
        _CrtRuntimePsfRedTex("Red-sigma RGB source", 2D) = "black" {}
        _CrtRuntimePsfBlueTex("Blue-sigma RGB source", 2D) = "black" {}
        _ProbeUV("Curved surface UV meter", Float) = 0
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Cull Off ZWrite Off ZTest Always
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment PointFrag
            #define CRT_PSF_DIAGNOSTIC 1
            #define CRT_RUNTIME_PSF 1
            #include "CrtCompositeShared.hlsl"
            float _ProbeUV;
            float4 PointFrag(Varyings input) : SV_Target
            {
                if (_ProbeUV > .5) return float4(input.uv, .25, 1);
                // Same16 impulses/all4x4 mip phases and4x sampling as the original meter.
                // Only their measured24x24 source-pixel windows need the dense81tap evaluation.
                // Empty areas are exact black; avoiding billions of redundant zero taps prevents GPU TDR.
                float2 p = input.uv * float2(640, 480) - .5;
                float2 nearest = clamp(round((p - float2(240, 160)) / 33), 0, 3) * 33 + float2(240, 160);
                if (any(abs(p - nearest) > 12.5)) return float4(0, 0, 0, 1);
                return float4(SampleLivePsf(input.uv, 0, _CrtPsfSigma.r, 0).r,
                              SampleLivePsf(input.uv, 0, _CrtPsfSigma.g, 0).g,
                              SampleLivePsf(input.uv, 0, _CrtPsfSigma.b, 0).b, 1);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
