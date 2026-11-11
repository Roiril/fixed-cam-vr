#nullable enable
using System;
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// <b>体験の時計はここ。</b>実時間の読み（<c>Time.unscaledTime</c> / <c>Time.unscaledDeltaTime</c> /
    /// 体験の進行に効く <c>Time.realtimeSinceStartup</c>）は全部このクラスを通す。
    ///
    /// 時計は 3 種類ある。混ぜない。
    /// 1. <b>体験の時計</b> — <see cref="SimTime"/>。演出・判定・入力の長押し・ログの t= など、体験の進行に効くもの
    /// 2. <b>壁時計</b> — <see cref="WallClock"/>。ネットワーク・ファイル I/O の待ちと性能計測。固定刻みでも実時間を返す
    /// 3. <b>メディアの時計</b> — <c>AudioSettings.dspTime</c>・<c>VideoPlayer</c>。仮想化しない
    ///
    /// 普段（Player と通常の Editor）は Unity の値を<b>そのまま</b>返す（キャッシュせず分岐 1 つだけ。挙動は変わらない）。
    /// 検証のときだけ <see cref="EnableFixedStep"/> で固定刻みへ切り替え、1 フレーム = stepSec で進める。
    /// Unity の <c>Time.captureDeltaTime</c> は scaled 側（<c>Time.time</c> / <c>Time.deltaTime</c>）にしか効かないので、
    /// unscaled 側を揃えるのがこのクラスの役目。休止を切る <see cref="ShowClock"/> はこの <see cref="Dt"/> を読む。
    ///
    /// 機械の門: <c>TimeSourceLintTests</c>（<c>Assets/Scripts</c> で Unity の実時間を直接読むと落ちる）。
    /// </summary>
    public static class SimTime
    {
        private static bool _fixed;
        private static SimTimeLogic _logic;
        private static int _frame = -1;
        private static float _now;
        private static float _realtime;

        /// <summary>固定刻みか。</summary>
        public static bool Fixed => _fixed;

        /// <summary>固定刻みの 1 フレーム（秒）。固定刻みでなければ 0。</summary>
        public static float StepSec => _fixed ? _logic.StepSec : 0f;

        /// <summary><c>Time.unscaledTime</c> の代わり。</summary>
        public static float Now
        {
            get
            {
                if (!_fixed) return Time.unscaledTime;
                Advance();
                return _now;
            }
        }

        /// <summary><c>Time.unscaledDeltaTime</c> の代わり。</summary>
        public static float Dt => _fixed ? _logic.Dt : Time.unscaledDeltaTime;

        /// <summary>体験の進行に効く <c>Time.realtimeSinceStartup</c> の代わり。壁時計が要るなら <see cref="WallClock.Now"/>。</summary>
        public static float Realtime
        {
            get
            {
                if (!_fixed) return Time.realtimeSinceStartup;
                Advance();
                return _realtime;
            }
        }

        /// <summary>
        /// 固定刻みへ切り替える。<c>Time.captureDeltaTime</c> も同じ値にして scaled 側を揃える。
        /// 以後 1 フレーム = <paramref name="stepSec"/>。呼んだフレームの <see cref="Now"/> は呼んだ瞬間の
        /// <c>Time.unscaledTime</c>、<see cref="Realtime"/> は <c>Time.realtimeSinceStartup</c>。
        /// 既に固定刻みなら、その時点の値から続ける（時計を巻き戻さない）。
        /// </summary>
        public static void EnableFixedStep(float stepSec)
        {
            if (!SimTimeLogic.IsValidStep(stepSec))
                throw new ArgumentOutOfRangeException(nameof(stepSec), stepSec, "固定刻みは正の有限値にする");
            int frame = Time.frameCount;
            float startNow, startRealtime;
            if (_fixed)
            {
                Advance();
                startNow = _now;
                startRealtime = _realtime;
            }
            else
            {
                startNow = Time.unscaledTime;
                startRealtime = Time.realtimeSinceStartup;
            }
            _logic = new SimTimeLogic(stepSec, frame, startNow, startRealtime);
            _fixed = true;
            _frame = frame;
            _now = startNow;
            _realtime = startRealtime;
            Time.captureDeltaTime = stepSec;
        }

        /// <summary>素通しへ戻す。<c>Time.captureDeltaTime</c> を 0 に戻す。以後は Unity の値をそのまま返す。</summary>
        public static void Disable()
        {
            ResetState();
            Time.captureDeltaTime = 0f;
        }

        // Play を抜けたとき・ドメイン再読込なしの再 Play で固定刻みを残さない。
        // 固定刻みでなかったなら Unity の状態には触らない（Player の起動で何も変えない）。
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad()
        {
            bool wasFixed = _fixed;
            ResetState();
            if (wasFixed) Time.captureDeltaTime = 0f;
        }

        private static void ResetState()
        {
            _fixed = false;
            _logic = default;
            _frame = -1;
            _now = 0f;
            _realtime = 0f;
        }

        private static void Advance()
        {
            int f = Time.frameCount;
            if (f == _frame) return;
            _frame = f;
            _now = _logic.NowAt(f);
            _realtime = _logic.RealtimeAt(f);
        }
    }
}
