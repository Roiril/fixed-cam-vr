using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// bed_heart.wav の10ms RMSで測った二連の鼓動。起動からの時計ではなく
    /// AudioSource.timeSamples を読むので、音量0での待機やループでもずれない。
    /// 音源を差し替えたらピークも再計測する。
    /// </summary>
    public static class HeartbeatPulseLogic
    {
        public const float ClipLengthSec = 379885f / 48000f;
        private static readonly float[] Peaks =
        {
            .13f, .46f, 1.10f, 1.44f, 2.11f, 2.46f, 3.09f, 3.44f,
            4.09f, 4.43f, 5.06f, 5.40f, 6.07f, 6.43f, 7.06f, 7.40f
        };

        public static float Evaluate(float seconds)
        {
            if (float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds < 0f) return 0f;
            float t = Mathf.Repeat(seconds, ClipLengthSec);
            float pulse = 0f;
            foreach (float peak in Peaks)
            {
                float age = t - peak;
                if (age < -.045f || age > .20f) continue;
                float value = age < 0f
                    ? Mathf.SmoothStep(0f, 1f, (age + .045f) / .045f)
                    : 1f - Mathf.SmoothStep(0f, 1f, age / .20f);
                pulse = Mathf.Max(pulse, value);
            }
            return pulse;
        }
    }
}
