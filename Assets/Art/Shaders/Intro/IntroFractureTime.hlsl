#ifndef FIXEDCAM_INTRO_FRACTURE_TIME_INCLUDED
#define FIXEDCAM_INTRO_FRACTURE_TIME_INCLUDED

// 中央と周辺が同じ一撃・スロー・吸引開始を読む。p は既存の5秒の段の進み。
static const float CrackEnd = 0.060;
static const float BreakSpan = 0.040;
static const float PullBegin = 0.520;
static const float PullArrest = 0.120;
static const float PullStart = 0.560;
static const float BurstTau = 0.024;
static const float DriftRate = 0.60;

// 集結（0223 / 0224）。中央の破片と、破片から生まれる光の粒（0235）が同じ着地時刻を読む。
// ⚠ 音（ingest-sounds.py の SWARM_ARRIVE_FIRST_P / SWARM_ARRIVE_SPAN_P）はこの 2 値の写し。片方だけ動かさない。
static const float ArriveFirst = 0.700;  // いちばん軽い片の着地
static const float ArriveSpan = 0.170;   // 着地 = ArriveFirst + ArriveSpan × w^0.6（重い片ほど遅く、密度は終わりへ増える）
static const float PullPowLight = 1.6;   // 進み = t^k。軽い片は早くから動き
static const float PullPowHeavy = 3.5;   // 重い片は遅れて一気に加速する（減速せずに嵌まる）
static const float CloserArrive = 0.900; // 枠を閉じる 3 片は最後（直前の 0.15 秒は静まる）

float Ease(float from, float to, float value)
{
    float t = saturate((value - from) / (to - from));
    return t * t * t * (t * (t * 6.0 - 15.0) + 10.0);
}

// 一撃で行程の8割を進む。その後も漂い続け、引力が立ち上がると止まる。
float WarpedTime(float x, float xs)
{
    float arrested = xs * Ease(0.0, PullArrest, xs);
    return 0.80 * (1.0 - exp(-x / BurstTau)) + DriftRate * (x - arrested);
}

// 回転は漂いが止まった後も順方向に進む。
float SpinTime(float x)
{
    return 0.80 * (1.0 - exp(-x / BurstTau)) + DriftRate * x;
}

// ---- 片ごとの時刻と場所（中央の破片 IntroFracture.shader と、その片から生まれる粒 IntroSpark.shader が同じ値を読む）----

// 覆いのローカル座標（±0.30・IntroFractureMesh.HalfExtentLocal）を、割れ始めの頭を中心とする
// 半径 1.6m のシェルの上の点（撮影時の頭の空間・m）へ。破片の出発点と、粒の放出点が同じ面に乗る。
float3 FractureShellPoint(float2 local)
{
    return normalize(float3(local / 0.15, 1.0)) * 1.6;
}

// 起点（tan 空間 (-0.16, 0.12)・IntroFractureMesh.Impact と同じ点）から見た向きと距離。
// 亀裂の光と破断の波はここから外へ走り、破片も粒もここから放射状に飛ぶ。
// 返り値: xy = 放射の単位ベクトル / z = 起点からの距離 0..1（2.2 で正規化）
float3 FractureRadial(float3 shellPoint)
{
    float2 slope = shellPoint.xy / max(shellPoint.z, 0.01);
    float2 fromOrigin = slope - float2(-0.16, 0.12);
    float distance = length(fromOrigin);
    return float3(fromOrigin / max(distance, 1e-4), saturate(distance / 2.2));
}

// 破断の時刻。大区分の順（macroOrder = macro.z 0..0.14 ＝ 起点からの距離順）に 0.20 秒で全域へ。
// noise は片ごとの乱数 0..1（IntroShardHash2(small.xy, macro.w + 11.0).x）。
float PieceBreakAt(float macroOrder, float noise)
{
    return CrackEnd + macroOrder * (BreakSpan / 0.14) + noise * 0.004;
}

// 大きさの順位 0..1（small.z = sqrt(面積)・覆いのローカル m）。
float PieceSizeRank(float size)
{
    return saturate((size - 0.016) / (0.055 - 0.016));
}

// 重さ = 大きさ・中心からの距離・乱数。noise2 は IntroShardHash2(small.xy + 0.37, macro.w + 29.0).y。
float PieceWeight(float sizeRank, float2 center, float noise2)
{
    float centerDist = saturate(length(center) / 0.15);
    return saturate(0.55 * sizeRank + 0.25 * centerDist + 0.20 * noise2);
}

// 着地の時刻（p）。枠を閉じる片（edgeCloser = small.w ≥ 0.5）は最後。
float PieceArrive(float weight, float edgeCloser)
{
    return lerp(ArriveFirst + ArriveSpan * pow(weight, 0.6), CloserArrive, edgeCloser);
}

// 引きの指数。重い片ほど遅れて一気に来る。
float PiecePullPow(float weight, float edgeCloser)
{
    return lerp(PullPowLight, PullPowHeavy, max(weight, edgeCloser));
}

#endif
