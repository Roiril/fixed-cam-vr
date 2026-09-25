#nullable enable
using NUnit.Framework;

namespace FixedCamVr.Input.Tests
{
    public sealed class ControllerConnectionGateTests
    {
        [Test]
        public void InitialConnection_RequiresReleasedFrame_ThenAcceptsNextFrame()
        {
            var gate = new ControllerConnectionGate();

            ControllerConnectionGate.Result connected = gate.Tick(true, anyTargetButtonHeld: false);
            Assert.IsTrue(connected.Reconnected);
            Assert.IsFalse(connected.AcceptInput, "接続を見つけたフレーム自体は捨てる");

            Assert.IsTrue(gate.Tick(true, anyTargetButtonHeld: true).AcceptInput,
                          "解放を観測した次のフレームから新しい押下を受け取る");
        }

        [Test]
        public void HeldAcrossReconnect_IsIgnoredUntilReleaseAndFollowingFrame()
        {
            var gate = new ControllerConnectionGate();
            Assert.IsFalse(gate.Tick(false, anyTargetButtonHeld: true).AcceptInput);
            Assert.IsFalse(gate.Tick(true, anyTargetButtonHeld: true).AcceptInput);
            Assert.IsFalse(gate.Tick(true, anyTargetButtonHeld: true).AcceptInput);
            Assert.IsFalse(gate.Tick(true, anyTargetButtonHeld: false).AcceptInput,
                           "解放を観測したフレームも入力には使わない");
            Assert.IsTrue(gate.Tick(true, anyTargetButtonHeld: true).AcceptInput);
        }

        [Test]
        public void DisconnectEdge_IsReportedOnceAndClosesGate()
        {
            var gate = new ControllerConnectionGate();
            gate.Tick(true, anyTargetButtonHeld: false);
            gate.Tick(true, anyTargetButtonHeld: false);

            ControllerConnectionGate.Result disconnected = gate.Tick(false, anyTargetButtonHeld: false);
            Assert.IsTrue(disconnected.Disconnected);
            Assert.IsFalse(disconnected.AcceptInput);
            Assert.IsFalse(gate.Tick(false, anyTargetButtonHeld: false).Disconnected);
        }
    }
}
