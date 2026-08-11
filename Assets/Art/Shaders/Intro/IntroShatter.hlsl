#ifndef FIXEDCAMVR_INTRO_SHATTER_INCLUDED
#define FIXEDCAMVR_INTRO_SHATTER_INCLUDED

// 段 4「現実が割れてスクリーンへ入る」の**時計と形**。
//
// ⚠ **この式は 2 つのシェーダが共有する**（覆い = IntroVeil / 封印の箱 = SealedBox）。
// 両方で同じ格子・同じ順番・同じ速さで割れることが演出の全部なので、片方に写して
// 別々に育てない（このリポジトリが何度も踏んだ「沈黙して食い違う」の型）。
// 数値そのものは C# の `IntroShatterCurve` が持ち、uniform で降りてくる。
//
// 空間は各シェーダに任せる。ここが返すのは**スカラーだけ**（進み・隙間・回り・移動・縮み・閉じ）。
// 覆いは自分の面の 2D で、箱は world の 3D で、同じスカラーを当てる。

struct IntroShard
{
    float p;        // このセルの進み 0..1
    float gap;      // 隙間（セルの寸法に対する割合）
    float spin;     // 回り (rad)
    float travel;   // 行き先へどれだけ寄ったか 0..1
    float shrink;   // 寸法の倍率 1..0
    float closed;   // 閉じた量 0..1（1 = 現実を返し終えた）
    float2 drift;   // 外れるときの微小なずれ（単位は呼び出し側の空間）
};

// far: 0 = スクリーンの上 / 1 = いちばん遠い周縁。**周縁から先に割れる**。
// r, r2: セルごとの乱数 0..1
float2 IntroShardHash2(float2 p, float seed)
{
    float3 p3 = frac(float3(p.xyx) * float3(0.1031, 0.1030, 0.0973) + seed);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.xx + p3.yz) * p3.zy);
}

// 「スクリーン矩形からどれだけ離れて見えるか」と「どこへ吸い込まれるか」を同時に解く。
// すべて覆いのローカル空間（頭固定・m）。
//
// ⚠⚠ **視線と矩形の面の交点で測る。直交投影で測ってはいけない。**
// 直交投影（点から面へ垂線を下ろす）だと、矩形の中を向いているセルでも投影先の
// **見かけの方向が元とずれる**ので、隔たりが 0 にならない。実測（2026-08-12 のプレビュー）で
// 枠の中のセルまで割れ、段 4 の終わりが「黒 + 枠の中も黒」になった。
// 交点で測れば、矩形の中を向いていれば隔たりは厳密に 0 になる。
//
// p:      セルの中心（覆いのローカル空間・m）
// sc/sr/su/sHalf: スクリーン矩形（中心・右・上・半寸法）
// planeZ: 覆いの面までの距離（見かけの隔たりをこの面の上の m で測る）
// 返り値: 見かけの隔たり (m)。out qPlane = 覆いの面の上の行き先 / out qLocal = 矩形の上の行き先。
float IntroShardTarget(float3 p, float3 sc, float3 sr, float3 su, float2 sHalf, float planeZ,
                       out float2 qPlane, out float3 qLocal)
{
    float3 n = cross(sr, su);
    float denom = dot(p, n);
    // 面と平行 / 眼の後ろ。どちらも段 4 では起きないが、起きたら「いちばん遠い周縁」に倒す。
    float3 h = (abs(denom) < 1e-5 || p.z <= 0.01) ? p : p * (dot(sc, n) / denom);

    float3 rel = h - sc;
    float a = clamp(dot(rel, sr), -sHalf.x, sHalf.x);
    float b = clamp(dot(rel, su), -sHalf.y, sHalf.y);
    qLocal = sc + sr * a + su * b;

    float2 pv = p.xy * (planeZ / max(p.z, 1e-3));
    qPlane = qLocal.xy * (planeZ / max(qLocal.z, 1e-3));
    return length(pv - qPlane);
}

IntroShard IntroShardEval(float far, float r, float r2, float shatter,
                          float stagger, float jitter, float gapMax, float spinMax,
                          float driftMax, float pullAt, float travelMax,
                          float shrinkAt, float closeAt)
{
    IntroShard s;
    far = saturate(far);

    // 出発の遅れ。**内側ほど遅く、そして内側ほど揃う**（ジッタも far で薄める）。
    // 揃えないと、スクリーンのすぐ脇のセルが最後まで数個だけ残って目に付く。
    float start = (1.0 - far) * stagger + r * jitter * far;
    float span = max(1.0 - stagger - jitter, 0.05);
    s.p = saturate((shatter - start) / span);
    float p = s.p;

    s.gap = gapMax * smoothstep(0.0, 0.18, p);
    s.spin = (r * 2.0 - 1.0) * spinMax * smoothstep(0.08, 0.90, p);
    s.drift = (float2(r, r2) * 2.0 - 1.0) * driftMax * smoothstep(0.10, 0.60, p);

    // 引きは二乗 ＝ 加速。落ちるのではなく**吸われる**。
    float pull = saturate((p - pullAt) / max(1.0 - pullAt, 0.05));
    s.travel = travelMax * pull * pull;

    // ⚠ **縮みは寄った分に比例させる**。放射状に集まるので、寸法を保つと行き先の近くで
    //    破片どうしが重なる。覆いの側は乗算ブレンドで穴が必ず勝つため、重なると
    //    「破片が集まる」ではなく**1 つの大きな穴に融合する**（＝吸い込まれる画が出ない）。
    s.shrink = (1.0 - s.travel) * (1.0 - smoothstep(shrinkAt, 1.0, p));
    s.closed = smoothstep(closeAt, 1.0, p);
    return s;
}

// 2D の回転（覆いの面用）。
float2 IntroShardSpin2(float2 v, float ang)
{
    float s, c;
    sincos(ang, s, c);
    return float2(v.x * c - v.y * s, v.x * s + v.y * c);
}

// 軸まわりの回転（箱の面用。面の法線を軸にすれば破片は平らなまま回る）。
// ⚠ 大きく回すと裏を向く。箱は `Cull Back` なので裏を向いた破片は**消える** —
//    だから振れ幅は C# 側で ±35° に抑えてある。
float3 IntroShardSpin3(float3 v, float3 axis, float ang)
{
    float s, c;
    sincos(ang, s, c);
    return v * c + cross(axis, v) * s + axis * dot(axis, v) * (1.0 - c);
}

#endif
