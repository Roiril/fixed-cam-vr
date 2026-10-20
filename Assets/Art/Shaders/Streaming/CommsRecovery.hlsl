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

// ---- ③a の起動（`canon/LEDGER.md` 0280・`CommsRecoveryLogic`）----
// 面は一度に点かない。横に細長いブロックごとに砂嵐として現れ、砂嵐から中身（地・顔・字）へ復号される。
// 地・顔・字が同じ格子と同じ順番を読むので、1 つのブロックの中では 3 つが同時に現れる。
// boot = まだ中身として現れていない量 0..1（0 = 通常の面と 1 画素も変わらない）。
static const float COMMS_BOOT_ROW_M = 0.009;

// 戻り値 x = 中身を出す量（0 / 1）, y = 砂嵐を出す量 0..1。
float2 CommsBootReveal(float2 p, float boot, float4 rect)
{
    if (boot <= 0.001) return float2(1.0, 0.0);
    float revealed = 1.0 - boot;
    float2 q = p - (rect.xy - rect.zw * 0.5);
    float row = floor(q.y / COMMS_BOOT_ROW_M);
    float hr = CurseHash21(float2(row, 3.1));
    float segW = lerp(0.022, 0.075, hr);
    float seg = floor((q.x + hr * segW) / segW);
    float h = CurseHash21(float2(row, seg) + float2(17.0, 41.0));
    float present = step(h, revealed * 2.0);
    float decoded = step(h, revealed * 2.0 - 1.0);
    // 復号した後も、全体が晴れ切るまで薄い砂が残る。
    float settle = saturate(revealed * 2.0 - 1.0);
    float haze = (1.0 - settle) * (1.0 - settle) * 0.45;
    return float2(decoded, saturate(present * (1.0 - decoded) + decoded * haze));
}

// 砂嵐の色。暗い側へ寄せた灰（白い砂嵐は眩しい）。行ごとに明るさを揺らして走査線を残す。
float3 CommsBootSand(float2 p, float time)
{
    float tick = floor(time * 24.0);
    float2 cell = floor(p / 0.0018);
    float v = pow(CurseHash21(cell + tick * float2(7.13, 3.71)), 1.35);
    float scan = fmod(abs(cell.y), 2.0) < 1.0 ? 0.86 : 1.0;
    return float3(0.91, 0.886, 0.84) * (v * scan * 0.62);
}
#endif
