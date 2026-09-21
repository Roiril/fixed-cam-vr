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
        private bool _introEnabled = true;
        private float _introMinSec = 20f;
        private bool _introAutoAdvance = true;
        private int _totalLaps = ShowRunDefaults.TotalLaps;
        private float _hardLimitSec = 300f;
        private float _endGraceSec = ShowRunDefaults.EndGraceSec;
        private float _endHoldMaxSec = ShowRunDefaults.EndHoldMaxSec;

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

        /// <param name="endGraceSec">
        /// 終了条件が成立してから、**演出が走っていなくても必ず待つ**秒数。
        ///
        /// これが無いと <b>帰りの A（lap = totalLaps + 1 の <c>order[0]</c>）に置いた演出が
        /// 始まる前に暗転する</b>。その区間に入ったフレームで周回が上がって終了条件が立つ一方、
        /// <c>at:"enter"</c> の演出はその同じ連鎖では**武装されるだけ**で、開始は次フレーム以降の
        /// <c>TakeRunner.Update</c> だから。スクリプト実行順は未定義なので、
        /// 「演出が走っているか」だけを見ると走り出す前に終わる順序が実在する。
        /// </param>
        /// <param name="endHoldMaxSec">走行中の演出を見せ切る上限 (秒)。これを超えたら終わらなくても終了する。</param>
        public void Configure(bool introEnabled, float introMinSec, bool introAutoAdvance,
                              int totalLaps, float hardLimitSec,
                              float endGraceSec = ShowRunDefaults.EndGraceSec,
                              float endHoldMaxSec = ShowRunDefaults.EndHoldMaxSec)
        {
            _introEnabled = introEnabled;
            _introMinSec = introMinSec > 0f ? introMinSec : 0f;
            _introAutoAdvance = introAutoAdvance;
            _totalLaps = totalLaps > 0 ? totalLaps : ShowRunDefaults.TotalLaps;
            _hardLimitSec = hardLimitSec;
            _endGraceSec = endGraceSec > 0f ? endGraceSec : 0f;
            _endHoldMaxSec = endHoldMaxSec > 0f ? endHoldMaxSec : 0f;
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

        /// <summary>
        /// 導入の計時を今から始め直す。<b>導入演出（パススルー → スクリーン）が終わって
        /// 「慣らし歩行」に入る合図</b>で、<c>IntroDirector</c> が 1 回だけ呼ぶ。
        ///
        /// 演出のあいだ <c>introMinSec</c> を数えてしまうと、慣らし歩行の時間が演出に食われて
        /// 「固定視点に慣れる」という導入の本来の目的（企画書 3 章）が果たせない。
        /// </summary>
        public void RestartIntroClock()
        {
            if (_phase != ShowPhase.Intro) return;
            _introElapsed = 0f;
        }

        /// <summary>体験を今すぐ終える（卓のスタッフ操作）。走行中の演出は待たない。</summary>
        public void RequestFinish() => _finishRequested = true;

        /// <summary>
        /// 時間を進める。
        /// </summary>
        /// <param name="dt">経過秒。</param>
        /// <param name="atStartZone">体験者がスタート区間に居るか（導入の自動終了条件）。</param>
        /// <param name="takeRunning">演出が走行中か（終了を保留するかの判定）。</param>
        /// <param name="introCompleted">
        /// 導入演出（パススルー → スクリーン）が<b>終わったか</b>。演出を出さない設定なら最初から true。
        ///
        /// ⚠ **「進行中でない」ではなく「終わった」で判定する**（2026-08-06 に置き換え）。
        /// 旧引数 <c>introPlaying</c> は段 0（開始待ち）を含めなかったので、そこでも
        /// 「進行中でない」が成立していた。慣らし歩行（<see cref="_introMinSec"/> = 既定 20 秒）が
        /// 「演出が始まるまでの猶予」を兼ねていたため露見しなかったが、**慣らしを 0 にした瞬間に
        /// 体験者がスタート区間に立った時点で本編へ飛び、演出が 1 度も出なくなる**。
        ///
        /// 既定を true にしてあるのは、演出のことを知らない呼び出し側（テスト・オフライン）で
        /// 体験を止めないため（フェイルソフト）。
        ///
        /// 旧実装が防いでいた事故もそのまま防げる: <see cref="_introElapsed"/> は起動から数えるので
        /// 設営で <see cref="_introMinSec"/> はとうに過ぎている。そこへ体験者が立っても、演出が
        /// 終わるまで false なので打ち切られない（2026-07-30 は Real → Degrade の 3.5 秒で打ち切られ、
        /// 核心の Structure / Frame / Swap が一度も出なかった）。
        /// </param>
        /// <param name="introAborted">
        /// 導入を中止したか（トラッキング原点が変わって部屋の座標がずれた）。
        /// <b>true のあいだ自動では本編へ進めない。</b>
        ///
        /// 中止は「終わった」ではないので <paramref name="introCompleted"/> は立たない。それでも別引数に
        /// してあるのは、<b>中止と「まだ終わっていない」を区別して現場へ出す</b>ため
        /// （StatusHud の異常 1 件・黒の上の 1 行）。
        ///
        /// スタッフの明示操作（<see cref="RequestAdvance"/>）は従来どおり通す — 「ずれていても進めたい」
        /// と人が言っているなら、それは運営の判断で、コードが止める話ではない。
        /// </param>
        /// <param name="deferNaturalEnd">
        /// 締めの演出中は報告と警告の結果を待つ。上限と hardLimit は引き続き有効。
        /// </param>
        public ShowRunEvent Tick(float dt, bool atStartZone, bool takeRunning, bool introCompleted = true,
                                 bool introAborted = false, bool deferNaturalEnd = false)
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
                    // 演出の最中は自動では進めない（明示操作 _advanceRequested は従来どおり効く —
                    // スタッフが「いま進めたい」と言っているなら演出より人の判断を優先する）。
                    bool auto = _introAutoAdvance && _introElapsed >= _introMinSec
                                && atStartZone && introCompleted && !introAborted;
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

                    // 時間切れは待たない（待つと hardLimitSec の意味が壊れる）。
                    if (timedOut)
                    {
                        _endHolding = false;
                        _phase = ShowPhase.Finished;
                        return ShowRunEvent.RunFinished;
                    }

                    // 周回を走り切った。
                    //   - grace のあいだは演出が走っていなくても必ず待つ（帰りの A の演出が始まる猶予）
                    //   - 走り出した演出は上限まで見せ切る
                    // 分岐を 2 本に割らず 1 つの式で表す（新しい状態・ラッチを増やさない）。
                    _endHeldSec += dt;
                    if (deferNaturalEnd && _endHeldSec < _endHoldMaxSec)
                    {
                        _endHolding = true;
                        return ShowRunEvent.None;
                    }
                    if (_endHeldSec < _endGraceSec || (takeRunning && _endHeldSec < _endHoldMaxSec))
                    {
                        _endHolding = true;
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

        /// <summary>終了条件成立後、演出が始まるのを必ず待つ秒数。</summary>
        public const float EndGraceSec = 3f;

        /// <summary>走行中の演出を見せ切る上限 (秒)。帰りの A で流す録画は 20〜40 秒になる。</summary>
        public const float EndHoldMaxSec = 60f;
    }

    /// <summary>
    /// 区間 (lap, camera) が体験中に踏まれうるか。
    ///
    /// 周回は進行ポインタ方式で <c>course.order[0]</c> へ戻った時に上がるので、
    /// <b><c>lap = totalLaps + 1</c> の <c>order[0]</c>（＝帰りの A）は構造的に必ず踏む。</b>
    /// 体験は「元の位置に戻って終わる」ので、この 1 区間だけは到達可能として扱う
    /// （<c>totalLaps</c> を 4 に上げると帰りの B・C まで生きてしまい、企画書の「3 周」とも食い違う）。
    ///
    /// ⚠ <b>同じ式が 3 箇所にある。</b> ここ / 卓の <c>run-model.mjs</c> の <c>isSegmentReachable</c> /
    /// <c>tools/analyze-xp-log.py</c> の <c>is_segment_reachable</c>。片方だけ直すと沈黙して食い違うので、
    /// 期待値を 3 者のテストにハードコードして突き合わせてある。
    /// </summary>
    public static class ShowRunReach
    {
        public static bool IsSegmentReachable(int lap, int camera, int totalLaps, int[]? order)
        {
            if (lap < 1) return false;
            if (totalLaps < 1) totalLaps = ShowRunDefaults.TotalLaps;
            if (lap <= totalLaps) return true;
            if (lap != totalLaps + 1) return false;
            // 順路が未著作なら判定できない。到達可能側に倒す（著作を黙って殺さない）。
            if (order == null || order.Length == 0) return true;
            return camera == order[0];
        }

        /// <summary>その区間が「帰りの A」か（表示の文言を変えるのに使う）。</summary>
        public static bool IsReturnSegment(int lap, int camera, int totalLaps, int[]? order)
        {
            if (totalLaps < 1) totalLaps = ShowRunDefaults.TotalLaps;
            if (lap != totalLaps + 1) return false;
            if (order == null || order.Length == 0) return true;
            return camera == order[0];
        }
    }
}
