using NUnit.Framework;
namespace FixedCamVr.Streaming.Tests
{
    public sealed class DecodedFrameEvidenceTests
    {
        [Test] public void FreshRequiresActualDecodeAndExpires()
        {
            var e = new DecodedFrameEvidence(); Assert.IsFalse(e.IsFresh(10f, 1, true, false));
            e.Decoded(10f, 10000, 1, 1, 10000);
            Assert.IsTrue(e.IsFresh(11.4f, 1, true, false)); Assert.IsFalse(e.IsFresh(11.6f, 1, true, false));
            Assert.IsFalse(e.IsFresh(10f, 1, false, false)); Assert.IsFalse(e.IsFresh(10f, 1, true, true));
        }
        [Test] public void ReconnectionAndOldFramesCannotReuseOldTexture()
        {
            var e = new DecodedFrameEvidence(); e.Decoded(10f, 10000, 1, 1, 10000);
            Assert.IsFalse(e.IsFresh(10f, 2, true, false)); e.Decoded(10f, 10000, 1, 2, 10000);
            Assert.IsFalse(e.IsFresh(10f, 2, true, false)); e.Decoded(10f, 8000, 2, 2, 10000);
            Assert.IsFalse(e.IsFresh(10f, 2, true, false));
            e.Decoded(10f, 10000, 2, 2, 10000); Assert.IsTrue(e.IsFresh(10f, 2, true, false));
        }
        [Test] public void ResumeClearsProof_QueuedPreResumeFramesCannotPass()
        {
            var e = new DecodedFrameEvidence(); e.Decoded(10f, 10000, 1, 1, 10000);
            e.Invalidate(20000); e.Decoded(20f, 19999, 1, 1, 20001);
            Assert.IsFalse(e.IsFresh(20f, 1, true, false));
            e.Decoded(20f, 20000, 1, 1, 20001);
            Assert.IsFalse(e.IsFresh(20f, 1, true, false));
            e.Decoded(20f, 20001, 1, 1, 20001); Assert.IsTrue(e.IsFresh(20f, 1, true, false));
        }
        [Test] public void WatchdogResumeTimestampIsNotDecodeEvidence()
        {
            var watchdog = new StreamWatchdogLogic(); var e = new DecodedFrameEvidence();
            watchdog.OnFrameDecoded(10f); e.Decoded(10f, 10000, 1, 1, 10000);
            Assert.IsTrue(watchdog.BeginTick(20f, 1f));
            Assert.AreEqual(20f, watchdog.LastFrameRealtime);
            Assert.IsFalse(e.IsFresh(20f, 1, true, false));
        }
    }
}
