#nullable enable
using System.Collections.Generic;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 乱れの種（<c>_GlitchSeed</c>）は起動からの秒を巻き戻して渡す。
    /// シェーダの <c>Hash21</c> は入力を 456.21 倍してから frac を取るので、展示で朝から開けっ放しにすると
    /// 7 分ほどで砂の小数が消え、2 人目以降の全員が「止まった砂」を見る（起動し直す検証では出ない）。
    /// ここでは同じ式を float で写して、巻き戻した種なら 10 時間後も砂が散ることを確かめる。
    /// </summary>
    public sealed class GlitchSeedFoldTests
    {
        private static float Frac(float x) => x - (float)System.Math.Floor(x);

        /// <summary>CrtCompositeShared.hlsl の Hash21 の写し（float の丸めを段ごとに掛ける）。</summary>
        private static float Hash21(float x, float y)
        {
            float px = Frac((float)(x * 123.34f));
            float py = Frac((float)(y * 456.21f));
            float d = (float)(px * (float)(px + 45.32f)) + (float)(py * (float)(py + 45.32f));
            px = (float)(px + d);
            py = (float)(py + d);
            return Frac((float)(px * py));
        }

        /// <summary>乱れの砂の 1 列（<c>Hash21(screenUv * 300 + t)</c>, t = floor(seed * 42)）が何通りに散るか。</summary>
        private static int DistinctSand(float seed)
        {
            float t = (float)System.Math.Floor(seed * 42f);
            var seen = new HashSet<int>();
            for (int i = 0; i < 400; i++)
            {
                float u = i / 400f;
                float h = Hash21(u * 300f + t, 0.37f * 300f + t);
                seen.Add((int)(h * 256f));
            }
            return seen.Count;
        }

        [Test]
        public void FoldSeed_StaysWithinThePeriod()
        {
            Assert.AreEqual(5f, GlitchFx.FoldSeed(5f), 1e-5f);
            foreach (float sec in new[] { 0f, 31.99f, 32f, 600f, 3600.5f, 36000.25f, 86400f })
            {
                float s = GlitchFx.FoldSeed(sec);
                Assert.That(s, Is.GreaterThanOrEqualTo(0f).And.LessThan(GlitchFx.SeedPeriodSec), $"sec={sec}");
            }
        }

        [Test]
        public void UnfoldedSeed_FlattensTheSand_AfterAnHour()
        {
            // 計器の校正: 巻き戻さない種では、1 時間で砂がほぼ 1 色になることを先に確かめる。
            Assert.Greater(DistinctSand(2f), 140, "起動直後の砂がそもそも散っていない（写しが壊れている）");
            Assert.Less(DistinctSand(3600f), 20, "巻き戻さなくても散っている（精度落ちの写しが効いていない）");
        }

        [Test]
        public void FoldedSeed_KeepsTheSandScattered_AllDay()
        {
            int fresh = DistinctSand(GlitchFx.FoldSeed(2f));
            foreach (float sec in new[] { 60f, 600f, 3600f, 4f * 3600f, 10f * 3600f })
            {
                for (float k = 0f; k < GlitchFx.SeedPeriodSec; k += 0.37f)
                {
                    int later = DistinctSand(GlitchFx.FoldSeed(sec + k));
                    Assert.Greater(later, 120, $"起動から {(sec + k) / 60f:F1} 分で砂が {later} 通りまで減った（起動直後は {fresh}）");
                }
            }
        }
    }
}
