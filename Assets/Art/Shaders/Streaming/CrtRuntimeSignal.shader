Shader "Hidden/FixedCamVr/CrtRuntimeSignal"
{
    Properties
    {
        _MainTex("Canonical linear pre-glass", 2D) = "black" {}
        _Bandwidth("Luma/chroma bandwidth", Float) = 1
        _Gain("Uniform display RGB gain", Float) = .85
        _CenterEv("Center exposure EV", Float) = 0
        _CenterRadius("Center radius", Float) = .8
        _CenterFalloff("Center falloff", Float) = 2
        _TintStrength("Thin tint strength", Float) = 0
        _TintCoolness("Neutral to cool", Float) = .55
        _Aperture("Image aperture xywh", Vector) = (0, 0, 1, 1)
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Cull Off ZWrite Off ZTest Always
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert_img
            #pragma fragment Filter
            #include "UnityCG.cginc"
            #include "CrtSignalAdjustment.hlsl"
            // Core14.0.12 Color.hlsl sRGB formulas, with private names and float precision.
            // PositivePow(base,power) is pow(abs(base),power). Do not use UnityCG's approximations.
            float CrtSignalSRGBToLinear(float c)
            {
                float lo = c / 12.92;
                float hi = pow(abs((c + .055) / 1.055), 2.4);
                return c <= .04045 ? lo : hi;
            }
            float3 CrtSignalSRGBToLinear(float3 c)
            {
                return float3(CrtSignalSRGBToLinear(c.r), CrtSignalSRGBToLinear(c.g), CrtSignalSRGBToLinear(c.b));
            }
            float CrtSignalLinearToSRGB(float c)
            {
                return c <= .0031308 ? c * 12.9232102 : 1.055 * pow(abs(c), 1.0 / 2.4) - .055;
            }
            float3 CrtSignalLinearToSRGB(float3 c)
            {
                return float3(CrtSignalLinearToSRGB(c.r), CrtSignalLinearToSRGB(c.g), CrtSignalLinearToSRGB(c.b));
            }
            Texture2D<float4> _MainTex;
            float4 _MainTex_TexelSize;
            float _Bandwidth, _Gain;
            float4 _Aperture;
            float _CenterEv, _CenterRadius, _CenterFalloff, _TintStrength, _TintCoolness;
            float4 SignalAt(int2 pixel)
            {
                return _MainTex.Load(int3(clamp(pixel, int2(0, 0), int2(_MainTex_TexelSize.zw) - 1), 0));
            }
            float4 Filter(v2f_img input) : SV_Target
            {
                int2 pixel = int2(input.pos.xy);
                float4 source = SignalAt(pixel);
                if (_Bandwidth < .5 && _Gain == 1)
                    return CrtAdjustSignal(source, pixel, _MainTex_TexelSize, _Aperture, _CenterEv, _CenterRadius, _CenterFalloff, _TintStrength, _TintCoolness);
                float3 rgb = CrtSignalLinearToSRGB(max(source.rgb, 0));
                if (_Bandwidth > .5)
                {
                    float ySum = 0, yWeight = 0, cWeight = 0;
                    float3 cSum = 0;
                    [unroll] for (int i = -4; i <= 4; i++)
                    {
                        float3 tap = CrtSignalLinearToSRGB(max(SignalAt(pixel + int2(i, 0)).rgb, 0));
                        float y = dot(tap, float3(.299, .587, .114));
                        float wy = exp(-float(i * i) / (2 * .6 * .6));
                        float wc = exp(-float(i * i) / 8);
                        ySum += y * wy; yWeight += wy; cSum += (tap - y) * wc; cWeight += wc;
                    }
                    rgb = ySum / yWeight + cSum / cWeight;
                }
                return CrtAdjustSignal(float4(CrtSignalSRGBToLinear(max(rgb * _Gain, 0)), source.a), pixel, _MainTex_TexelSize, _Aperture,
                    _CenterEv, _CenterRadius, _CenterFalloff, _TintStrength, _TintCoolness);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
