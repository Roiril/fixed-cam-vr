// 増えるスクリーンの面（ScreenMultiply.shader）と暈（ScreenMultiplyGlow.shader）が同じ式で読むもの。
// 片方だけ直すと、面と暈が別の色・別の場所へ動く。値の正は ScreenMultiply / ScreenMultiplyLayout（C#）。
#ifndef FIXEDCAM_SCREEN_MULTIPLY_COMMON_INCLUDED
#define FIXEDCAM_SCREEN_MULTIPLY_COMMON_INCLUDED

// 席ごとの点いた時刻・消えた時刻。4 席ずつ詰めてある（ScreenMultiplyLayout.MaxSeats / 4 と対）。
float4 _WallOpenAt[40];
float4 _WallClosedAt[40];

// 0..3 の番号の成分だけ 1 にした float4（帯の番号・詰めた表の何番目か）。
float4 WallLane(float k)
{
    return float4(k < 0.5, abs(k - 1.0) < 0.5, abs(k - 2.0) < 0.5, k > 2.5);
}

float WallSeatOpenAt(float seatIndex)
{
    float slot = floor(seatIndex * 0.25);
    return dot(_WallOpenAt[(int)slot], WallLane(seatIndex - slot * 4.0));
}

float WallSeatClosedAt(float seatIndex)
{
    float slot = floor(seatIndex * 0.25);
    return dot(_WallClosedAt[(int)slot], WallLane(seatIndex - slot * 4.0));
}

// 電源が切れた後の残光 1..0。消えていない席・残光の終わった席は 0。
float WallAfterglow(float open, float closedAt, float now, float afterglowSec)
{
    if (open > 0.001 || closedAt > 1e8) return 0.0;
    return saturate(1.0 - (now - closedAt) / max(0.01, afterglowSec));
}

// 点いた直後は橙（warm.rgb）、warm.w 秒かけて席の色へ落ち着く。ScreenMultiplyLayout.TintAt と同じ式。
float3 WallTint(float3 seatTint, float sinceOpen, float4 warm)
{
    float t = saturate(sinceOpen / max(0.01, warm.w));
    return lerp(warm.rgb, seatTint, t * t * (3.0 - 2.0 * t));
}

// 頭を通る鉛直軸のまわりに回す（正は方位が増える向き。Quaternion.Euler(0, 角, 0) と同じ）。
float3 WallRotateY(float3 p, float angleRad)
{
    float s = sin(angleRad), c = cos(angleRad);
    return float3(p.x * c + p.z * s, p.y, -p.x * s + p.z * c);
}

// 床（群れの座標の y = floorY）に映した位置。水平にごく弱く揺らぐ（水面に映った灯）。
// look = (明るさ, 足すぼけ, 揺らぎの振幅 rad, 揺らぎの周期 秒)。
float3 WallMirror(float3 p, float floorY, float4 look, float now)
{
    p.y = 2.0 * floorY - p.y;
    float sway = look.z * sin(6.2831853 * now / max(0.1, look.w) + length(p.xz) * 1.7);
    return WallRotateY(p, sway);
}

// 床の映り込みは頭から離れるほど薄く、fadeM で消える。
float WallMirrorFade(float3 p, float fadeM)
{
    return 1.0 - smoothstep(fadeM * 0.35, fadeM, length(p.xz));
}

#endif
