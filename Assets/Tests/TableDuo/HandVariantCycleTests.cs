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
        public void Next_CyclesAllVariantsAndWrapsAround()
        {
            Assert.AreEqual(HandVariant.Realistic, HandVariantCycle.Next(HandVariant.Default));
            Assert.AreEqual(HandVariant.Robot, HandVariantCycle.Next(HandVariant.Realistic));
            Assert.AreEqual(HandVariant.FullBody, HandVariantCycle.Next(HandVariant.Robot));
            Assert.AreEqual(HandVariant.Default, HandVariantCycle.Next(HandVariant.FullBody));
        }

        [Test]
        public void Next_CountStepsReturnsToStart()
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
            Assert.AreEqual("Remy", HandVariantCycle.Label(HandVariant.FullBody));
        }

        [Test]
        public void FullBody_FitsInTwoBitDeclarationField()
        {
            // _studyFlags の申告は bit2-3 の 2bit（& 0x3）。全バリアントが 0..3 に収まることが同期の前提
            foreach (HandVariant v in System.Enum.GetValues(typeof(HandVariant)))
            {
                Assert.LessOrEqual((byte)v, (byte)3, $"{v} が 2bit を超えると _studyFlags 同期が壊れる");
            }
        }
    }
}
