// 連絡の面の呪いの斑（まだら）の場。
//
// 顔（CommsAvatar.shader）・地と走り書き（CommsPanelPlate.shader）・文字の切断（地のステンシル）が
// **全部この 1 つの場を読む**。面のローカル座標 (m・根の倍率を掛ける前) で解くので、
// 顔の枠の中と地の上で斑が途切れず、同じ塊として繋がって見える。
//
// ⚠⚠ C# の写し `CommsCurseLogic.Field` と同じ式でなければならない。片方だけ直すと、
//    テレメトリの「切られた字数」（commsCx）が画と食い違う。数値は両方に書いてある。
// ⚠ 実行時に乱数を振らない（`canon/LEDGER.md` 0073 — ちらつくと侵食ではなくノイズに見える）。
#ifndef FIXEDCAMVR_COMMS_CURSE_INCLUDED
#define FIXEDCAMVR_COMMS_CURSE_INCLUDED

// 斑の基本セル (m)。細かいオクターブはこの半分。
static const float CURSE_CELL_M = 0.05;
// 細かいオクターブの重み。
static const float CURSE_DETAIL_K = 0.35;
// 境目の柔らかさ（斑の閾値に対する幅）。
static const float CURSE_SOFT = 0.12;
// 文字を切る閾値（この k 以上の画素にステンシルを書く）。
static const float CURSE_CUT = 0.5;

float CurseHash21(float2 p)
{
    p = frac(p * float2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return frac(p.x * p.y);
}

float CurseValueNoise(float2 uv)
{
    float2 i = floor(uv);
    float2 f = frac(uv);
    f = f * f * (3.0 - 2.0 * f);
    float a = CurseHash21(i);
    float b = CurseHash21(i + float2(1.0, 0.0));
    float c = CurseHash21(i + float2(0.0, 1.0));
    float d = CurseHash21(i + float2(1.0, 1.0));
    return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
}

// 面のローカル座標 p (m) → 0..1 の場。
float CurseField(float2 p)
{
    float n1 = CurseValueNoise(p / CURSE_CELL_M + float2(3.7, 1.9));
    float n2 = CurseValueNoise(p / (CURSE_CELL_M * 0.5) + float2(11.3, 7.1));
    return (n1 + CURSE_DETAIL_K * n2) / (1.0 + CURSE_DETAIL_K);
}

// 斑の量 mask (0..1) に対する、その画素の「呪われている度」0..1。
// mask = 1 なら全画素 1、0 なら全画素 0。境目だけが CURSE_SOFT の幅で柔らかい。
float CurseK(float field, float mask)
{
    return saturate((mask * (1.0 + CURSE_SOFT) - field) / CURSE_SOFT);
}

#endif
