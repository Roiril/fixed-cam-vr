#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace MyCobotHandVr
{
    /// <summary>
    /// 右手 5 指の curl を PC のロボットハンドサーバへストリームする。
    /// 各指 [根元, 第2関節, 先端] の 3 ボーン world 位置から関節角 (acos) を出し、
    /// calibOpen/Closed で 0..1 に正規化して POST /hand/fingers（親指のみ反転）。
    ///
    /// 送信は 40Hz cap・single-in-flight・latest-wins・1s タイムアウト。
    /// IsDataHighConfidence=false のフレームは送らず、直近 bends を HUD 用に保持する
    /// （WebXR 版 scripts/hand.html と同じ思想）。
    ///
    /// ⚠ curl の初期閾値は WebXR 版の値をそのまま種にしている。WebXR は指の
    /// metacarpal→proximal→tip（MCP 屈曲）を測るのに対し、こちらは OVR スケルトンの
    /// proximal→intermediate→tip（PIP 屈曲）を測るため rad レンジが異なる。
    /// 実機では HUD の生 curl 値を見て calibOpen/Closed を各指ごとに合わせ直すこと。
    /// </summary>
    public sealed class FingerCurlSender : MonoBehaviour
    {
        [Header("Tracking source（右手）")]
        [SerializeField] private OVRHand? rightHand;
        [SerializeField] private OVRSkeleton? rightSkeleton;

        [Header("Server")]
        [Tooltip("ハンドサーバの base URL。adb reverse で localhost へ橋渡しする前提")]
        [SerializeField] private string serverUrl = "http://localhost:8001";
        [Tooltip("送信レート上限 (Hz)。サーバ側も 40Hz スロットル・超過 429")]
        [SerializeField, Range(1f, 60f)] private float sendRateHz = 40f;
        [Tooltip("POST タイムアウト (秒)。詰まった adb トンネルで in-flight が固まるのを防ぐ")]
        [SerializeField, Range(1, 5)] private int requestTimeoutSec = 1;

        [Header("Calibration (rad)  bend 0 = 開き / 1 = 握り  （親指→小指順）")]
        [SerializeField] private float[] calibOpen = { 0.50f, 0.25f, 0.25f, 0.25f, 0.25f };
        [SerializeField] private float[] calibClosed = { 1.40f, 1.80f, 1.80f, 1.80f, 1.80f };
        [Tooltip("curl の向きが逆の指を反転（親指のみ true）")]
        [SerializeField] private bool[] invert = { true, false, false, false, false };

        public const int FingerCount = 5;

        // ---- HUD 用の読み取り専用ステート ----
        /// <summary>右手が高信頼で追従中か（このフレーム）。</summary>
        public bool RightTracked { get; private set; }
        /// <summary>連続 POST 失敗が閾値超え＝サーバ応答なし。</summary>
        public bool ServerError => _netFail >= 3;
        /// <summary>生 curl 値 (rad)。joint 欠落フレームは NaN。実機校正用。</summary>
        public IReadOnlyList<float> LastCurls => _curls;
        /// <summary>送信中の bends (0..1)。</summary>
        public IReadOnlyList<float> LastBends => _bends;

        private readonly float[] _curls = { float.NaN, float.NaN, float.NaN, float.NaN, float.NaN };
        private readonly float[] _bends = new float[FingerCount];

        private int _netFail;
        private bool _inflight;
        private float _lastSend;
        private CancellationToken _ct;

        // OVR レガシー 24-bone スケルトン: 指ごと [proximal-ish, intermediate, tip]。
        private static readonly OVRSkeleton.BoneId[][] LegacyTriples =
        {
            new[] { OVRSkeleton.BoneId.Hand_Thumb1, OVRSkeleton.BoneId.Hand_Thumb2, OVRSkeleton.BoneId.Hand_ThumbTip },
            new[] { OVRSkeleton.BoneId.Hand_Index1, OVRSkeleton.BoneId.Hand_Index2, OVRSkeleton.BoneId.Hand_IndexTip },
            new[] { OVRSkeleton.BoneId.Hand_Middle1, OVRSkeleton.BoneId.Hand_Middle2, OVRSkeleton.BoneId.Hand_MiddleTip },
            new[] { OVRSkeleton.BoneId.Hand_Ring1, OVRSkeleton.BoneId.Hand_Ring2, OVRSkeleton.BoneId.Hand_RingTip },
            new[] { OVRSkeleton.BoneId.Hand_Pinky1, OVRSkeleton.BoneId.Hand_Pinky2, OVRSkeleton.BoneId.Hand_PinkyTip },
        };

        // SDK が OpenXR ハンドスケルトンを返す場合のフォールバック。
        private static readonly OVRSkeleton.BoneId[][] XrTriples =
        {
            new[] { OVRSkeleton.BoneId.XRHand_ThumbMetacarpal, OVRSkeleton.BoneId.XRHand_ThumbProximal, OVRSkeleton.BoneId.XRHand_ThumbTip },
            new[] { OVRSkeleton.BoneId.XRHand_IndexProximal, OVRSkeleton.BoneId.XRHand_IndexIntermediate, OVRSkeleton.BoneId.XRHand_IndexTip },
            new[] { OVRSkeleton.BoneId.XRHand_MiddleProximal, OVRSkeleton.BoneId.XRHand_MiddleIntermediate, OVRSkeleton.BoneId.XRHand_MiddleTip },
            new[] { OVRSkeleton.BoneId.XRHand_RingProximal, OVRSkeleton.BoneId.XRHand_RingIntermediate, OVRSkeleton.BoneId.XRHand_RingTip },
            new[] { OVRSkeleton.BoneId.XRHand_LittleProximal, OVRSkeleton.BoneId.XRHand_LittleIntermediate, OVRSkeleton.BoneId.XRHand_LittleTip },
        };

        private readonly Dictionary<OVRSkeleton.BoneId, Transform> _boneMap = new();
        private OVRSkeleton.BoneId[][]? _activeTriples;

        [Serializable]
        private struct BendsMsg { public float[] bends; }

        private void Awake() => _ct = destroyCancellationToken;

        private void Update()
        {
            RightTracked = SampleAndCompute();

            if (RightTracked && !_inflight &&
                Time.unscaledTime - _lastSend >= 1f / Mathf.Max(1f, sendRateHz))
            {
                _lastSend = Time.unscaledTime;
                _ = SendBendsAsync((float[])_bends.Clone());
            }
        }

        /// <summary>
        /// 右手 tracking をサンプルし _curls/_bends を更新。高信頼で追従できていれば true。
        /// 追従できていないフレームは _curls を NaN にし、_bends は直前値のまま保持する。
        /// </summary>
        private bool SampleAndCompute()
        {
            if (rightHand == null || rightSkeleton == null ||
                !rightHand.IsTracked || !rightHand.IsDataHighConfidence ||
                !rightSkeleton.IsInitialized)
            {
                for (int i = 0; i < FingerCount; i++) _curls[i] = float.NaN;
                return false;
            }

            if (!RebuildBoneMapIfNeeded())
            {
                for (int i = 0; i < FingerCount; i++) _curls[i] = float.NaN;
                return false;
            }

            for (int i = 0; i < FingerCount; i++)
            {
                float curl = FingerCurl(_activeTriples![i]);
                _curls[i] = curl;
                if (!float.IsNaN(curl)) _bends[i] = CurlToBend(i, curl);
            }
            return true;
        }

        private bool RebuildBoneMapIfNeeded()
        {
            // Bones は初期化後に安定。マップが空／ボーン数が変わったら作り直す。
            var bones = rightSkeleton!.Bones;
            if (bones == null || bones.Count == 0) return false;
            if (_boneMap.Count == bones.Count && _activeTriples != null) return true;

            _boneMap.Clear();
            for (int i = 0; i < bones.Count; i++)
            {
                var b = bones[i];
                if (b != null && b.Transform != null) _boneMap[b.Id] = b.Transform;
            }

            _activeTriples = _boneMap.ContainsKey(OVRSkeleton.BoneId.Hand_Index1) ? LegacyTriples
                : _boneMap.ContainsKey(OVRSkeleton.BoneId.XRHand_IndexProximal) ? XrTriples
                : null;
            return _activeTriples != null;
        }

        /// <summary>3 ボーン world 位置から関節角 (rad, 0=真っ直ぐ) を返す。欠落は NaN。</summary>
        private float FingerCurl(OVRSkeleton.BoneId[] triple)
        {
            if (!_boneMap.TryGetValue(triple[0], out var t0) ||
                !_boneMap.TryGetValue(triple[1], out var t1) ||
                !_boneMap.TryGetValue(triple[2], out var t2))
                return float.NaN;

            Vector3 v1 = t1.position - t0.position;
            Vector3 v2 = t2.position - t1.position;
            float m = v1.magnitude * v2.magnitude;
            if (m < 1e-9f) return float.NaN;
            float dot = Mathf.Clamp(Vector3.Dot(v1, v2) / m, -1f, 1f);
            return Mathf.Acos(dot);
        }

        private float CurlToBend(int i, float curl)
        {
            float o = calibOpen[i];
            float cl = calibClosed[i];
            float span = cl - o;
            if (Mathf.Abs(span) < 1e-6f) span = 1f;
            float b = Mathf.Clamp01((curl - o) / span);
            if (i < invert.Length && invert[i]) b = 1f - b;
            return b;
        }

        private async Task SendBendsAsync(float[] bends)
        {
            _inflight = true;
            try
            {
                string json = JsonUtility.ToJson(new BendsMsg { bends = bends });
                string url = serverUrl.TrimEnd('/') + "/hand/fingers";
                using var req = UnityWebRequest.Post(url, json, "application/json");
                req.timeout = Mathf.Max(1, requestTimeoutSec);

                var op = req.SendWebRequest();
                while (!op.isDone)
                {
                    if (_ct.IsCancellationRequested) return;
                    await Task.Yield();
                }

                if (req.result == UnityWebRequest.Result.Success) _netFail = 0;
                else _netFail++;
            }
            catch (OperationCanceledException) { }
            catch (Exception) { _netFail++; }
            finally { _inflight = false; }
        }
    }
}
