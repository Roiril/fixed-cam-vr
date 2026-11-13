#ifndef FIXEDCAMVR_COMMS_RECOVERY_INCLUDED
#define FIXEDCAMVR_COMMS_RECOVERY_INCLUDED
#include "CommsCurse.hlsl"

// ---- ③a / ③b の復帰: 面の縁から這い込む黒髪と、それを焼いて押し返す熾の線 ----
// 出どころは `canon/LEDGER.md` 0270 と 0350（「すごくチープ。根本的にリデザインしてほしい」）。
// 値ノイズの斑をやめ、描かせた髪のシルエットから焼いた「到達順」の版を閾値で切る。
// 版は `tools/comms-creep/bake.py` が焼く（R = 到達順 / G = 房の芯までの深さ / B = 房そのもの）。
// ⚠ 定数と式は C# の `CommsCreepLogic` と bake.py の確認用の描画が同じ値で持つ。直すときは 3 か所。
// ⚠ 地・顔・字が同じ場を読む。_Recovery = (髪の寄り 0..1, 復帰の時計 秒, 起動の残り, 0)。
sampler2D _CommsCreepTex;

static const float COMMS_CREEP_TILE_M = 0.40;    // 版の半分が辺に沿って占める長さ
static const float COMMS_CREEP_REACH_M = 0.24;   // 版の高さが辺から内へ占める長さ
static const float COMMS_CREEP_ROOT_V = 0.12;    // 版の中で根元の塊が終わる高さ
static const float COMMS_CREEP_THETA_MAX = 0.98;
// 辺ごとの (寄り 0 のときの根元の位置 m, 寄り 1 あたりの進み m, 版の横の送り)。負は面の外。
static const float3 COMMS_CREEP_LEFT = float3(-0.20, 0.44, 0.07);
static const float3 COMMS_CREEP_TOP = float3(-0.20, 0.40, 0.31);
static const float3 COMMS_CREEP_RIGHT = float3(-0.24, 0.42, 0.57);

struct CommsCreepSurface
{
    float f;      // 前線からの深さ（版の到達順の単位）。正が髪の側。
    float k;      // 髪の側か 0..1（縁は 1 画素でなめらか）
    float seam;   // 熾の線の芯 0..1
    float glow;   // 熾のにじみ 0..1（両側）
    float core;   // 房の芯までの深さ 0..1
    float strand; // 房そのもの 0..1（0 は房のあいだの墨のにじみ）
};

float CommsCreepTheta(float reach)
{
    return COMMS_CREEP_THETA_MAX * smoothstep(0.0, 0.85, reach);
}

// 上の辺の髪は全幅に並べない（等間隔の房が並ぶと「毛の生えた額縁」に見える）。
// 顔の側と右の角に寄せ、あいだは引かせる。xn = 辺に沿った位置 0..1。戻り値は根元を外へ引く量 (m)。
float CommsCreepTopEbb(float xn)
{
    return 0.07 * smoothstep(0.26, 0.52, xn) * (1.0 - smoothstep(0.84, 1.0, xn));
}

void CommsCreepSide(float d, float along, float span, float3 side, float ebb, float reach, float swallow,
                    float theta, float sec, inout float bestF, inout float2 bestCs)
{
    float adv = side.x + side.y * reach + swallow * span - ebb * (1.0 - swallow);
    float v = (d - adv) / COMMS_CREEP_REACH_M + COMMS_CREEP_ROOT_V;
    float u = along / (2.0 * COMMS_CREEP_TILE_M) + side.z;
    // 房の先ほど大きく、ゆっくり揺れる。周期の合わない 2 つの和（等間隔の往復にしない）。
    float sway = sin(sec * 0.61 + u * 40.0) * 0.6 + sin(sec * 0.23 + u * 17.0 + 1.7) * 0.4;
    u += 0.006 * saturate(v) * sway;
    float3 t = tex2D(_CommsCreepTex, float2(u, saturate(v))).rgb;
    float f = theta - t.r;
    float strand = v < 0.0 ? 1.0 : t.b;
    if (f > bestF)
    {
        bestF = f;
        bestCs = float2(t.g, strand);
    }
}

