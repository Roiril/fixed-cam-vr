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

#endif
