#nullable enable

namespace FixedCamVr.Streaming
{
    /// <summary>体験 1 回の相。</summary>
    public enum ShowPhase
    {
        /// <summary>導入。固定視点に慣らす自由歩行。演出は武装せず、録画もしない。</summary>
        Intro,
        /// <summary>本編。1..totalLaps 周。</summary>
        Run,
        /// <summary>終了。暗転して次の体験者を待つ。</summary>
        Finished,
    }

    /// <summary>相が切り替わった合図。</summary>
    public enum ShowRunEvent
    {
        None,
        /// <summary>導入が終わって本編が始まった。</summary>
        RunBegan,
        /// <summary>本編が終わった。</summary>
        RunFinished,
    }

    /// <summary>
    /// 体験 1 回の骨格（UnityEngine 非依存・dt 注入）。企画書 3 章
    /// 「経路を 3 周する／全体は導入を含め 3 分以内／導入で固定視点に慣れてから追跡を開始する」を状態機械にしたもの。
    ///
    /// <b>導入はランの第 1 相</b>であって「ラン開始前の待機」ではない。ラン開始 = 導入の開始で、
    /// 導入→本編は周回と演出だけを初期化する（端末内録画の世代は切り替えない）。ここを別のラン開始として
    /// 扱うと「ラン開始が複数系統に増え、遅れて届いた runEpoch が 1 周目の録画を消す」経路ができる。
    ///
    /// <b>終了の判定は必ず <see cref="Tick"/>（次フレーム）で行う。</b> 周回の確定と離脱時演出の発火は
    /// 同じ同期連鎖の中で起きるので、周回の変化を受けたその場で終了を確定させると、
    /// 3 周目最後の区間に置いた離脱時演出が始まる前に体験が終わる。
    /// </summary>
    public sealed class ShowRunLogic
    {
        /// <summary>走行中の演出を待つ上限 (秒)。これを超えたら演出が終わらなくても終了する。</summary>
        public const float MaxEndHoldSec = 12f;

        private bool _introEnabled = true;
        private float _introMinSec = 20f;
        private bool _introAutoAdvance = true;
        private int _totalLaps = ShowRunDefaults.TotalLaps;
        private float _hardLimitSec = 300f;

        private ShowPhase _phase = ShowPhase.Intro;
        private float _introElapsed;
        private float _runElapsed;
        private float _lapElapsed;
        private int _lap = 1;

        private bool _advanceRequested;
        private bool _finishRequested;
        private float _endHeldSec;
        private bool _endHolding;

        public ShowPhase Phase => _phase;
        public int Lap => _lap;
        public int TotalLaps => _totalLaps;
        public float IntroElapsedSec => _introElapsed;
        public float RunElapsedSec => _runElapsed;
        public float LapElapsedSec => _lapElapsed;

        /// <summary>終了条件は満たしたが、走行中の演出を見せ切るために待っている状態か。</summary>
        public bool EndHolding => _endHolding;

        /// <summary>本編の区間進行を下流へ流してよい相か（＝ CueScheduler のゲート）。</summary>
        public bool GateOpen => _phase == ShowPhase.Run;

        public void Configure(bool introEnabled, float introMinSec, bool introAutoAdvance,
                              int totalLaps, float hardLimitSec)
        {
            _introEnabled = introEnabled;
            _introMinSec = introMinSec > 0f ? introMinSec : 0f;
            _introAutoAdvance = introAutoAdvance;
            _totalLaps = totalLaps > 0 ? totalLaps : ShowRunDefaults.TotalLaps;
            _hardLimitSec = hardLimitSec;
        }

