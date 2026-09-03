#nullable enable
using FixedCamVr.Streaming.Cg;
using UnityEngine;

namespace FixedCamVr.OvrBridge
{
    /// <summary>
    /// 体験者の**素手**（ハンドトラッキング）と頭の姿勢を CG 人形へ流す橋渡し。
    /// asmdef を持たない Assembly-CSharp に居るので Meta XR SDK（OVRPlugin / OVRManager）を直接触れる。
    /// Streaming 側は <see cref="ShowBodyInput"/>（純データ）しか知らない、という既存の境界を守る。
    ///
    /// 読むのは <b>手首のワールド位置と頭の姿勢だけ</b>。手の回転・指は使わない
    /// （OVR の手 basis は素直でなく、監視カメラ画質で指の造形は見えないため。計画 2026-07-27 参照）。
    ///
    /// **人形を出していない間は GetHandState すら呼ばない**（<see cref="ShowCgLayer.WantsBody"/> が false）。
    /// スタッフのコントローラとの同居は OVRManager の
    /// <c>SimultaneousHandsAndControllersEnabled</c> + <c>launchSimultaneousHandsControllersOnStartup</c>
    /// （どちらもビルド時に立てる必要がある＝シーンの OVRManager 側。MainDemoSceneSetup が設定する）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class OvrHandTrackingBridge : MonoBehaviour
    {
        [Tooltip("人形へ身体入力を渡す先（Screen 上の ShowCgLayer）。null なら Start でシーンから探す。")]
        [SerializeField] private ShowCgLayer? cgLayer;

        [Tooltip("OVRCameraRig の TrackingSpace。手の姿勢はここのローカル座標で来る。null なら自動取得。")]
        [SerializeField] private Transform? trackingSpace;

        [Tooltip("CenterEyeAnchor（体験者の頭）。null なら自動取得。")]
        [SerializeField] private Transform? centerEye;

        [Tooltip("低信頼（TrackingConfidence.Low）の手も使う。既定 OFF＝ブレる手を人形に写さない" +
                 "（無効な間は腕が idle へ滑らかに戻るので演出は止まらない）。")]
        [SerializeField] private bool acceptLowConfidence;

        /// <summary>
        /// コントローラの原点から手首までのオフセット（コントローラのローカル座標）。
        /// 握ると手首はグリップの**後ろ下**に来る。人形の腕は上下にしか振らないので
        /// （<see cref="ActorArmLogic.LimitToVerticalSwing"/>）、ここの精度は数 cm あれば足りる。
        /// </summary>
        private static readonly Vector3 ControllerToWrist = new Vector3(0f, -0.02f, -0.05f);

        private OVRPlugin.HandState _left = new OVRPlugin.HandState();
        private OVRPlugin.HandState _right = new OVRPlugin.HandState();
        private bool _warnedMultimodal;
        private bool _loggedLeftController;

        private void Start()
        {
            if (cgLayer == null) cgLayer = FindObjectOfType<ShowCgLayer>();
            if (trackingSpace == null || centerEye == null)
            {
                OVRCameraRig? rig = FindObjectOfType<OVRCameraRig>();
                if (rig != null)
                {
                    if (trackingSpace == null) trackingSpace = rig.trackingSpace;
                    if (centerEye == null) centerEye = rig.centerEyeAnchor;
                }
            }
            WarnIfMultimodalOff();
        }

        private void Update()
        {
            ShowCgLayer? layer = cgLayer;
            if (layer == null || !layer.WantsBody) return;

            bool hasHead = centerEye != null;
            Vector3 headPos = hasHead ? centerEye!.position : Vector3.zero;
            float headYaw = hasHead ? centerEye!.eulerAngles.y : 0f;

            bool lv = TryReadHand(OVRPlugin.Hand.HandLeft, ref _left, out Vector3 lp);
            bool rv = TryReadHand(OVRPlugin.Hand.HandRight, ref _right, out Vector3 rp);

            // **左だけ、素手が取れなければコントローラの姿勢で代える**（2026-08-15 ユーザー指示）。
            // 体験者は左コントローラを持って歩く（どれかのボタンの長押しで異変を報告する・show-design.md）ので、
            // 握った手はハンドトラッキングされず、**映像の中の人形の左腕が体側で止まっていた**。
            //
            // ⚠ **右は代えない。** 右を持つのはスタッフで、体験者ではない。代えると
            // スタッフが手を動かすたびに映像の中の人形の右腕が動く（人形は体験者の分身なので破綻する）。
            if (!lv) lv = TryReadController(OVRInput.Controller.LTouch, out lp);

            layer.SetBodyInput(new ShowBodyInput(hasHead, headPos, headYaw, lv, lp, rv, rp));
        }

        /// <summary>
        /// コントローラの姿勢から手首のワールド位置を出す。掴めなければ false
        /// （呼び出し側は idle へ滑らかに合流するので、人形は止まらない）。
        /// </summary>
        private bool TryReadController(OVRInput.Controller controller, out Vector3 world)
        {
            world = Vector3.zero;
            if (!OVRInput.IsControllerConnected(controller)) return false;
            if (!OVRInput.GetControllerPositionValid(controller)) return false;

            Vector3 local = OVRInput.GetLocalControllerPosition(controller)
                            + OVRInput.GetLocalControllerRotation(controller) * ControllerToWrist;
            world = trackingSpace != null ? trackingSpace.TransformPoint(local) : local;

            if (!_loggedLeftController && controller == OVRInput.Controller.LTouch)
            {
                _loggedLeftController = true;
                Debug.Log("[OvrHandTracking] 左は素手が取れないのでコントローラの姿勢で代える。");
            }
            return true;
        }

        private bool TryReadHand(OVRPlugin.Hand hand, ref OVRPlugin.HandState state, out Vector3 world)
        {
            world = Vector3.zero;
            if (!OVRPlugin.GetHandState(OVRPlugin.Step.Render, hand, ref state)) return false;
            if ((state.Status & OVRPlugin.HandStatus.HandTracked) == 0) return false;
            if (!acceptLowConfidence && state.HandConfidence != OVRPlugin.TrackingConfidence.High) return false;

            // RootPose は TrackingSpace ローカル（OVRSkeleton と同じ扱い）。
            Vector3 local = state.RootPose.Position.FromFlippedZVector3f();
            world = trackingSpace != null ? trackingSpace.TransformPoint(local) : local;
            return true;
        }

        // 同時使用（素手 + スタッフのコントローラ）はビルド時フラグ。切れていると
        // 「コントローラを握った瞬間に手が取れなくなる」ので、現地で気づけるよう 1 度だけ警告する。
        private void WarnIfMultimodalOff()
        {
            if (_warnedMultimodal) return;
            _warnedMultimodal = true;
            OVRManager m = OVRManager.instance;
            if (m == null) return;
            if (!m.SimultaneousHandsAndControllersEnabled || !m.launchSimultaneousHandsControllersOnStartup)
                Debug.LogWarning("[OvrHandTracking] OVRManager の同時使用（hands + controllers）が OFF。" +
                                 "スタッフがコントローラを持つと体験者の手が取れなくなる。" +
                                 "Tools/FixedCamVr/Setup/Setup Main Demo Scene を再実行して有効化する。");
        }
    }
}
