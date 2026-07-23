#nullable enable
using System.Text;
using NUnit.Framework;
using TableDuoVr.Net;

namespace TableDuoVr.Tests
{
    /// <summary>
    /// HostBeaconMessage の組立・パース・sceneHash 照合（LAN 自動発見 + ビルド不一致検出）。
    /// </summary>
    public class HostBeaconMessageTests
    {
        [Test]
        public void Build_ProducesExactWireString()
        {
            byte[] payload = HostBeaconMessage.Build(7777, "abc123");
            Assert.AreEqual("TDVB1|7777|abc123", Encoding.UTF8.GetString(payload));
        }

        [Test]
        public void Build_TryParse_RoundTrip()
        {
            string text = Encoding.UTF8.GetString(HostBeaconMessage.Build(7777, "abc123"));
            bool ok = HostBeaconMessage.TryParse(text, "abc123", out ushort port, out bool match);
            Assert.IsTrue(ok);
            Assert.AreEqual(7777, port);
            Assert.IsTrue(match);
        }

        [Test]
        public void TryParse_HashMismatch_MatchesFalse()
        {
            bool ok = HostBeaconMessage.TryParse("TDVB1|7777|abc123", "zzz", out ushort port, out bool match);
            Assert.IsTrue(ok);
            Assert.AreEqual(7777, port);
            Assert.IsFalse(match);
        }

        [Test]
        public void TryParse_EmptyLocalHash_MatchesTrue()
        {
            bool ok = HostBeaconMessage.TryParse("TDVB1|7777|abc123", "", out ushort port, out bool match);
            Assert.IsTrue(ok);
            Assert.AreEqual(7777, port);
            Assert.IsTrue(match, "ローカル未スタンプは照合スキップ＝一致扱い");
        }

        [Test]
        public void TryParse_EmptyBeaconHash_MatchesTrue()
        {
            bool ok = HostBeaconMessage.TryParse("TDVB1|7777", "abc", out ushort port, out bool match);
            Assert.IsTrue(ok);
            Assert.AreEqual(7777, port);
            Assert.IsTrue(match, "beacon 側 hash 無し＝照合スキップ");
        }

        [Test]
        public void TryParse_WrongMagic_ReturnsFalse()
        {
            Assert.IsFalse(HostBeaconMessage.TryParse("XXXX|7777|h", "abc", out _, out _));
        }

        [Test]
        public void TryParse_NonNumericPort_ReturnsFalse()
        {
            Assert.IsFalse(HostBeaconMessage.TryParse("TDVB1|notaport|h", "abc", out _, out _));
        }

        [Test]
        public void TryParse_TooFewParts_ReturnsFalse()
        {
            Assert.IsFalse(HostBeaconMessage.TryParse("TDVB1", "abc", out _, out _));
        }
    }
}
