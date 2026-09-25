#nullable enable
using NUnit.Framework;

namespace FixedCamVr.Input.Tests
{
    public sealed class RegistrationSampleInputGateTests
    {
        [Test]
        public void TrackingLossDuringSample_RequiresReleaseAfterTrackingReturns()
        {
            var gate = new RegistrationSampleInputGate();
            Assert.IsTrue(gate.Tick(tracked: true, buttonHeld: true, sampleActive: true));
            Assert.IsFalse(gate.Tick(tracked: false, buttonHeld: true, sampleActive: true));
            Assert.IsFalse(gate.Tick(tracked: true, buttonHeld: true, sampleActive: false));
            Assert.IsTrue(gate.Tick(tracked: true, buttonHeld: false, sampleActive: false));
            Assert.IsTrue(gate.Tick(tracked: true, buttonHeld: true, sampleActive: false));
        }

        [Test]
        public void TrackingLossDoesNotLatchWhenNoSampleOrPressExists()
        {
            var gate = new RegistrationSampleInputGate();
            Assert.IsFalse(gate.Tick(tracked: false, buttonHeld: false, sampleActive: false));
            Assert.IsTrue(gate.Tick(tracked: true, buttonHeld: true, sampleActive: false));
        }

        [Test]
        public void ReleasedButtonOnRetrackedFrame_RearmsFollowingPress()
        {
            var gate = new RegistrationSampleInputGate();
            gate.Tick(tracked: false, buttonHeld: false, sampleActive: true);
            Assert.IsTrue(gate.Tick(tracked: true, buttonHeld: false, sampleActive: false));
            Assert.IsTrue(gate.Tick(tracked: true, buttonHeld: true, sampleActive: false));
        }
    }
}
