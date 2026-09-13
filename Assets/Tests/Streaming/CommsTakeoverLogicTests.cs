#nullable enable
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    public sealed class CommsTakeoverLogicTests
    {
        [Test]
        public void SamplesEveryAuthoredBoundary()
        {
            Assert.AreEqual(CommsTakeoverPhase.Truth,
                CommsTakeoverLogic.Sample(CommsTakeoverLogic.TruthHoldSec - 0.001f).phase);
            Assert.AreEqual(CommsTakeoverPhase.Erase,
                CommsTakeoverLogic.Sample(CommsTakeoverLogic.TruthHoldSec).phase);
            Assert.AreEqual(CommsTakeoverPhase.Blank,
                CommsTakeoverLogic.Sample(CommsTakeoverLogic.TruthHoldSec
                    + CommsTakeoverLogic.EraseSec).phase);
            Assert.AreEqual(CommsTakeoverPhase.LieReveal,
                CommsTakeoverLogic.Sample(CommsTakeoverLogic.TruthHoldSec
                    + CommsTakeoverLogic.EraseSec + CommsTakeoverLogic.BlankSec).phase);
            Assert.AreEqual(CommsTakeoverPhase.LieHold,
                CommsTakeoverLogic.Sample(CommsTakeoverLogic.TruthHoldSec
                    + CommsTakeoverLogic.EraseSec + CommsTakeoverLogic.BlankSec
                    + CommsTakeoverLogic.LieRevealSec).phase);
            Assert.AreEqual(CommsTakeoverPhase.Complete,
                CommsTakeoverLogic.Sample(CommsTakeoverLogic.TotalSec).phase);
        }

        [Test]
        public void LowFrameRate_NeverMovesTheContentBackwards()
        {
            float previousErase = 0f, previousLie = 0f;
            for (float t = 0f; t < CommsTakeoverLogic.TotalSec + 1f; t += 0.73f)
            {
                CommsTakeoverSample s = CommsTakeoverLogic.Sample(t);
                Assert.GreaterOrEqual(s.erase, previousErase);
                Assert.GreaterOrEqual(s.lie, previousLie);
                previousErase = s.erase;
                previousLie = s.lie;
            }
            Assert.AreEqual(CommsTakeoverPhase.Complete,
                CommsTakeoverLogic.Sample(CommsTakeoverLogic.TotalSec + 2f).phase);
        }

        [Test]
        public void DedicatedDelivery_UsesItsOwnHoldLengths()
        {
            Assert.AreEqual(CommsTakeoverLogic.TotalSec,
                CommsPanelLogic.HoldSecFor(CommsDelivery.Takeover));
            Assert.AreEqual(CommsTakeoverLogic.LieHoldSec,
                CommsPanelLogic.HoldSecFor(CommsDelivery.TakeoverLie));
        }

        [Test]
        public void OrdinaryMissAndSuccessKeepTheirExistingAnswers()
        {
            var logic = new CommsCueLogic();
            Assert.AreEqual(CommsNotice.MarkNothing, logic.Tick(Input(mark: true)));
            Assert.AreEqual(CommsNotice.MarkLogged, logic.Tick(Input(mark: true, resolved: true)));
        }

        [Test]
        public void FinalLap_AutoFiresOnlyAfterTheDollWasActuallyShown()
        {
            var logic = new CommsCueLogic();
            for (float t = 0f; t < 2f; t += 0.1f)
                Assert.AreNotEqual(CommsNotice.Takeover, logic.Tick(Input(lap: 3)));
            for (float t = 0f; t < CommsTakeoverLogic.AutoDelaySec - 0.1f; t += 0.1f)
                Assert.AreNotEqual(CommsNotice.Takeover, logic.Tick(Input(lap: 3, doll: true)));
            Assert.AreEqual(CommsNotice.Takeover, logic.Tick(Input(lap: 3, doll: true)));
            Assert.AreEqual(CommsNotice.Takeover, logic.Tick(Input(lap: 3, doll: true)),
                "実際に Deliver されるまでは one-shot を消費しない");
            logic.NotifyDelivered(CommsNotice.Takeover);
            Assert.AreNotEqual(CommsNotice.Takeover, logic.Tick(Input(lap: 3, doll: true)));
        }

        [Test]
        public void EarlierLapsAndReturnLapNeverAutoFire()
        {
            foreach (int lap in new[] { 1, 2, 4 })
            {
                var logic = new CommsCueLogic();
                for (int i = 0; i < 30; i++)
                    Assert.AreNotEqual(CommsNotice.Takeover,
                        logic.Tick(Input(lap: lap, doll: true)));
            }
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(4)]
        public void DollReportOutsideFinalLapIsAnOrdinaryResponse(int lap)
        {
            var logic = new CommsCueLogic();
            Assert.AreEqual(CommsNotice.MarkNothing,
                logic.Tick(Input(mark: true, markDoll: true, lap: lap)));
        }

        [Test]
        public void HoldingReportButtonDoesNotInterruptConcealment()
        {
            var logic = new CommsPanelLogic();
            logic.Begin(9, CommsDelivery.Takeover);
            for (int i = 0; i < 180; i++)
            {
                logic.SetGuideWanted(i % 2 == 0);
                logic.Tick(1f / 30f);
                Assert.AreNotEqual(CommsStage.Guide, logic.Stage);
            }
            Assert.Greater(logic.TakeoverSample.lie, 0f);
        }

        [Test]
        public void ReportWhileTheDollIsShown_FiresImmediatelyAndReplayIsProtected()
        {
            var logic = new CommsCueLogic();
            Assert.AreEqual(CommsNotice.Takeover,
                logic.Tick(Input(mark: true, markDoll: true, lap: 3)));
            logic.NotifyDelivered(CommsNotice.Takeover);
            Assert.AreEqual(CommsNotice.None,
                logic.Tick(Input(mark: true, markDoll: true, lap: 3, playing: true)));
            Assert.AreEqual(CommsNotice.TakeoverLie,
                logic.Tick(Input(mark: true, markDoll: true, lap: 3, modified: true)));
        }

        [Test]
        public void SuppressedReportDoesNotMasqueradeAsTheDollSignal()
        {
            var logic = new CommsCueLogic();
            Assert.AreEqual(CommsNotice.MarkNothing,
                logic.Tick(Input(mark: true, markDoll: true, suppressed: true, lap: 3)));
        }

        [Test]
        public void ReturnLapRejectsAReportThatWasSnapshottedOnTheFinalLap()
        {
            var logic = new CommsCueLogic();
            CommsCueInput input = Input(mark: true, markDoll: true, lap: 3);
            input.takeoverAllowed = false;
            Assert.AreEqual(CommsNotice.MarkNothing, logic.Tick(input));
        }

        [Test]
        public void DollSignalCanRiseAfterTheStepWasApplied()
        {
            Assert.IsFalse(TakeRunner.IsDollReplacementShowing(true, true, false),
                "人形の実体がまだ出ていない段階を表示済みにしない");
            Assert.IsTrue(TakeRunner.IsDollReplacementShowing(true, true, true),
                "次フレームで実体が出たら、カットを適用し直さなくても検出する");
            Assert.IsFalse(TakeRunner.IsDollReplacementShowing(false, true, true));
            Assert.IsFalse(TakeRunner.IsDollReplacementShowing(true, false, true));
        }

        [Test]
        public void LeavingRunResetsTheOneShotForTheNextVisitor()
        {
            var logic = new CommsCueLogic();
            logic.NotifyDelivered(CommsNotice.Takeover);
            logic.Tick(new CommsCueInput { inRun = false, dt = 0.1f });
            for (int i = 0; i < 6; i++) logic.Tick(Input(lap: 3, doll: true));
            Assert.AreEqual(CommsNotice.Takeover, logic.Tick(Input(lap: 3, doll: true)));
        }

        private static CommsCueInput Input(bool mark = false, bool resolved = false,
            bool markDoll = false, bool suppressed = false, int lap = 1, bool doll = false,
            bool playing = false, bool modified = false) => new CommsCueInput
        {
            inRun = true,
            panelDoneReading = false,
            markPressed = mark,
            markResolved = resolved,
            markDollReplacementShowing = markDoll,
            markSuppressed = suppressed,
            lap = lap,
            totalLaps = 3,
            dollReplacementShowing = doll,
            takeoverPlaying = playing,
            takeoverModified = modified,
            takeoverAllowed = true,
            dt = 0.1f,
        };

    }
}
