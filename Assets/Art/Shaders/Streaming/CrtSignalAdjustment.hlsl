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
