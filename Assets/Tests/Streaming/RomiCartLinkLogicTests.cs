#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>終幕で台車へ合図を送る時機（<see cref="RomiCartLinkLogic"/>）。</summary>
    public sealed class RomiCartLinkLogicTests
    {
        private static RomiCartLinkLogic.Signals T(RomiCartLinkLogic l, int released, ShowPhase phase,
                                                   ShowEndingOutcome outcome = ShowEndingOutcome.Pending)
            => l.Tick(released, phase, outcome);

        [Test]
        public void FirstObservation_OnlySeeds_EvenIfAlreadyReleased()
        {
            var l = new RomiCartLinkLogic();
            var s = T(l, 3, ShowPhase.Run);
            Assert.IsFalse(s.go);
            Assert.IsFalse(s.cut);
        }

        [Test]
        public void Release_SendsGo_ThenPowerCut_SendsCut()
        {
            var l = new RomiCartLinkLogic();
            T(l, 0, ShowPhase.Run);
            Assert.IsTrue(T(l, 1, ShowPhase.Run).go);
            Assert.IsFalse(T(l, 1, ShowPhase.Run, ShowEndingOutcome.Released).go);
            var cut = T(l, 1, ShowPhase.Finished, ShowEndingOutcome.Released);
            Assert.IsTrue(cut.cut);
            Assert.IsFalse(T(l, 1, ShowPhase.Finished, ShowEndingOutcome.Released).cut, "電源断は 1 回だけ");
        }

        [Test]
        public void Trapped_NeverSendsAnything()
        {
            var l = new RomiCartLinkLogic();
            T(l, 0, ShowPhase.Run);
            var s = T(l, 0, ShowPhase.Finished, ShowEndingOutcome.Trapped);
            Assert.IsFalse(s.go);
            Assert.IsFalse(s.cut);
        }

        [Test]
        public void Interrupted_AfterGo_DoesNotSendCut()
        {
            var l = new RomiCartLinkLogic();
            T(l, 0, ShowPhase.Run);
            T(l, 1, ShowPhase.Run);
            Assert.IsFalse(T(l, 1, ShowPhase.Finished, ShowEndingOutcome.Interrupted).cut);
        }

        [Test]
        public void NextVisitor_CanSendAgain()
        {
            var l = new RomiCartLinkLogic();
            T(l, 0, ShowPhase.Run);
            Assert.IsTrue(T(l, 1, ShowPhase.Run).go);
            T(l, 1, ShowPhase.Finished, ShowEndingOutcome.Released);
            T(l, 1, ShowPhase.Intro);
            T(l, 1, ShowPhase.Run);
            Assert.IsTrue(T(l, 2, ShowPhase.Run).go, "次の体験者の解除でもう一度送る");
            Assert.IsTrue(T(l, 2, ShowPhase.Finished, ShowEndingOutcome.Released).cut);
        }

        [Test]
        public void ReleaseAndFinishInSameFrame_SendsBoth()
        {
            var l = new RomiCartLinkLogic();
            T(l, 0, ShowPhase.Run);
            var s = T(l, 1, ShowPhase.Finished, ShowEndingOutcome.Released);
            Assert.IsTrue(s.go);
            Assert.IsTrue(s.cut);
        }

        [Test]
        public void SecondIncrementInSameRun_DoesNotResend()
        {
            var l = new RomiCartLinkLogic();
            T(l, 0, ShowPhase.Run);
            Assert.IsTrue(T(l, 1, ShowPhase.Run).go);
            Assert.IsFalse(T(l, 2, ShowPhase.Run).go);
        }
    }
}
