#nullable enable
using System;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    public sealed class ExperienceShotLogicTests
    {
        [Test]
        public void SetFolderName_IsSortableAndCarriesTheSequence()
        {
            string a = ExperienceShotLogic.SetFolderName(new DateTime(2026, 9, 30, 14, 23, 15), 1);
            string b = ExperienceShotLogic.SetFolderName(new DateTime(2026, 9, 30, 14, 23, 15), 12);
            Assert.AreEqual("20260930_142315_001", a);
            Assert.AreEqual("20260930_142315_012", b);
            Assert.Less(string.CompareOrdinal(a, b), 0, "通し番号で並ぶ");
        }

        [Test]
        public void FileNames_AreDistinct_AndOrderedByShotNumber()
        {
            string[] names =
            {
                ExperienceShotLogic.ScreenFile, ExperienceShotLogic.RawFile, ExperienceShotLogic.LayersFile,
                ExperienceShotLogic.CgAlphaFile, ExperienceShotLogic.HmdFile, ExperienceShotLogic.InfoFile,
            };
            CollectionAssert.AllItemsAreUnique(names);
            Assert.IsTrue(ExperienceShotLogic.ScreenFile.StartsWith("1_"), "① 表示中のスクリーン");
            Assert.IsTrue(ExperienceShotLogic.RawFile.StartsWith("2_"), "② 生映像");
            Assert.IsTrue(ExperienceShotLogic.LayersFile.StartsWith("3_"), "③ 合成のみ");
            Assert.IsTrue(ExperienceShotLogic.CgAlphaFile.StartsWith("3_"), "③ の CG 層");
            Assert.IsTrue(ExperienceShotLogic.HmdFile.StartsWith("4_"), "④ 体験者の視界");
        }

        [Test]
        public void Decide_AcceptsTheFirstPress()
        {
            var v = ExperienceShotLogic.Decide(true, 10f, float.NegativeInfinity, 0);
            Assert.AreEqual(ExperienceShotLogic.Verdict.Accepted, v);
        }

        [Test]
        public void Decide_RefusesWhenDisabled_EvenIfEverythingElseIsFine()
        {
            var v = ExperienceShotLogic.Decide(false, 10f, float.NegativeInfinity, 0);
            Assert.AreEqual(ExperienceShotLogic.Verdict.Disabled, v);
        }

        [Test]
        public void Decide_RefusesInsideTheCooldown_AndAcceptsAfterIt()
        {
            float last = 100f;
            Assert.AreEqual(ExperienceShotLogic.Verdict.Cooldown,
                ExperienceShotLogic.Decide(true, last + ExperienceShotLogic.CooldownSec - 0.01f, last, 1));
            Assert.AreEqual(ExperienceShotLogic.Verdict.Accepted,
                ExperienceShotLogic.Decide(true, last + ExperienceShotLogic.CooldownSec, last, 1));
        }

        [Test]
        public void Decide_StopsAtTheCap_SoStorageCannotRunAway()
        {
            Assert.AreEqual(ExperienceShotLogic.Verdict.Accepted,
                ExperienceShotLogic.Decide(true, 1000f, 0f, ExperienceShotLogic.MaxSetsPerLaunch - 1));
            Assert.AreEqual(ExperienceShotLogic.Verdict.CapReached,
                ExperienceShotLogic.Decide(true, 1000f, 0f, ExperienceShotLogic.MaxSetsPerLaunch));
        }

        [TestCase(true, false, false, true, TestName = "開発ビルドは既定で有効")]
        [TestCase(false, false, false, false, TestName = "リリースビルドは既定で無効")]
        [TestCase(false, true, false, true, TestName = "リリースでも ON の印があれば有効")]
        [TestCase(true, false, true, false, TestName = "OFF の印は開発ビルドでも止める（展示本番）")]
        [TestCase(false, true, true, false, TestName = "ON と OFF が両方あれば OFF が勝つ")]
        public void IsEnabled(bool debugBuild, bool on, bool off, bool expected)
            => Assert.AreEqual(expected, ExperienceShotLogic.IsEnabled(debugBuild, on, off));
    }
}
