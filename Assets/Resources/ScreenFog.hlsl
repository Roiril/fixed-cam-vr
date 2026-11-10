// スクリーンの周りの闇に置く黒い霧。式の正は ScreenFogLogic.cs（濃さ・霧の無い距離は材質の値で受け取る）。
//
// 黒い霧は黒の上では見えない。見えるのは 3 つ — 遠い光が暗くなる / ぼける / 周りへにじむ。
// 濃さには方角ごとのむらを掛け、ゆっくり流す（同じ距離の面が同じ暗さに揃わない）。
#ifndef FIXEDCAM_SCREEN_FOG_INCLUDED
#define FIXEDCAM_SCREEN_FOG_INCLUDED

float ScreenFogHash(float3 p)
{
    p = frac(p * 0.1031);
    p += dot(p, p.yzx + 33.33);
    return frac((p.x + p.y) * p.z);
}

float ScreenFogNoise(float3 p)
{
    float3 i = floor(p);
    float3 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = lerp(ScreenFogHash(i), ScreenFogHash(i + float3(1, 0, 0)), f.x);
    float b = lerp(ScreenFogHash(i + float3(0, 1, 0)), ScreenFogHash(i + float3(1, 1, 0)), f.x);
    float c = lerp(ScreenFogHash(i + float3(0, 0, 1)), ScreenFogHash(i + float3(1, 0, 1)), f.x);
    float d = lerp(ScreenFogHash(i + float3(0, 1, 1)), ScreenFogHash(i + float3(1, 1, 1)), f.x);
    return lerp(lerp(a, b, f.y), lerp(c, d, f.y), f.z);
}

// 霧の濃さのむら（0.55〜1.6 倍）。頂点で引いて面の中は補間する（むらは面より大きい）。
float ScreenFogWisp(float3 dir, float t)
{
    float3 p = dir * 2.1 + float3(t * 0.021, -t * 0.013, t * 0.017);
    float n = ScreenFogNoise(p) * 0.65 + ScreenFogNoise(p * 2.3 + 7.1) * 0.35;
    return lerp(0.55, 1.6, n);
}

// 距離 distM の光がどれだけ届くか。ScreenFogLogic.Transmit と同じ式にむらを掛けたもの。
float ScreenFogTransmit(float distM, float density, float clearM, float wisp)
{
    return exp(-density * wisp * max(0.0, distM - clearM));
}

#endif
