#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 呪いの斑の立ち上がり（<c>canon/LEDGER.md</c> 0229「出た初めはこれ ← 1s ほどで重なる」）。
    /// 面が開くたびに 0 から目標へ <see cref="CommsCurseLogic.RampSec"/> で寄り、
    /// 同じ面のまま次の文面へ繋ぐときは重なったまま、畳めば 0。
    /// </summary>
    public sealed class CommsCurseRampTests
    {
        private const float Dt = 1f / 60f;

        private static void Advance(CommsPanelLogic l, float sec)
        {
            int n = (int)(sec / Dt + 0.5f);
            for (int i = 0; i < n; i++) l.Tick(Dt);
        }

        [Test]
        public void OpeningStartsClean_AndReachesTheTargetInOneSecond()
        {
            var l = new CommsPanelLogic();
            l.SetCurseTarget(0.6f);
            Assert.AreEqual(0f, l.Weights.curse, "畳まれていれば 0");
            l.Begin(30);
            Assert.AreEqual(0f, l.Weights.curse, 1e-6f, "出た初めは通常の面");
            Advance(l, 0.5f);
            Assert.That(l.Weights.curse, Is.GreaterThan(0.2f).And.LessThan(0.4f), "半分で半分ほど");
            Advance(l, 0.55f);
            Assert.AreEqual(0.6f, l.Weights.curse, 1e-4f, "1 秒で目標");
            Advance(l, 1f);
            Assert.AreEqual(0.6f, l.Weights.curse, 1e-4f, "目標に留まる");
        }

        [Test]
        public void ChainedNotice_KeepsTheCurse()
        {
            var l = new CommsPanelLogic();
            l.SetCurseTarget(0.6f);
            l.Begin(30);
            Advance(l, CommsPanelLogic.InSec + l.TypeSec + 0.2f);
            Assert.AreEqual(0.6f, l.Weights.curse, 1e-4f);
            l.Begin(12);   // 同じ面のまま次の文面へ（chained）
            Assert.AreEqual(0.6f, l.Weights.curse, 1e-4f, "繋いだ面では重なったまま");
        }

        [Test]
        public void ClosingResetsToZero_AndReopeningRampsAgain()
        {
            var l = new CommsPanelLogic();
            l.SetCurseTarget(1f);
            l.Begin(10);
            Advance(l, 2f);
            Assert.AreEqual(1f, l.Weights.curse, 1e-4f);
            l.Disable();
            Assert.AreEqual(0f, l.Weights.curse);
            l.Begin(10);
            Assert.AreEqual(0f, l.Weights.curse, 1e-6f, "開き直したらまた 0 から");
            Advance(l, 0.3f);
            Assert.That(l.Weights.curse, Is.GreaterThan(0f).And.LessThan(1f));
        }

        [Test]
        public void TargetChangeWhileOpen_RampsFromTheCurrentAmount()
        {
            var l = new CommsPanelLogic();
            l.SetCurseTarget(0.3f);
            l.Begin(40);
            Advance(l, 1.2f);
            Assert.AreEqual(0.3f, l.Weights.curse, 1e-4f);
            l.SetCurseTarget(1f);
            Assert.AreEqual(0.3f, l.Weights.curse, 1e-4f, "変えた瞬間は飛ばない");
            Advance(l, 0.5f);
            Assert.That(l.Weights.curse, Is.GreaterThan(0.5f).And.LessThan(0.8f));
            Advance(l, 0.6f);
            Assert.AreEqual(1f, l.Weights.curse, 1e-4f);
            l.SetCurseTarget(0f);
            Advance(l, 1.1f);
            Assert.AreEqual(0f, l.Weights.curse, 1e-4f, "目標 0 へも同じ速さで戻る（止まってください！）");
        }

        [Test]
        public void GuideFromClosed_StartsClean()
        {
            var l = new CommsPanelLogic();
            l.SetCurseTarget(0.6f);
            l.SetGuideWanted(true);
            Assert.AreEqual(CommsStage.Guide, l.Stage);
            Assert.AreEqual(0f, l.Weights.curse, 1e-6f);
            Advance(l, 1.1f);
            Assert.AreEqual(0.6f, l.Weights.curse, 1e-4f);
        }
    }
}
