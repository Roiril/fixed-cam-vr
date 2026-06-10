#nullable enable
using UnityEngine;

namespace TableDuoVr.Hands
{
    /// <summary>
    /// OVRHand / OVRSkeleton から毎フレーム AvatarPose をサンプルする実トラッキングソース。
    /// 座標は trackingSpace 基準ローカルに変換する（席アラインと独立にするため）。
    /// スケルトン初期化時に HandSkeletonLayout もキャプチャする。
    /// </summary>
    public sealed class HandPoseSampler : MonoBehaviour, IHandPoseSource
    {
        [SerializeField] private Transform? rigRoot;        // OVRCameraRig ルート（席アライン用に公開）
        [SerializeField] private Transform? trackingSpace;  // 座標基準（OVRCameraRig/TrackingSpace）
        [SerializeField] private Transform? centerEye;      // CenterEyeAnchor
        [SerializeField] private OVRHand? leftHand;
        [SerializeField] private OVRHand? rightHand;
        [SerializeField] private OVRSkeleton? leftSkeleton;
        [SerializeField] private OVRSkeleton? rightSkeleton;

        private readonly AvatarPose _pose = new();

        public AvatarPose Current => _pose;
        public bool IsValid => trackingSpace != null && centerEye != null;
        public int Priority => 0;

        /// <summary>席アライン対象のリグルート。L0（リグ無し）では null。</summary>
        public Transform? RigRoot => rigRoot;

        private void OnEnable() => HandPoseSourceRegistry.Register(this);
        private void OnDisable() => HandPoseSourceRegistry.Unregister(this);

        private void LateUpdate()
        {
            if (trackingSpace == null || centerEye == null) return;

            ToLocal(trackingSpace, centerEye, out _pose.HeadPos, out _pose.HeadRot);

            _pose.TrackedL = SampleHand(trackingSpace, leftHand, leftSkeleton,
                ref _pose.WristPosL, ref _pose.WristRotL, _pose.BonesL);
            _pose.TrackedR = SampleHand(trackingSpace, rightHand, rightSkeleton,
                ref _pose.WristPosR, ref _pose.WristRotR, _pose.BonesR);

            _pose.PinchL = _pose.TrackedL && leftHand != null &&
                leftHand.GetFingerIsPinching(OVRHand.HandFinger.Index);
            _pose.PinchR = _pose.TrackedR && rightHand != null &&
                rightHand.GetFingerIsPinching(OVRHand.HandFinger.Index);

            CaptureLayoutIfReady(leftSkeleton, ref HandSkeletonLayout.CapturedL);
            CaptureLayoutIfReady(rightSkeleton, ref HandSkeletonLayout.CapturedR);
        }

        private static bool SampleHand(Transform space, OVRHand? hand, OVRSkeleton? skeleton,
            ref Vector3 wristPos, ref Quaternion wristRot, Quaternion[] bones)
        {
            if (hand == null || !hand.IsTracked) return false;

            ToLocal(space, hand.transform, out wristPos, out wristRot);

            if (skeleton != null && skeleton.IsInitialized)
            {
                var list = skeleton.Bones;
                int n = Mathf.Min(list.Count, AvatarPose.BonesPerHand);
                for (int i = 0; i < n; i++)
                {
                    bones[i] = list[i].Transform.localRotation;
                }
            }
            return true;
        }

        private static void CaptureLayoutIfReady(OVRSkeleton? skeleton, ref HandSkeletonLayout? slot)
        {
            if (slot != null || skeleton == null || !skeleton.IsInitialized) return;
            var list = skeleton.Bones;
            if (list.Count == 0) return;

            var layout = new HandSkeletonLayout
            {
                BoneCount = Mathf.Min(list.Count, AvatarPose.BonesPerHand)
            };
            for (int i = 0; i < layout.BoneCount; i++)
            {
                layout.ParentIndex[i] = list[i].ParentBoneIndex;
                layout.BindLocalPos[i] = list[i].Transform.localPosition;
                layout.BindLocalRot[i] = list[i].Transform.localRotation;
            }
            slot = layout;
        }

        private static void ToLocal(Transform space, Transform target, out Vector3 pos, out Quaternion rot)
        {
            pos = space.InverseTransformPoint(target.position);
            rot = Quaternion.Inverse(space.rotation) * target.rotation;
        }
    }
}
