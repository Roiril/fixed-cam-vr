#nullable enable
using UnityEngine;

namespace TableDuoVr.Hands
{
    /// <summary>
    /// AvatarPose（手首 pose + bone 回転）と HandSkeletonLayout から
    /// 7 ランドマーク（手首・掌中心・5指先）を順運動学で算出する。
    /// SessionLogger の RQ2/RQ3 分析用（study-design.md §4）。GameObject 不要の純計算。
    /// </summary>
    public static class HandLandmarks
    {
        public const int Count = 7;

        /// <summary>CSV 列順と一致させること。</summary>
        public static readonly string[] Names =
        {
            "wrist", "palm", "thumbTip", "indexTip", "middleTip", "ringTip", "pinkyTip",
        };

        // bone index は HandBoneTable に集約（メッシュ駆動・送信側と同じ BoneId 順を共有）

        // FK 作業バッファ（再利用・アロケーションゼロ）
        private static readonly Vector3[] Pos = new Vector3[AvatarPose.BonesPerHand];
        private static readonly Quaternion[] Rot = new Quaternion[AvatarPose.BonesPerHand];

        /// <summary>
        /// トラッキングスペース基準の 7 ランドマーク位置を results に書き込む。
        /// layout 不在（実機未キャプチャ）の場合は手首位置で全埋めし false を返す。
        /// </summary>
        public static bool Compute(HandSkeletonLayout? layout,
            Vector3 wristPos, Quaternion wristRot, Quaternion[] boneRots, Vector3[] results)
        {
            results[0] = wristPos;
            if (layout == null || layout.BoneCount == 0)
            {
                for (int i = 1; i < Count; i++) results[i] = wristPos;
                return false;
            }

            int n = Mathf.Min(layout.BoneCount, AvatarPose.BonesPerHand);
            for (int i = 0; i < n; i++)
            {
                int p = layout.ParentIndex[i];
                Vector3 parentPos;
                Quaternion parentRot;
                if (p >= 0 && p < i)
                {
                    parentPos = Pos[p];
                    parentRot = Rot[p];
                }
                else
                {
                    parentPos = wristPos;
                    parentRot = wristRot;
                }
                Pos[i] = parentPos + parentRot * layout.BindLocalPos[i];
                Rot[i] = parentRot * boneRots[i];
            }

            results[1] = SafeBone(n, HandBoneTable.Palm, wristPos);
            results[2] = SafeBone(n, HandBoneTable.ThumbTip, wristPos);
            results[3] = SafeBone(n, HandBoneTable.IndexTip, wristPos);
            results[4] = SafeBone(n, HandBoneTable.MiddleTip, wristPos);
            results[5] = SafeBone(n, HandBoneTable.RingTip, wristPos);
            results[6] = SafeBone(n, HandBoneTable.PinkyTip, wristPos);
            return true;
        }

        private static Vector3 SafeBone(int boneCount, int index, Vector3 fallback) =>
            index < boneCount ? Pos[index] : fallback;
    }
}
