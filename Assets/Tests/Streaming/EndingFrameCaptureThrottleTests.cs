#nullable enable
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 終幕の写真を撮る頻度。窓が開いたら最初の 1 枚は待たず、撮り直しと失敗のやり直しは
    /// <see cref="EndingFrameCapture.RefreshIntervalSec"/> ごとに絞る（毎フレーム撮らない）。
    /// </summary>
    public sealed class EndingFrameCaptureThrottleTests
    {
        [Test]
        public void FirstShot_IsTakenWithoutWaiting()
        {
            Assert.IsTrue(EndingFrameCapture.CaptureDue(hasShot: false, lastAttemptFailed: false, now: 10f, nextAt: 99f));
        }

        [Test]
        public void Refresh_WaitsForTheInterval()
        {
            const float at = 10f;
            float next = at + EndingFrameCapture.RefreshIntervalSec;
            Assert.IsFalse(EndingFrameCapture.CaptureDue(true, false, at + 0.01f, next));
            Assert.IsFalse(EndingFrameCapture.CaptureDue(true, false, next - 0.001f, next));
            Assert.IsTrue(EndingFrameCapture.CaptureDue(true, false, next, next));
        }

        [Test]
        public void FailedFirstShot_IsRetriedOnlyAfterTheInterval()
        {
            const float at = 10f;
            float next = at + EndingFrameCapture.RefreshIntervalSec;
            Assert.IsFalse(EndingFrameCapture.CaptureDue(false, true, at + 0.01f, next), "失敗を毎フレーム繰り返さない");
            Assert.IsTrue(EndingFrameCapture.CaptureDue(false, true, next, next));
        }

        [Test]
        public void RefreshInterval_IsFarBelowEveryFrameButStillUnderOneSecond()
        {
            Assert.That(EndingFrameCapture.RefreshIntervalSec, Is.GreaterThan(1f / 30f));
            Assert.That(EndingFrameCapture.RefreshIntervalSec, Is.LessThanOrEqualTo(1f));
        }
    }
}
