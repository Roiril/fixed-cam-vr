#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming.Cg
{
    /// <summary>
    /// 解析的な 2 ボーン IK（肩 → 肘 → 手首）。純関数・UnityEngine の数学型のみに依存する。
    ///
    /// 目標が腕の長さを超えたら**まっすぐ伸ばして届く範囲へクランプ**する（発散させない）。
    /// 長さ 0 や退化した pole でも NaN を出さないことをテストで固定している
    /// （人形が一瞬でも消し飛ぶと演出が壊れるため、数値の安全側倒しを優先する）。
    /// </summary>
    public static class TwoBoneIk
    {
        /// <summary>完全に伸ばし切らない係数（真っ直ぐだと肘の向きが決まらず暴れる）。</summary>
        public const float MaxReachRatio = 0.995f;

        /// <param name="root">肩のワールド位置</param>
        /// <param name="target">手首の目標ワールド位置</param>
        /// <param name="poleHint">肘を向けたい方向の参照点（体の後ろ下あたり）</param>
        /// <param name="upperLen">上腕長</param>
        /// <param name="lowerLen">前腕長</param>
        /// <param name="joint">解いた肘のワールド位置</param>
        /// <param name="end">解いた手首のワールド位置（届かない場合はクランプ済み）</param>
        public static void Solve(Vector3 root, Vector3 target, Vector3 poleHint,
                                 float upperLen, float lowerLen,
                                 out Vector3 joint, out Vector3 end)
        {
            upperLen = Mathf.Max(1e-4f, upperLen);
            lowerLen = Mathf.Max(1e-4f, lowerLen);

            Vector3 toTarget = target - root;
            float dist = toTarget.magnitude;
            // 目標が肩に重なっている等の退化時は「腕を下ろす」向きを既定にする。
            Vector3 dir = dist > 1e-5f ? toTarget / dist : Vector3.down;

            float maxReach = (upperLen + lowerLen) * MaxReachRatio;
            float minReach = Mathf.Abs(upperLen - lowerLen) + 1e-3f;
            float reach = Mathf.Clamp(dist, minReach, maxReach);
            end = root + dir * reach;

            // 余弦定理で肩の開き角を出し、pole 方向へ肘を倒す。
            float cos = (upperLen * upperLen + reach * reach - lowerLen * lowerLen) / (2f * upperLen * reach);
            float angle = Mathf.Acos(Mathf.Clamp(cos, -1f, 1f));

            Vector3 perp = Vector3.ProjectOnPlane(poleHint - root, dir);
            if (perp.sqrMagnitude < 1e-8f) perp = Vector3.ProjectOnPlane(Vector3.forward, dir);
            if (perp.sqrMagnitude < 1e-8f) perp = Vector3.ProjectOnPlane(Vector3.right, dir);
            if (perp.sqrMagnitude < 1e-8f) perp = Vector3.up;   // dir がどの軸とも平行になり得ない保険
            perp = perp.normalized;

            joint = root + upperLen * (Mathf.Cos(angle) * dir + Mathf.Sin(angle) * perp);
        }
    }
}
