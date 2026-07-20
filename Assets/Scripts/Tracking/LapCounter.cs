#nullable enable
using System;
using FixedCamVr.Streaming;
using UnityEngine;

namespace FixedCamVr.Tracking
{
    /// <summary>
    /// 周回カウントの純粋ロジック。MonoBehaviour（<see cref="LapCounter"/>）から分離して EditMode テスト可能にする。
    ///
    /// セマンティクス（計画 2026-07-17_pre-authored-cue-schedule.md）:
    ///   - lap は 1 始まり（体験開始 = 1 周目）
    ///   - 進行ポインタ方式: 期待する次カメラ = order[(pos+1) % n]。アクティブカメラがそれに一致した時だけ前進。
    ///     逆走・同一・スキップ遷移では前進しない（境界 jitter・行き来・逆走はカウントされない）
    ///   - 全カメラを順方向に踏破し order[0] に戻った時点で lap++
    ///   - リセットで pos=0 / lap=1 に戻る
    /// 初期状態は pos=0（= order[0] = スタート領域に居る前提）。
    /// </summary>
    public sealed class LapCounterLogic
    {
        private int[] _order = Array.Empty<int>();
        private int _pos;      // order 内の現在確定位置
        private int _lap = 1;

        /// <summary>現在の周回数（1 始まり）。</summary>
        public int CurrentLap => _lap;

        /// <summary>order 内の現在確定位置（テスト・診断用）。</summary>
        public int Position => _pos;

        /// <summary>順方向のカメラ巡回順。order[0] = スタート領域のカメラ。</summary>
        public int[] Order => _order;

        /// <summary>巡回順を差し替える。進行ポインタと lap はスタート状態（pos=0 / lap=1）にリセットする。</summary>
        public void SetOrder(int[] order)
        {
            _order = order ?? Array.Empty<int>();
            _pos = 0;
            _lap = 1;
        }

        /// <summary>進行と周回をスタート状態に戻す（体験の明示リセット）。order は保持。</summary>
        public void Reset()
        {
            _pos = 0;
            _lap = 1;
        }

