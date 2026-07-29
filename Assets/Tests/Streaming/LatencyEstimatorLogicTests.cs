#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 遅延の内訳の検証。企画書「視覚遅延は 100ms 程度以内を目標として管理する」を、
    /// **測れるものだけで**支える設計を固定する。
    ///
    /// とくに大事なのは「端末間の時計のずれは窓内最小値の側に吸われ、揺らぎの差分だけが残る」こと。
    /// ここが崩れると、時計のずれがそのまま遅延として現場に表示される。
    /// </summary>
    public sealed class LatencyEstimatorLogicTests
    {
        [Test]
        public void NoSample_ReportsZeroJitter()
        {
            var l = new LatencyEstimatorLogic();
            Assert.That(l.HasCaptureStamp, Is.False);
            Assert.That(l.ArrivalJitterMs, Is.EqualTo(0f));
        }

        [Test]
        public void ClockOffsetIsAbsorbed_OnlyJitterRemains()
        {
            var l = new LatencyEstimatorLogic();
            // 端末の時計が Unity より 1,000,000 ms ずれていても、揺らぎだけが出る。
            const double offset = 1_000_000.0;
            l.ObserveArrival(0.0, offset + 40.0);          // 伝送 40ms（この窓の底）
            l.ObserveArrival(33.0, offset + 33.0 + 55.0);  // 伝送 55ms
            Assert.That(l.ArrivalJitterMs, Is.EqualTo(15f).Within(0.5f));
        }

        [Test]
        public void Jitter_IsZeroAtTheFloor()
        {
            var l = new LatencyEstimatorLogic();
            l.ObserveArrival(0.0, 60.0);
            l.ObserveArrival(33.0, 33.0 + 90.0);
            l.ObserveArrival(66.0, 66.0 + 60.0);
            Assert.That(l.ArrivalJitterMs, Is.EqualTo(0f).Within(0.5f));
        }

        [Test]
        public void OldFloorLeavesTheWindow()
        {
            var l = new LatencyEstimatorLogic();
            // 最初に非常に速い 1 枚（底 = 10ms）
            l.ObserveArrival(0.0, 10.0);
            // 窓（10 秒）を十分に超えて、以後は 50ms 前後で安定
            double t = 0.0;
            for (int i = 0; i < 400; i++)
            {
                t += 33.0;
                l.ObserveArrival(t, t + 50.0);
            }
            Assert.That(l.ArrivalJitterMs, Is.EqualTo(0f).Within(1f),
                "窓を出た古い底に引きずられない");
        }

        [Test]
        public void Decode_IsSmoothedNotSnapped()
        {
            var l = new LatencyEstimatorLogic();
            l.ObserveDecode(10f);
            Assert.That(l.DecodeMs, Is.EqualTo(10f).Within(1e-3f), "初回はそのまま入る");
            l.ObserveDecode(20f);
            Assert.That(l.DecodeMs, Is.GreaterThan(10f).And.LessThan(20f), "以後は平滑化される");
        }

        [Test]
        public void PresentFollowsDisplayRate()
        {
            var l = new LatencyEstimatorLogic();
            l.SetDisplayRate(72f);
            float at72 = l.PresentMs;
            l.SetDisplayRate(90f);
            Assert.That(l.PresentMs, Is.LessThan(at72), "表示レートを上げると提示ぶんが縮む");
            Assert.That(l.PresentMs, Is.EqualTo(1.5f / 90f * 1000f).Within(1e-3f));
        }

        [Test]
        public void ObservedIsTheSumOfWhatWeCanMeasure()
        {
            var l = new LatencyEstimatorLogic();
            l.SetDisplayRate(90f);
            l.ObserveDecode(8f);
            l.ObserveArrival(0.0, 40.0);
            l.ObserveArrival(33.0, 33.0 + 52.0);
            Assert.That(l.ObservedMs,
                Is.EqualTo(l.ArrivalJitterMs + l.DecodeMs + l.PresentMs).Within(1e-3f));
        }

        [Test]
        public void Reset_DropsTheWindow()
        {
            var l = new LatencyEstimatorLogic();
            l.ObserveArrival(0.0, 10.0);
            l.ObserveDecode(9f);
            l.Reset();
            Assert.That(l.HasCaptureStamp, Is.False);
            Assert.That(l.DecodeMs, Is.EqualTo(0f));
            Assert.That(l.SourceAgeMs, Is.EqualTo(0f));
        }

        [Test]
        public void ClockGoingBackwardsRebuildsTheWindow()
        {
            var l = new LatencyEstimatorLogic();
            l.ObserveArrival(0.0, 1000.0);
            // 受信側の時計が巻き戻る（再接続でリセット等）
            l.ObserveArrival(0.0, 5.0);
            Assert.That(l.ArrivalJitterMs, Is.EqualTo(0f).Within(0.5f));
        }

        [Test]
        public void SourceAgeIsClampedToNonNegative()
        {
            var l = new LatencyEstimatorLogic();
            l.SetSourceAgeMs(-5f);
            Assert.That(l.SourceAgeMs, Is.EqualTo(0f));
            l.SetSourceAgeMs(120f);
            Assert.That(l.SourceAgeMs, Is.EqualTo(120f));
        }
    }
}
