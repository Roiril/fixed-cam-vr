#nullable enable
using System;

namespace FixedCamVr.Streaming
{
    public enum CommsTakeoverPhase
    {
        Off,
        Output,
        Pursuit,
        Seized,
        Complete,
    }

    public struct CommsTakeoverSample
    {
        public CommsTakeoverPhase phase;
        public float reveal;
        public float erase;
        public float collapse;
        public bool soundCut;
    }

    /// <summary>印字へ侵食が追いつき、未完成の文面を捕捉する純粋な時計。</summary>
    public static class CommsTakeoverLogic
    {
        public const float AutoDelaySec = 0.6f;
        public const float PursuitStartRatio = 0.35f;
        public const float CaptureRatio = 1f;
        public const float SeizedHoldSec = 0.35f;
        public const float CollapseLeadSec = 0.18f;
        public const float CollapseTailSec = 0.06f;
        public const float MinOutputSec = 1.25f;
        public const float MaxOutputSec = 2.2f;
        // Mono の中間計算と float の戻り値の丸め差だけを吸収する。描画1コマより十分小さい。
        private const float BoundaryToleranceSec = 0.000001f;

        public static readonly float MaxReveal = SourceReveal(1f);

        public static float OutputSecFor(int charCount, ShowLang lang)
            => Clamp(charCount / CommsPanelLogic.CharsPerSecFor(lang), MinOutputSec, MaxOutputSec);

        public static float CaptureAt(float outputSec) => Positive(outputSec) * CaptureRatio;

        public static float DurationFor(float outputSec) => CaptureAt(outputSec) + SeizedHoldSec;

        public static int MaxGeneratedChars(int charCount)
        {
            if (charCount <= 1) return 0;
            int generated = (int)Math.Ceiling(MaxReveal * charCount);
            return generated < charCount ? generated : charCount - 1;
        }

        public static CommsTakeoverSample Sample(float elapsedSinceTypeStart, float outputSec)
        {
            float duration = Positive(outputSec);
            float captureAt = CaptureAt(duration);
            float t = elapsedSinceTypeStart < 0f ? 0f : elapsedSinceTypeStart;
            float collapse = Smooth(Clamp01((t - (captureAt - CollapseLeadSec))
                                             / (CollapseLeadSec + CollapseTailSec)));

            if (t + BoundaryToleranceSec >= DurationFor(duration))
                return At(CommsTakeoverPhase.Complete, MaxReveal, 1f, 1f, true);
            if (t + BoundaryToleranceSec >= captureAt)
                return At(CommsTakeoverPhase.Seized, MaxReveal, 1f, collapse, true);

            float p = Clamp01(t / duration);
            float reveal = SourceReveal(p);
            if (p < PursuitStartRatio)
                return At(CommsTakeoverPhase.Output, reveal, 0f, collapse, false);

            float q = Clamp01((p - PursuitStartRatio) / (CaptureRatio - PursuitStartRatio));
            float erase = MaxReveal * (0.04f * q + 0.96f * q * q * q);
            return At(CommsTakeoverPhase.Pursuit, reveal, erase, collapse, false);
        }

        private static float SourceReveal(float p)
        {
            p = Clamp01(p);
            if (p <= PursuitStartRatio) return p;
            float d = p - PursuitStartRatio;
            return PursuitStartRatio + 0.70f * d
                   + 0.06f * (1f - (float)Math.Exp(-d / 0.20f));
        }

        private static CommsTakeoverSample At(CommsTakeoverPhase phase, float reveal,
            float erase, float collapse, bool soundCut)
            => new CommsTakeoverSample
            {
                phase = phase,
                reveal = reveal,
                erase = erase,
                collapse = collapse,
                soundCut = soundCut,
            };

        private static float Positive(float v) => v > 0f ? v : MinOutputSec;

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);

        private static float Smooth(float t)
        {
            t = Clamp01(t);
            return t * t * (3f - 2f * t);
        }
    }
}
