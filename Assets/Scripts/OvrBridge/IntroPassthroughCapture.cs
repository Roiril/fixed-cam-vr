#nullable enable

using System;
using FixedCamVr.Streaming;
using Meta.XR;
using UnityEngine;
using UnityEngine.Android;

namespace FixedCamVr.OvrBridge
{
    /// <summary>
    /// 導入の破砕直前から左右パススルーカメラを準備し、撮影時の姿勢と内部パラメータを
    /// Streaming 側へ渡す。カメラ texture は借用で、保存や CPU readback は行わない。
    ///
    /// ⚠ <b>カメラ権限は起動直後に求める</b>（2026-09-14）。段 1 で初めて求めると、
    /// 体験者の目の前に許可ダイアログが出るうえ、答えが間に合わなければ静止画が取れず、
    /// 破片は代替経路で描かれる。スタッフが起動時に 1 度許可すれば以後は残る
    /// （<c>OVRManager.requestPassthroughCameraAccessPermissionOnStartup</c> もシーンへ焼く）。
    /// 取れなかった理由は <see cref="IntroVeil.FrozenFrameStatus"/> へ書き、テレメトリが <c>shatCam=</c> で出す。
    /// </summary>
    [DefaultExecutionOrder(-50)]
    [DisallowMultipleComponent]
    public sealed class IntroPassthroughCapture : MonoBehaviour
    {
        private const string CameraPermission = OVRPermissionsRequester.PassthroughCameraAccessPermission;
        private const float MaxStereoDeltaSec = 0.1f;
        private const float MaxFrameAgeSec = 0.25f;
        private static readonly Vector2Int RequestedResolution = new(1280, 1280);

        [SerializeField] private IntroDirector? director;
        [SerializeField] private IntroVeil? veil;
        [SerializeField] private OVRCameraRig? cameraRig;

        private GameObject? _cameraRoot;
        private PassthroughCameraAccess? _leftCamera;
        private PassthroughCameraAccess? _rightCamera;
        private Func<IntroFrozenFrameSource?>? _provider;
        private IntroVeil? _boundVeil;
        private PermissionCallbacks? _permissionCallbacks;
        private FrameSample _leftSample;
        private FrameSample _rightSample;
        private FrameSample _leftRenderedSample;
        private FrameSample _rightRenderedSample;
        private bool _preparing;
        private bool _enableAttempted;
        private bool _permissionRequested;
        private bool _permissionDenied;
        private bool _supportConfirmed;
        private bool _providerFailureLogged;
        private float _supportRetryAt;

        private struct FrameSample
        {
            public Texture? texture;
            public Matrix4x4 worldToUv;
            public DateTime timestamp;
            public float observedAt;
        }

        private void Awake()
        {
            _provider = ProvideFrozenFrame;
            ResolveAndBind();
        }

        private void OnEnable()
        {
            if (!Application.isPlaying) return;
            ResolveAndBind();
        }

        private void Start()
        {
            if (!Application.isPlaying) return;
            // 起動直後に権限を求める。段 1 まで待つと体験の途中でダイアログが出て、間に合わない。
            if (!PassthroughCameraAccess.IsSupported)
            {
                PublishStatus("unsupported");
                return;
            }
            _supportConfirmed = true;
            RequestPermissionIfNeeded();
        }

        private void Update()
        {
            if (!Application.isPlaying) return;

            ResolveAndBind();
            if (director == null) return;

            bool shouldPrepare = director.Stage is IntroStage.Real or IntroStage.Degrade
                or IntroStage.Structure or IntroStage.Frame;
            if (!shouldPrepare)
            {
                if (_preparing) StopCameras();
                _preparing = false;
                return;
            }

            if (!_preparing)
            {
                _preparing = true;
                _enableAttempted = false;
                _providerFailureLogged = false;
                ClearSamples();
            }

            PrepareCameras();
            _leftRenderedSample = _leftSample;
            _rightRenderedSample = _rightSample;
            Observe(_leftCamera, ref _leftSample);
            Observe(_rightCamera, ref _rightSample);
        }

