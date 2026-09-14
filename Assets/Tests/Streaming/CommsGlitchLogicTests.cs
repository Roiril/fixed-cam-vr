#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    public sealed class CommsGlitchLogicTests
    {
        [Test]
        public void Progress0_ChangesNoGlyph()
        {
            var missing = new bool[24];
            Assert.AreEqual(0f, CommsGlitchLogic.LevelFor(0f));
            Assert.AreEqual(0, CommsGlitchLogic.FillMissing(missing, missing.Length, 0f, 12));
            Assert.That(System.Array.Exists(missing, value => value), Is.False);
            Assert.AreEqual(0f, CommsGlitchLogic.LineOffsetM(0f, 12, 0));
            Assert.IsFalse(CommsGlitchLogic.EchoAt(0f, 12, 0));
        }

        [Test]
        public void Progress1_ReachesFullLevel()
        {
            Assert.AreEqual(1f, CommsGlitchLogic.LevelFor(1f), 0.0001f);
            Assert.AreEqual(1f, CommsGlitchLogic.LevelFor(1.7f), 0.0001f);
            Assert.AreEqual(0f, CommsGlitchLogic.LevelFor(-3f));
        }

        [Test]
        public void AuthoredInvasionLevels_AreAppliedWithoutAnotherCurve()
        {
            Assert.AreEqual(0.25f, CommsGlitchLogic.LevelFor(0.25f), 0.0001f);
            Assert.AreEqual(0.75f, CommsGlitchLogic.LevelFor(0.75f), 0.0001f);
            Assert.AreEqual(1f, CommsGlitchLogic.LevelFor(1f), 0.0001f);
        }

        [Test]
        public void Level_IsMonotonic()
        {
            float previous = -1f;
            for (float progress = 0f; progress <= 1f; progress += 0.01f)
            {
                float level = CommsGlitchLogic.LevelFor(progress);
                Assert.GreaterOrEqual(level, previous);
                previous = level;
            }
        }

        [Test]
        public void Maximum_MissesAboutThreeQuarters_ExactlyAsBudgeted()
        {
            const int glyphs = 40;
            var missing = new bool[glyphs];
            for (int tick = 0; tick < 100; tick++)
            {
                int budget = CommsGlitchLogic.MissingCountFor(glyphs, 1f, tick);
                int applied = CommsGlitchLogic.FillMissing(missing, glyphs, 1f, tick);
                Assert.AreEqual(30, budget, "40 字の 75% は常に 30 字");
                Assert.AreEqual(budget, applied);
                Assert.AreEqual(budget, System.Array.FindAll(missing, value => value).Length,
                                "テレメトリへ渡す数と実際に alpha=0 にする数を一致させる");
            }
        }

        [Test]
        public void MissingSelection_PreservesGlyphSlots()
        {
            var missing = new bool[31];
            CommsGlitchLogic.FillMissing(missing, missing.Length, 1f, 5);
            Assert.AreEqual(31, missing.Length, "欠落しても字形スロットを削除しない");
            Assert.Less(System.Array.FindAll(missing, value => value).Length, missing.Length,
                        "最大時も輪郭と整列を残す");
        }

        [Test]
        public void MissingSelection_IsStableAcrossTime_AndMonotonicWithLevel()
        {
            var a = new bool[32];
            var b = new bool[32];
            CommsGlitchLogic.FillMissing(a, a.Length, 0.5f, 8);
            CommsGlitchLogic.FillMissing(b, b.Length, 0.5f, 80);
            CollectionAssert.AreEqual(a, b);

            CommsGlitchLogic.FillMissing(b, b.Length, 0.8f, 80);
            for (int i = 0; i < a.Length; i++)
                if (a[i]) Assert.IsTrue(b[i], $"侵食量を増やしたら {i} 番の欠けが戻った");
        }

        [Test]
        public void SmearsDoNotJumpWhenOnlyTimeChanges()
        {
            for (int line = 0; line < 4; line++)
                Assert.AreEqual(CommsGlitchLogic.LineOffsetM(0.8f, 1, line),
                                CommsGlitchLogic.LineOffsetM(0.8f, 999, line));
            for (int glyph = 0; glyph < 80; glyph++)
            {
                Assert.AreEqual(CommsGlitchLogic.EchoAt(0.8f, 1, glyph),
                                CommsGlitchLogic.EchoAt(0.8f, 999, glyph));
                Assert.AreEqual(CommsGlitchLogic.EchoOffsetM(0.8f, 1, glyph),
                                CommsGlitchLogic.EchoOffsetM(0.8f, 999, glyph));
            }
        }

        [Test]
        public void LineOffsets_AreHorizontalAndShort()
        {
            bool moved = false;
            for (int tick = 0; tick < 200; tick++)
            for (int line = 0; line < 4; line++)
            {
                float offset = CommsGlitchLogic.LineOffsetM(1f, tick, line);
                Assert.LessOrEqual(System.Math.Abs(offset), CommsGlitchLogic.MaxLineOffsetM + 1e-6f);
                moved |= offset != 0f;
            }
            Assert.IsTrue(moved);
        }

        [Test]
        public void RedEcho_IsLimitedToPartOfTheText()
        {
            const int glyphs = 80;
            int worst = 0;
            int total = 0;
            for (int tick = 0; tick < 200; tick++)
            {
                int count = 0;
                for (int i = 0; i < glyphs; i++)
                {
                    if (!CommsGlitchLogic.EchoAt(1f, tick, i)) continue;
                    count++;
                    Assert.LessOrEqual(System.Math.Abs(CommsGlitchLogic.EchoOffsetM(1f, tick, i)),
                                       CommsGlitchLogic.MaxEchoOffsetM + 1e-6f);
                }
                worst = System.Math.Max(worst, count);
                total += count;
            }
            Assert.Greater(total, 0);
            Assert.Less(worst, glyphs / 2, "全文の色複製へ戻さない");
        }

        [Test]
        public void TickAt_AdvancesWithTime()
        {
            Assert.AreEqual(0, CommsGlitchLogic.TickAt(0f));
            Assert.AreEqual(0, CommsGlitchLogic.TickAt(-5f));
            Assert.AreEqual(1, CommsGlitchLogic.TickAt(CommsGlitchLogic.TickSec * 1.5f));
            Assert.Less(CommsGlitchLogic.TickAt(1f), CommsGlitchLogic.TickAt(2f));
        }
    }
}