        /// <summary>
        /// アクティブカメラの進入を供給する。順方向に一致したら前進し、order[0] へ戻れば lap++。
        /// 戻り値は「この供給で lap が前進したか」。
        /// </summary>
        public bool Feed(int camera)
        {
            int n = _order.Length;
            if (n == 0) return false;
            int nextPos = (_pos + 1) % n;
            if (_order[nextPos] != camera) return false; // 逆走 / 同一 / スキップ = 前進しない
            _pos = nextPos;
            if (_pos == 0)
            {
                _lap++;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// アクティブカメラ切替（<see cref="CameraStreamRegistry.ActiveChanged"/>）を周回カウントへ写像し、
    /// 進入イベントを <see cref="CueScheduler"/> へ橋渡しする。
    ///
    /// 巡回順（order）は show.json layout.course.order を <see cref="ShowControlClient"/> から供給される
    /// （Streaming → Tracking の参照を作らないため、Tracking 側の本コンポーネントが Streaming を読む
    /// ZoneLayoutApplier と同じ方向）。lap の up-to-date 値を CueScheduler へ push するため、
    /// ActiveChanged の唯一の駆動点を本コンポーネントに集約し評価順の非決定性を避ける。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LapCounter : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("アクティブカメラ切替の供給元。director 未割当時のフォールバック購読に使う（後方互換）。")]
        [SerializeField] private CameraStreamRegistry? registry;

        [Tooltip("切替の出どころ付き確定イベント（SwitchCommitted）の供給元。割当時はここを購読し、" +
                 "source==Zone（体験者のゾーン進行）だけを周回へ数える。手動 / Web 固定 / 外部は数えない。" +
                 "null なら registry.ActiveChanged を無差別購読（従来挙動）。")]
        [SerializeField] private CameraSwitchDirector? director;

        [Tooltip("巡回順（layout.course.order）と CourseChanged / RunReset の供給元。")]
        [SerializeField] private ShowControlClient? showControl;

        [Tooltip("進入イベントを橋渡しするスケジューラ。null なら周回カウントのみ行う。")]
        [SerializeField] private CueScheduler? cueScheduler;

        [Header("Behavior")]
        [Tooltip("Start 時に現在のアクティブカメラを『初回進入』として scheduler へ供給する（lap 1・スタート領域の cue を発火可能にする）。")]
        [SerializeField] private bool seedInitialZone = true;

        [Tooltip("周回・進行の変化を Debug.Log する。")]
        [SerializeField] private bool logChanges = true;

        private readonly LapCounterLogic _logic = new();
        private bool _subscribed;
        // heartbeat（Web ライブ表示）へ現在 lap を渡す供給元。再購読での再割当を避けキャッシュする。
        private Func<int>? _lapProvider;

        /// <summary>現在の周回数（1 始まり）。</summary>
        public int CurrentLap => _logic.CurrentLap;

        /// <summary>コース順（order）内の現在確定位置。StatusHud の「次の cue」照会に渡す。</summary>
        public int Position => _logic.Position;

        /// <summary>順方向のカメラ巡回順（layout.course.order 由来）。StatusHud の「次の cue」照会に渡す。</summary>
        public int[] Order => _logic.Order;

        private void OnEnable()
        {
            // director があれば「出どころ付き」確定を購読し、source==Zone のみ周回へ数える。
            // 無ければ従来どおり registry.ActiveChanged を無差別購読（後方互換）。
            if (director != null) director.SwitchCommitted += OnSwitchCommitted;
            else if (registry != null) registry.ActiveChanged += OnActiveCameraChanged;
            if (showControl != null)
            {
                showControl.CourseChanged += OnCourseChanged;
                showControl.RunReset += OnRunReset;
                _lapProvider ??= () => _logic.CurrentLap;
                showControl.CurrentLapProvider = _lapProvider;
            }
            _subscribed = true;
        }

        private void OnDisable()
        {
            if (!_subscribed) return;
            if (director != null) director.SwitchCommitted -= OnSwitchCommitted;
            else if (registry != null) registry.ActiveChanged -= OnActiveCameraChanged;
            if (showControl != null)
            {
                showControl.CourseChanged -= OnCourseChanged;
                showControl.RunReset -= OnRunReset;
                if (ReferenceEquals(showControl.CurrentLapProvider, _lapProvider))
                    showControl.CurrentLapProvider = null;
            }
            _subscribed = false;
        }

        // 出どころ付き確定。体験者のゾーン進行（Zone）だけを周回・cue 進入へ写す。
        // 手動 / Web 固定 / 外部（Manual/Override/External）は周回もカメラ進入通知も動かさない
        // （＝表示カメラは変わっても進行ポインタは体験者のゾーン進行だけを追う。desync は
        //   進行ポインタが順方向一致でしか進まない性質で吸収される — LapCounterTests 参照）。
        private void OnSwitchCommitted(int camera, CameraSwitchDirector.SwitchSource source)
        {
            if (source != CameraSwitchDirector.SwitchSource.Zone) return;
            OnActiveCameraChanged(camera);
        }

        // runEpoch 変化（Web の「ラン開始」）を ShowControlClient から受けて周回・cue をリセットする。
        private void OnRunReset() => ResetRun();

        private void Start() => ApplyOrder();

        /// <summary>周回・進行をスタート状態へ戻す（体験の明示リセット）。scheduler の once もリセットする。</summary>
        public void ResetRun()
        {
            _logic.Reset();
            cueScheduler?.ResetRun();
            if (logChanges) Debug.Log("[LapCounter] リセット: lap=1 / pos=0");
            SeedCurrentZone();
        }

        // order を（再）取得して周回ロジックへ適用し、現在のゾーンをシードする。
        // Start と CourseChanged の両方から呼ばれる。ShowControlClient は CourseChanged を
        // 発火する直前に schedule を CueScheduler へ push 済みなので、CourseChanged 経由の
        // ApplyOrder では schedule が揃った状態でシードできる（起動時ロード順の穴を塞ぐ）。
        private void ApplyOrder()
        {
            int[] order = showControl != null ? showControl.CourseOrder : Array.Empty<int>();
            _logic.SetOrder(order);
            if (logChanges)
                Debug.Log($"[LapCounter] order={FormatOrder(order)} (n={order.Length}) → lap=1 / pos=0");
            SeedCurrentZone();
        }

        // 起動時 / order 変更時、既にスタート領域に居るため ActiveChanged は発火しない。
        // lap 1・スタート領域の cue を発火可能にするため、現在のアクティブカメラを進入として渡す。
        // once 制限があるので CourseChanged で二重に呼ばれても多重発火しない。
        private void SeedCurrentZone()
        {
            if (!seedInitialZone || registry == null || registry.Count == 0) return;
            cueScheduler?.NotifyCameraEntered(registry.ActiveIndex, _logic.CurrentLap);
        }

        private void OnCourseChanged() => ApplyOrder();

        private void OnActiveCameraChanged(int camera)
        {
            bool advanced = _logic.Feed(camera);
            if (logChanges && advanced)
                Debug.Log($"[LapCounter] lap → {_logic.CurrentLap}（camera={camera} でスタート領域へ復帰）");
            // lap を更新した後に進入を橋渡しする（scheduler は最新 lap で評価される）。
            cueScheduler?.NotifyCameraEntered(camera, _logic.CurrentLap);
        }

        private static string FormatOrder(int[] order)
        {
            if (order == null || order.Length == 0) return "[]";
            var sb = new System.Text.StringBuilder("[");
            for (int i = 0; i < order.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(order[i]);
            }
            sb.Append(']');
            return sb.ToString();
        }
    }
}