        private void OnDisable()
        {
            StopCameras();
            UnbindProvider();
            _preparing = false;
        }

        private void ResolveAndBind()
        {
            if (director == null) director = FindObjectOfType<IntroDirector>();
            if (veil == null) veil = FindObjectOfType<IntroVeil>();
            if (cameraRig == null) cameraRig = FindObjectOfType<OVRCameraRig>();

            if (_provider == null) _provider = ProvideFrozenFrame;
            if (_boundVeil != null && _boundVeil != veil)
            {
                if (_boundVeil.FrozenFrameProvider == _provider) _boundVeil.FrozenFrameProvider = null;
                _boundVeil = null;
            }
            if (veil != null && veil.FrozenFrameProvider == null)
            {
                veil.FrozenFrameProvider = _provider;
                _boundVeil = veil;
            }
        }

        private void UnbindProvider()
        {
            if (_boundVeil != null && _boundVeil.FrozenFrameProvider == _provider)
                _boundVeil.FrozenFrameProvider = null;
            _boundVeil = null;
        }

        private void PrepareCameras()
        {
            if (!_supportConfirmed)
            {
                if (Time.unscaledTime < _supportRetryAt) return;
                _supportRetryAt = Time.unscaledTime + 1f;
                _supportConfirmed = PassthroughCameraAccess.IsSupported;
                if (!_supportConfirmed) return;
            }

            if (!RequestPermissionIfNeeded()) return;

            _permissionDenied = false;
            EnsureCameraObjects();
            if (_enableAttempted || _leftCamera == null || _rightCamera == null) return;

            _enableAttempted = true;
            _leftCamera.enabled = true;
            _rightCamera.enabled = true;
        }

        /// <summary>権限が無ければ 1 度だけ求める。戻り値は「いま許可されているか」。</summary>
        private bool RequestPermissionIfNeeded()
        {
            if (Permission.HasUserAuthorizedPermission(CameraPermission)) return true;
            if (!_permissionRequested)
            {
                _permissionRequested = true;
                _permissionCallbacks = new PermissionCallbacks();
                _permissionCallbacks.PermissionGranted += OnPermissionGranted;
                _permissionCallbacks.PermissionDenied += OnPermissionDenied;
                _permissionCallbacks.PermissionDeniedAndDontAskAgain += OnPermissionDenied;
                Permission.RequestUserPermission(CameraPermission, _permissionCallbacks);
            }
            return false;
        }

        private void PublishStatus(string status)
        {
            if (veil != null) veil.FrozenFrameStatus = status;
        }

        private void EnsureCameraObjects()
        {
            if (_cameraRoot != null) return;

            _cameraRoot = new GameObject("Intro Passthrough Cameras");
            _cameraRoot.transform.SetParent(transform, worldPositionStays: false);
            _cameraRoot.SetActive(false);
            _leftCamera = CreateCamera("Left", PassthroughCameraAccess.CameraPositionType.Left);
            _rightCamera = CreateCamera("Right", PassthroughCameraAccess.CameraPositionType.Right);
            _cameraRoot.SetActive(true);
        }

