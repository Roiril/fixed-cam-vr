#ifndef FIXEDCAMVR_COMMS_RECOVERY_INCLUDED
#define FIXEDCAMVR_COMMS_RECOVERY_INCLUDED
#include "CommsCurse.hlsl"

// 全層が同じ面座標を使う。顔だけの模様や、文字ごとの独立した赤にはしない。
float CommsRecoveryNoise(float2 p)
{
    return 0.74 * CurseValueNoise(p * 4.0 + float2(3.7, 1.9))
         + 0.26 * CurseValueNoise(p * 13.0 + float2(11.3, 7.1));
}

float2 CommsRecoveryWarp(float2 p, float4 recovery, float4 rect)
{
    if (recovery.x <= 0.001) return 0.0;
    float strength = saturate(recovery.x * 2.0);
    float2 uv = (p - rect.xy) / max(rect.zw, 0.001);
    float band = floor(uv.y * 21.0);
    float tick = floor(recovery.y * 8.0);
    float h = CurseHash21(float2(band, tick));
    float jump = step(0.77, h) * (h - 0.885) * 0.035;
    return float2(sin(uv.y * 19.0 + recovery.y * 0.7) * 0.0025 + jump,
                  sin(uv.x * 15.0 - recovery.y * 0.5) * 0.0010) * strength;
}

float CommsRecoveryMask(float2 p, float4 recovery, float4 rect)
{
    if (recovery.x <= 0.001) return 0.0;
    // 正方形に近い大きな塊を面全体へ。反転差の中心は0.5なので半々を保つ。
    float2 uv = (p - rect.xy) / max(rect.w, 0.001);
    float2 drift = float2(recovery.y * 0.025, -recovery.y * 0.035);
    float2 warp = CommsRecoveryWarp(p, recovery, rect) / max(rect.w, 0.001);
    float field = 0.5 + (CommsRecoveryNoise(uv + warp + drift)
                       - CommsRecoveryNoise(-uv - warp + drift)) * 0.65;
    return smoothstep(field - 0.035, field + 0.035, recovery.x);
}

float CommsRecoveryGrain(float2 p, float time)
{
    return CurseValueNoise(p * 420.0 + floor(time * 8.0) * float2(0.31, 0.17));
}
#endif
