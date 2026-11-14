#nullable enable
using System;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 4 周目 A の③a / ③b で、連絡の面の上の辺から人形の面が縦の筋で降りてくる場
    /// （<c>CommsRecovery.hlsl</c> の <c>CommsStreakEval</c>）の C# の写し。UnityEngine 非依存。
    /// 画は GPU が描き、ここは「その字は人形の面の中か」を数えるためだけに同じ式を解く
    /// （テレメトリの赤い字の数。<c>canon/LEDGER.md</c> 0356 / 0358）。
    ///
    /// ⚠ 定数と式は HLSL が同じ値で持つ。直すときは 2 か所。
    /// </summary>
    public static class CommsStreakLogic
    {
        public const float CellM = 0.0032f;
        public const float FixedSeed = 11f;
        public const float LiveSeed = 500f;
        public const float LiveShare = 0.61f;

        /// <summary>組み替わる層の番号（HLSL の <c>CommsStreakEpoch</c>）。</summary>
        public static float Epoch(float sec) => MathF.Floor(sec * 1.9f + 0.6f * MathF.Sin(sec * 0.7f));

        /// <summary>
        /// 面のローカル座標 (<paramref name="px"/>, <paramref name="py"/>) が人形の面の中か。
        /// 量が 0 なら、どこも人形の面ではない。
        /// </summary>
        public static bool Covered(float px, float py, float rectCx, float rectCy, float rectW, float rectH,
                                   float reach, float sec)
        {
            if (reach <= 0.001f) return false;
            reach = reach > 1f ? 1f : reach;
            float xl = px - (rectCx - rectW * 0.5f);
            float yd = rectCy + rectH * 0.5f - py;
            if (xl < 0f || xl > rectW || yd < 0f || yd > rectH) return false;
            return LayerCovers(xl, yd, rectW, rectH, reach, FixedSeed, 1f)
                   || LayerCovers(xl, yd, rectW, rectH, reach, LiveSeed + Epoch(sec), LiveShare);
        }

        /// <summary>止まっている層だけで見た「人形の面の中か」（時計に依らない。面積を数えるテスト用）。</summary>
        public static bool CoveredByFixedLayer(float px, float py, float rectCx, float rectCy, float rectW,
                                               float rectH, float reach)
        {
            if (reach <= 0.001f) return false;
            reach = reach > 1f ? 1f : reach;
            float xl = px - (rectCx - rectW * 0.5f);
            float yd = rectCy + rectH * 0.5f - py;
            if (xl < 0f || xl > rectW || yd < 0f || yd > rectH) return false;
            return LayerCovers(xl, yd, rectW, rectH, reach, FixedSeed, 1f);
        }

        private static bool LayerCovers(float xl, float yd, float w, float h, float reach, float seed, float share)
        {
            Column(xl, seed, out float id, out float mult);
            float keyX = id + mult * 517f;
            float u = CommsCurseLogic.Hash21(keyX + 1.3f, seed + 2.1f);
            float kind = CommsCurseLogic.Hash21(keyX + 5.7f, seed + 9.2f);
            float hd = CommsCurseLogic.Hash21(keyX + 11.1f, seed + 4.4f);
            float xc = (id + 0.5f) * mult * CellM;
            float xn = Clamp01(xc / MathF.Max(w, 1e-4f));
            float clump = CommsCurseLogic.ValueNoise(xc / 0.035f, seed * 3.1f + 0.5f);
            u = Clamp01(u - 0.45f * (clump - 0.5f));
            float bias = 0.34f * MathF.Exp(-xn / 0.16f) + 0.20f * MathF.Exp(-(1f - xn) / 0.10f);
            float quiet = xn > 0.22f && xn < 0.62f ? 0.55f : 1f;
            float gate = (0.62f * MathF.Pow(reach, 1.7f) + bias * reach) * quiet * share;
            if (u >= gate || kind < 0.32f) return false;
            float depth = Clamp01(0.75f * MathF.Exp((hd - 0.5f) * 1.9f) * reach * 1.25f) * h;
            return yd < depth;
        }

        private static void Column(float xl, float seed, out float id, out float mult)
        {
            float fine = MathF.Floor(xl / CellM);
            float mid = MathF.Floor(fine / 4f);
            float wide = MathF.Floor(fine / 12f);
            if (CommsCurseLogic.Hash21(wide, seed + 71f) < 0.07f) { id = wide; mult = 12f; return; }
            if (CommsCurseLogic.Hash21(mid, seed + 37f) < 0.24f) { id = mid; mult = 4f; return; }
            id = fine;
            mult = 1f;
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    }
}
