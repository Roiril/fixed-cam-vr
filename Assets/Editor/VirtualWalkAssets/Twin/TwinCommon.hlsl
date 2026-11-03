// デジタルツインの部屋（VirtualRoom）が全シェーダで共有する大域値。撮影の瞬間だけ VirtualRoom.BeginShot が書く。
#ifndef TWIN_COMMON_INCLUDED
#define TWIN_COMMON_INCLUDED

float4 _TwinAmbient;      // rgb = 環境光（床から天井へ明るくなる前の値）
float4 _TwinFogColor;     // rgb = 霧の色
float  _TwinFogDensity;   // 1m あたりの減衰
float  _TwinTime;         // 撮影時刻（埃の漂い・霞の流れ）
float4 _TwinKeyPos;       // xyz = 電球の主光（スポット）の位置
float4 _TwinKeyDir;       // xyz = 主光の向き
float4 _TwinKeyCos;       // x = 外側の cos(半角), y = 内側の cos(半角)
float4 _TwinKeyColor;     // rgb = 主光の色×強さ（URP が渡す追加ライトの色と同じ値。霞はこの光だけを積分する）

// 主光の円錐の中か（0..1）。霞と埃は円錐の中だけに出す
float TwinKeyCone(float3 positionWS)
{
    float c = dot(normalize(positionWS - _TwinKeyPos.xyz), _TwinKeyDir.xyz);
    return smoothstep(_TwinKeyCos.x, _TwinKeyCos.y, c);
}

float TwinHash13(float3 p)
{
    p = frac(p * 0.1031);
    p += dot(p, p.zyx + 31.32);
    return frac((p.x + p.y) * p.z);
}

float TwinNoise3(float3 p)
{
    float3 i = floor(p);
    float3 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);
    float n000 = TwinHash13(i);
    float n100 = TwinHash13(i + float3(1, 0, 0));
    float n010 = TwinHash13(i + float3(0, 1, 0));
    float n110 = TwinHash13(i + float3(1, 1, 0));
    float n001 = TwinHash13(i + float3(0, 0, 1));
    float n101 = TwinHash13(i + float3(1, 0, 1));
    float n011 = TwinHash13(i + float3(0, 1, 1));
    float n111 = TwinHash13(i + float3(1, 1, 1));
    return lerp(lerp(lerp(n000, n100, f.x), lerp(n010, n110, f.x), f.y),
                lerp(lerp(n001, n101, f.x), lerp(n011, n111, f.x), f.y), f.z);
}

float TwinFbm(float3 p)
{
    float v = 0.0, a = 0.5;
    [unroll] for (int k = 0; k < 4; k++) { v += a * TwinNoise3(p); p = p * 2.03 + 17.1; a *= 0.5; }
    return v;
}

float2 TwinHash22(float2 c) { return float2(TwinHash13(float3(c, 1.7)), TwinHash13(float3(c, 5.3))); }

// ボロノイ: いちばん近い欠片の番号と、その欠片の縁までの正確な距離（セル単位）
void TwinVoronoi(float2 x, out float2 cellId, out float edge)
{
    float2 n = floor(x), f = frac(x);
    float d1 = 8.0;
    float2 id = n, r1 = 0;
    [unroll] for (int j = -1; j <= 1; j++)
        [unroll] for (int i = -1; i <= 1; i++)
        {
            float2 g = float2(i, j);
            float2 r = g + TwinHash22(n + g) - f;
            float d = dot(r, r);
            if (d < d1) { d1 = d; id = n + g; r1 = r; }
        }
    float md = 8.0;
    [unroll] for (int j2 = -2; j2 <= 2; j2++)
        [unroll] for (int i2 = -2; i2 <= 2; i2++)
        {
            float2 g = float2(i2, j2);
            float2 r = g + TwinHash22(n + g) - f;
            float2 dr = r - r1;
            if (dot(dr, dr) > 1e-5) md = min(md, dot(0.5 * (r1 + r), normalize(dr)));
        }
    cellId = id;
    edge = md;
}

float3 TwinApplyFog(float3 color, float3 positionWS)
{
    float d = distance(positionWS, _WorldSpaceCameraPos);
    float f = exp(-_TwinFogDensity * d);
    return lerp(_TwinFogColor.rgb, color, f);
}

#endif