        /// <summary>
        /// 新しい体験者のランを頭から始める。導入を挟まない設定なら即 <see cref="ShowPhase.Run"/> になり、
        /// <see cref="ShowRunEvent.RunBegan"/> を返す（呼び出し側が周回・演出の初期化を打つ）。
        /// </summary>
        public ShowRunEvent BeginRun()
        {
            _introElapsed = 0f;
            _runElapsed = 0f;
            _lapElapsed = 0f;
            _lap = 1;
            _advanceRequested = false;
            _finishRequested = false;
            _endHeldSec = 0f;
            _endHolding = false;

            if (_introEnabled)
            {
                _phase = ShowPhase.Intro;
                return ShowRunEvent.None;
            }
            _phase = ShowPhase.Run;
            return ShowRunEvent.RunBegan;
        }

        /// <summary>周回が変わったことを受ける（本編のみ意味を持つ）。ここでは終了を確定させない。</summary>
        public void NotifyLap(int lap)
        {
            if (lap == _lap) return;
            _lap = lap;
            _lapElapsed = 0f;
        }

        /// <summary>導入を今すぐ終える（卓 / 現地のスタッフ操作）。</summary>
        public void RequestAdvance() => _advanceRequested = true;

        /// <summary>体験を今すぐ終える（卓のスタッフ操作）。走行中の演出は待たない。</summary>
        public void RequestFinish() => _finishRequested = true;

        /// <summary>
        /// 時間を進める。
        /// </summary>
        /// <param name="dt">経過秒。</param>
        /// <param name="atStartZone">体験者がスタート区間に居るか（導入の自動終了条件）。</param>
        /// <param name="takeRunning">演出が走行中か（終了を保留するかの判定）。</param>
        public ShowRunEvent Tick(float dt, bool atStartZone, bool takeRunning)
        {
            if (dt < 0f) dt = 0f;

            switch (_phase)
            {
                case ShowPhase.Intro:
                    _introElapsed += dt;
                    if (_finishRequested)
                    {
                        // 導入中の「体験を終える」は、そのまま終了させる（本編に入れずに畳む）。
                        _finishRequested = false;
                        _phase = ShowPhase.Finished;
                        return ShowRunEvent.RunFinished;
                    }
                    bool auto = _introAutoAdvance && _introElapsed >= _introMinSec && atStartZone;
                    if (_advanceRequested || auto)
                    {
                        _advanceRequested = false;
                        _phase = ShowPhase.Run;
                        _runElapsed = 0f;
                        _lapElapsed = 0f;
                        _lap = 1;
                        return ShowRunEvent.RunBegan;
                    }
                    return ShowRunEvent.None;

                case ShowPhase.Run:
                    _runElapsed += dt;
                    _lapElapsed += dt;

                    if (_finishRequested)
                    {
                        // 明示操作は待たない（スタッフが「いま終わらせたい」と言っている）。
                        _finishRequested = false;
                        _phase = ShowPhase.Finished;
                        _endHolding = false;
                        return ShowRunEvent.RunFinished;
                    }

                    bool lapsDone = _lap > _totalLaps;
                    bool timedOut = _hardLimitSec > 0f && _runElapsed >= _hardLimitSec;
                    if (!lapsDone && !timedOut)
                    {
                        _endHolding = false;
                        _endHeldSec = 0f;
                        return ShowRunEvent.None;
                    }

                    // 終了条件を満たした。走行中の演出は見せ切る（上限つき）。
                    if (takeRunning && _endHeldSec < MaxEndHoldSec)
                    {
                        _endHolding = true;
                        _endHeldSec += dt;
                        return ShowRunEvent.None;
                    }
                    _endHolding = false;
                    _phase = ShowPhase.Finished;
                    return ShowRunEvent.RunFinished;

                default:
                    // 終了後は何も進まない。次のランは BeginRun で始まる。
                    _advanceRequested = false;
                    _finishRequested = false;
                    return ShowRunEvent.None;
            }
        }
    }

    /// <summary>コード既定（<c>show.json</c> に <c>run</c> が無いときの値）。</summary>
    public static class ShowRunDefaults
    {
        public const int TotalLaps = 3;
        public const float IntroMinSec = 20f;
        public const float TargetSec = 180f;
        public const float HardLimitSec = 300f;
        public const float EndFadeSec = 1.5f;
    }
}
