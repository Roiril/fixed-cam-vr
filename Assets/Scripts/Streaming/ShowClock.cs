#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 体験の時計。<b>休止していた時間を数えない</b>実時間。
    ///
    /// ⚠ <c>Time.unscaledDeltaTime</c> を素のまま足すと、HMD を外して Quest が休止した秒数が
    /// 復帰した最初のフレームにまとめて入る。実機ログでは 1 回の休止で 232 秒・304 秒が出ており、
    /// 本編の時間切れ（300 秒）や締めの 15 秒がそのフレームで成立して、被り直した瞬間に終幕へ飛んでいた。
    /// ⇒ 1 フレームに進める量を <see cref="MaxStepSec"/> で切る。休止の間は 1 フレームぶんしか進まない。
    ///
    /// 体験の判定（相・終幕・締めの時間切れ・劣化）はすべてここを読む。演出の時計（<c>Time.time</c>）は
    /// Unity が <c>maximumDeltaTime</c> で既に切っているので触らない。
    /// </summary>
    public static class ShowClock
    {
        /// <summary>1 フレームに進める上限 (秒)。30fps を割り込んだ重いフレームでも体験は止めない値。</summary>
        public const float MaxStepSec = 0.25f;

        private static int _frame = -1;
        private static float _dt;
        private static float _now;
        private static int _gaps;

        /// <summary>このフレームの経過秒（休止を切ったもの）。</summary>
        public static float Dt { get { Advance(); return _dt; } }

        /// <summary>起動からの体験の時計（休止を含まない）。</summary>
        public static float Now { get { Advance(); return _now; } }

        /// <summary>休止とみなして時間を捨てた回数（観測用）。</summary>
        public static int Gaps { get { Advance(); return _gaps; } }

        /// <summary>1 フレームの経過を切る（純関数・テスト用）。</summary>
        public static float Clamp(float rawDt, out bool gap)
        {
            gap = rawDt > MaxStepSec;
            if (rawDt < 0f || float.IsNaN(rawDt)) return 0f;
            return gap ? MaxStepSec : rawDt;
        }

        private static void Advance()
        {
            int f = Time.frameCount;
            if (f == _frame) return;
            _frame = f;
            _dt = Clamp(Time.unscaledDeltaTime, out bool gap);
            if (gap) _gaps++;
            _now += _dt;
        }
    }
}
