#nullable enable
using UnityEngine;

namespace TableDuoVr.Hands
{
    /// <summary>
    /// 1人分のアバター姿勢スナップショット。座標はすべてトラッキングスペース（席）基準のローカル。
    /// 受信側で使い回すため class（参照共有でアロケーションゼロ運用）。
    /// </summary>
    public sealed class AvatarPose
    {
        /// <summary>OVRSkeleton の Hand skeleton bone 数（Hand_Start..Hand_End）。</summary>
        public const int BonesPerHand = 24;

        public Vector3 HeadPos;
        public Quaternion HeadRot = Quaternion.identity;

        public Vector3 WristPosL;
        public Quaternion WristRotL = Quaternion.identity;
        public Vector3 WristPosR;
        public Quaternion WristRotR = Quaternion.identity;

        public bool TrackedL;
        public bool TrackedR;

        public readonly Quaternion[] BonesL = NewIdentityArray();
        public readonly Quaternion[] BonesR = NewIdentityArray();

        public void CopyFrom(AvatarPose src)
        {
            HeadPos = src.HeadPos;
            HeadRot = src.HeadRot;
            WristPosL = src.WristPosL;
            WristRotL = src.WristRotL;
            WristPosR = src.WristPosR;
            WristRotR = src.WristRotR;
            TrackedL = src.TrackedL;
            TrackedR = src.TrackedR;
            System.Array.Copy(src.BonesL, BonesL, BonesPerHand);
            System.Array.Copy(src.BonesR, BonesR, BonesPerHand);
        }

        private static Quaternion[] NewIdentityArray()
        {
            var a = new Quaternion[BonesPerHand];
            for (int i = 0; i < a.Length; i++) a[i] = Quaternion.identity;
            return a;
        }
    }

    /// <summary>
    /// 手スケルトンのバインド構造（親 index とバインドローカル姿勢）。
    /// リモート側で bone 回転から手の形を再構築するのに使う。
    /// ローカルの OVRSkeleton 初期化時に <see cref="HandPoseSampler"/> がキャプチャする。
    /// 録画ファイルにも埋め込まれるので PC（OVR 無し）でも再生できる。
    /// </summary>
    public sealed class HandSkeletonLayout
    {
        public static HandSkeletonLayout? CapturedL;
        public static HandSkeletonLayout? CapturedR;

        public int BoneCount;
        public readonly short[] ParentIndex = new short[AvatarPose.BonesPerHand];
        public readonly Vector3[] BindLocalPos = new Vector3[AvatarPose.BonesPerHand];
        public readonly Quaternion[] BindLocalRot = new Quaternion[AvatarPose.BonesPerHand];
    }
}
