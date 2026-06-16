#nullable enable
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// 解析的 2 ボーン IK（世界座標・1 ステップで厳密解）。肘曲げを“そこそこ”推定するのに使う。
    /// a=上腕根(肩側) / b=肘 / c=手首。target に c が来るよう a,b の回転を解き、pole で肘の向きを決める。
    /// 毎フレーム現在姿勢から再導出するので呼び続けても破綻しない（既知の標準実装）。
    /// </summary>
    public static class TwoBoneIK
    {
        public static void Solve(Transform a, Transform b, Transform c, Vector3 target, Vector3 pole)
        {
            Vector3 ap = a.position, bp = b.position, cp = c.position;
            float lab = Vector3.Distance(ap, bp);
            float lcb = Vector3.Distance(bp, cp);
            float lat = Mathf.Clamp(Vector3.Distance(ap, target), 0.001f, lab + lcb - 0.001f);

            Vector3 ac = cp - ap;
            Vector3 at = target - ap;
            float ac_ab_0 = Mathf.Acos(Mathf.Clamp(Vector3.Dot(ac.normalized, (bp - ap).normalized), -1f, 1f));
            float ba_bc_0 = Mathf.Acos(Mathf.Clamp(Vector3.Dot((ap - bp).normalized, (cp - bp).normalized), -1f, 1f));
            float ac_at_0 = Mathf.Acos(Mathf.Clamp(Vector3.Dot(ac.normalized, at.normalized), -1f, 1f));

            float ac_ab_1 = Mathf.Acos(Mathf.Clamp((lab * lab + lat * lat - lcb * lcb) / (2f * lab * lat), -1f, 1f));
            float ba_bc_1 = Mathf.Acos(Mathf.Clamp((lab * lab + lcb * lcb - lat * lat) / (2f * lab * lcb), -1f, 1f));

            // 曲げ平面の法線（現姿勢）。直線で縮退するときは pole で平面を決める
            Vector3 bendAxis = Vector3.Cross(ac, bp - ap).normalized;
            if (bendAxis.sqrMagnitude < 1e-8f) bendAxis = Vector3.Cross(ac, pole - ap).normalized;
            // 根を target へ向ける回転軸は cross(ac, at)（pole ではない）
            Vector3 aimAxis = Vector3.Cross(ac, at).normalized;

            Quaternion aInv = Quaternion.Inverse(a.rotation);
            Quaternion bInv = Quaternion.Inverse(b.rotation);
            Quaternion r0 = Quaternion.AngleAxis((ac_ab_1 - ac_ab_0) * Mathf.Rad2Deg, aInv * bendAxis);
            Quaternion r1 = Quaternion.AngleAxis((ba_bc_1 - ba_bc_0) * Mathf.Rad2Deg, bInv * bendAxis);
            Quaternion r2 = Quaternion.AngleAxis(ac_at_0 * Mathf.Rad2Deg, aInv * aimAxis);

            a.localRotation = a.localRotation * r0 * r2;
            b.localRotation = b.localRotation * r1;

            // 肘を pole 方向へ：肩→target 軸まわりに上腕をひねる（手先位置は不変＝target 上に保つ）
            Vector3 axis = (target - a.position).normalized;
            Vector3 elbowOnPlane = Vector3.ProjectOnPlane(b.position - a.position, axis);
            Vector3 poleOnPlane = Vector3.ProjectOnPlane(pole - a.position, axis);
            if (elbowOnPlane.sqrMagnitude > 1e-8f && poleOnPlane.sqrMagnitude > 1e-8f)
            {
                float twist = Vector3.SignedAngle(elbowOnPlane, poleOnPlane, axis);
                a.rotation = Quaternion.AngleAxis(twist, axis) * a.rotation;
            }
        }
    }
}
