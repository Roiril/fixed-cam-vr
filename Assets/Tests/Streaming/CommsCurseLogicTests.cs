#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 連絡の面の呪い（<c>canon/LEDGER.md</c> 0229）の純粋な判断を固定する。
    /// 面積の目標（0.25 → 22% / 0.75 → 55% / 1 → 全面）と、場が実行時に揺れないこと。
    /// </summary>
    public sealed class CommsCurseLogicTests
    {
        [Test]
        public void Level_PassesThroughAuthoredSteps()
        {
            Assert.AreEqual(0f, CommsCurseLogic.LevelFor(0f));
            Assert.AreEqual(0f, CommsCurseLogic.LevelFor(-1f));
            Assert.AreEqual(0.25f, CommsCurseLogic.LevelFor(0.25f), 1e-5f);
            Assert.AreEqual(0.75f, CommsCurseLogic.LevelFor(0.75f), 1e-5f);
            Assert.AreEqual(1f, CommsCurseLogic.LevelFor(1.7f), 1e-5f);
        }

        [Test]
        public void Mask_CoversTheAuthoredAreas()
        {
            Assert.AreEqual(0f, CommsCurseLogic.MaskFor(0f));
            Assert.AreEqual(1f, CommsCurseLogic.MaskFor(1f));
            Assert.AreEqual(CommsCurseLogic.CoverageAtFirstPov,
                            CommsCurseLogic.CoverageOf(CommsCurseLogic.MaskFor(0.25f)), 0.02f,
                            "0.25 は 2 割強を覆う");
            Assert.AreEqual(CommsCurseLogic.CoverageAtChainedPov,
                            CommsCurseLogic.CoverageOf(CommsCurseLogic.MaskFor(0.75f)), 0.02f,
                            "0.75 は半分強を覆う（報告の返事がまだ読める）");
            Assert.AreEqual(1f, CommsCurseLogic.CoverageOf(1f), 1e-6f, "1 は全面");
            Assert.AreEqual(0f, CommsCurseLogic.CoverageOf(0f), 1e-6f, "0 は 1 画素も覆わない");
        }

        [Test]
        public void Mask_IsMonotonicInLevel()
        {
            float previous = -1f;
            for (float level = 0f; level <= 1.0001f; level += 0.05f)
            {
                float mask = CommsCurseLogic.MaskFor(level);
                Assert.GreaterOrEqual(mask, previous, $"level {level}");
                previous = mask;
            }
            Assert.Less(CommsCurseLogic.MaskFor(0.25f), CommsCurseLogic.MaskFor(0.75f));
            Assert.Less(CommsCurseLogic.MaskFor(0.75f), CommsCurseLogic.MaskFor(1f));
        }

        [Test]
        public void Field_IsDeterministic_AndBounded()
        {
            for (int i = 0; i < 400; i++)
            {
                float x = -0.7f + i * 0.0025f, y = 0.15f - i * 0.0008f;
                float a = CommsCurseLogic.Field(x, y);
                float b = CommsCurseLogic.Field(x, y);
                Assert.AreEqual(a, b, "同じ点は同じ値（実行時に乱数を振らない）");
                Assert.That(a, Is.InRange(0f, 1f));
            }
        }

        [Test]
        public void K_IsAllOrNothingAtTheEnds()
        {
            Assert.AreEqual(0f, CommsCurseLogic.K(0.5f, 0f));
            Assert.AreEqual(1f, CommsCurseLogic.K(1f, 1f), "斑 1 は場の最大値の画素も覆う");
            Assert.IsFalse(CommsCurseLogic.IsCut(0.1f, 0.05f, 0f));
            Assert.IsTrue(CommsCurseLogic.IsCut(0.1f, 0.05f, 1f));
        }

        [Test]
        public void Cut_GrowsWithMask_AndNeverShrinks()
        {
            const int n = 300;
            int previous = 0;
            for (float mask = 0f; mask <= 1.0001f; mask += 0.1f)
            {
                int cut = 0;
                for (int i = 0; i < n; i++)
                    if (CommsCurseLogic.IsCut(-0.6f + i * 0.003f, 0.02f + (i % 7) * 0.02f, mask)) cut++;
                Assert.GreaterOrEqual(cut, previous, $"mask {mask} で切られる点が減った");
                previous = cut;
            }
            Assert.AreEqual(n, previous, "斑 1 は全点を切る");
        }
    }
}
