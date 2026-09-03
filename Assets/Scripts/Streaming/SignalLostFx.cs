#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// フェイルソフト演出（「信号」語彙への一本化）。配信断とトラッキングロスト/pause 明けを、
    /// バグではなく diegetic な「カメラの信号が落ちた」表現に見せる。
    ///
    /// 2 系統:
    ///   - 配信断: アクティブカメラの新フレームが <see cref="lostThresholdSec"/> 来なければ砂嵐へ
    ///     <see cref="rampSec"/> でクロスフェード（強・レベル 1.0）。復帰で同時間かけてグリッチイン。
    ///   - トラッキングロスト / pause 明け: resume-gap（大きな unscaledDeltaTime）or 外部通知で
    ///     <see cref="ScreenAnchor"/> の追従を凍結（暴れ防止）+ 弱い砂嵐（<see cref="weakLevel"/>）を重ねる。
    ///     復帰は ScreenAnchor 側が現在ヨーから減衰合流する（スナップ禁止）。
    ///
    /// 砂嵐・減光は ScreenComposite の <c>_SignalLost</c> uniform で行う（post 数式 = Web FS_POST 一致規約とは
    /// 別系統）。material は Screen の Renderer から取得（MjpegScreen 等と共有）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SignalLostFx : MonoBehaviour
    {
        private static readonly int SignalLostId = Shader.PropertyToID("_SignalLost");
        private static readonly int SignalFloorId = Shader.PropertyToID("_SignalFloor");

        [Header("References")]
        [Tooltip("アクティブカメラの最終フレーム時刻を読む CameraStreamRegistry。")]
        [SerializeField] private CameraStreamRegistry? registry;

        [Tooltip("トラッキングロスト時に追従を凍結する ScreenAnchor。null なら凍結しない。")]
        [SerializeField] private ScreenAnchor? screenAnchor;

        [Header("Signal-lost (配信断 → 砂嵐)")]
        [Tooltip("アクティブカメラの新フレームがこの秒数来なければ『信号ロスト』と判定。")]
        [SerializeField, Min(0.05f)] private float lostThresholdSec = 0.6f;

        [Tooltip("砂嵐フェードイン / 復帰グリッチインの時間 (秒)。")]
        [SerializeField, Min(0.01f)] private float rampSec = 0.15f;

        [Header("Tracking-lost / pause 明け（弱適用 + 追従凍結）")]
        [Tooltip("この秒数を超える unscaledDeltaTime を pause / HMD 着脱明けとみなす。")]
        [SerializeField, Min(0.1f)] private float resumeGapSec = 0.5f;

        [Tooltip("resume / トラッキングロストで追従凍結 + 弱ノイズを保持する時間 (秒)。")]
        [SerializeField, Min(0f)] private float trackingHoldSec = 0.4f;

        [Tooltip("トラッキングロスト時の弱適用レベル（0-1）。配信断の強い砂嵐は常に 1.0。")]
        [SerializeField, Range(0f, 1f)] private float weakLevel = 0.35f;

        private Material? _material;
        private float _level;
        private float _floor;
        private float _trackingHoldRemaining;
        private bool _trackingReported;

        // 未受信カメラ（LastFrameRealtime==0・黒プレースホルダ表示中）の砂嵐カバー判定に使う。
        // アクティブ stream の参照が変わった時刻を追跡し、そこから lostThresholdSec 超過で信号ロスト扱いにする。
        private CameraStream? _lastActiveStream;
        private float _activeSinceRealtime;

        /// <summary>強い信号ロスト（配信断・砂嵐）中か（HUD 表示用）。</summary>
        public bool SignalLost => _level > 0.5f;

        /// <summary>トラッキングロスト/pause 明けで追従凍結中か（HUD 表示用）。</summary>
        public bool TrackingFrozen => _trackingHoldRemaining > 0f || _trackingReported;

        /// <summary>現在の砂嵐レベル（0-1）。</summary>
        public float Level => _level;

        /// <summary>
        /// OVR 由来のトラッキングロストを外部（OvrControllerBridge）から通知する。
        /// resume-gap と OR で「弱適用 + 追従凍結」を発火する。
        /// </summary>
        public void ReportTrackingLost(bool lost) => _trackingReported = lost;

        private void Awake()
        {
            var r = GetComponent<Renderer>();
            _material = r != null ? r.material : null;
        }

        private void OnEnable()
        {
            _level = 0f;
            _floor = 0f;
            _lastActiveStream = null; // 次の Update でアクティブ参照を再シードさせる
            SetLevel(0f);
        }

        private void OnDisable()
        {
            _level = 0f;
            _floor = 0f;
            SetLevel(0f);
        }

        private void Update()
        {
            float dt = Time.deltaTime;

            // resume-gap（pause / HMD 着脱明け）検知。既存 CameraStream.Tick と同じ閾値パターン。
            if (Time.unscaledDeltaTime > resumeGapSec)
            {
                _trackingHoldRemaining = trackingHoldSec;
                // 未受信カメラのタイマーも再シード（凍結中に進んだ実時間で即・強砂嵐化するのを防ぐ）。
                _activeSinceRealtime = Time.realtimeSinceStartup;
            }
            else if (_trackingHoldRemaining > 0f) _trackingHoldRemaining -= dt;

            bool trackingLost = _trackingReported || _trackingHoldRemaining > 0f;

            // 追従凍結（暴れ防止）。解除時 ScreenAnchor が現在ヨーから合流する。
            if (screenAnchor != null) screenAnchor.SetFollowFrozen(trackingLost);

            // アクティブ stream の参照が変わったら「アクティブ化時刻」を再シードする（切替でタイマーリセット）。
            var active = registry != null ? registry.GetActive() : null;
            if (active != _lastActiveStream)
            {
                _lastActiveStream = active;
                _activeSinceRealtime = Time.realtimeSinceStartup;
            }

            // 配信断判定（suspend 中は表示を止めているだけなので数えない = トラッキング系で扱う）。
            bool signalStale = false;
            // 砂の下に画が 1 枚も無いか。**砂は掛け算で乗る**（ScreenComposite の `_SignalFloor`）ので、
            // 下が真っ黒だと何も見えない。一度も受信していないカメラのときだけ地を持ち上げる。
            bool noPicture = false;
            if (active != null && !active.IsSuspended)
            {
                float last = active.LastFrameRealtime;
                if (last > 0f)
                {
                    if (Time.realtimeSinceStartup - last > lostThresholdSec) signalStale = true;
                }
                // 一度もフレームを受信していないカメラ（黒プレースホルダ表示中）は last==0 で上の判定が
                // 発火せず素の黒が露出する。アクティブ化から lostThresholdSec 超過で砂嵐に覆う。
                else if (Time.realtimeSinceStartup - _activeSinceRealtime > lostThresholdSec)
                {
                    signalStale = true;
                    noPicture = true;
                }
            }

            // 目標レベル: 配信断 = 強（1.0） / トラッキングロスト = 弱 / 通常 = 0。
            float target = signalStale ? 1f : (trackingLost ? weakLevel : 0f);
            float rate = rampSec > 0f ? dt / rampSec : 1f;
            _level = Mathf.MoveTowards(_level, target, rate);
            _floor = Mathf.MoveTowards(_floor, noPicture ? 1f : 0f, rate);
            SetLevel(_level);
        }

        private void SetLevel(float v)
        {
            if (_material == null) return;
            _material.SetFloat(SignalLostId, Mathf.Clamp01(v));
            _material.SetFloat(SignalFloorId, Mathf.Clamp01(_floor));
        }
    }
}