        private PassthroughCameraAccess CreateCamera(
            string name, PassthroughCameraAccess.CameraPositionType position)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_cameraRoot!.transform, worldPositionStays: false);
            var camera = go.AddComponent<PassthroughCameraAccess>();
            camera.enabled = false;
            camera.CameraPosition = position;
            camera.RequestedResolution = RequestedResolution;
            camera.MaxFramerate = 60;
            return camera;
        }

        private void Observe(PassthroughCameraAccess? camera, ref FrameSample sample)
        {
            if (camera == null || !camera.enabled || !camera.IsPlaying || !camera.IsUpdatedThisFrame) return;
            if (cameraRig == null || cameraRig.trackingSpace == null) return;

            Texture texture = camera.GetTexture();
            if (texture == null || camera.Timestamp == default || !HasUsableIntrinsics(camera)) return;
            if (camera.Timestamp == sample.timestamp) return;

            Pose trackingPose = camera.GetCameraPose();
            float rotationMagnitude = trackingPose.rotation.x * trackingPose.rotation.x
                + trackingPose.rotation.y * trackingPose.rotation.y
                + trackingPose.rotation.z * trackingPose.rotation.z
                + trackingPose.rotation.w * trackingPose.rotation.w;
            if (rotationMagnitude < 0.5f) return;

            Transform trackingSpace = cameraRig.trackingSpace;
            var worldPose = new Pose(
                trackingSpace.TransformPoint(trackingPose.position),
                trackingSpace.rotation * trackingPose.rotation);
            RecordSample(ref sample, texture,
                BuildWorldToUv(worldPose, camera.Intrinsics, camera.CurrentResolution),
                camera.Timestamp, Time.unscaledTime);
        }

        private IntroFrozenFrameSource? ProvideFrozenFrame()
        {
            if (!_supportConfirmed)
                return Fail("unsupported", "Quest 3 / Quest 3S の Passthrough Camera Access に対応していません");
            if (_permissionDenied || !Permission.HasUserAuthorizedPermission(CameraPermission))
                return Fail("denied", "HEADSET_CAMERA 権限が許可されていません");
            if (cameraRig == null || cameraRig.trackingSpace == null
                || _leftCamera == null || _rightCamera == null
                || !_leftCamera.IsPlaying || !_rightCamera.IsPlaying)
                return Fail("nocam", "左右パススルーカメラが初期化されていません");

#if UNITY_EDITOR
            FrameSample left = Application.isEditor ? _leftSample : _leftRenderedSample;
            FrameSample right = Application.isEditor ? _rightSample : _rightRenderedSample;
#else
            FrameSample left = _leftRenderedSample;
            FrameSample right = _rightRenderedSample;
#endif
            if (left.texture == null || right.texture == null)
                return Fail("noimage", "左右パススルーカメラの画像がまだ届いていません");

            float now = Time.unscaledTime;
            if (now - left.observedAt > MaxFrameAgeSec
                || now - right.observedAt > MaxFrameAgeSec
                || _leftCamera.Timestamp != _leftSample.timestamp
                || _rightCamera.Timestamp != _rightSample.timestamp)
                return Fail("stale", "パススルーカメラの画像が古いため、破片への撮影を見送ります");

            double deltaMs = Math.Abs((left.timestamp - right.timestamp).TotalMilliseconds);
            if (deltaMs >= MaxStereoDeltaSec * 1000.0)
                return Fail("stereo", $"左右カメラの撮影時刻差が {deltaMs:0.0}ms のため、破片への撮影を見送ります");

            PublishStatus("ok");
            PublishProjectionProbe(left.worldToUv, right.worldToUv);

            Debug.Log($"[IntroCamera] 静止画用の入力取得: left={left.timestamp:O} "
                      + $"right={right.timestamp:O} delta={deltaMs:0.0}ms "
                      + $"leftRes={_leftCamera.CurrentResolution.x}x{_leftCamera.CurrentResolution.y} "
                      + $"rightRes={_rightCamera.CurrentResolution.x}x{_rightCamera.CurrentResolution.y}");
            return new IntroFrozenFrameSource(
                left.texture, right.texture, left.worldToUv, right.worldToUv);
        }

        /// <summary>
        /// 投影の検算（0236）。頭の正面 1.6m（破片のシェル半径）の点が左右カメラの投影で「前」に居るか。
        /// 後ろに出ると中央の片は写真の範囲判定で 1 枚も描かれず、周辺の板が中央まで覆う。
        /// 未装着の自動走行でだけ起きる型なので、ログだけでなくテレメトリ（shatProj=）へも出す。
        /// </summary>
        private void PublishProjectionProbe(Matrix4x4 leftWorldToUv, Matrix4x4 rightWorldToUv)
        {
            if (veil == null || cameraRig == null || cameraRig.centerEyeAnchor == null) return;
            Transform head = cameraRig.centerEyeAnchor;
            Vector3 probe = head.position + head.forward * 1.6f;
            var p = new Vector4(probe.x, probe.y, probe.z, 1f);
            Vector4 ql = leftWorldToUv * p;
            Vector4 qr = rightWorldToUv * p;
            string Describe(Vector4 q) => q.w > 1e-4f
                ? FormattableString.Invariant($"{q.x / q.w:0.00},{q.y / q.w:0.00}")
                : "behind";
            string note = Describe(ql) + "/" + Describe(qr);
            veil.FrozenFrameProjection = note;
            Debug.Log($"[IntroCamera] 投影の検算: 頭 {head.position:F2} 向き {head.forward:F2} → 正面 1.6m の uv 左/右 = {note}"
                      + (ql.w <= 1e-4f || qr.w <= 1e-4f
                          ? "（カメラ姿勢の後ろ ＝ 中央の片は写真の範囲判定で描かれない。未装着の自動走行で起きる型）" : string.Empty));
        }

        private IntroFrozenFrameSource? Fail(string status, string reason)
        {
            PublishStatus(status);
            if (!_providerFailureLogged)
            {
                Debug.LogWarning($"[IntroCamera] {reason}。従来の破片表示へ戻します");
                _providerFailureLogged = true;
            }
            return null;
        }

        private static bool HasUsableIntrinsics(PassthroughCameraAccess camera)
        {
            var intrinsics = camera.Intrinsics;
            return camera.CurrentResolution.x > 0 && camera.CurrentResolution.y > 0
                && intrinsics.SensorResolution.x > 0 && intrinsics.SensorResolution.y > 0
                && intrinsics.FocalLength.x > 0f && intrinsics.FocalLength.y > 0f;
        }

        private static Matrix4x4 BuildWorldToUv(
            Pose worldPose, PassthroughCameraAccess.CameraIntrinsics intrinsics,
            Vector2Int currentResolution)
        {
            Vector2 sensorResolution = intrinsics.SensorResolution;
            Vector2 scale = (Vector2)currentResolution / sensorResolution;
            scale /= Mathf.Max(scale.x, scale.y);
            var crop = new Rect(
                sensorResolution.x * (1f - scale.x) * 0.5f,
                sensorResolution.y * (1f - scale.y) * 0.5f,
                sensorResolution.x * scale.x,
                sensorResolution.y * scale.y);

            var sensorToCrop = Matrix4x4.zero;
            sensorToCrop.m00 = intrinsics.FocalLength.x / crop.width;
            sensorToCrop.m02 = (intrinsics.PrincipalPoint.x - crop.x) / crop.width;
            sensorToCrop.m11 = intrinsics.FocalLength.y / crop.height;
            sensorToCrop.m12 = (intrinsics.PrincipalPoint.y - crop.y) / crop.height;
            sensorToCrop.m22 = 1f;
            sensorToCrop.m32 = 1f;
            Matrix4x4 worldToCamera = Matrix4x4.TRS(
                worldPose.position, worldPose.rotation, Vector3.one).inverse;
            return sensorToCrop * worldToCamera;
        }

        private static void RecordSample(
            ref FrameSample sample, Texture? texture, Matrix4x4 worldToUv,
            DateTime timestamp, float observedAt)
        {
            if (timestamp == sample.timestamp) return;
            sample.texture = texture;
            sample.worldToUv = worldToUv;
            sample.timestamp = timestamp;
            sample.observedAt = observedAt;
        }

        private void StopCameras()
        {
            if (_leftCamera != null) _leftCamera.enabled = false;
            if (_rightCamera != null) _rightCamera.enabled = false;
            _enableAttempted = false;
            ClearSamples();
        }

        private void ClearSamples()
        {
            _leftSample = default;
            _rightSample = default;
            _leftRenderedSample = default;
            _rightRenderedSample = default;
        }

        private void OnPermissionGranted(string permission)
        {
            if (permission == CameraPermission) _permissionDenied = false;
        }

        private void OnPermissionDenied(string permission)
        {
            if (permission == CameraPermission) _permissionDenied = true;
        }
    }
}
