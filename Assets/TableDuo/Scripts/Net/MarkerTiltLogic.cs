#nullable enable
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// 「あと6画のくま」のペン（marker）保持中の傾き計算（シーン・ネットワーク非依存 = EditMode テスト可能）。
    /// 意図: ペン先が自然に斜め下を向く。ただし机（描画パッド面）が邪魔なら寝て（水平）、
    /// 持ち上げるほど傾き、最大 <c>maxPitchDeg</c>（=45°）まで下を向く。
    ///
    /// 幾何: ペンは保持点（transform 原点 = バレル底面）を中心に回転する。ペン先は原点から
    /// 長軸（ローカル +Z）方向へ <c>tipDistance</c> 離れている。ペン先が下を向く角度を θ（水平からの俯角）とすると、
    /// ペン先の垂直落差 = tipDistance·sin(θ)。ペン先が面（surfaceY）を貫かない条件は
    /// tipDistance·sin(θ) ≤ heldY − surfaceY。ゆえに許容俯角は asin((heldY−surfaceY)/tipDistance)、上限 45°。
    /// </summary>
    public static class MarkerTiltLogic
    {
        /// <summary>
        /// 保持点高さ・面高さ・ペン先距離から、ペン先が面を貫かない範囲での俯角（度）を返す。
        /// heldY ≤ surfaceY（面より低く持っている）や tipDistance ≤ 0 のときは 0（水平）。
        /// </summary>
        public static float ComputeAllowedPitchDeg(float heldY, float surfaceY, float tipDistance, float maxPitchDeg)
        {
            if (tipDistance <= 0f) return 0f;
            float ratio = Mathf.Clamp01((heldY - surfaceY) / tipDistance);
            float pitch = Mathf.Asin(ratio) * Mathf.Rad2Deg;
            return Mathf.Min(maxPitchDeg, pitch);
        }

        /// <summary>俯角を指数平滑（1-exp(-rate·dt)）で target へ寄せる。dt≤0 は current 据え置き。</summary>
        public static float SmoothPitch(float current, float target, float dt, float rate = 16f)
        {
            if (dt <= 0f) return current;
            float k = 1f - Mathf.Exp(-rate * dt);
            return Mathf.Lerp(current, target, k);
        }

        /// <summary>
        /// 生の forward（保持追従で決まった向き）の水平成分を heading とし、俯角 pitchDeg で下へ倒した
        /// 目標 forward ベクトルを返す。heading = XZ 射影を正規化（縮退時＝真上/真下向きは lastHeading フォールバック）。
        /// dir = (cos·h.x, −sin, cos·h.z)。ロール（軸回りのねじれ）は円筒ペンなので不問（最終回転は
        /// LookRotation(dir, up) で確定 = ロールは捨てる）。
        /// </summary>
        public static Vector3 ComposeTiltedForward(Vector3 rawForward, float pitchDeg,
            Vector3 lastHeading, out Vector3 heading)
        {
            Vector3 flat = new Vector3(rawForward.x, 0f, rawForward.z);
            heading = flat.sqrMagnitude > 1e-8f ? flat.normalized : lastHeading;

            float rad = pitchDeg * Mathf.Deg2Rad;
            float cos = Mathf.Cos(rad);
            float sin = Mathf.Sin(rad);
            return new Vector3(cos * heading.x, -sin, cos * heading.z);
        }
    }
}
