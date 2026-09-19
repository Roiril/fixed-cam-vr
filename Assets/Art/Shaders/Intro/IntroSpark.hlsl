#ifndef FIXEDCAM_INTRO_SPARK_INCLUDED
#define FIXEDCAM_INTRO_SPARK_INCLUDED

// 光の粒（LEDGER 0235）。破片の面（IntroFracture.shader）と粒（Resources/IntroSpark.shader）が
// **同じ色の表と同じ「代表の粒」の軌道**を読む。粒が破片を照らし返す反射は、ここで決めた
// 代表の粒（HeroSpark）の位置を点光源にして作る。粒側も同じ関数で同じ場所に描くので、
// 面に映る閃きの出どころが必ず画の中に居る。
//
// 依存: IntroFractureTime.hlsl（CrackEnd / PullBegin / PullStart / WarpedTime / Ease）を先に include すること。

// ---- 色（0010「全体的に暖色に寄せてほしい」との折り合い）----
// 朱 → 琥珀 → 金 の暖色だけの表。⚠ チャンネルごとに位相をずらした cos パレットは、振幅を絞っても
// 色相そのものは一周する（実機の周辺片が青みの灰色に写った・2026-09-19）。R ≥ G ≥ B を崩さない 3 点の補間にする。
static const float3 SparkPaletteShu = float3(0.86, 0.42, 0.30);
static const float3 SparkPaletteKohaku = float3(0.90, 0.70, 0.40);
static const float3 SparkPaletteKin = float3(0.92, 0.84, 0.58);
// 粒の芯・面の閃きの芯。生成り寄りの白（題字と同じ側の色）。
static const float3 SparkCoreColor = float3(0.98, 0.94, 0.85);

float3 SparkPalette(float t)
{
    float s = 0.5 + 0.5 * cos(6.2831853 * t);
    return s < 0.5
        ? lerp(SparkPaletteShu, SparkPaletteKohaku, s * 2.0)
        : lerp(SparkPaletteKohaku, SparkPaletteKin, (s - 0.5) * 2.0);
}

// ---- 代表の粒（点光源を兼ねる）----
static const int HeroSparkCount = 6;
static const float HeroDeathMin = 0.64;
static const float HeroDeathMax = 0.78;
static const float HeroMaxRadius = 1.45;   // 周辺の殻（半径 1.6m・ZWrite On）の内側に留める

float HeroHash(float k, float salt)
{
    return frac(sin(k * 12.9898 + salt * 78.233) * 43758.5453);
}

// k: 0..HeroSparkCount-1 / p: 段 4 の進み 0..1
// captureHeadToWorld: 割れ始めに固定した頭の姿勢（_CaptureHeadToWorld）
// screenCenterWS: スクリーン矩形の中心（world）。集結でここへ引かれる
// 返り値: posWS = world の位置 / intensity = 明るさ 0..1（0 = まだ生まれていない or 消えた）
void HeroSpark(int k, float p, float4x4 captureHeadToWorld, float3 screenCenterWS,
               out float3 posWS, out float intensity)
{
    float fk = (float)k;
    float h0 = HeroHash(fk, 1.0);
    float h1 = HeroHash(fk, 2.0);
    float h2 = HeroHash(fk, 3.0);

    // 起点（tan 空間 (-0.16, 0.12) をシェル半径 1.6m へ。IntroFracture.shader の起点と同じ点）。
    float3 origin = normalize(float3(-0.16, 0.12, 1.0)) * 1.6;
    // 60° 間隔＋揺らぎで放射。眼側（-z）へわずかに寄せて、破片の手前を横切る粒を作る。
    float angle = fk * 1.0471976 + (h0 - 0.5) * 0.6;
    float3 direction = normalize(float3(cos(angle), sin(angle), -0.10 - 0.25 * h1));
    float breakAt = CrackEnd + 0.004 * fk;
    float elapsed = max(p - breakAt, 0.0);
    float u = WarpedTime(elapsed, max(p - PullBegin, 0.0));
    float reach = lerp(0.55, 0.85, h2);
    float3 flight = origin + direction * reach * u
                  + float3(sin(u * 3.1 + fk), cos(u * 2.3 + fk * 1.7), 0.0) * 0.02;
    float radius = length(flight);
    if (radius > HeroMaxRadius) flight *= HeroMaxRadius / radius;

    float death = lerp(HeroDeathMin, HeroDeathMax, h1);
    float pullT = saturate((p - PullStart) / max(death - PullStart, 0.01));
    float travel = pow(pullT, 1.8);
    float3 flightWS = mul(captureHeadToWorld, float4(flight, 1.0)).xyz;
    posWS = lerp(flightWS, screenCenterWS, travel);

    // 明るさ: 破断で点き、一撃は 0.7、スローで 1.0、死ぬ 0.05 前から消えて、最後の 0.03 に閃く。
    float born = Ease(breakAt, breakAt + 0.010, p);
    float shockPhase = 1.0 - Ease(CrackEnd + 0.030, CrackEnd + 0.060, p);
    float base = lerp(1.0, 0.7, shockPhase);
    float fade = 1.0 - Ease(death - 0.05, death, p);
    float flash = 0.8 * Ease(death - 0.03, death - 0.01, p) * (1.0 - Ease(death - 0.01, death, p));
    intensity = born * (base * fade + flash) * step(p, death);
}

#endif
