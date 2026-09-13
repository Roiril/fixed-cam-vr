#nullable enable

namespace FixedCamVr.Streaming
{
    public enum CommsTakeoverPhase
    {
        Off,
        Truth,
        Erase,
        Blank,
        LieReveal,
        LieHold,
        Complete,
    }

    public struct CommsTakeoverSample
    {
        public CommsTakeoverPhase phase;
        public float erase;
        public float lie;
    }

    /// <summary>報告文が書き換わる間の純粋な時計。表示と入力は持たない。</summary>
    public static class CommsTakeoverLogic
    {
        public const float TruthHoldSec = 1.8f;
        public const float EraseSec = 1.0f;
        public const float BlankSec = 0.25f;
        public const float LieRevealSec = 0.3f;
        public const float LieHoldSec = 2.2f;
        public const float AutoDelaySec = 0.6f;

        public const float TotalSec = TruthHoldSec + EraseSec + BlankSec + LieRevealSec + LieHoldSec;
        public const float BlankAt = TruthHoldSec + EraseSec;
        public const float LieAt = BlankAt + BlankSec;
        public const float LieHoldAt = LieAt + LieRevealSec;

        public static CommsTakeoverSample Sample(float holdElapsedSec)
        {
            float t = holdElapsedSec < 0f ? 0f : holdElapsedSec;
            if (t < TruthHoldSec)
                return At(CommsTakeoverPhase.Truth, 0f, 0f);
            if (t < BlankAt)
                return At(CommsTakeoverPhase.Erase, Smooth((t - TruthHoldSec) / EraseSec), 0f);
            if (t < LieAt)
                return At(CommsTakeoverPhase.Blank, 1f, 0f);
            if (t < LieHoldAt)
                return At(CommsTakeoverPhase.LieReveal, 1f, Smooth((t - LieAt) / LieRevealSec));
            if (t < TotalSec)
                return At(CommsTakeoverPhase.LieHold, 1f, 1f);
            return At(CommsTakeoverPhase.Complete, 1f, 1f);
        }

        public static CommsTakeoverSample LieOnly(float holdElapsedSec)
            => holdElapsedSec < LieHoldSec
                ? At(CommsTakeoverPhase.LieHold, 1f, 1f)
                : At(CommsTakeoverPhase.Complete, 1f, 1f);

        private static CommsTakeoverSample At(CommsTakeoverPhase phase, float erase, float lie)
            => new CommsTakeoverSample { phase = phase, erase = erase, lie = lie };

        private static float Smooth(float t)
        {
            t = t < 0f ? 0f : (t > 1f ? 1f : t);
            return t * t * (3f - 2f * t);
        }
    }
}
