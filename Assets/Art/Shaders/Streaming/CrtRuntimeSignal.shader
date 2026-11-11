Shader "Hidden/FixedCamVr/CrtRuntimeSignal"
{
    Properties
    {
        _MainTex("Canonical linear pre-glass", 2D) = "black" {}
        _Bandwidth("Luma/chroma bandwidth", Float) = 1
        _SourceEncoded("Pre-glass already holds sRGB values", Float) = 0
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
            Texture2D<float4> _MainTex;
            float4 _MainTex_TexelSize;
            float _Bandwidth, _Gain, _SourceEncoded;
            float4 _Aperture;
            float _CenterEv, _CenterRadius, _CenterFalloff, _TintStrength, _TintCoolness;
            float4 SignalAt(int2 pixel)
            {
                return _MainTex.Load(int3(clamp(pixel, int2(0, 0), int2(_MainTex_TexelSize.zw) - 1), 0));
            }
            float3 BandwidthFilter(int2 pixel, bool encoded)
            {
                float ySum = 0, yWeight = 0, cWeight = 0;
                float3 cSum = 0;
                [unroll] for (int i = -4; i <= 4; i++)
                {
                    float3 tap = SignalAt(pixel + int2(i, 0)).rgb;
                    if (!encoded) tap = CrtSignalLinearToSRGB(max(tap, 0));
                    float y = dot(tap, float3(.299, .587, .114));
                    float wy = exp(-float(i * i) / (2 * .6 * .6));
                    float wc = exp(-float(i * i) / 8);
                    ySum += y * wy; yWeight += wy; cSum += (tap - y) * wc; cWeight += wc;
                }
                return ySum / yWeight + cSum / cWeight;
            }
            float4 Filter(v2f_img input) : SV_Target
            {
                int2 pixel = int2(input.pos.xy);
                float4 source = SignalAt(pixel);
                if (_Bandwidth < .5 && _Gain == 1)
                    return CrtAdjustSignal(source, pixel, _MainTex_TexelSize, _Aperture, _CenterEv, _CenterRadius, _CenterFalloff, _TintStrength, _TintCoolness);
                float3 rgb;
                // CrtSignalProcessor encodes pre-glass once at its exit, so these 9 taps need no pow of their own.
                [branch] if (_Bandwidth > .5 && _SourceEncoded > .5) rgb = BandwidthFilter(pixel, true);
                else
                {
                    [branch] if (_Bandwidth > .5) rgb = BandwidthFilter(pixel, false);
                    else rgb = CrtSignalLinearToSRGB(max(source.rgb, 0));
                }
                return CrtAdjustSignal(float4(CrtSignalSRGBToLinear(max(rgb * _Gain, 0)), source.a), pixel, _MainTex_TexelSize, _Aperture,
                    _CenterEv, _CenterRadius, _CenterFalloff, _TintStrength, _TintCoolness);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
