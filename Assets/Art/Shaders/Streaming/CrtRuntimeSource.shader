Shader "Hidden/FixedCamVr/CrtRuntimeSource"
{
    Properties
    {
        _MainTex("Source", 2D) = "black" {}
        _Sigma("Source pixel Gaussian sigma", Vector) = (.85, .65, .65, 0)
        _ActiveLod("Raw linear mip to read before Gaussian", Float) = 0
        _KernelTexel("Original source pixel size", Vector) = (0, 0, 0, 0)
        _Phase("Source sampling phase", Vector) = (0, 0, 0, 0)
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        Cull Off ZWrite Off ZTest Always
        HLSLINCLUDE
        #include "UnityCG.cginc"
        sampler2D _MainTex;
        float4 _MainTex_TexelSize;
        float4 _Sigma;
        float _ActiveLod;
        float4 _KernelTexel, _Phase;
        float4 Decode(v2f_img input) : SV_Target { return tex2Dlod(_MainTex, float4(input.uv, 0, 0)); }
        float4 Gaussian(float2 uv, float2 axis)
        {
            float3 total = 0, weights = 0;
            [unroll] for (int i = -4; i <= 4; i++)
            {
                float3 w = exp(-float(i * i) / max(2 * _Sigma.xyz * _Sigma.xyz, 1e-8));
                total += tex2Dlod(_MainTex, float4(uv + i * axis * _KernelTexel.xy, 0, _ActiveLod)).rgb * w;
                weights += w;
            }
            return float4(total / max(weights, 1e-8), 1);
        }
        float4 Horizontal(v2f_img input) : SV_Target { return Gaussian(input.uv + _Phase.xy, float2(1, 0)); }
        float4 Vertical(v2f_img input) : SV_Target { return Gaussian(input.uv, float2(0, 1)); }
        ENDHLSL
        Pass
        {
            Name "DecodeRawLinear"
            Tags { "LightMode"="CrtSourceDecode" }
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert_img
            #pragma fragment Decode
            ENDHLSL
        }
        Pass
        {
            Name "GaussianHorizontalFromRawMip"
            Tags { "LightMode"="CrtSourceGaussianH" }
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert_img
            #pragma fragment Horizontal
            ENDHLSL
        }
        Pass
        {
            Name "GaussianVertical"
            Tags { "LightMode"="CrtSourceGaussianV" }
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert_img
            #pragma fragment Vertical
            ENDHLSL
        }
    }
    FallBack Off
}
