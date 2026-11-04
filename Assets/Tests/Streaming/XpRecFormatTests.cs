#nullable enable
using System.IO;
using FixedCamVr.Streaming.Replay;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 体験の記録（track.bin）の形。録画中にアプリが落ちるのは正常系なので、壊れた尾を捨てて読めること、
    /// 時刻が戻った行を捨てること、入力のビットが往復することを固定する。
    /// </summary>
    public sealed class XpRecFormatTests
    {
        private static XpRecFormat.Frame F(float t, int bits = 0, byte lang = 0)
            => new XpRecFormat.Frame
            {
                t = t,
                head = new XpRecFormat.Pose { px = t, py = 1.6f, pz = -t, qx = 0f, qy = 0.7071f, qz = 0f, qw = 0.7071f },
                left = XpRecFormat.Pose.Identity,
                right = XpRecFormat.Pose.Identity,
                input = new RawInput { bits = bits },
                lang = lang,
                relief = 1,
                marks = (ushort)(t * 2f),
            };

        private static MemoryStream Write(params XpRecFormat.Frame[] frames)
        {
            var ms = new MemoryStream();
            XpRecFormat.WriteHeader(ms);
            var w = new BinaryWriter(ms);
            foreach (var f in frames) XpRecFormat.WriteFrame(w, f);
            w.Flush();
            return ms;
        }

        [Test]
        public void FrameBytes_MatchesWhatIsWritten()
        {
            var ms = Write(F(0f));
            Assert.AreEqual(4 + XpRecFormat.FrameBytes, ms.Length);
        }

        [Test]
        public void RoundTrip_KeepsPoseInputAndPrefs()
        {
            int bits = RawInput.X | RawInput.LConnected | RawInput.StartAuthorized | RawInput.TitleClosed;
            var ms = Write(F(0f), F(0.5f, bits, 2));
            ms.Position = 0;
            var r = XpRecFormat.ReadTrack(ms);
            Assert.AreEqual(2, r.Length);
            Assert.AreEqual(0.5f, r[1].t);
            Assert.AreEqual(0.5f, r[1].head.px);
            Assert.AreEqual(0.7071f, r[1].head.qy, 1e-6f);
            Assert.IsTrue(r[1].input.Has(RawInput.X));
            Assert.IsTrue(r[1].input.Has(RawInput.StartAuthorized));
            Assert.IsTrue(r[1].input.Has(RawInput.TitleClosed));
            Assert.IsFalse(r[1].input.Has(RawInput.Y));
            Assert.AreEqual(2, r[1].lang);
            Assert.AreEqual(1, r[1].relief);
            Assert.AreEqual(1, r[1].marks);
        }

        [Test]
        public void TruncatedTail_IsDropped()
        {
            var ms = Write(F(0f), F(1f), F(2f));
            ms.SetLength(ms.Length - 10);
            ms.Position = 0;
            Assert.AreEqual(2, XpRecFormat.ReadTrack(ms).Length);
        }

        [Test]
        public void TimeGoingBackwards_IsDropped()
        {
            var ms = Write(F(0f), F(2f), F(1f), F(3f));
            ms.Position = 0;
            var r = XpRecFormat.ReadTrack(ms);
            Assert.AreEqual(new[] { 0f, 2f, 3f }, System.Array.ConvertAll(r, f => f.t));
        }

        [Test]
        public void WrongMagic_ReadsNothing()
        {
            var ms = new MemoryStream(new byte[] { (byte)'X', (byte)'X', (byte)'X', (byte)'X', 0, 0, 0, 0 });
            Assert.AreEqual(0, XpRecFormat.ReadTrack(ms).Length);
        }

        [Test]
        public void IndexAt_FindsLastRowNotAfterT_BothWays()
        {
            var track = new[] { F(0f), F(1f), F(2f), F(3f) };
            Assert.AreEqual(0, XpRecFormat.IndexAt(track, -1f, 0));
            Assert.AreEqual(2, XpRecFormat.IndexAt(track, 2.5f, 0));
            Assert.AreEqual(3, XpRecFormat.IndexAt(track, 99f, 1));
            Assert.AreEqual(1, XpRecFormat.IndexAt(track, 1.2f, 3));   // 巻き戻し
        }

        [Test]
        public void CamFileName_IsFileSafe()
        {
            Assert.AreEqual("cam_Phone01.mjr", XpRecFormat.CamFileName("Phone01"));
            Assert.AreEqual("cam_a_b_c.mjr", XpRecFormat.CamFileName("a/b:c"));
        }

        [Test]
        public void RawInput_From_SetsEachBitOnce()
        {
            var all = RawInput.From(true, true, true, true, true, true, true, true, true, true, true);
            Assert.AreEqual((1 << 11) - 1, all.bits);
            Assert.AreEqual(0, RawInput.From(false, false, false, false, false, false, false, false, false, false, false).bits);
        }
    }
}
