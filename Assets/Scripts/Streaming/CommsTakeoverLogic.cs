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
        public float strain;
        public float collapse;
        public bool resistance;
        public bool soundCut;
    }

    /// <summary>印字へ侵食が追いつき、未完成の文面を捕捉する純粋な時計。</summary>
    public static class CommsTakeoverLogic
    {
        public const float AutoDelaySec = 0.6f;
        public const float PursuitStartRatio = 0.20f;
        public const float ResistanceStartRatio = 0.74f;
        public const float ResistanceEndRatio = 0.82f;
        public const float CaptureRatio = 1f;
        public const float SeizedHoldSec = 0.35f;
        public const float MinOutputSec = 2.2f;
        public const float MaxOutputSec = 3f;
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

            if (t + BoundaryToleranceSec >= DurationFor(duration))
                return At(CommsTakeoverPhase.Complete, MaxReveal, 1f, 1f, 1f, false, true);
            if (t + BoundaryToleranceSec >= captureAt)
                return At(CommsTakeoverPhase.Seized, MaxReveal, 1f, 1f, 1f, false, true);

            float p = Clamp01(t / duration);
            float reveal = SourceReveal(p);
            if (p < PursuitStartRatio)
                return At(CommsTakeoverPhase.Output, reveal, 0f, 0f, 0f, false, false);

            bool resistance = p >= ResistanceStartRatio && p < ResistanceEndRatio;
            float authoredP = resistance ? ResistanceStartRatio : p;
            float erase, strain;
            if (authoredP <= ResistanceStartRatio)
            {
                float q = Clamp01((authoredP - PursuitStartRatio)
                                  / (ResistanceStartRatio - PursuitStartRatio));
                float shaped = Smooth(q);
                erase = 0.46f * shaped;
                strain = 0.52f * shaped;
            }
            else
            {
                float q = Clamp01((authoredP - ResistanceEndRatio)
                                  / (CaptureRatio - ResistanceEndRatio));
                float accelerated = q * q;
                erase = 0.46f + 0.54f * accelerated;
                strain = 0.52f + 0.48f * accelerated;
            }
            float collapse = authoredP <= ResistanceEndRatio ? 0f
                : Smooth((authoredP - ResistanceEndRatio) / (CaptureRatio - ResistanceEndRatio));
            return At(CommsTakeoverPhase.Pursuit, reveal, erase, strain, collapse,
                      resistance, false);
        }

        private static float SourceReveal(float p)
        {
            p = Clamp01(p);
            const float readableHead = 0.44f;
            const float beforeResistance = 0.68f;
            const float finalReveal = 0.88f;
            if (p <= PursuitStartRatio)
                return readableHead * p / PursuitStartRatio;
            if (p <= ResistanceStartRatio)
                return readableHead + (beforeResistance - readableHead)
                    * (p - PursuitStartRatio) / (ResistanceStartRatio - PursuitStartRatio);
            if (p < ResistanceEndRatio) return beforeResistance;
            // 捕捉の直前に出た字にも打鍵を対応させるため、最後の 4% は同じ未完状態を保つ。
            float q = Clamp01((p - ResistanceEndRatio) / 0.14f);
            return beforeResistance + (finalReveal - beforeResistance) * Smooth(q);
        }

        private static CommsTakeoverSample At(CommsTakeoverPhase phase, float reveal,
            float erase, float strain, float collapse, bool resistance, bool soundCut)
            => new CommsTakeoverSample
            {
                phase = phase,
                reveal = reveal,
                erase = erase,
                strain = strain,
                collapse = collapse,
                resistance = resistance,
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
