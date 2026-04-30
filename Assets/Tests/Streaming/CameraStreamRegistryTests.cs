#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    public sealed class CameraStreamRegistryTests
    {
        [Test]
        public void WrapIndex_PositiveInRange_ReturnsAsIs()
        {
            Assert.That(CameraStreamRegistry.WrapIndex(0, 3), Is.EqualTo(0));
            Assert.That(CameraStreamRegistry.WrapIndex(2, 3), Is.EqualTo(2));
        }

        [Test]
        public void WrapIndex_OverflowsForward()
        {
            Assert.That(CameraStreamRegistry.WrapIndex(3, 3), Is.EqualTo(0));
            Assert.That(CameraStreamRegistry.WrapIndex(4, 3), Is.EqualTo(1));
            Assert.That(CameraStreamRegistry.WrapIndex(7, 3), Is.EqualTo(1));
        }

        [Test]
        public void WrapIndex_NegativeWrapsBackward()
        {
            Assert.That(CameraStreamRegistry.WrapIndex(-1, 3), Is.EqualTo(2));
            Assert.That(CameraStreamRegistry.WrapIndex(-3, 3), Is.EqualTo(0));
            Assert.That(CameraStreamRegistry.WrapIndex(-4, 3), Is.EqualTo(2));
        }

        [Test]
        public void WrapIndex_ZeroCount_ReturnsZero()
        {
            Assert.That(CameraStreamRegistry.WrapIndex(0, 0), Is.EqualTo(0));
            Assert.That(CameraStreamRegistry.WrapIndex(5, 0), Is.EqualTo(0));
            Assert.That(CameraStreamRegistry.WrapIndex(-5, 0), Is.EqualTo(0));
        }

        [Test]
        public void WrapIndex_SingleElement_AlwaysZero()
        {
            Assert.That(CameraStreamRegistry.WrapIndex(0, 1), Is.EqualTo(0));
            Assert.That(CameraStreamRegistry.WrapIndex(1, 1), Is.EqualTo(0));
            Assert.That(CameraStreamRegistry.WrapIndex(-1, 1), Is.EqualTo(0));
            Assert.That(CameraStreamRegistry.WrapIndex(99, 1), Is.EqualTo(0));
        }
    }
}
