using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Tests.Streaming
{
    public sealed class EndingDecisionLogicTests
    {
        [Test]
        public void PromptStartsTheClock_AndOnlyTheConfirmedReturnSegmentIsSafe()
        {
            var logic = new EndingDecisionLogic();
            logic.Tick(100f, true, 3, 2);
            Assert.AreEqual(ShowEndingOutcome.Pending, logic.Outcome);

            logic.NotifyPromptReadable(100f);
            logic.Tick(114.99f, true, 4, 0);
            Assert.AreEqual(ShowEndingOutcome.Pending, logic.Outcome);
            logic.Tick(115f, true, 4, 0);
            Assert.AreEqual(ShowEndingOutcome.Trapped, logic.Outcome);
        }

        [Test]
        public void LeavingAfterPromptTraps_EvenIfAlreadyOutsideAtTheEdge()
        {
            var logic = new EndingDecisionLogic();
            logic.NotifyPromptReadable(10f);
            logic.Tick(10f, true, 3, 2);
            Assert.AreEqual(ShowEndingOutcome.Trapped, logic.Outcome);
        }

        [Test]
        public void UnknownSegmentDoesNotCountAsLeaving()
        {
            var logic = new EndingDecisionLogic();
            logic.NotifyPromptReadable(10f);
            logic.Tick(11f, false, -1, -1);
            Assert.AreEqual(ShowEndingOutcome.Pending, logic.Outcome);
        }

        [Test]
        public void ReleasedReportWinsOverLeavingAndTimeoutInTheSameTick()
        {
            var logic = new EndingDecisionLogic();
            logic.NotifyPromptReadable(10f);
            logic.NotifyReleased();
            logic.Tick(25f, true, 3, 2);
            Assert.AreEqual(ShowEndingOutcome.Released, logic.Outcome);
        }

        [Test]
        public void ReleasedReportDoesNotRequireThePromptToHaveFinishedTyping()
        {
            var logic = new EndingDecisionLogic();
            logic.NotifyReleased();
            logic.Tick(5f, true, 4, 0);
            Assert.AreEqual(ShowEndingOutcome.Released, logic.Outcome);
        }

        [Test]
        public void OtherReportsCannotRelease_AndResetClearsThePreviousRun()
        {
            var logic = new EndingDecisionLogic();
            logic.NotifyPromptReadable(10f);
            logic.Tick(25f, true, 4, 0);
            Assert.AreEqual(ShowEndingOutcome.Trapped, logic.Outcome);
            logic.NotifyReleased();
            Assert.AreEqual(ShowEndingOutcome.Trapped, logic.Outcome);

            logic.ResetRun();
            Assert.AreEqual(ShowEndingOutcome.Pending, logic.Outcome);
            Assert.IsFalse(logic.PromptReadable);
            Assert.IsFalse(logic.ReleaseReported);
            logic.Tick(100f, true, 3, 2);
            Assert.AreEqual(ShowEndingOutcome.Pending, logic.Outcome);
        }

        [Test]
        public void PromptNotificationIsIdempotent()
        {
            var logic = new EndingDecisionLogic();
            logic.NotifyPromptReadable(10f);
            logic.NotifyPromptReadable(20f);
            logic.Tick(25f, true, 4, 0);
            Assert.AreEqual(ShowEndingOutcome.Trapped, logic.Outcome);
        }

        [Test]
        public void OutsidePhysicalAreaTrapsEvenWhenCameraRetainsA_AndReportStillWins()
        {
            var logic = new EndingDecisionLogic();
            logic.Tick(1f, true, 4, 0, true);
            Assert.AreEqual(ShowEndingOutcome.Pending, logic.Outcome);
            logic.NotifyPromptReadable(10f);
            logic.Tick(11f, true, 4, 0, true);
            Assert.AreEqual(ShowEndingOutcome.Trapped, logic.Outcome);
            logic.ResetRun();
            logic.NotifyPromptReadable(20f);
            logic.NotifyReleased();
            logic.Tick(21f, true, 4, 0, true);
            Assert.AreEqual(ShowEndingOutcome.Released, logic.Outcome);
        }

        [Test]
        public void UnexpectedEndIsInterrupted()
        {
            var logic = new EndingDecisionLogic();
            logic.NotifyReleased();
            logic.Tick(1f, true, 4, 0);
            logic.Interrupt();
            Assert.AreEqual(ShowEndingOutcome.Interrupted, logic.Outcome);
        }
    }
}
