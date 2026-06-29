#nullable enable
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// 解析的 2 ボーン IK（世界座標・前乗算・1 ステップ厳密解）。肩(upper)→肘(lower)→手首(end) で
    /// end が target に**必ず届く**よう解き、pole で肘の向きを決める。
    ///
    /// 手順（各ステップがワールド空間で意味が明快・前乗算 = ワールドで回す）:
    ///   1. 余弦定理で肩・肘の内角を目標値へ（現在の肢平面内で曲げる → 三角形の形が決まり |a→c|=lat）
    ///   2. FromToRotation(a→c, a→target) で肩を回し、end を target 直線上に乗せる（|a→c|=lat=|a→target| なので end は target に一致）
    ///   3. a→target 軸まわりに肩をひねって肘を pole 側へ（target は軸上なので end は動かない）
    /// 毎フレ base 姿勢から解き直す前提（累積しない）。原版は局所軸・後乗算で照準/ひねりが噛み合わず
    /// 遠い/低い target に届かなかった。本版は照準を FromToRotation で強制するので必ず届く。
    /// </summary>
    public static class TwoBoneIK
    {
        public static void Solve(Transform upper, Transform lower, Transform end, Vector3 target, Vector3 pole)
        {
            const float eps = 1e-8f;
            Vector3 a = upper.position;
            Vector3 b = lower.position;
            Vector3 c = end.position;

            float lab = (b - a).magnitude;
            float lcb = (c - b).magnitude;
            if (lab < 1e-5f || lcb < 1e-5f) return;
            float lat = Mathf.Clamp((target - a).magnitude, 1e-4f, lab + lcb - 1e-4f);

            Vector3 ac = c - a;
            Vector3 ab = b - a;
            if (ac.sqrMagnitude < eps) return;

            // 1) 内角を目標へ（現肢平面で曲げる）
            float ac_ab_0 = Mathf.Acos(Mathf.Clamp(Vector3.Dot(ac.normalized, ab.normalized), -1f, 1f));
            float ba_bc_0 = Mathf.Acos(Mathf.Clamp(Vector3.Dot((a - b).normalized, (c - b).normalized), -1f, 1f));
            float ac_ab_1 = Mathf.Acos(Mathf.Clamp((lab * lab + lat * lat - lcb * lcb) / (2f * lab * lat), -1f, 1f));
            float ba_bc_1 = Mathf.Acos(Mathf.Clamp((lab * lab + lcb * lcb - lat * lat) / (2f * lab * lcb), -1f, 1f));

            Vector3 axis = Vector3.Cross(ac, ab);
            if (axis.sqrMagnitude < eps) axis = Vector3.Cross(ac, pole - a); // 直線で縮退 → pole で平面決定
            if (axis.sqrMagnitude < eps) axis = Vector3.Cross(ac, Vector3.up);
            if (axis.sqrMagnitude < eps) return;
            axis.Normalize();

            upper.rotation = Quaternion.AngleAxis((ac_ab_1 - ac_ab_0) * Mathf.Rad2Deg, axis) * upper.rotation;
            lower.rotation = Quaternion.AngleAxis((ba_bc_1 - ba_bc_0) * Mathf.Rad2Deg, axis) * lower.rotation;

            // 2) 照準: end を target 直線上へ（|a→c|=lat なので end は target に一致）
            Vector3 ac2 = end.position - a;
            if (ac2.sqrMagnitude > eps)
            {
                upper.rotation = Quaternion.FromToRotation(ac2, target - a) * upper.rotation;
            }

            // 3) a→target 軸まわりにひねって肘を pole 側へ（end は軸上なので不動）
            Vector3 n = (target - a).normalized;
            Vector3 elbowProj = Vector3.ProjectOnPlane(lower.position - a, n);
            Vector3 poleProj = Vector3.ProjectOnPlane(pole - a, n);
            if (elbowProj.sqrMagnitude > eps && poleProj.sqrMagnitude > eps)
            {
                float twist = Vector3.SignedAngle(elbowProj, poleProj, n);
                upper.rotation = Quaternion.AngleAxis(twist, n) * upper.rotation;
            }
        }
    }
}
