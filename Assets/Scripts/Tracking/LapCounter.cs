#nullable enable
using System;
using System.Collections.Generic;
using FixedCamVr.Streaming;
using UnityEngine;

namespace FixedCamVr.Tracking
{
    /// <summary>
    /// 周回カウントの純粋ロジック。MonoBehaviour（<see cref="LapCounter"/>）から分離して EditMode テスト可能にする。
    ///
    /// <b>周回数は 2 つある。</b>混ぜると必ず事故になるので、名前で分けてある（2026-08-17）:
    ///
    ///   - <see cref="CurrentLap"/>（<b>進行の周</b>）= 進行ポインタが数えた周。**単調増加で減らない**。
    ///     体験の終了判定（<c>lap &gt; totalLaps</c>）・StatusHud の「N周目／全3周」・heartbeat が読む
    ///   - <see cref="SegmentLap"/>（<b>区間の周</b>）= <b>いま体験者が居る区間</b>の周。**逆走で戻る**。
    ///     演出・区間 post・区間 BGM・端末内録画・実測滞在が読む区間キー (lap, camera) の lap
    ///
    /// セマンティクス（計画 2026-07-17_pre-authored-cue-schedule.md）:
    ///   - lap は 1 始まり（体験開始 = 1 周目）
    ///   - 進行ポインタ方式: 期待する次カメラ = order[(pos+1) % n]。アクティブカメラがそれに一致した時だけ前進。
    ///     逆走・同一・スキップ遷移では前進しない（境界 jitter・行き来・逆走はカウントされない）
    ///   - 全カメラを順方向に踏破し order[0] に戻った時点で lap++
    ///   - リセットで pos=0 / lap=1 に戻る
    /// 初期状態は pos=0（= order[0] = スタート領域に居る前提）。
    ///
    /// <b>⚠⚠ 逆走で戻った区間は「前にそこに居たときの周」として扱う</b>（2026-08-17 に追加）。
    /// 旧実装は進行の周をそのまま区間キーに使っていたので、
    /// <c>1周目C → 2周目A → 引き返して C</c> が <c>(2, C)</c> と読まれ、
    /// <b>体験者が 2 周目の B を一度も通らないまま 2 周目 C の演出が消費されていた</b>
    /// （<c>once=true</c> なので、後で正規に 2 周目 C へ来ても二度と出ない）。
    /// カメラごとに「最後にそこを確定したときの周」を覚えておき、順方向でない進入ではその値を返す。
    /// これで上の例は <c>(1, C) → (2, A) → (2, B)</c> と、体験者の実感どおりに並ぶ。
    ///
    /// <b>⚠ 前進の判定には「直前に居たカメラ」も要る</b>（2026-08-17 の設計レビューで判明）。
    /// 進入カメラが <c>order[pos+1]</c> と一致するかだけを見ると、<b>引き返した後の 1 歩</b>で外れる:
    /// <c>A(2周目) → C → B</c> のとき、ポインタは <c>pos=0(A)</c> のままなので B は「A の次」に見え、
    /// 実際には C から**戻ってきた**のに前進として数えてしまう。
    /// 前進は「<b>ポインタの居る所に居た人が、その次へ進んだ</b>」＝
    /// <c>prev == order[pos] &amp;&amp; camera == order[pos+1]</c> のときだけ。
    /// 引き返している間ポインタは据え置かれ、順路へ戻れば元どおり進む。
    /// </summary>
    public sealed class LapCounterLogic
    {
        private int[] _order = Array.Empty<int>();
        private int _pos;      // order 内の現在確定位置
        private int _lap = 1;
        private int _segLap = 1;

        // 直前に確定したカメラ。前進の判定に要る（上の注記）。未確定なら「ポインタの位置に居る」と見なす
        // （このクラスの初期状態の約束: pos=0 = order[0] に居る）。
        private bool _hasPrev;
        private int _prevCamera;

        // カメラ index → そこを最後に確定したときの周。逆走で戻ったときの区間キーの lap になる。
        private readonly Dictionary<int, int> _lastLapByCamera = new();

        /// <summary>
        /// <b>進行の周</b>（1 始まり・単調増加）。順方向に 1 周踏破したときだけ上がる。
        /// 終了判定・HUD・heartbeat 用。区間キーには <see cref="SegmentLap"/> を使うこと。
        /// </summary>
        public int CurrentLap => _lap;

        /// <summary>
        /// <b>区間の周</b> ＝ 直近の <see cref="Feed"/> / <see cref="Seed"/> で確定した区間の周。
        /// 順方向なら <see cref="CurrentLap"/> と同じ。逆走なら「前にそのカメラに居たときの周」。
        /// </summary>
        public int SegmentLap => _segLap;

        /// <summary>order 内の現在確定位置（テスト・診断用）。</summary>
        public int Position => _pos;

        /// <summary>順方向のカメラ巡回順。order[0] = スタート領域のカメラ。</summary>
        public int[] Order => _order;

        /// <summary>巡回順を差し替える。進行ポインタと lap はスタート状態（pos=0 / lap=1）にリセットする。</summary>
        public void SetOrder(int[] order)
        {
            _order = order ?? Array.Empty<int>();
            Reset();
        }

