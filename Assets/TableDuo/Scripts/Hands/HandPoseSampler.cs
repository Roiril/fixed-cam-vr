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

        // 手トラッキングの診断ログ（1Hz, [TDV-DIAG]）。トラッキング不調の切り分け用。
        // 既定オフ。必要時に Inspector か manage_components set_property で true にして実機ログを見る
        [Header("診断")]
        [SerializeField] private bool logDiagnostics;

        private readonly AvatarPose _pose = new();
        private float _nextDiagLog;

        public AvatarPose Current => _pose;
        public bool IsValid => trackingSpace != null && centerEye != null;
        public int Priority => 0;

        /// <summary>席アライン対象のリグルート。L0（リグ無し）では null。</summary>
        public Transform? RigRoot => rigRoot;

        /// <summary>頭（CenterEyeAnchor）。手動リセットで「頭→席」を合わせるのに使う。L0 では null。</summary>
        public Transform? CenterEye => centerEye;

        /// <summary>片手モード: 左手を抑制（pose 非送信 + ローカル描画も隠す＝身体感の一貫性）。</summary>
        public bool SuppressLeftHand
        {
            get => _suppressLeft;
            set
            {
                _suppressLeft = value;
                if (leftHand != null) leftHand.gameObject.SetActive(!value);
            }
        }

        private bool _suppressLeft;

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

            if (_suppressLeft)
            {
                _pose.TrackedL = false;
                _pose.PinchL = false;
            }

            CaptureLayoutIfReady(leftSkeleton, ref HandSkeletonLayout.CapturedL);
            CaptureLayoutIfReady(rightSkeleton, ref HandSkeletonLayout.CapturedR);

            if (logDiagnostics && Time.time >= _nextDiagLog)
            {
                _nextDiagLog = Time.time + 1f;
                Debug.Log("[TDV-DIAG] " + Describe("L", leftHand, leftSkeleton, _suppressLeft) +
                          " || " + Describe("R", rightHand, rightSkeleton, suppressed: false) +
                          $" || poseTrackedR={_pose.TrackedR} wristR={_pose.WristPosR:F2} headLocal={_pose.HeadPos:F2}");
            }
        }

        private static string Describe(string tag, OVRHand? hand, OVRSkeleton? skel, bool suppressed)
        {
            string h = hand == null ? "null"
                : $"act={hand.gameObject.activeInHierarchy} tracked={hand.IsTracked} valid={hand.IsDataValid} hi={hand.IsDataHighConfidence} conf={hand.HandConfidence}";
            string s = skel == null ? "skelNull"
                : $"skelInit={skel.IsInitialized} bones={skel.Bones.Count}";
            return $"{tag}[supp={suppressed} {h} {s}]";
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
