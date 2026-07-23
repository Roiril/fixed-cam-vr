#nullable enable
using TableDuoVr.Hands;
using UnityEngine;

// RemyHandGeometry（internal・純幾何ヘルパ）を EditMode テスト（TableDuoVr.Tests）から検証するため。
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("TableDuoVr.Tests")]

namespace TableDuoVr.Net
{
    /// <summary>
    /// <see cref="RemyAvatarRig"/> から移設した手首基底・W 写像・FK の純幾何ヘルパ（数式不変・behavior-preserving）。
    /// memory が示す最多バグ領域（Remy 指・席フレーム・handSkeletonVersion）で手掌性の符号反転と
    /// FK 親子順の解決が微妙なため、private static を internal static クラスへ移設して EditMode テスト可能にした。
    /// FK バッファ（<see cref="FkPos"/>/<see cref="FkRot"/>/<see cref="FkDone"/>）は毎フレーム呼ぶので static 共有（GC ゼロ）。
    /// </summary>
    internal static class RemyHandGeometry
    {
        /// <summary>W = 「Remy 手ボーンローカル → OVR アンカーローカル」の回転。両側とも同じ実測基底
        /// （指方向 f・手の甲法線 b、左手は cross 符号を反転して物理的な甲に統一）から構築する。</summary>
        public static Quaternion MakeW(Vector3 remyF, Vector3 remyB, Vector3 anchorF, Vector3 anchorB)
            => Quaternion.LookRotation(anchorF, anchorB)
               * Quaternion.Inverse(Quaternion.LookRotation(remyF, remyB));

        /// <summary>手首→中指付け根を指方向 f、(index1-pinky1)×f を甲法線 b とする実測基底
        /// （左手は cross の掌性で符号が反転するため -b に統一 = 常に物理的な手の甲）。</summary>
        public static bool TryHandBasis(Vector3 wrist, Vector3? mid, Vector3? idx, Vector3? pnk,
            bool right, out Vector3 f, out Vector3 b)
        {
            f = Vector3.zero; b = Vector3.zero;
            if (mid == null || idx == null || pnk == null) return false;
            f = mid.Value - wrist;
            if (f.sqrMagnitude < 1e-8f) return false;
            f.Normalize();
            b = Vector3.Cross(idx.Value - pnk.Value, f);
            if (b.sqrMagnitude < 1e-8f) return false;
            b.Normalize();
            if (!right) b = -b; // 左手は掌性で cross が掌側を向く → 甲へ統一
            return true;
        }

        /// <summary>「指=前方やや下・手の甲=上」になる wristRot をアンカー基準から逆算する。</summary>
        public static Quaternion RestRotFor(Vector3 anchorF, Vector3 anchorB)
        {
            var desiredF = new Vector3(0f, -0.21f, 0.98f); // 指: 前・やや下（旧 Euler 12° 相当）
            return Quaternion.LookRotation(desiredF, Vector3.up)
                * Quaternion.Inverse(Quaternion.LookRotation(anchorF, anchorB));
        }

        // FK 作業バッファ（毎フレーム呼ぶので GC ゼロ化）
        internal static readonly Vector3[] FkPos = new Vector3[AvatarPose.BonesPerHand];
        internal static readonly Quaternion[] FkRot = new Quaternion[AvatarPose.BonesPerHand];
        internal static readonly bool[] FkDone = new bool[AvatarPose.BonesPerHand];

        /// <summary>layout（骨長 BindLocalPos・ParentIndex）× live bone 回転をアンカー空間へ FK し、
        /// FkPos/FkDone を埋める。戻り値は解けたボーン数（0=失敗）。ParentIndex は親→子順とは限らないので反復で解く。</summary>
        public static int FkLive(HandSkeletonLayout layout, Quaternion[] liveRots)
        {
            int n = Mathf.Min(layout.BoneCount, Mathf.Min(liveRots.Length, AvatarPose.BonesPerHand));
            if (n < 17) return 0;
            var pos = FkPos; var rot = FkRot; var done = FkDone;
            System.Array.Clear(done, 0, done.Length);
            for (int pass = 0; pass < n; pass++)
            {
                bool progressed = false;
                for (int i = 0; i < n; i++)
                {
                    if (done[i]) continue;
                    int p = layout.ParentIndex[i];
                    if (p < 0 || p >= n)
                    {
                        pos[i] = layout.BindLocalPos[i];
                        rot[i] = liveRots[i];
                        done[i] = true; progressed = true;
                    }
                    else if (done[p])
                    {
                        rot[i] = rot[p] * liveRots[i];
                        pos[i] = pos[p] + rot[p] * layout.BindLocalPos[i];
                        done[i] = true; progressed = true;
                    }
                }
                if (!progressed) break;
            }
            return n;
        }

        /// <summary>FK 結果（FkPos/FkDone）から wrist(0)/index1(6)/middle1(9)/pinky1(16) の実基底を測る。</summary>
        public static bool TryAnchorBasisFromLayout(HandSkeletonLayout layout, Quaternion[] liveRots,
            bool right, out Vector3 f0, out Vector3 b0)
        {
            f0 = Vector3.zero; b0 = Vector3.zero;
            if (layout.BoneCount < 17) return false;
            if (FkLive(layout, liveRots) == 0) return false;
            if (!FkDone[0] || !FkDone[6] || !FkDone[9] || !FkDone[16]) return false;
            return TryHandBasis(FkPos[0], FkPos[9], FkPos[6], FkPos[16], right, out f0, out b0);
        }
    }
}
