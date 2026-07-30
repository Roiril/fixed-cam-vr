#nullable enable
using System;
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 体験 1 回の骨格を回す実行体。企画書 3 章「経路を 3 周する／導入を含め 3 分以内／
    /// 導入で固定視点に慣れてから追跡体験を開始する」の実装側。
    ///
    /// 判定はすべて純ロジック <see cref="ShowRunLogic"/> にあり、ここは配線だけを持つ:
    ///   - 導入・終了のあいだ <see cref="CueScheduler.SetShowGate"/> を閉じる
    ///     （演出の武装・端末内録画・区間 post / BGM・実測滞在がまとめて止まる単一の首）
    ///   - 導入 → 本編 で <see cref="ShowControlClient.BeginMainRun"/>（周回と演出だけを初期化。**録画は消さない**）
    ///   - 終了で 走行中の演出を畳む / BGM を落とす / 乱れを畳む → <see cref="PhaseChanged"/> を配る
    ///
    /// <b>凍結ラッチは増やさない。</b> 終了しても画面のカメラ切替は裏で回ったままで、見えなくなるのは
    /// 暗転（<c>ShowEndingFader</c>）のおかげ。凍結を足すと「解除されずに残る」事故を新しく作る。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ShowRunDirector : MonoBehaviour
    {
        [Header("References (未配線でも実行時に自己解決する)")]
        [SerializeField] private CueScheduler? cueScheduler;
        [SerializeField] private TimelineDirector? timelineDirector;
        [SerializeField] private ShowControlClient? showControl;
        [SerializeField] private CameraSwitchDirector? switchDirector;

        private readonly ShowRunLogic _logic = new ShowRunLogic();

        private BgmDirector? _bgm;
        private GlitchFx? _glitch;
        private bool _subscribed;
        private ShowPhase _lastPhase = ShowPhase.Intro;
        private float _endFadeSec = ShowRunDefaults.EndFadeSec;
        private float _targetSec = ShowRunDefaults.TargetSec;

        /// <summary>相が変わったときに発火する（<c>ShowEndingFader</c> / StatusHud / heartbeat が読む）。</summary>
        public event Action<ShowPhase>? PhaseChanged;

        public ShowPhase Phase => _logic.Phase;
        public int Lap => _logic.Lap;
        public int TotalLaps => _logic.TotalLaps;
        public float RunElapsedSec => _logic.RunElapsedSec;
        public float LapElapsedSec => _logic.LapElapsedSec;
        public float IntroElapsedSec => _logic.IntroElapsedSec;

        /// <summary>目安の尺 (秒)。超過は卓に出すだけで体験は止めない。</summary>
        public float TargetSec => _targetSec;

        /// <summary>終了時に黒へ落とす時間 (秒)。<c>ShowEndingFader</c> が読む。</summary>
        public float EndFadeSec => _endFadeSec;

        /// <summary>終了条件は満たしたが走行中の演出を見せ切るために待っているか（卓表示用）。</summary>
        public bool EndHolding => _logic.EndHolding;

        /// <summary>show.json <c>run</c> を反映する（欠落ならコード既定）。</summary>
        public void Configure(ShowRunDef? def)
        {
            bool introEnabled = def?.introEnabled ?? true;
            float introMin = def != null ? def.introMinSec : ShowRunDefaults.IntroMinSec;
            bool introAuto = def?.introAutoAdvance ?? true;
            int laps = def?.ResolveTotalLaps() ?? ShowRunDefaults.TotalLaps;
            float hard = def != null ? def.hardLimitSec : ShowRunDefaults.HardLimitSec;
            _targetSec = def != null && def.targetSec > 0f ? def.targetSec : ShowRunDefaults.TargetSec;
            _endFadeSec = def != null && def.endFadeSec >= 0f ? def.endFadeSec : ShowRunDefaults.EndFadeSec;
            _logic.Configure(introEnabled, introMin, introAuto, laps, hard);
            // JsonUtility は `intro` キーが無くても「全部 0」の実体を作るので、それは未設定として扱う
            // （そのまま渡すと enabled=false に化けて導入演出が黙って出なくなる）。
            var i = def?.intro;
            IntroDef = (i == null || i.LooksUnset) ? new ShowIntroDef() : i;
            IntroDefChanged?.Invoke(IntroDef);
        }

        /// <summary>導入設定が更新された（<c>IntroDirector</c> が購読して尺を入れ替える）。</summary>
        public event Action<ShowIntroDef>? IntroDefChanged;

        /// <summary>
        /// ランを頭から始め直した（体験者交代）。**相が変わらなくても必ず発火する。**
        /// 導入演出はこれを購読して武装し直す — 慣らし歩行の途中で ▶ ラン開始を押すと
        /// 相は Intro → Intro で <see cref="PhaseChanged"/> が発火せず、
        /// **次の体験者に演出が一度も出なかった**（2026-07-30 の監査 high 7）。
        /// </summary>
        public event Action? RunRestarted;

        /// <summary>新しい体験者のランを頭から始める（導入があれば導入から）。</summary>
        public void BeginRun()
        {
            ShowRunEvent ev = _logic.BeginRun();
            ApplyGate();
            if (ev == ShowRunEvent.RunBegan) OnRunBegan();
            NotifyPhaseIfChanged();
            RunRestarted?.Invoke();
        }

        /// <summary>導入を今すぐ終える（卓 / 現地のスタッフ操作）。</summary>
        public void RequestAdvanceIntro() => _logic.RequestAdvance();

        /// <summary>
        /// 慣らし歩行の計時を今から始める（導入演出が終わった合図）。<c>IntroDirector</c> が呼ぶ。
        /// これが無いと、演出の秒数が <c>introMinSec</c> を食って慣らし歩行が短くなる。
        /// </summary>
        public void RestartIntroClock() => _logic.RestartIntroClock();

        /// <summary>show.json の導入設定（<c>IntroDirector</c> が読む）。未設定ならコード既定。</summary>
        public ShowIntroDef IntroDef { get; private set; } = new ShowIntroDef();

        /// <summary>体験を今すぐ終える（卓のスタッフ操作）。</summary>
        public void RequestFinish() => _logic.RequestFinish();

        private void Awake()
        {
            ResolveRefs();
            ApplyGate();
        }

        private void OnEnable()
        {
            ResolveRefs();
            if (cueScheduler != null && !_subscribed)
            {
                cueScheduler.CameraEntered += OnCameraEntered;
                _subscribed = true;
            }
            ApplyGate();
        }

        private void OnDisable()
        {
            if (cueScheduler != null && _subscribed) cueScheduler.CameraEntered -= OnCameraEntered;
            _subscribed = false;
            // 自分が閉じたゲートを開けたまま去る（このコンポーネントを外したら従来どおり常に流れる）。
            cueScheduler?.SetShowGate(true);
        }

        private void ResolveRefs()
        {
            if (cueScheduler == null) cueScheduler = FindObjectOfType<CueScheduler>();
            if (timelineDirector == null) timelineDirector = FindObjectOfType<TimelineDirector>();
            if (showControl == null) showControl = FindObjectOfType<ShowControlClient>();
            if (switchDirector == null) switchDirector = FindObjectOfType<CameraSwitchDirector>();
            if (_bgm == null) _bgm = FindObjectOfType<BgmDirector>();
            if (_glitch == null) _glitch = FindObjectOfType<GlitchFx>();
        }

        // 周回は「本編の区間進行」からだけ受ける（ゲートが閉じている導入・終了では来ない）。
        private void OnCameraEntered(int camera, int lap) => _logic.NotifyLap(lap);

        private void Update()
        {
            bool atStart = AtStartZone();
            bool takeRunning = timelineDirector != null && !string.IsNullOrEmpty(timelineDirector.ActiveTakeId);

            ShowRunEvent ev = _logic.Tick(Time.unscaledDeltaTime, atStart, takeRunning);

            // ⚠ ゲートは**イベントを配る前に**合わせる。RunBegan の処理は
            // BeginMainRun → LapCounter.ResetRun → SeedCurrentZone → CueScheduler.NotifyCameraEntered
            // を通る。ここが後だと、その進入が**まだ閉じているゲートに捨てられる**。
            // 周回は進行ポインタ方式なので、捨てられた (lap 1, course.order[0]) は二度と来ない
            //（次に order[0] へ入る時は lap 2）。結果、**スタート区間だけが 1 周目に録画されず**、
            // それを背景に使う 3 周目の録画カットが実機で無言で飛ぶ（2026-07-29 監査）。
            ApplyGate();

            if (ev == ShowRunEvent.RunBegan) OnRunBegan();
            else if (ev == ShowRunEvent.RunFinished) OnRunFinished();

            NotifyPhaseIfChanged();
        }

        private bool AtStartZone()
        {
            if (switchDirector == null || !switchDirector.TryGetCurrentZoneCamera(out int cam)) return false;
            int[]? order = showControl != null ? showControl.CourseOrder : null;
            // 順路が未著作なら「どこに居ても導入は終われる」（現地で order を書き忘れて詰むのを避ける）。
            if (order == null || order.Length == 0) return true;
            return cam == order[0];
        }

        private void ApplyGate() => cueScheduler?.SetShowGate(_logic.GateOpen);

        private void OnRunBegan()
        {
            Debug.Log("[ShowRun] 導入を終えて本編へ（周回 1 から）");
            // **録画の世代は切り替えない。** ここを録画のリセット系統に載せると、ラン開始が複数系統に増え、
            // 遅れて届いた runEpoch が 1 周目の録画を消す経路ができる（3 周目の素材が消える）。
            showControl?.BeginMainRun();
        }

        private void OnRunFinished()
        {
            Debug.Log($"[ShowRun] 体験の終了（{_logic.Lap - 1} 周 / 経過 {_logic.RunElapsedSec:F0} 秒）");
            timelineDirector?.AbortActive();
            _glitch?.ResetAll();
            if (_bgm != null) _bgm.StopAll(_endFadeSec > 0f ? _endFadeSec : ShowRunDefaults.EndFadeSec);
        }

        private void NotifyPhaseIfChanged()
        {
            if (_logic.Phase == _lastPhase) return;
            _lastPhase = _logic.Phase;
            PhaseChanged?.Invoke(_lastPhase);
        }
    }
}
