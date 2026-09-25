using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    public sealed class HeartbeatPulseTests
    {
        [TestCase(.13f)]
        [TestCase(.46f)]
        [TestCase(1.10f)]
        [TestCase(7.40f)]
        public void MeasuredAudioPeaksHaveFullPulse(float seconds)
            => Assert.That(HeartbeatPulseLogic.Evaluate(seconds), Is.EqualTo(1f).Within(.001f));

        [TestCase(0f)]
        [TestCase(.38f)]
        [TestCase(.85f)]
        [TestCase(7.80f)]
        public void GapsAreStill(float seconds)
            => Assert.That(HeartbeatPulseLogic.Evaluate(seconds), Is.Zero);

        [Test]
        public void AudioLoopRetainsPhase()
        {
            for (float t = 0f; t < HeartbeatPulseLogic.ClipLengthSec; t += .01f)
                Assert.That(HeartbeatPulseLogic.Evaluate(t + HeartbeatPulseLogic.ClipLengthSec),
                    Is.EqualTo(HeartbeatPulseLogic.Evaluate(t)).Within(.0001f));
        }
    }
}
