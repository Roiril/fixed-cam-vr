#nullable enable

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 連絡の印字が周回とともに欠けていく。UnityEngine 非依存・時刻注入。
    /// 元の文面と字幅は変えず、表示側が頂点の不透明度と位置だけを変える。
    /// </summary>
    public sealed class CommsGlitchLogic
    {
        public const float Exponent = 2f;
        public const float MaxMissingShare = 0.75f;
        public const float RecoverSec = 3.2f;
        public const float RecoveredLevel = 0.12f;
        public const float TickSec = 0.22f;
        public const float MaxLineOffsetM = 0.006f;
        public const float MaxEchoShare = 0.16f;
        public const float MaxEchoOffsetM = 0.0035f;
        public const float OffThreshold = 0.002f;

        public static float LevelFor(float decayProgress)
        {
            float p = Clamp01(decayProgress);
            if (p <= 0f) return 0f;
            float level = Pow(p, Exponent);
            return level <= OffThreshold ? 0f : level;
        }

        /// <summary>
        /// AI の侵食。3 周目で 1、帰りの A で 0.12、呪いの解除後に 0 となる。
        /// 顔の侵食と本文の欠落がこの同じ値を読む。
        /// </summary>
        public static float CorruptionFor(float decayProgress, int lap, int totalLaps, float returnSec,
                                          float releaseK)
        {
            float level = LevelFor(decayProgress);
            if (totalLaps < 1) totalLaps = 3;
            if (lap > totalLaps)
            {
                float k = Smooth(Clamp01(returnSec / RecoverSec));
                level += (RecoveredLevel - level) * k;
            }

            float released = Clamp01(releaseK);
            if (released > 0f) level *= 1f - released;
            return level <= OffThreshold ? 0f : level;
        }

        public static int TickAt(float timeSec)
            => timeSec <= 0f ? 0 : (int)(timeSec / TickSec);

        /// <summary>この刻みで欠ける字形数。最大時はおよそ 75%。</summary>
        public static int MissingCountFor(int glyphCount, float level, int tick)
        {
            if (glyphCount <= 0) return 0;
            float share = MaxMissingShare * Clamp01(level);
            if (share <= OffThreshold) return 0;
            float expected = glyphCount * share;
            int count = (int)expected;
            if (Hash01((uint)tick * 2246822519u + 99u) < expected - count) count++;
            return count > glyphCount ? glyphCount : count;
        }

        /// <summary>
        /// 欠ける字形をちょうど予算数だけ選ぶ。文字列には触れない。
        /// </summary>
        public static int FillMissing(bool[] missing, int glyphCount, float level, int tick)
        {
            if (missing == null) throw new System.ArgumentNullException(nameof(missing));
            if (glyphCount < 0 || glyphCount > missing.Length)
                throw new System.ArgumentOutOfRangeException(nameof(glyphCount));

            System.Array.Clear(missing, 0, missing.Length);
            int budget = MissingCountFor(glyphCount, level, tick);
            for (int n = 0; n < budget; n++)
            {
                int best = -1;
                uint bestScore = uint.MaxValue;
                for (int i = 0; i < glyphCount; i++)
                {
                    if (missing[i]) continue;
                    uint score = Hash((uint)tick * 2654435761u + (uint)i * 2246822519u + 374761393u);
                    if (best >= 0 && score >= bestScore) continue;
                    bestScore = score;
                    best = i;
                }
                if (best >= 0) missing[best] = true;
            }
            return budget;
        }

        /// <summary>行全体を短く横へずらす。縦位置と改行位置は保つ。</summary>
        public static float LineOffsetM(float level, int tick, int line)
        {
            float amount = Clamp01(level);
            if (amount <= OffThreshold || line < 0) return 0f;
            float chance = 0.18f + 0.42f * amount;
            float pick = Hash01((uint)tick * 668265263u + (uint)line * 374761393u + 17u);
            if (pick >= chance) return 0f;
            float magnitude = 0.35f + 0.65f
                * Hash01((uint)tick * 1103515245u + (uint)line * 22695477u + 12345u);
            float sign = Hash01((uint)tick * 22695477u + (uint)line * 668265263u + 1u) < 0.5f
                ? -1f : 1f;
            return sign * MaxLineOffsetM * amount * magnitude;
        }

        /// <summary>この字形に鈍い赤の残像を残すか。</summary>
        public static bool EchoAt(float level, int tick, int glyphOrdinal)
        {
            float amount = Clamp01(level);
            if (amount <= OffThreshold || glyphOrdinal < 0) return false;
            return Hash01((uint)tick * 3266489917u + (uint)glyphOrdinal * 668265263u + 41u)
                   < MaxEchoShare * amount;
        }

        public static float EchoOffsetM(float level, int tick, int glyphOrdinal)
        {
            if (!EchoAt(level, tick, glyphOrdinal)) return 0f;
            float magnitude = 0.45f + 0.55f
                * Hash01((uint)tick * 747796405u + (uint)glyphOrdinal * 2891336453u + 73u);
            float sign = Hash01((uint)tick * 277803737u + (uint)glyphOrdinal * 668265263u + 9u) < 0.5f
                ? -1f : 1f;
            return sign * MaxEchoOffsetM * Clamp01(level) * magnitude;
        }

        private static float Smooth(float t)
        {
            t = Clamp01(t);
            return t * t * (3f - 2f * t);
        }

        private static uint Hash(uint n)
        {
            unchecked
            {
                uint x = n * 747796405u + 2891336453u;
                x = ((x >> (int)((x >> 28) + 4)) ^ x) * 277803737u;
                return (x >> 22) ^ x;
            }
        }

        private static float Hash01(uint n) => (Hash(n) & 0xFFFFFFu) / 16777215f;
        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
        private static float Pow(float b, float e) => (float)System.Math.Pow(b, e);
    }
}
