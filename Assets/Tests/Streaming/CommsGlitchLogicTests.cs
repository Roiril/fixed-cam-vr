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
        public void EarlyLaps_AreLighterThanLinear()
        {
            Assert.Less(CommsGlitchLogic.LevelFor(0.33f), 0.33f * 0.5f);
            Assert.Less(CommsGlitchLogic.LevelFor(0.5f), 0.5f);
            Assert.Greater(CommsGlitchLogic.LevelFor(0.9f), 0.75f);
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
        public void MissingSelection_ChangesAcrossTicks_ButIsDeterministic()
        {
            var a = new bool[32];
            var b = new bool[32];
            CommsGlitchLogic.FillMissing(a, a.Length, 1f, 8);
            CommsGlitchLogic.FillMissing(b, b.Length, 1f, 8);
            CollectionAssert.AreEqual(a, b);

            bool changed = false;
            for (int tick = 9; tick < 30 && !changed; tick++)
            {
                CommsGlitchLogic.FillMissing(b, b.Length, 1f, tick);
                changed = !System.Linq.Enumerable.SequenceEqual(a, b);
            }
            Assert.IsTrue(changed);
        }

        [Test]
        public void Corruption_RecoversOnReturnLap()
        {
            const int total = 3;
            Assert.AreEqual(1f, CommsGlitchLogic.CorruptionFor(1f, total, total, 0f, 0f), 0.001f);
            Assert.Greater(CommsGlitchLogic.CorruptionFor(1f, total + 1, total, 0f, 0f), 0.9f);
            float done = CommsGlitchLogic.CorruptionFor(1f, total + 1, total,
                                                        CommsGlitchLogic.RecoverSec * 2f, 0f);
            Assert.AreEqual(CommsGlitchLogic.RecoveredLevel, done, 0.001f);
        }

        [Test]
        public void Corruption_ClearsWhenCurseReleased()
        {
            const int total = 3;
            float before = CommsGlitchLogic.CorruptionFor(1f, total + 1, total,
                                                          CommsGlitchLogic.RecoverSec * 2f, 0f);
            float halfway = CommsGlitchLogic.CorruptionFor(1f, total + 1, total,
                                                           CommsGlitchLogic.RecoverSec * 2f, 0.5f);
            float after = CommsGlitchLogic.CorruptionFor(1f, total + 1, total,
                                                         CommsGlitchLogic.RecoverSec * 2f, 1f);
            Assert.Less(halfway, before);
            Assert.Greater(halfway, 0f);
            Assert.AreEqual(0f, after, 0.0001f);
            Assert.AreEqual(0, CommsGlitchLogic.MissingCountFor(30, after, 7));
        }

        [Test]
        public void Corruption_ReleaseClearsRegardlessOfLap()
        {
            for (int lap = 1; lap <= 4; lap++)
                Assert.AreEqual(0f, CommsGlitchLogic.CorruptionFor(1f, lap, 3, 0f, 1f), 0.0001f);
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
