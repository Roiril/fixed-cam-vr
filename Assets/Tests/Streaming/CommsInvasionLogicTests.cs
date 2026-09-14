#nullable enable
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    public sealed class CommsInvasionLogicTests
    {
        [Test]
        public void NewRun_StartsWithNoInvasion()
        {
            var logic = new CommsInvasionLogic();

            Assert.That(logic.Level, Is.Zero);
        }

        [TestCase(1, 2)]
        [TestCase(2, 0)]
        [TestCase(2, 1)]
        public void BeforeLap2CameraC_PovDoesNotAdvance(int lap, int cameraIndex)
        {
            var logic = new CommsInvasionLogic();

            bool changed = logic.Observe(lap, cameraIndex, "pov_0", false);

            Assert.That(changed, Is.False);
            Assert.That(logic.Level, Is.Zero);
        }

        [Test]
        public void FirstPovAtLap2CameraC_AdvancesToQuarter()
        {
            var logic = new CommsInvasionLogic();

            bool changed = logic.Observe(2, 2, "pov_0", false);

            Assert.That(changed, Is.True);
            Assert.That(logic.Level, Is.EqualTo(0.25f));
        }

        [Test]
        public void FirstPovLevel_HoldsAfterCueEnds()
        {
            var logic = new CommsInvasionLogic();
            logic.Observe(2, 2, "pov_0", false);

            bool changed = logic.Observe(2, 2, null, false);

            Assert.That(changed, Is.False);
            Assert.That(logic.Level, Is.EqualTo(0.25f));
        }

        [TestCase("pov_1")]
        [TestCase("pov_2")]
        [TestCase("pov_3")]
        [TestCase("pov_4")]
        public void FirstChainedPovAtLap2CameraC_AdvancesToThreeQuarters(string cueId)
        {
            var logic = new CommsInvasionLogic();

            bool changed = logic.Observe(2, 2, cueId, false);

            Assert.That(changed, Is.True);
            Assert.That(logic.Level, Is.EqualTo(0.75f));
        }

        [TestCase(1, 2)]
        [TestCase(2, 0)]
        [TestCase(2, 1)]
        [TestCase(3, 2)]
        public void ChainedPovOutsideLap2CameraC_DoesNotAdvance(int lap, int cameraIndex)
        {
            var logic = new CommsInvasionLogic();

            bool changed = logic.Observe(lap, cameraIndex, "pov_1", false);

            Assert.That(changed, Is.False);
            Assert.That(logic.Level, Is.Zero);
        }

        [Test]
        public void ChainedPovLevel_HoldsAfterCueEnds()
        {
            var logic = new CommsInvasionLogic();
            logic.Observe(2, 2, "pov_1", false);

            bool changed = logic.Observe(2, 2, null, false);

            Assert.That(changed, Is.False);
            Assert.That(logic.Level, Is.EqualTo(0.75f));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("pov")]
        [TestCase("pov_5")]
        public void UnrelatedCue_DoesNotAdvance(string? cueId)
        {
            var logic = new CommsInvasionLogic();

            bool changed = logic.Observe(2, 2, cueId, false);

            Assert.That(changed, Is.False);
            Assert.That(logic.Level, Is.Zero);
        }

        [Test]
        public void Progression_IsMonotonicWhenEarlierCueIsObservedAgain()
        {
            var logic = new CommsInvasionLogic();
            logic.Observe(2, 2, "pov_0", false);
            logic.Observe(2, 2, "pov_1", false);

            bool changed = logic.Observe(2, 2, "pov_0", false);

            Assert.That(changed, Is.False);
            Assert.That(logic.Level, Is.EqualTo(0.75f));
        }

        [Test]
        public void DuplicateObservation_IsIdempotent()
        {
            var logic = new CommsInvasionLogic();
            Assert.That(logic.Observe(2, 2, "pov_0", false), Is.True);

            Assert.That(logic.Observe(2, 2, "pov_0", false), Is.False);
            Assert.That(logic.Level, Is.EqualTo(0.25f));
        }

        [TestCase(2, 0)]
        [TestCase(2, 2)]
        [TestCase(3, 1)]
        [TestCase(4, 0)]
        public void DollReplacementOutsideLap3CameraA_DoesNotAdvance(int lap, int cameraIndex)
        {
            var logic = new CommsInvasionLogic();

            bool changed = logic.Observe(lap, cameraIndex, null, true);

            Assert.That(changed, Is.False);
            Assert.That(logic.Level, Is.Zero);
        }

        [Test]
        public void DollReplacementAtLap3CameraA_AdvancesDirectlyToFull()
        {
            var logic = new CommsInvasionLogic();

            bool changed = logic.Observe(3, 0, null, true);

            Assert.That(changed, Is.True);
            Assert.That(logic.Level, Is.EqualTo(1f));
        }

        [Test]
        public void Lap3CameraAWithoutDollReplacement_DoesNotAdvance()
        {
            var logic = new CommsInvasionLogic();

            bool changed = logic.Observe(3, 0, null, false);

            Assert.That(changed, Is.False);
            Assert.That(logic.Level, Is.Zero);
        }

        [Test]
        public void FullInvasion_HoldsAcrossReverseAndLaterObservations()
        {
            var logic = new CommsInvasionLogic();
            logic.Observe(3, 0, null, true);

            Assert.That(logic.Observe(1, 0, null, false), Is.False);
            Assert.That(logic.Observe(2, 2, "pov_0", false), Is.False);
            Assert.That(logic.Observe(4, 2, null, false), Is.False);
            Assert.That(logic.Level, Is.EqualTo(1f));
        }

        [Test]
        public void Reset_ClearsLevelAndAllowsProgressionAgain()
        {
            var logic = new CommsInvasionLogic();
            logic.Observe(3, 0, null, true);

            logic.Reset();

            Assert.That(logic.Level, Is.Zero);
            Assert.That(logic.Observe(2, 2, "pov_0", false), Is.True);
            Assert.That(logic.Level, Is.EqualTo(0.25f));
        }
    }
}
