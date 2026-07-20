#nullable enable
using NUnit.Framework;
using TableDuoVr.Hands;

namespace TableDuoVr.Tests
{
    /// <summary>
    /// HandVariantCycle（ホスト卓の「手役の手」巡回ボタンの純ロジック）の EditMode テスト。
    /// GameObject/NGO を使わない純関数テストのみ。
    /// </summary>
    public class HandVariantCycleTests
    {
        [Test]
        public void Next_CyclesDefaultRealisticRobotAndWrapsAround()
        {
            Assert.AreEqual(HandVariant.Realistic, HandVariantCycle.Next(HandVariant.Default));
            Assert.AreEqual(HandVariant.Robot, HandVariantCycle.Next(HandVariant.Realistic));
            Assert.AreEqual(HandVariant.Default, HandVariantCycle.Next(HandVariant.Robot));
        }

        [Test]
        public void Next_ThreeStepsReturnsToStart()
        {
            var v = HandVariant.Default;
            for (int i = 0; i < HandVariantCycle.Count; i++) v = HandVariantCycle.Next(v);
            Assert.AreEqual(HandVariant.Default, v);
        }

        [Test]
        public void Count_MatchesEnumMemberCount()
        {
            Assert.AreEqual(System.Enum.GetValues(typeof(HandVariant)).Length, HandVariantCycle.Count);
        }

        [Test]
        public void Label_MapsEachVariant()
        {
            Assert.AreEqual("白手", HandVariantCycle.Label(HandVariant.Default));
            Assert.AreEqual("リアル", HandVariantCycle.Label(HandVariant.Realistic));
            Assert.AreEqual("ロボ", HandVariantCycle.Label(HandVariant.Robot));
        }
    }
}
