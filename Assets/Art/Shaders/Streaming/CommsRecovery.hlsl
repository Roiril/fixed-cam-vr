#ifndef FIXEDCAMVR_COMMS_RECOVERY_INCLUDED
#define FIXEDCAMVR_COMMS_RECOVERY_INCLUDED
#include "CommsCurse.hlsl"

// ---- ③a / ③b の復帰: 上の辺から人形の面が縦の筋で降りてくる ----
// 出どころは `canon/LEDGER.md` 0356 / 0358（参照の絵の「縦に流れ落ちる」。這い込む髪と熾は不採用）。
// 面を幅の違う縦の列に割り、列ごとに「人形の面がどこまで降りたか」と「その先へ引く筋の長さ」を決める。
// 列の 3 割はスイの側の光（シアンと象牙）で、上の辺から面の外へ昇る。
// ⚠ 定数と式は C# の `CommsStreakLogic` が同じ値で持つ（赤い字を数える）。直すときは 2 か所。
// ⚠ 地・顔・字・罫線が同じ場を読む。_Recovery = (侵食の量 0..1, 復帰の時計 秒, 起動の残り, 0)。
static const float COMMS_STREAK_CELL_M = 0.0032;   // いちばん細い列の幅
static const float COMMS_STREAK_OVER_M = 0.045;    // 面の外へはみ出す筋の長さの上限（地の quad の余白の内）
static const float COMMS_STREAK_FIXED_SEED = 11.0; // 止まっている層
static const float COMMS_STREAK_LIVE_SEED = 500.0; // 組み替わる層（+ 時計から作る番号）
static const float COMMS_STREAK_LIVE_SHARE = 0.61;

// 人形の側の字と顔の色（朱・リニア）と、人形の側の地。
static const float3 COMMS_DOLL_INK = float3(0.79, 0.075, 0.021);
static const float3 COMMS_DOLL_PLATE = float3(0.033, 0.0034, 0.0025);
static const float3 COMMS_STREAK_CYAN = float3(0.033, 0.83, 0.955);
static const float3 COMMS_STREAK_IVORY = float3(0.84, 0.77, 0.62);
// 顔か字を呑んだ列から落ちる筋（朱の 1.25 倍）と、字の赤の写しの色。
static const float3 COMMS_STREAK_SHU = float3(1.0, 0.12, 0.033);
static const float3 COMMS_GHOST_RED = float3(0.80, 0.035, 0.018);
// 面の左端（顔の枠を含む）から測った、顔のセルと字の始まり (m)。`CommsFaceLayout` の MarginM / CellM。
static const float COMMS_FACE_X0_M = 0.022;
static const float COMMS_FACE_X1_M = 0.172;
static const float COMMS_TEXT_X0_M = 0.194;

struct CommsStreakSurface
{
    float k;      // 人形の面か（0 / 1）
    float3 tail;  // 筋の色（リニア）
    float tailA;  // 筋の濃さ 0..1
};

// 列の (番号, 幅の倍率)。細い列が多く、ときどき 4 倍、まれに 12 倍（等幅に並べない）。
float2 CommsStreakColumn(float xl, float seed)
{
    float fine = floor(xl / COMMS_STREAK_CELL_M);
    float mid = floor(fine / 4.0);
    float wide = floor(fine / 12.0);
    if (CurseHash21(float2(wide, seed + 71.0)) < 0.07) return float2(wide, 12.0);
    if (CurseHash21(float2(mid, seed + 37.0)) < 0.24) return float2(mid, 4.0);
    return float2(fine, 1.0);
}

// 組み替わる層の番号。等間隔にしない（0.3〜0.8 秒ほど止まってから跳ぶ）。
float CommsStreakEpoch(float sec)
{
    return floor(sec * 1.9 + 0.6 * sin(sec * 0.7));
}

