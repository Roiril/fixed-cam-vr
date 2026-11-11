#nullable enable
using System;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 固定刻みの進み方（純関数・テスト用）。<see cref="SimTime"/> が固定刻みのときに使う。
    ///
    /// 1 フレーム = 1 刻み。<b>フレームが飛んだら飛んだ分だけ進める</b>（何フレーム分を 1 回で読んでも、
    /// 毎フレーム読んだときと同じ値になる）。同じフレームなら何度読んでも同じ値。
    /// 積算せず「始点 + 刻み数 × stepSec」で出すので、長く回しても誤差が溜まらない。
    /// </summary>
    public readonly struct SimTimeLogic
    {
        /// <summary>1 フレームの秒。</summary>
        public readonly float StepSec;

        /// <summary>固定刻みを始めたフレーム（このフレームは刻み 0）。</summary>
        public readonly int StartFrame;

        /// <summary>始めた瞬間の <c>Now</c>（Unity の unscaledTime か、続きから始めたときは直前の値）。</summary>
        public readonly float StartNow;

        /// <summary>始めた瞬間の <c>Realtime</c>。</summary>
        public readonly float StartRealtime;

        public SimTimeLogic(float stepSec, int startFrame, float startNow, float startRealtime)
        {
            if (!IsValidStep(stepSec))
                throw new ArgumentOutOfRangeException(nameof(stepSec), stepSec, "固定刻みは正の有限値にする");
            StepSec = stepSec;
            StartFrame = startFrame;
            StartNow = startNow;
            StartRealtime = startRealtime;
        }

        /// <summary>固定刻みに使える値か（正の有限値）。</summary>
        public static bool IsValidStep(float stepSec)
            => stepSec > 0f && !float.IsNaN(stepSec) && !float.IsInfinity(stepSec);

        /// <summary>始めたフレームから進んだ刻みの数。始める前のフレーム番号を渡したら 0。</summary>
        public long StepsAt(int frame) => frame > StartFrame ? (long)frame - StartFrame : 0L;

        /// <summary>そのフレームの <c>Now</c>。</summary>
        public float NowAt(int frame) => (float)(StartNow + StepsAt(frame) * (double)StepSec);

        /// <summary>そのフレームの <c>Realtime</c>。</summary>
        public float RealtimeAt(int frame) => (float)(StartRealtime + StepsAt(frame) * (double)StepSec);

        /// <summary>1 フレームの経過。始めたフレームも 0 ではなく 1 刻み。</summary>
        public float Dt => StepSec;
    }
}