        /// <summary>進行と周回をスタート状態に戻す（体験の明示リセット）。order は保持。</summary>
        public void Reset()
        {
            _pos = 0;
            _lap = 1;
            _segLap = 1;
            _hasPrev = false;
            // ⚠ 前の体験者の足跡を残さない。残すと、次の体験者の 1 周目が
            //   「前の人が最後に居たときの周」として読まれる。
            _lastLapByCamera.Clear();
        }

        /// <summary>
        /// アクティブカメラの進入を供給する。順方向に一致したら前進し、order[0] へ戻れば lap++。
        /// 戻り値は「この供給で lap が前進したか」。<see cref="SegmentLap"/> は必ず更新される。
        /// </summary>
        public bool Feed(int camera)
        {
            int n = _order.Length;
            if (n == 0)
            {
                _segLap = _lap;
                return false;
            }
            if (!IsForward(camera, n))
            {
                // 逆走 / 同一 / スキップ / 順路から外れた所からの進入 = 進行は前進しない。
                // 区間だけ「前にそこに居たときの周」へ戻す。
                _segLap = LastLapOf(camera);
                Remember(camera, _segLap);
                return false;
            }
            _pos = (_pos + 1) % n;
            bool advanced = _pos == 0;
            if (advanced) _lap++;
            _segLap = _lap;
            Remember(camera, _segLap);
            return advanced;
        }

        /// <summary>
        /// 起動・ランリセットで「いま居るゾーン」を初回進入として流す前に呼ぶ（進行は動かさない）。
        /// 戻り値は区間キーに使う周。
        /// </summary>
        public int Seed(int camera)
        {
            _segLap = LastLapOf(camera);
            Remember(camera, _segLap);
            return _segLap;
        }

        // 「ポインタの居る所に居た人が、その次へ進んだ」ときだけ前進。
        // prev を見ない実装は、引き返した後の 1 歩を前進と読み違える（クラスの注記）。
        private bool IsForward(int camera, int n)
        {
            if (_order[(_pos + 1) % n] != camera) return false;
            return !_hasPrev || _prevCamera == _order[_pos];
        }

        // そのカメラを最後に確定したときの周。一度も居たことが無ければ現在の進行の周
        //（未知のカメラを「過去に居た」ことにしない）。
        private int LastLapOf(int camera)
            => _lastLapByCamera.TryGetValue(camera, out int lap) ? lap : _lap;

        private void Remember(int camera, int lap)
        {
            _lastLapByCamera[camera] = lap;
            _hasPrev = true;
            _prevCamera = camera;
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

        [Tooltip("ショーの時計（ZoneCommitted）の供給元。割当時はここを購読し、体験者のゾーン進行だけを" +
                 "周回へ数える。手動 / Web 固定 / インサートの画面切替は数えない（そもそも発火しない）。" +
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

        /// <summary>
        /// <b>進行の周</b>（1 始まり・単調増加）。HUD・heartbeat・終了判定が読む。
        /// 区間キーの周は <see cref="LapCounterLogic.SegmentLap"/>（逆走で戻る）で、別物。
        /// </summary>
        public int CurrentLap => _logic.CurrentLap;

        /// <summary>コース順（order）内の現在確定位置。StatusHud の「次の cue」照会に渡す。</summary>
        public int Position => _logic.Position;

        /// <summary>順方向のカメラ巡回順（layout.course.order 由来）。StatusHud の「次の cue」照会に渡す。</summary>
        public int[] Order => _logic.Order;

        private void OnEnable()
        {
            // director があればショーの時計（ZoneCommitted）を購読する。画面が凍結していても発火するので
            // 演出中に歩かれても周回が止まらない（段 B）。無ければ従来どおり registry.ActiveChanged を
            // 無差別購読（後方互換）。
            if (director != null) director.ZoneCommitted += OnActiveCameraChanged;
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
            if (director != null) director.ZoneCommitted -= OnActiveCameraChanged;
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
            // **時計（いま体験者が居るゾーン）を第一候補にする**。registry.ActiveIndex は「画面」なので、
            // 演出が画面を横取りしている最中にリセットすると、ゾーンとして存在しないインサート先カメラを
            // 「進入」として流し込んでしまう（2026-07-26 監査 HIGH）。時計が未確定のときだけ画面へ落ちる。
            int camera = director != null && director.TryGetCurrentZoneCamera(out int zoneCam)
                ? zoneCam
                : registry.ActiveIndex;
            int segLap = _logic.Seed(camera);
            cueScheduler?.NotifyCameraEntered(camera, segLap, _logic.CurrentLap);
        }

        private void OnCourseChanged() => ApplyOrder();

        private void OnActiveCameraChanged(int camera)
        {
            bool advanced = _logic.Feed(camera);
            if (logChanges && advanced)
                Debug.Log($"[LapCounter] lap → {_logic.CurrentLap}（camera={camera} でスタート領域へ復帰）");
            if (logChanges && _logic.SegmentLap != _logic.CurrentLap)
                Debug.Log($"[LapCounter] 引き返し: camera={camera} は {_logic.SegmentLap} 周目の区間として扱う" +
                          $"（進行は {_logic.CurrentLap} 周目）");
            // lap を更新した後に進入を橋渡しする（scheduler は最新 lap で評価される）。
            cueScheduler?.NotifyCameraEntered(camera, _logic.SegmentLap, _logic.CurrentLap);
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