// 1 層ぶん。xl = 面の左端からの距離 (m)、yd = 上の辺からの深さ (m・下が正)。
void CommsStreakLayer(float xl, float yd, float w, float h, float reach, float seed, float share,
                      inout CommsStreakSurface s)
{
    float2 id = CommsStreakColumn(xl, seed);
    float2 key = float2(id.x + id.y * 517.0, seed);
    float u = CurseHash21(key + float2(1.3, 2.1));
    float kind = CurseHash21(key + float2(5.7, 9.2));
    float hd = CurseHash21(key + float2(11.1, 4.4));
    float hl = CurseHash21(key + float2(3.3, 17.9));
    float hg = CurseHash21(key + float2(23.0, 8.8));
    // 列の中心で決める（列の中で値が変わると縁がぎざぎざになる）。
    float xc = (id.x + 0.5) * id.y * COMMS_STREAK_CELL_M;
    float xn = saturate(xc / max(w, 1e-4));
    // 列は群れる。顔の側と右の端に多く、字の所は少ない。
    float clump = CurseValueNoise(float2(xc / 0.035, seed * 3.1 + 0.5));
    u = saturate(u - 0.45 * (clump - 0.5));
    float bias = 0.34 * exp(-xn / 0.16) + 0.20 * exp(-(1.0 - xn) / 0.10);
    float quiet = (xn > 0.22 && xn < 0.62) ? 0.55 : 1.0;
    float gate = (0.62 * pow(reach, 1.7) + bias * reach) * quiet * share;
    if (u >= gate) return;

    // 深さと長さは短いものが多く、長いものがまれ。
    float depth = saturate(0.75 * exp((hd - 0.5) * 1.9) * reach * 1.25) * h;
    float len = max(0.38 * exp((hl - 0.5) * 2.8) * (0.35 + reach) * h, 1e-4);
    if (kind < 0.32)
    {
        // スイの側の光。面の中では薄い帯、上の辺からは面の外へ昇る。
        float3 col = (kind < 0.24 ? COMMS_STREAK_CYAN : COMMS_STREAK_IVORY) * (0.25 + 0.75 * hg);
        float a = 0.0;
        if (yd >= 0.0 && yd < depth) a = 0.55;
        else if (yd < 0.0) a = pow(1.0 - saturate(-yd / min(len, COMMS_STREAK_OVER_M)), 1.6) * 0.8;
        if (a > s.tailA) { s.tail = col; s.tailA = a; }
    }
    else if (yd >= 0.0 && yd < depth)
    {
        s.k = 1.0;
    }
    else if (yd >= depth)
    {
        // 人形の面の先から流れ落ちる筋。面の下へもはみ出す。色はその列が呑んだ中でいちばん明るいもの:
        // 顔か字に届いた列は明るい朱、地だけの列は暗い朱（試作 `mock.py` の opt_b と同じ選び方）。
        float a = pow(1.0 - saturate((yd - depth) / len), 1.3) * (1.0 - saturate((yd - h) / COMMS_STREAK_OVER_M));
        float onFace = step(COMMS_FACE_X0_M, xc) * step(xc, COMMS_FACE_X1_M) * step(0.12 * h, depth);
        float onText = step(COMMS_TEXT_X0_M, xc) * step(xn, 0.90) * step(0.30 * h, depth) * step(hg, 0.55);
        float3 col = lerp(COMMS_DOLL_PLATE * 2.2, COMMS_STREAK_SHU, saturate(onFace + onText));
        if (a > s.tailA) { s.tail = col; s.tailA = a; }
    }
}

// p = 面のローカル座標 (m)。rect = (中心 x, 中心 y, 幅, 高さ)。recovery.x = 0 なら全部 0。
CommsStreakSurface CommsStreakEval(float2 p, float4 recovery, float4 rect)
{
    CommsStreakSurface s;
    s.k = 0.0; s.tail = 0.0; s.tailA = 0.0;
    float reach = saturate(recovery.x);
    float xl = p.x - (rect.x - rect.z * 0.5);
    float yd = (rect.y + rect.w * 0.5) - p.y;
    if (reach <= 0.001 || xl < 0.0 || xl > rect.z) return s;
    CommsStreakLayer(xl, yd, rect.z, rect.w, reach, COMMS_STREAK_FIXED_SEED, 1.0, s);
    CommsStreakLayer(xl, yd, rect.z, rect.w, reach, COMMS_STREAK_LIVE_SEED + CommsStreakEpoch(recovery.y),
                     COMMS_STREAK_LIVE_SHARE, s);
    // 人形の面の中に筋は引かない。
    s.tailA *= 1.0 - s.k;
    return s;
}

// 顔と字の赤の横ずれ (m)。侵食が強いほど大きい。0 なら 1 画素も変わらない。
float CommsStreakSplitM(float reach)
{
    return (0.0017 + 0.0084 * saturate(reach)) * step(0.001, reach);
}

// 字の行の細い横の擦れ（試作の「行ごとに横へずれる帯」）。字の高さの帯の中だけに、
// 途切れた細い線を引く。返すのは濃さ 0..1、色は象牙か朱（rgb）。組み替わる層の時計で並びが替わる。
float4 CommsStreakRowSmear(float2 p, float4 recovery, float4 rect)
{
    float reach = saturate(recovery.x);
    float xl = p.x - (rect.x - rect.z * 0.5);
    float yd = (rect.y + rect.w * 0.5) - p.y;
    if (reach <= 0.001 || xl < COMMS_TEXT_X0_M || xl > rect.z || yd < 0.20 * rect.w || yd > 0.88 * rect.w)
        return 0.0;
    const float rowM = 0.0024;
    float row = floor(yd / rowM);
    float epoch = CommsStreakEpoch(recovery.y);
    float u = CurseHash21(float2(row, 900.0 + epoch));
    if (u >= 0.16 * reach) return 0.0;
    // 線は帯の中の 0.8mm だけ。
    float inRow = yd - row * rowM;
    if (abs(inRow - rowM * 0.5) > 0.0004) return 0.0;
    float x0 = COMMS_TEXT_X0_M + CurseHash21(float2(row, 913.0 + epoch)) * 0.45 * rect.z;
    float len = 0.04 * exp((CurseHash21(float2(row, 927.0 + epoch)) - 0.5) * 2.4) * rect.z;
    if (xl < x0 || xl > x0 + len) return 0.0;
    // 途切れさせる（6mm ごとに 4 割を抜く）。
    if (CurseHash21(float2(floor(xl / 0.006), row + 31.0 * epoch)) < 0.40) return 0.0;
    float red = step(CurseHash21(float2(row, 941.0 + epoch)), 0.45);
    float3 col = lerp(COMMS_STREAK_IVORY * 0.85, COMMS_GHOST_RED, red);
    return float4(col, 0.55);
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
