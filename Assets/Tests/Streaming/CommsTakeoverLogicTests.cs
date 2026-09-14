#nullable enable
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    public sealed class CommsTakeoverLogicTests
    {
        private const float Dt = 1f / 30f;

        [Test]
        public void OutputDurationUsesTheNormalLanguageSpeedWithinItsBounds()
        {
            Assert.AreEqual(CommsTakeoverLogic.MinOutputSec,
                CommsTakeoverLogic.OutputSecFor(1, ShowLang.Ja), 0.001f);
            Assert.AreEqual(CommsTakeoverLogic.MinOutputSec,
                CommsTakeoverLogic.OutputSecFor(18, ShowLang.Ja), 0.001f);
            Assert.AreEqual(CommsTakeoverLogic.MinOutputSec,
                CommsTakeoverLogic.OutputSecFor(18, ShowLang.En), 0.001f);
            Assert.AreEqual(CommsTakeoverLogic.MaxOutputSec,
                CommsTakeoverLogic.OutputSecFor(100, ShowLang.Fr), 0.001f);
        }

        [Test]
        public void SamplesEveryAuthoredBoundary()
        {
            const float outputSec = 2f;
            float pursuitAt = outputSec * CommsTakeoverLogic.PursuitStartRatio;
            float captureAt = CommsTakeoverLogic.CaptureAt(outputSec);

            Assert.AreEqual(CommsTakeoverPhase.Output,
                CommsTakeoverLogic.Sample(0f, outputSec).phase);
            Assert.AreEqual(CommsTakeoverPhase.Pursuit,
                CommsTakeoverLogic.Sample(pursuitAt, outputSec).phase);
            Assert.AreEqual(CommsTakeoverPhase.Seized,
                CommsTakeoverLogic.Sample(captureAt, outputSec).phase);
            Assert.AreEqual(CommsTakeoverPhase.Complete,
                CommsTakeoverLogic.Sample(CommsTakeoverLogic.DurationFor(outputSec), outputSec).phase);
        }

        [Test]
        public void SourceKeepsPrintingWhileThePursuitAdvances()
        {
            const float outputSec = 2f;
            CommsTakeoverSample early = CommsTakeoverLogic.Sample(0.9f, outputSec);
            CommsTakeoverSample late = CommsTakeoverLogic.Sample(1.4f, outputSec);

            Assert.AreEqual(CommsTakeoverPhase.Pursuit, early.phase);
            Assert.AreEqual(CommsTakeoverPhase.Pursuit, late.phase);
            Assert.Greater(late.reveal, early.reveal);
            Assert.Greater(late.erase, early.erase);
        }

        [Test]
        public void PursuitEndsFasterThanTheSourcePrints()
        {
            const float outputSec = 2.2f;
            CommsTakeoverSample a = CommsTakeoverLogic.Sample(2.0f, outputSec);
            CommsTakeoverSample b = CommsTakeoverLogic.Sample(2.1f, outputSec);

            Assert.Greater(b.erase - a.erase, b.reveal - a.reveal);
        }

        [Test]
        public void ResistanceFreezesEveryRenderedValue()
        {
            const float outputSec = 2.2f;
            CommsTakeoverSample a = CommsTakeoverLogic.Sample(outputSec * 0.75f, outputSec);
            CommsTakeoverSample b = CommsTakeoverLogic.Sample(outputSec * 0.81f, outputSec);

            Assert.IsTrue(a.resistance);
            Assert.IsTrue(b.resistance);
            Assert.AreEqual(a.reveal, b.reveal, 0.000001f);
            Assert.AreEqual(a.erase, b.erase, 0.000001f);
            Assert.AreEqual(a.strain, b.strain, 0.000001f);
            Assert.AreEqual(a.collapse, b.collapse, 0.000001f);
        }

        [Test]
        public void RevealEraseAndCollapseNeverRunBackward()
        {
            const float outputSec = 2.2f;
            CommsTakeoverSample previous = CommsTakeoverLogic.Sample(0f, outputSec);
            for (int i = 1; i <= 220; i++)
            {
                CommsTakeoverSample current = CommsTakeoverLogic.Sample(i * 0.01f, outputSec);
                Assert.GreaterOrEqual(current.reveal + 0.000001f, previous.reveal);
                Assert.GreaterOrEqual(current.erase + 0.000001f, previous.erase);
                Assert.GreaterOrEqual(current.collapse + 0.000001f, previous.collapse);
                Assert.GreaterOrEqual(current.strain + 0.000001f, previous.strain);
                previous = current;
            }
        }

        [TestCase(11)]
        [TestCase(24)]
        [TestCase(31)]
        public void AtLeastOneCharacterIsGeneratedAfterResistance(int charCount)
        {
            CommsTakeoverSample held = CommsTakeoverLogic.Sample(
                CommsTakeoverLogic.MinOutputSec * 0.81f, CommsTakeoverLogic.MinOutputSec);
            int before = (int)System.Math.Ceiling(held.reveal * charCount);
            Assert.GreaterOrEqual(CommsTakeoverLogic.MaxGeneratedChars(charCount), before + 1);
        }

        [Test]
        public void CaptureCutsSoundBeforeTheSentenceCanFinish()
        {
            const float outputSec = 2f;
            CommsTakeoverSample seized = CommsTakeoverLogic.Sample(
                CommsTakeoverLogic.CaptureAt(outputSec), outputSec);

            Assert.Less(CommsTakeoverLogic.MaxReveal, 1f);
            Assert.AreEqual(CommsTakeoverLogic.MaxReveal, seized.reveal, 0.0001f);
            Assert.AreEqual(1f, seized.erase, 0.0001f);
            Assert.IsTrue(seized.soundCut);
            for (int count = 1; count <= 40; count++)
                Assert.LessOrEqual(CommsTakeoverLogic.MaxGeneratedChars(count), count - 1);
        }

        [Test]
        public void CollapseStartsAfterResistanceAndFinishesAtCapture()
        {
            const float outputSec = 2f;
            float captureAt = CommsTakeoverLogic.CaptureAt(outputSec);

            Assert.AreEqual(0f, CommsTakeoverLogic.Sample(
                outputSec * CommsTakeoverLogic.ResistanceEndRatio, outputSec).collapse, 0.0001f);
            Assert.Greater(CommsTakeoverLogic.Sample(outputSec * 0.9f, outputSec).collapse, 0f);
            Assert.AreEqual(1f, CommsTakeoverLogic.Sample(
                captureAt, outputSec).collapse, 0.0001f);
        }

        [Test]
        public void TakeoverUsesTypeUntilCompleteAndNeverRestoresItsGlyphs()
        {
            var logic = new CommsPanelLogic();
            logic.Begin(9, CommsDelivery.Takeover);
            Assert.AreEqual(CommsTakeoverPhase.Off, logic.TakeoverSample.phase);

            logic.Tick(CommsPanelLogic.InSec);
            Assert.AreEqual(CommsStage.Type, logic.Stage);
            Assert.AreEqual(CommsTakeoverPhase.Output, logic.TakeoverSample.phase);
            Assert.AreEqual(0f, logic.Weights.hint);

            for (int i = 0; i < 100 && logic.Stage == CommsStage.Type; i++)
                logic.Tick(0.19f);

            Assert.AreEqual(CommsStage.Out, logic.Stage);
            Assert.AreEqual(CommsTakeoverPhase.Complete, logic.TakeoverSample.phase);
            Assert.AreEqual(0f, logic.Weights.glyph);
            Assert.AreEqual(CommsTakeoverLogic.MaxReveal, logic.Weights.reveal, 0.0001f);

            logic.Tick(CommsPanelLogic.OutSec * 0.5f);
            Assert.AreEqual(0f, logic.Weights.glyph);
            Assert.Less(logic.Weights.panel, 1f);
            logic.Tick(CommsPanelLogic.OutSec);
            Assert.AreEqual(CommsStage.Off, logic.Stage);
            Assert.AreEqual(0f, logic.Weights.glyph);
        }

        [Test]
        public void HoldingReportButtonNeverEntersGuideDuringTakeover()
        {
            var logic = new CommsPanelLogic();
            logic.Begin(9, CommsDelivery.Takeover);

            for (int i = 0; i < 120 && logic.Stage != CommsStage.Out; i++)
            {
                logic.SetGuideWanted(true);
                logic.Tick(Dt);
                Assert.AreNotEqual(CommsStage.Guide, logic.Stage);
                Assert.AreNotEqual(CommsStage.Hold, logic.Stage);
            }

            Assert.AreEqual(CommsStage.Out, logic.Stage);
        }

        [Test]
        public void OrdinaryMissAndSuccessKeepTheirExistingAnswers()
        {
            var logic = new CommsCueLogic();
            Assert.AreEqual(CommsNotice.MarkNothing, logic.Tick(Input(mark: true)));
            Assert.AreEqual(CommsNotice.MarkLogged, logic.Tick(Input(mark: true, resolved: true)));
        }

        [Test]
        public void SuccessTakesPriorityDuringAndAfterTakeover()
        {
            var logic = new CommsCueLogic();
            Assert.AreEqual(CommsNotice.MarkLogged,
                logic.Tick(Input(mark: true, resolved: true, playing: true)));
            Assert.AreEqual(CommsNotice.MarkLogged,
                logic.Tick(Input(mark: true, resolved: true, modified: true)));
        }

        [Test]
        public void UnresolvedReportDuringOrAfterTakeoverProducesNoReply()
        {
            var logic = new CommsCueLogic();
            Assert.AreEqual(CommsNotice.None,
                logic.Tick(Input(mark: true, markDoll: true, lap: 3, playing: true)));
            Assert.AreEqual(CommsNotice.None,
                logic.Tick(Input(mark: true, markDoll: true, lap: 3, modified: true)));
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
        public void EarlierLapsReturnLapAndModifiedRunNeverAutoFire()
        {
            foreach (int lap in new[] { 1, 2, 4 })
            {
                var logic = new CommsCueLogic();
                for (int i = 0; i < 30; i++)
                    Assert.AreNotEqual(CommsNotice.Takeover,
                        logic.Tick(Input(lap: lap, doll: true)));
            }

            var modified = new CommsCueLogic();
            for (int i = 0; i < 30; i++)
                Assert.AreNotEqual(CommsNotice.Takeover,
                    modified.Tick(Input(lap: 3, doll: true, modified: true)));
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
        public void ReportWhileTheDollIsShownFiresImmediately()
        {
            var logic = new CommsCueLogic();
            Assert.AreEqual(CommsNotice.Takeover,
                logic.Tick(Input(mark: true, markDoll: true, lap: 3)));
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
