#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 体験者が人形になったときの背丈の倍率を固定する（<c>canon/LEDGER.md</c> 0384
    /// 「エリアA,Cの間だけ1.5倍くらいに補正してほしい。下に表示されていて見れない。Bでは現在のままでok」）。
    /// </summary>
    public sealed class DollReplacementHeightScaleTests
    {
        [Test]
        public void VisitorTurnedDoll_IsOneAndAHalf_InAAndC()
        {
            Assert.That(TakeRunner.DollReplacementHeightScale(true, 0), Is.EqualTo(1.5f));
            Assert.That(TakeRunner.DollReplacementHeightScale(true, 2), Is.EqualTo(1.5f));
        }

        [Test]
        public void VisitorTurnedDoll_StaysAsItIs_InB()
            => Assert.That(TakeRunner.DollReplacementHeightScale(true, 1), Is.EqualTo(1f));

        [Test]
        public void DollsThatAreNotTheVisitor_StayAsTheyAre()
        {
            // 2 周目の後ろをついてくる人形（trail）は体験者の代わりではない
            bool trail = TakeRunner.IsDollReplacementStep(true, TakeSchema.CgTrail);
            Assert.That(TakeRunner.DollReplacementHeightScale(trail, 0), Is.EqualTo(1f));
            Assert.That(TakeRunner.DollReplacementHeightScale(false, 2), Is.EqualTo(1f));
        }

        [Test]
        public void UnknownCamera_StaysAsItIs()
            => Assert.That(TakeRunner.DollReplacementHeightScale(true, -1), Is.EqualTo(1f));
    }
}
