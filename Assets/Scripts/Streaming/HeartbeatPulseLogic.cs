using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// bed_heart.wav の二連の鼓動のうち、最初の音だけで波紋を始める。
    /// 二つ目の音と次の組までの間は同じ波紋の余韻。起動からの時計ではなく
    /// AudioSource.timeSamples を読むので、音量0での待機やループでもずれない。
    /// 音源を差し替えたらピークも再計測する。
    /// </summary>
    public static class HeartbeatPulseLogic
    {
        public const float ClipLengthSec = 379885f / 48000f;
        public const float RiseSec = .18f;
        public const float DurationSec = .94f;
        // 10ms RMSで測った最初の山。その45ms前から音の立ち上がりに合わせる。
        private static readonly float[] FirstPeaks =
        {
            .13f, 1.10f, 2.11f, 3.09f, 4.09f, 5.06f, 6.07f, 7.06f
        };

        /// <summary>直前の最初の「ど」からの秒数。ループの境界でも余韻を繋ぐ。</summary>
        public static float Age(float seconds)
        {
            if (float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds < 0f) return -1f;
            float t = Mathf.Repeat(seconds, ClipLengthSec);
            for (int i = FirstPeaks.Length - 1; i >= 0; i--)
            {
                float start = FirstPeaks[i] - .045f;
                if (t >= start) return t - start;
            }
            return t + ClipLengthSec - (FirstPeaks[FirstPeaks.Length - 1] - .045f);
        }

        public static float Evaluate(float seconds)
        {
            float age = Age(seconds);
            if (age < 0f || age >= DurationSec) return 0f;
            return age < RiseSec
                ? SmootherStep(age / RiseSec)
                : 1f - SmootherStep((age - RiseSec) / (DurationSec - RiseSec));
        }

        // 始まり・頂点・終わりで速度と加速度を0にする。
        private static float SmootherStep(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);
    }
}
