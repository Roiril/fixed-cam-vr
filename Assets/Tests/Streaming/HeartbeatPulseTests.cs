using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    public sealed class HeartbeatPulseTests
    {
        [TestCase(.46f)]
        [TestCase(1.44f)]
        [TestCase(2.46f)]
        [TestCase(3.44f)]
        [TestCase(4.43f)]
        [TestCase(5.40f)]
        [TestCase(6.43f)]
        [TestCase(7.40f)]
        public void SecondSoundContinuesTheFirstSoundsDecay(float seconds)
        {
            float value = HeartbeatPulseLogic.Evaluate(seconds);
            Assert.That(value, Is.GreaterThan(.5f), "二つ目の音でも余韻が残る");
            Assert.That(HeartbeatPulseLogic.Evaluate(seconds - .02f), Is.GreaterThan(value));
            Assert.That(HeartbeatPulseLogic.Evaluate(seconds + .02f), Is.LessThan(value));
            Assert.That(HeartbeatPulseLogic.Age(seconds + .02f)
                      - HeartbeatPulseLogic.Age(seconds - .02f), Is.EqualTo(.04f).Within(.0001f));
        }

        [Test]
        public void ALoopHasEightWavesInsteadOfSixteen()
        {
            int crests = 0;
            float before = HeartbeatPulseLogic.Evaluate(-.001f + HeartbeatPulseLogic.ClipLengthSec);
            float current = HeartbeatPulseLogic.Evaluate(0f);
            for (float t = .001f; t < HeartbeatPulseLogic.ClipLengthSec; t += .001f)
            {
                float next = HeartbeatPulseLogic.Evaluate(t);
                if (current > before && current >= next && current > .99f) crests++;
                before = current;
                current = next;
            }
            Assert.That(crests, Is.EqualTo(8));
        }

        [Test]
        public void WaveLingersBetweenPairsThenSettlesBeforeTheNextOne()
        {
            Assert.That(HeartbeatPulseLogic.Evaluate(.8f), Is.GreaterThan(.05f));
            Assert.That(HeartbeatPulseLogic.Evaluate(1.04f), Is.Zero);
        }

        [Test]
        public void AudioWrapDoesNotRestartTheTravellingWave()
        {
            float before = HeartbeatPulseLogic.Age(HeartbeatPulseLogic.ClipLengthSec - .001f);
            float after = HeartbeatPulseLogic.Age(.001f);
            Assert.That(after - before, Is.EqualTo(.002f).Within(.0001f));
        }

        [Test]
        public void AudioLoopRetainsPhase()
        {
            for (float t = 0f; t < HeartbeatPulseLogic.ClipLengthSec; t += .01f)
                Assert.That(HeartbeatPulseLogic.Evaluate(t + HeartbeatPulseLogic.ClipLengthSec),
                    Is.EqualTo(HeartbeatPulseLogic.Evaluate(t)).Within(.0001f));
        }
    }
}
