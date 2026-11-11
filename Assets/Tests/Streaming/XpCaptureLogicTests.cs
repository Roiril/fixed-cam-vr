#nullable enable
using System.IO;
using System.Text.RegularExpressions;
using FixedCamVr.Streaming.Replay;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 目線の動画を撮る側と撮らせる入口が共有する値。ツイン走行の同時撮影（capture=eye）と 2 段の再生で、
    /// replay_xp.log の形・撮影の寸法・受け付ける capture= が食い違わないことを固定する。
    /// </summary>
    public sealed class XpCaptureLogicTests
    {
        // tools/twin-look.py の XP_LINE と同じ形（tau= は動画の秒）。
        private static readonly Regex TwinLookLine =
            new(@"^(?:tau=(?<tau>-?[\d.]+) )?.*?\[XP\]\s+t=(?<t>[\d.]+)\s+ev=(?<ev>\S+)\s*(?<rest>.*)$");

        [Test]
        public void TauLine_IsWhatTheToolsRead()
        {
            var w = new StringWriter { NewLine = "\n" };
            XpCaptureLogic.WriteTauLine(w, 12.5f, "[XP] t=28.10 ev=seg lap=1 cam=0 plap=1");
            Assert.AreEqual("tau=12.50 [XP] t=28.10 ev=seg lap=1 cam=0 plap=1\n", w.ToString());
            Match m = TwinLookLine.Match(w.ToString().TrimEnd('\n'));
            Assert.IsTrue(m.Success);
            Assert.AreEqual("12.50", m.Groups["tau"].Value);
            Assert.AreEqual("seg", m.Groups["ev"].Value);
        }

        [Test]
        public void TauLine_KeepsTheSignBeforeTheHeadAndUsesADot()
        {
            var w = new StringWriter { NewLine = "\n" };
            XpCaptureLogic.WriteTauLine(w, -0.25f, "[XP] t=1.00 ev=x");
            StringAssert.StartsWith("tau=-0.25 ", w.ToString());
        }

        [TestCase(null, XpCaptureLogic.TwinCapture.None)]
        [TestCase("", XpCaptureLogic.TwinCapture.None)]
        [TestCase("off", XpCaptureLogic.TwinCapture.None)]
        [TestCase("eye", XpCaptureLogic.TwinCapture.Eye)]
        [TestCase(" EYE ", XpCaptureLogic.TwinCapture.Eye)]
        public void TwinCapture_AcceptsEyeOrNothing(string? value, XpCaptureLogic.TwinCapture want)
        {
            Assert.IsTrue(XpCaptureLogic.TryParseTwinCapture(value, out XpCaptureLogic.TwinCapture got, out string error));
            Assert.AreEqual(want, got);
            Assert.AreEqual("", error);
        }

        [Test]
        public void TwinCapture_RefusesFollowWithTheReason()
        {
            // 後ろ上は撮影カメラをもう 1 台立てないと撮れない。黙って目線で撮らず、落として理由を言う。
            Assert.IsFalse(XpCaptureLogic.TryParseTwinCapture("follow", out XpCaptureLogic.TwinCapture got, out string error));
            Assert.AreEqual(XpCaptureLogic.TwinCapture.None, got);
            StringAssert.Contains("replay", error);
        }

        [Test]
        public void TwinCapture_RefusesUnknownValues()
        {
            Assert.IsFalse(XpCaptureLogic.TryParseTwinCapture("1", out _, out string error));
            StringAssert.Contains("1", error);
        }

        [TestCase(1921, 1081, 30, 1, 1920, 1080, 30, 1)]
        [TestCase(100, 100, 5, 3, 320, 240, 10, 2)]
        [TestCase(9000, 9000, 500, 5, 7680, 4320, 120, 4)]
        [TestCase(1280, 720, 24, 16, 1280, 720, 24, 8)]
        [TestCase(1280, 720, 24, 0, 1280, 720, 24, 1)]
        public void Normalize_KeepsWhatTheEncoderAndRenderTextureTake(int w, int h, int fps, int msaa,
                                                                        int ew, int eh, int efps, int emsaa)
        {
            XpCaptureLogic.Normalize(ref w, ref h, ref fps, ref msaa);
            Assert.AreEqual(new[] { ew, eh, efps, emsaa }, new[] { w, h, fps, msaa });
        }

        [Test]
        public void DefaultFov_IsWiderForTheIntroductionVideoThanForTheTwinCheck()
        {
            Assert.AreEqual(72f, XpCaptureLogic.DefaultFovDeg("eye"));
            Assert.AreEqual(62f, XpCaptureLogic.DefaultFovDeg("follow"));
            Assert.AreEqual(52f, XpCaptureLogic.SinglePassEyeFovDeg);
        }
    }
}
