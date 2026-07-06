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

        /// <summary>
        /// pose を表現する基準フレーム（未設定なら trackingSpace）。TableDuoPlayer が席アンカーを設定する。
        /// 受信側（RemoteAvatarView / Grabbable / Remy）は pose を「席ローカル」として解釈するため、
        /// ここを席に固定しないと契約が崩れる: 手動リセット（A ボタン=RigRecenter.HeadToSeat）は
        /// 頭を席に合わせるためにリグごと平行移動する＝以後 trackingSpace 原点 ≠ 席。
        /// trackingSpace 基準のまま送ると、リセット時の頭ドリフト分だけリモート手・頭が系統的にズレる
        /// （2026-07-06 実害: HMD 内の自分の手と、ホストから見たリモート手の位置が根本ズレ）。
        /// </summary>
        public Transform? ReferenceFrame { get; set; }

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

            // 基準フレーム: 席（設定済みなら）。リグ再センタと独立に「席から見た pose」を送る
            Transform frame = ReferenceFrame != null ? ReferenceFrame : trackingSpace;

            ToLocal(frame, centerEye, out _pose.HeadPos, out _pose.HeadRot);

            _pose.TrackedL = SampleHand(frame, leftHand, leftSkeleton,
                ref _pose.WristPosL, ref _pose.WristRotL, _pose.BonesL);
            _pose.TrackedR = SampleHand(frame, rightHand, rightSkeleton,
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
            // 低 confidence（遮蔽・両手交差時など）は IsTracked=true のままノイズ関節を出すため
            // 「ロスト」として扱う → 既存のフリーズ描画 / Grabbable の未追跡ホールドに乗せる
            //（生値を通すとリモート手の指が暴れ、掴んだ駒が手首ノイズで振り回される）
            if (hand == null || !hand.IsTracked || !hand.IsDataHighConfidence) return false;

            ToLocal(space, hand.transform, out wristPos, out wristRot);

            if (skeleton != null && skeleton.IsInitialized)
            {
                var list = skeleton.Bones;
                int n = Mathf.Min(list.Count, AvatarPose.BonesPerHand);
                for (int i = 0; i < n; i++)
                {
                    bones[i] = list[i].Transform.localRotation;
                }

                // 位置ズレ切り分け: 送信で使う hand.transform（アンカー）と実追跡手首 Bones[0] の
                // space ローカル差を 1Hz でログ。delta が大きければ「アンカー採取」が①の位置ズレ原因。
                if (list.Count > 0)
                {
                    bool right = skeleton.GetSkeletonType() == OVRSkeleton.SkeletonType.HandRight;
                    if ((right ? _nextWristLogR : _nextWristLogL) <= Time.time)
                    {
                        if (right) _nextWristLogR = Time.time + 1f; else _nextWristLogL = Time.time + 1f;
                        var b0 = list[0].Transform;
                        Vector3 anchorLocal = wristPos;
                        Vector3 bone0Local = space.InverseTransformPoint(b0.position);
                        Vector3 d = bone0Local - anchorLocal;
                        Debug.Log($"[TDV-WRIST] {(right ? "R" : "L")} anchorLocal={anchorLocal:F3} bone0Local={bone0Local:F3} delta={d:F3} |delta|={d.magnitude:F3} bone0.localPos={b0.localPosition:F3}");
                    }
                }
            }
            return true;
        }

        private static float _nextWristLogL, _nextWristLogR;

        // スケルトン構造を1回だけダンプ（手崩れ切り分け用）。送信 index 順・BoneId・名前・可動性を
        // logcat で確認し、HandBoneTable（legacy 24-bone 前提）の並びと実ランタイム（SDK 201 で
        // OpenXR hand に変わっていないか）が一致するかを判定する。左右それぞれ初回のみ。
        private static bool _dumpedL, _dumpedR;
        private static void DumpSkeletonOnce(OVRSkeleton skeleton, bool isRight)
        {
            if (isRight ? _dumpedR : _dumpedL) return;
            if (isRight) _dumpedR = true; else _dumpedL = true;
            var list = skeleton.Bones;
            var sb = new System.Text.StringBuilder(512);
            sb.Append($"[TDV-SKEL] {(isRight ? "R" : "L")} type={skeleton.GetSkeletonType()} count={list.Count}\n");
            for (int i = 0; i < list.Count; i++)
            {
                var lr = list[i].Transform.localRotation.eulerAngles;
                sb.Append($"  i={i} id={list[i].Id} parent={list[i].ParentBoneIndex} localEuler=({lr.x:F0},{lr.y:F0},{lr.z:F0})\n");
            }
            Debug.Log(sb.ToString());
        }

        private static void CaptureLayoutIfReady(OVRSkeleton? skeleton, ref HandSkeletonLayout? slot)
        {
            if (skeleton == null || !skeleton.IsInitialized) return;
            var list = skeleton.Bones;
            if (list.Count == 0) return;
            DumpSkeletonOnce(skeleton, skeleton.GetSkeletonType() == OVRSkeleton.SkeletonType.HandRight);
            if (slot != null) return;

            // 回転は BindPoses（正準バインド）から取る。live Bones はキャプチャ時点の実手ポーズ
            // （半握り等）で既に回っており、それを bind と偽るとリターゲットが全セッション分ズレる
            var bind = skeleton.BindPoses;
            bool hasBind = bind != null && bind.Count >= list.Count;

            var layout = new HandSkeletonLayout
            {
                BoneCount = Mathf.Min(list.Count, AvatarPose.BonesPerHand)
            };
            for (int i = 0; i < layout.BoneCount; i++)
            {
                layout.ParentIndex[i] = list[i].ParentBoneIndex;
                layout.BindLocalPos[i] = hasBind ? bind![i].Transform.localPosition : list[i].Transform.localPosition;
                layout.BindLocalRot[i] = hasBind ? bind![i].Transform.localRotation : list[i].Transform.localRotation;
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
