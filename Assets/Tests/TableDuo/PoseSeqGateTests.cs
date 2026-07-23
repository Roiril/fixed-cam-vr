#nullable enable
using NUnit.Framework;
using TableDuoVr.Net;

namespace TableDuoVr.Tests
{
    /// <summary>
    /// PoseSeqGate.Accept（Unreliable の後着/重複/uint wraparound 棄却）。
    /// 「凍結 vs 欠落」の判別と状態汚染防止の核。wraparound は符号付き差分で判定。
    /// </summary>
    public class PoseSeqGateTests
    {
        [Test]
        public void Accept_StrictlyIncreasing_True()
        {
            Assert.IsTrue(PoseSeqGate.Accept(6u, 5u));
        }

        [Test]
        public void Accept_Equal_False()
        {
            Assert.IsFalse(PoseSeqGate.Accept(5u, 5u), "重複は棄却");
        }

        [Test]
        public void Accept_Older_False()
        {
            Assert.IsFalse(PoseSeqGate.Accept(4u, 5u), "後着は棄却");
        }

        [Test]
        public void Accept_Wraparound_True()
        {
            Assert.IsTrue(PoseSeqGate.Accept(0u, uint.MaxValue), "一周した次を採用");
        }

        [Test]
        public void Accept_WraparoundStale_False()
        {
            Assert.IsFalse(PoseSeqGate.Accept(uint.MaxValue - 1u, uint.MaxValue));
        }

        [Test]
        public void Accept_LargeForwardJump_True()
        {
            Assert.IsTrue(PoseSeqGate.Accept(1000u, 5u), "欠落後の飛びは採用");
        }
    }
}
