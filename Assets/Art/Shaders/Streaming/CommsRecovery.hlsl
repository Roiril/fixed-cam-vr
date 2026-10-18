#ifndef FIXEDCAMVR_COMMS_RECOVERY_INCLUDED
#define FIXEDCAMVR_COMMS_RECOVERY_INCLUDED
#include "CommsCurse.hlsl"

// 2026-10-18 採用: ノイズは静止。確認画像の位相を固定し、復帰の時計では動かさない。
static const float COMMS_RECOVERY_NOISE_PHASE = 5.5;

// 全層が同じ面座標を使う。顔だけの模様や、文字ごとの独立した赤にはしない。
float CommsRecoveryNoise(float2 p)
{
    return 0.74 * CurseValueNoise(p * 4.0 + float2(3.7, 1.9))
         + 0.26 * CurseValueNoise(p * 13.0 + float2(11.3, 7.1));
}

float CommsRecoveryBaseMask(float2 p, float4 recovery, float4 rect)
{
    if (recovery.x <= 0.001) return 0.0;
    float2 uv = (p - rect.xy) / max(rect.w, 0.001);
    float2 drift = float2(COMMS_RECOVERY_NOISE_PHASE * 0.025, -COMMS_RECOVERY_NOISE_PHASE * 0.035);
    float field = 0.5 + (CommsRecoveryNoise(uv + drift)
                       - CommsRecoveryNoise(-uv + drift)) * 0.65;
    return smoothstep(field - 0.035, field + 0.035, recovery.x);
}

float2 CommsRecoveryWarp(float2 p, float4 recovery, float4 rect)
{
    if (recovery.x <= 0.001) return 0.0;
    float strength = saturate(recovery.x * 2.0);
    float2 uv = (p - rect.xy) / max(rect.zw, 0.001);
    float band = floor((p.y - rect.y) / 0.006);
    float tick = floor(COMMS_RECOVERY_NOISE_PHASE * 10.0);
    float h = CurseHash21(float2(band, tick));
    float jump = step(0.58, h) * (CurseHash21(float2(band + 71.0, tick)) - 0.5) * 0.045;
    float red = CommsRecoveryBaseMask(p, recovery, rect);
    return float2(sin(uv.y * 19.0 + COMMS_RECOVERY_NOISE_PHASE * 0.7) * 0.0025 + jump,
                  sin(uv.x * 15.0 - COMMS_RECOVERY_NOISE_PHASE * 0.5) * 0.0010) * strength * red;
}

float CommsRecoveryMask(float2 p, float4 recovery, float4 rect)
{
    if (recovery.x <= 0.001) return 0.0;
    // 赤の輪郭も帯ごとに横へ裂く。正常領域には変位を掛けない。
    return CommsRecoveryBaseMask(p - CommsRecoveryWarp(p, recovery, rect), recovery, rect);
}

// 赤い領域の細い明線と暗線。横へ走った一瞬の形を固定する。
float CommsRecoveryLines(float2 p, float4 recovery, float4 rect)
{
    float row = floor((p.y - rect.y) / 0.005);
    float seed = CurseHash21(float2(row, 17.3));
    float thin = 1.0 - smoothstep(0.10, 0.27, frac((p.y - rect.y) / 0.005));
    float travel = frac((p.x - rect.x) / 0.28 - COMMS_RECOVERY_NOISE_PHASE * 2.1 + seed);
    float streak = smoothstep(0.02, 0.12, travel) * (1.0 - smoothstep(0.52, 0.9, travel));
    return thin * streak * step(0.35, seed);
}

float CommsRecoveryGrain(float2 p, float time)
{
    return CurseValueNoise(p * 420.0 + floor(COMMS_RECOVERY_NOISE_PHASE * 8.0) * float2(0.31, 0.17));
}
#endif
