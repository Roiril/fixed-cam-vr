#nullable enable

namespace TableDuoVr.Hands
{
    /// <summary>
    /// OVRSkeleton（legacy Hand, 24 bone）の BoneId 順に関する単一情報源。
    /// 送信側（<see cref="HandPoseSampler"/> の skeleton.Bones 並び）・受信メッシュ駆動
    /// （RemoteHandMeshProvider の名前マッピング）・FK ランドマーク（<see cref="HandLandmarks"/>）が
    /// 同じ BoneId 順を前提にするため、名前と index をここに集約して drift を防ぐ。
    /// 命名は Meta の OVRCustomSkeletonEditor.FbxBoneNameFromBoneId（legacy Hand）に一致させること。
    /// </summary>
    public static class HandBoneTable
    {
        /// <summary>BoneId 0..18（skinnable）の FBX 名（"b_&lt;side&gt;" を前置）。skeleton.Bones の並びと一致。</summary>
        public static readonly string[] SkinnableBoneNames =
        {
            "wrist", "forearm_stub",
            "thumb0", "thumb1", "thumb2", "thumb3",
            "index1", "index2", "index3",
            "middle1", "middle2", "middle3",
            "ring1", "ring2", "ring3",
            "pinky0", "pinky1", "pinky2", "pinky3",
        };

        /// <summary>BoneId 19..23（指先マーカー）の指名（"&lt;side&gt;" 前置 + "_finger_tip_marker"）。</summary>
        public static readonly string[] FingerTipNames = { "thumb", "index", "middle", "ring", "pinky" };

        // ランドマーク用 BoneId（24 bone スケルトン）。CSV 列順 = HandLandmarks.Names と対応。
        public const int Palm = 9;        // 掌中心の代理: 中指基節の根本（middle1）
        public const int ThumbTip = 19;
        public const int IndexTip = 20;
        public const int MiddleTip = 21;
        public const int RingTip = 22;
        public const int PinkyTip = 23;

        /// <summary>BoneId i（0..23）の FBX ボーン名。範囲外は null。isRight で side prefix を切替。</summary>
        public static string? FbxBoneName(int boneId, bool isRight)
        {
            string side = isRight ? "r_" : "l_";
            if (boneId >= 0 && boneId < SkinnableBoneNames.Length)
            {
                return "b_" + side + SkinnableBoneNames[boneId];
            }
            int tip = boneId - SkinnableBoneNames.Length; // 19..23 → 0..4
            if (tip >= 0 && tip < FingerTipNames.Length)
            {
                return side + FingerTipNames[tip] + "_finger_tip_marker";
            }
            return null;
        }
    }
}
