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

        // ---- 時間切れの手前の沈み ----------------------------------------------------

        [Test]
        public void Sink_IsZeroUntilSixSecondsBeforeTheLimit_AndOneAtTheLimit()
        {
            float start = EndingDecisionLogic.PromptLimitSec - EndingDecisionLogic.SinkSec;
            Assert.AreEqual(9f, start, 1e-6f);
            Assert.AreEqual(0f, EndingDecisionLogic.SinkAmount(0f));
            Assert.AreEqual(0f, EndingDecisionLogic.SinkAmount(start - 0.01f));
            Assert.AreEqual(0f, EndingDecisionLogic.SinkAmount(start));
            Assert.Greater(EndingDecisionLogic.SinkAmount(start + 0.01f), 0f);
            Assert.AreEqual(0.5f, EndingDecisionLogic.SinkAmount(12f), 1e-6f);
            Assert.Less(EndingDecisionLogic.SinkAmount(14.99f), 1f);
            Assert.AreEqual(1f, EndingDecisionLogic.SinkAmount(EndingDecisionLogic.PromptLimitSec));
            Assert.AreEqual(1f, EndingDecisionLogic.SinkAmount(30f));
            // 読めていない（-1）・NaN は沈めない。
            Assert.AreEqual(0f, EndingDecisionLogic.SinkAmount(-1f));
            Assert.AreEqual(0f, EndingDecisionLogic.SinkAmount(float.NaN));
        }

        [Test]
        public void Sink_RisesMonotonically_AndTheScreenOnlyGetsDarker()
        {
            float prevSink = 0f, prevBright = 1f, prevVig = 0f;
            for (float t = 0f; t <= 16f; t += 0.05f)
            {
                float s = EndingDecisionLogic.SinkAmount(t);
                float b = EndingDecisionLogic.SinkBrightness(s);
                float v = EndingDecisionLogic.SinkVignette(s);
                Assert.GreaterOrEqual(s, prevSink, $"t={t}");
                Assert.LessOrEqual(b, prevBright + 1e-6f, $"t={t}");
                Assert.GreaterOrEqual(v, prevVig - 1e-6f, $"t={t}");
                prevSink = s; prevBright = b; prevVig = v;
            }
            Assert.AreEqual(1f, EndingDecisionLogic.SinkBrightness(0f), 1e-6f);
            Assert.AreEqual(EndingDecisionLogic.SinkFloor, EndingDecisionLogic.SinkBrightness(1f), 1e-6f);
            Assert.AreEqual(0f, EndingDecisionLogic.SinkVignette(0f), 1e-6f);
            Assert.AreEqual(CameraFeelFx.HeartVignetteBase, EndingDecisionLogic.SinkVignette(1f), 1e-6f);
        }

        [Test]
        public void Sink_FollowsTheDecisionClock_AndHoldsAtOneWhenTimeRunsOut()
        {
            var logic = new EndingDecisionLogic();
            Assert.AreEqual(0f, logic.SinkAt(50f), "③b を読む前は沈まない");
            logic.NotifyPromptReadable(100f);
            Assert.AreEqual(0f, logic.SinkAt(108f));
            logic.Tick(112f, true, 4, 0);
            Assert.AreEqual(0.5f, logic.SinkAt(112f), 1e-5f);
            logic.Tick(115f, true, 4, 0);
            Assert.AreEqual(ShowEndingOutcome.Trapped, logic.Outcome);
            Assert.IsTrue(logic.TrappedByTimeout);
            Assert.AreEqual(1f, logic.SinkAt(115f));
            Assert.AreEqual(1f, logic.SinkAt(130f), "決まった後は時計を読まず、決まった時点の値で止まる");
        }

        [Test]
        public void Sink_KeepsGoingWhileTheReportIsHeld_AndReturnsWithTheDecayReleaseOnceReported()
        {
            var logic = new EndingDecisionLogic();
            var decay = new ScreenDecayLogic();
            logic.NotifyPromptReadable(0f);
            // 長押しの途中（まだ報告は成立していない）も時計どおりに進む。
            logic.Tick(13.5f, true, 4, 0);
            float atReport = logic.SinkAt(13.5f);
            Assert.AreEqual(0.75f, atReport, 1e-5f);

            logic.NotifyReleased();
            decay.Release();
            logic.Tick(13.5f, true, 4, 0);
            Assert.AreEqual(ShowEndingOutcome.Released, logic.Outcome);
            Assert.AreEqual(0f, logic.SinkAt(13.5f), "Released は時計の沈みを出さない（戻りは ReleaseK で引く）");
            Assert.AreEqual(atReport, EndingDecisionLogic.SinkAfterRelease(atReport, decay.ReleaseK), 1e-6f,
                "報告の縁は報告前の値から始まる（飛ばない）");

            decay.Tick(ScreenDecayLogic.ReleaseSec * 0.5f, false, 4, 3, 0f);
            float mid = EndingDecisionLogic.SinkAfterRelease(atReport, decay.ReleaseK);
            Assert.Greater(mid, 0f);
            Assert.Less(mid, atReport);
            decay.Tick(ScreenDecayLogic.ReleaseSec * 0.5f, false, 4, 3, 0f);
            Assert.AreEqual(0f, EndingDecisionLogic.SinkAfterRelease(atReport, decay.ReleaseK), 1e-6f,
                "劣化の戻りと同じ 0.5 秒で 1.0 の明るさへ戻る");
            // 時間切れの時刻を過ぎても、解除後は沈まない。
            Assert.AreEqual(0f, logic.SinkAt(20f));
        }

        [Test]
        public void Sink_StaysZero_ForTrapsOtherThanTheTimeout_AndForInterruptions()
        {
            // 左半分（③b の前）。
            var left = new EndingDecisionLogic();
            left.Tick(1f, true, 4, 0, false, true);
            Assert.AreEqual(ShowEndingOutcome.Trapped, left.Outcome);
            Assert.IsFalse(left.TrappedByTimeout);
            Assert.AreEqual(0f, left.SinkAt(1f));
            Assert.AreEqual(0f, left.SinkAt(30f));

            // 左半分（③b の後・沈みの途中）。
            var leftLate = new EndingDecisionLogic();
            leftLate.NotifyPromptReadable(0f);
            leftLate.Tick(12f, true, 4, 0, false, true);
            Assert.AreEqual(0f, leftLate.SinkAt(12f));

            // A を出る。
            var exit = new EndingDecisionLogic();
            exit.NotifyPromptReadable(0f);
            exit.Tick(5f, true, 4, 0, true);
            Assert.AreEqual(ShowEndingOutcome.Trapped, exit.Outcome);
            Assert.IsFalse(exit.TrappedByTimeout);
            Assert.AreEqual(0f, exit.SinkAt(5f));
            Assert.AreEqual(0f, exit.SinkAt(30f));

            // 区間が 4-A 以外になった（沈みの途中）。
            var moved = new EndingDecisionLogic();
            moved.NotifyPromptReadable(0f);
            moved.Tick(12f, true, 3, 2);
            Assert.AreEqual(ShowEndingOutcome.Trapped, moved.Outcome);
            Assert.AreEqual(0f, moved.SinkAt(12f));

            // 中断。
            var stop = new EndingDecisionLogic();
            stop.NotifyPromptReadable(0f);
            stop.Tick(12f, true, 4, 0);
            stop.Interrupt();
            Assert.AreEqual(0f, stop.SinkAt(12f));
        }

        [Test]
        public void Sink_LeavingAtTheLimitCountsAsTheTimeout()
        {
            var logic = new EndingDecisionLogic();
            logic.NotifyPromptReadable(0f);
            logic.Tick(15f, true, 4, 0, true);
            Assert.AreEqual(ShowEndingOutcome.Trapped, logic.Outcome);
            Assert.IsTrue(logic.TrappedByTimeout);
            Assert.AreEqual(1f, logic.SinkAt(15f));
        }

        [Test]
        public void Sink_ResetRunStartsTheNextVisitorUnsunk()
        {
            var logic = new EndingDecisionLogic();
            logic.NotifyPromptReadable(0f);
            logic.Tick(15f, true, 4, 0);
            Assert.AreEqual(1f, logic.SinkAt(15f));

            logic.ResetRun();
            Assert.IsFalse(logic.TrappedByTimeout);
            Assert.AreEqual(0f, logic.SinkAt(15f));
            Assert.AreEqual(0f, logic.SinkAt(100f));
        }

        [Test]
        public void SinkAfterRelease_ClampsItsInputs()
        {
            Assert.AreEqual(0f, EndingDecisionLogic.SinkAfterRelease(float.NaN, 0f));
            Assert.AreEqual(0f, EndingDecisionLogic.SinkAfterRelease(1f, float.NaN));
            Assert.AreEqual(1f, EndingDecisionLogic.SinkAfterRelease(2f, -1f));
            Assert.AreEqual(0f, EndingDecisionLogic.SinkAfterRelease(1f, 2f));
        }
    }
}
