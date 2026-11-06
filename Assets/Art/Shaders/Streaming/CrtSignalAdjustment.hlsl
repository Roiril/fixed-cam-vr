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

// The same pre-glass linear adjustment is used by the runtime filter and the tuning preview.
float4 CrtAdjustSignal(float4 source, int2 pixel, float4 texelSize, float4 aperture,
                      float centerEv, float centerRadius, float centerFalloff,
                      float tintStrength, float tintCoolness)
{
    if (centerEv == 0 && tintStrength == 0) return source;
    float2 uv = (float2(pixel) + .5) * texelSize.xy;
    float2 imageUv = (uv - aperture.xy) / aperture.zw;
    float distanceFromCenter = length((imageUv - .5) * 2);
    float radial = pow(1 - smoothstep(0, centerRadius, distanceFromCenter), centerFalloff);
    float3 rgb = source.rgb * exp2(centerEv * radial);
    float3 tint = lerp(float3(.98, .98, .98), float3(.85, .94, 1), tintCoolness);
    rgb *= lerp(float3(1, 1, 1), tint, tintStrength);
    return float4(rgb, source.a);
}
