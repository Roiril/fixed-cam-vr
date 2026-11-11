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
        struct GaussianVaryings
        {
            float4 pos : SV_POSITION;
            float2 uv : TEXCOORD0;
            // Weights of the taps 1..4 away. Sigma holds for a whole Blit (CrtSignalProcessor.Gaussian sets one
            // value for all channels), so the quad's vertices compute them and the pixels only read.
            nointerpolation float4 weights : TEXCOORD1;
        };
        GaussianVaryings VertGaussian(appdata_img v)
        {
            v2f_img image = vert_img(v);
            GaussianVaryings output;
            output.pos = image.pos;
            output.uv = image.uv;
            output.weights = exp(-float4(1, 4, 9, 16) / max(2 * _Sigma.x * _Sigma.x, 1e-8));
            return output;
        }
        float4 Gaussian(float2 uv, float2 axis, float4 w)
        {
            float tapWeight[9] = { w.w, w.z, w.y, w.x, 1, w.x, w.y, w.z, w.w };
            float3 total = 0;
            float weights = 0;
            [unroll] for (int i = -4; i <= 4; i++)
            {
                total += tex2Dlod(_MainTex, float4(uv + i * axis * _KernelTexel.xy, 0, _ActiveLod)).rgb * tapWeight[i + 4];
                weights += tapWeight[i + 4];
            }
            return float4(total / max(weights, 1e-8), 1);
        }
        float4 Horizontal(GaussianVaryings input) : SV_Target { return Gaussian(input.uv + _Phase.xy, float2(1, 0), input.weights); }
        float4 Vertical(GaussianVaryings input) : SV_Target { return Gaussian(input.uv, float2(0, 1), input.weights); }
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
            #pragma vertex VertGaussian
            #pragma fragment Horizontal
            ENDHLSL
        }
        Pass
        {
            Name "GaussianVertical"
            Tags { "LightMode"="CrtSourceGaussianV" }
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex VertGaussian
            #pragma fragment Vertical
            ENDHLSL
        }
    }
    FallBack Off
}
