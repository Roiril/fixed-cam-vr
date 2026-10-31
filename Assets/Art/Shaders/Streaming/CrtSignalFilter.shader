Shader "Hidden/FixedCamVr/CrtSignalFilter"
{
    Properties
    {
        _SignalSource("Linear pre-glass signal", 2D) = "black" {}
        _Bandwidth("Separate luma/chroma horizontal bandwidth", Float) = 0
        _Gain("Uniform display RGB gain", Float) = 1
        [HideInInspector] _DiagnosticSaturation("Calibration negative control saturation", Float) = 1
        [HideInInspector] _FlipY("Calibration negative control Y reversal", Float) = 0
        [HideInInspector] _DiagnosticOutput("Calibration unencoded display output", Float) = 0
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Cull Off ZWrite Off ZTest Always
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
            TEXTURE2D(_SignalSource);
            CBUFFER_START(UnityPerMaterial)
                float4 _SignalSource_TexelSize;
                float _Bandwidth;
                float _Gain;
                float _DiagnosticSaturation;
                float _FlipY;
                float _DiagnosticOutput;
            CBUFFER_END
            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionHCS : SV_POSITION; };
            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionHCS = float4((input.uv * 2 - 1) * float2(1, _ProjectionParams.x), 0, 1);
                return output;
            }
            float4 SignalAt(int2 pixel)
            {
                pixel = clamp(pixel, int2(0, 0), int2(_SignalSource_TexelSize.zw) - 1);
                return LOAD_TEXTURE2D(_SignalSource, pixel);
            }
            float4 Frag(Varyings input) : SV_Target
            {
                int2 pixel = int2(input.positionHCS.xy);
                if (_FlipY > 0.5) pixel.y = int(_SignalSource_TexelSize.w) - 1 - pixel.y;
                float4 source = SignalAt(pixel);
                if (_Bandwidth < 0.5 && _Gain == 1 && _DiagnosticSaturation == 1 && _DiagnosticOutput < 0.5) return source;
                float3 rgb = LinearToSRGB(max(source.rgb, 0));
                if (_Bandwidth > 0.5)
                {
                    float ySum = 0, yWeight = 0, cWeight = 0;
                    float3 cSum = 0;
                    [unroll] for (int i = -4; i <= 4; i++)
                    {
                        float3 tap = LinearToSRGB(max(SignalAt(pixel + int2(i, 0)).rgb, 0));
                        float y = dot(tap, float3(.299, .587, .114));
                        float wy = exp(-float(i * i) / (2 * .6 * .6));
                        float wc = exp(-float(i * i) / (2 * 2.0 * 2.0));
                        ySum += y * wy; yWeight += wy;
                        cSum += (tap - y) * wc; cWeight += wc;
                    }
                    rgb = ySum / yWeight + cSum / cWeight;
                }
                float y = dot(rgb, float3(.299, .587, .114));
                rgb = (y + (rgb - y) * _DiagnosticSaturation) * _Gain;
                if (_DiagnosticOutput > 0.5) return float4(rgb, source.a);
                // Preserve HDR signal; only the common final display output clamps to [0,1].
                return float4(SRGBToLinear(max(rgb, 0)), source.a);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
