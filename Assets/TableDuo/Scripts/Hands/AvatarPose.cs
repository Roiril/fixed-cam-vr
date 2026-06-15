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

        /// <summary>人差し指ピンチ（掴み入力）。</summary>
        public bool PinchL;
        public bool PinchR;

        /// <summary>送信側で単調増加する連番。受信側は歯抜けで「凍結 vs パケット欠落」を判別できる
        /// （study-validity: 欠落 frame は意図的静止ではない）。送信ソースごとに採番。</summary>
        public uint Seq;

        /// <summary>送信ソース端末の壁時計 ms（DateTimeOffset.UtcNow）。host 受信時刻との差で
        /// ネット遅延・client 内タイミングを復元する。端末間クロック差はあるので絶対整列は host epoch を使う。</summary>
        public long CaptureMs;

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
            PinchL = src.PinchL;
            PinchR = src.PinchR;
            Seq = src.Seq;
            CaptureMs = src.CaptureMs;
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

        // domain-reload を切った Play では static が前回 Play のレイアウトを引き継ぐ。
        // Editor で役割/手を変えて再生すると古い指レイアウトが混入するので毎 Play クリアする。
        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            CapturedL = null;
            CapturedR = null;
        }

        public int BoneCount;
        public readonly short[] ParentIndex = new short[AvatarPose.BonesPerHand];
        public readonly Vector3[] BindLocalPos = new Vector3[AvatarPose.BonesPerHand];
        public readonly Quaternion[] BindLocalRot = new Quaternion[AvatarPose.BonesPerHand];
    }
}