// p = 面のローカル座標 (m)。rect = (中心 x, 中心 y, 幅, 高さ)。
CommsCreepSurface CommsCreepEval(float2 p, float4 recovery, float4 rect)
{
    CommsCreepSurface s;
    s.f = -1.0; s.k = 0.0; s.seam = 0.0; s.glow = 0.0; s.core = 0.0; s.strand = 0.0;
    float reach = saturate(recovery.x);
    float2 lo = rect.xy - rect.zw * 0.5;
    float2 hi = rect.xy + rect.zw * 0.5;
    float swallow = smoothstep(0.72, 1.0, reach);
    float theta = CommsCreepTheta(reach);
    float bestF = -9.0;
    float2 bestCs = 0.0;
    CommsCreepSide(p.x - lo.x, p.y - lo.y, rect.z, COMMS_CREEP_LEFT, 0.0, reach, swallow, theta, recovery.y, bestF, bestCs);
    CommsCreepSide(hi.y - p.y, p.x - lo.x, rect.w, COMMS_CREEP_TOP, CommsCreepTopEbb((p.x - lo.x) / max(rect.z, 1e-4)), reach, swallow, theta, recovery.y, bestF, bestCs);
    CommsCreepSide(hi.x - p.x, p.y - lo.y, rect.z, COMMS_CREEP_RIGHT, 0.0, reach, swallow, theta, recovery.y, bestF, bestCs);
    // 縁の幅は微分から取らない。画素の勾配は 2x2 画素ごとにしか出ず、辺の切り替わりで跳ねるので、
    // 前線が点々と階段になる（最初の 2 回の焼きで見えた）。到達順は房の中でゆっくり、房の外で 9 倍速く
    // 増えるので、房かどうかで幅を選べば面の上でほぼ同じ太さ（約 1.5mm）になる。
    float fw = lerp(0.050, 0.0075, smoothstep(0.2, 0.8, bestCs.y));
    float px = bestF / fw;
    float on = step(0.001, recovery.x);
    s.f = bestF;
    s.k = smoothstep(-1.0, 1.0, px) * on;
    s.seam = exp(-px * px / 1.6) * on;
    s.glow = exp(-abs(px) / 2.6) * on;
    s.core = bestCs.x;
    s.strand = bestCs.y;
    return s;
}

// 熾の線は途切れ、強弱がある（一様な輪郭線にしない）。版の芯の模様を大きく引いて濃淡に使う。
float CommsCreepEmber(float2 p, float sec)
{
    float2 q = p * float2(0.9, 2.3) + float2(sec * 0.011, 0.37);
    float m = tex2D(_CommsCreepTex, q).g;
    return smoothstep(0.02, 0.55, m) * 0.85 + 0.15;
}

// 髪の側の地の色（リニア）。房は黒、房のあいだは暗い朱のにじみ。前線に近いほど熾が透ける。
float3 CommsCreepInk(CommsCreepSurface s)
{
    // 主従: 見えるのは「暗い朱のにじみの上の、黒い髪の影」。髪の縁を光らせると炎に見える（最初の焼き）。
    float near = exp(-max(s.f, 0.0) / 0.12);
    float3 stain = float3(0.050, 0.0058, 0.0036) * (0.45 + 0.55 * near);
    float3 hair = float3(0.0026, 0.0020, 0.0018) + float3(0.012, 0.0014, 0.0010) * s.core;
    return lerp(stain, hair, smoothstep(0.25, 0.75, s.strand));
}

static const float3 COMMS_EMBER_CORE = float3(1.0, 0.76, 0.48);
static const float3 COMMS_EMBER_GLOW = float3(0.90, 0.085, 0.022);
// 髪の下の字と顔の線の色（人形の嘘の朱より明るい側。黒い髪の上で読める）。
static const float3 COMMS_EMBER_INK = float3(0.93, 0.105, 0.040);

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
