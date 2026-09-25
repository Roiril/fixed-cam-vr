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
            logic.Tick(25f, true, 3, 2, true, true);
            Assert.AreEqual(ShowEndingOutcome.Released, logic.Outcome);
            Assert.IsFalse(logic.TrappedByLeftHalf);
        }

        [Test]
        public void EndingDollOnLeftTrapsBeforeThePromptIsReadable()
        {
            var logic = new EndingDecisionLogic();
            logic.Tick(1f, true, 4, 0, false, true);
            Assert.AreEqual(ShowEndingOutcome.Trapped, logic.Outcome);
            Assert.IsTrue(logic.TrappedByLeftHalf);
        }

        [TestCase(true, 0f, 0f, true)]
        [TestCase(true, 0.4999f, 1f, true)]
        [TestCase(true, 0.5f, 0.5f, false)]
        [TestCase(true, -0.0001f, 0.5f, false)]
        [TestCase(true, 0.25f, -0.0001f, false)]
        [TestCase(true, 0.25f, 1.0001f, false)]
        [TestCase(false, 0.25f, 0.5f, false)]
        public void LeftHalfRequiresAnOnScreenProjectedFoot(
            bool projectionValid, float u, float v, bool expected)
        {
            Assert.AreEqual(expected, EndingDecisionLogic.IsFootOnLeftHalf(projectionValid, u, v));
        }

        [Test]
        public void LeftHalfRejectsNonFiniteCoordinates()
        {
            Assert.IsFalse(EndingDecisionLogic.IsFootOnLeftHalf(true, float.NaN, 0.5f));
            Assert.IsFalse(EndingDecisionLogic.IsFootOnLeftHalf(true, float.PositiveInfinity, 0.5f));
            Assert.IsFalse(EndingDecisionLogic.IsFootOnLeftHalf(true, 0.25f, float.NegativeInfinity));
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
            Assert.IsFalse(logic.TrappedByLeftHalf);
            logic.Tick(100f, true, 3, 2);
            Assert.AreEqual(ShowEndingOutcome.Pending, logic.Outcome);
        }

        [Test]
        public void ResetClearsTheLeftHalfReason()
        {
            var logic = new EndingDecisionLogic();
            logic.Tick(1f, true, 4, 0, false, true);
            Assert.IsTrue(logic.TrappedByLeftHalf);

            logic.ResetRun();
            Assert.AreEqual(ShowEndingOutcome.Pending, logic.Outcome);
            Assert.IsFalse(logic.TrappedByLeftHalf);
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
