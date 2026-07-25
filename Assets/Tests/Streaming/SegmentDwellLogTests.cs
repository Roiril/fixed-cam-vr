#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// SegmentDwellLog（区間 (lap, camera) の実測滞在時間）の検証。
    /// 「区間を離れた時にだけ確定する」「同一区間の再通知では計時を切らない」
    /// 「ラン開始で部分区間を捨てる」「送信失敗分は順序を保って戻る」を固定する。
    /// </summary>
    public sealed class SegmentDwellLogTests
    {
        [Test]
        public void Enter_NextSegment_CompletesPreviousWithElapsed()
        {
            var log = new SegmentDwellLog();
            log.Enter(1, 0, 10f);
            Assert.AreEqual(0, log.PendingCount, "進入しただけでは確定しない（滞在中）");

            log.Enter(1, 1, 22.5f);
            var s = log.TakePending();
            Assert.AreEqual(1, s.Length);
            Assert.AreEqual(1, s[0].lap);
            Assert.AreEqual(0, s[0].camera);
            Assert.AreEqual(12.5f, s[0].sec, 1e-4f);
            Assert.AreEqual(0, log.PendingCount, "取り出したら空になる");
        }

        [Test]
        public void Enter_SameSegmentAgain_KeepsTiming()
        {
            var log = new SegmentDwellLog();
            log.Enter(2, 1, 5f);
            log.Enter(2, 1, 9f);      // seed と最初の commit が重なるケース
            log.Enter(2, 2, 12f);
            var s = log.TakePending();
            Assert.AreEqual(1, s.Length);
            Assert.AreEqual(7f, s[0].sec, 1e-4f, "再通知で計時が切れると滞在が短く出る");
        }

        [Test]
        public void Enter_SameCameraNextLap_IsSeparateSegment()
        {
            var log = new SegmentDwellLog();
            log.Enter(1, 0, 0f);
            log.Enter(2, 0, 6f);      // 1 周して同じカメラへ = 別区間
            var s = log.TakePending();
            Assert.AreEqual(1, s.Length);
            Assert.AreEqual(1, s[0].lap);
            Assert.AreEqual(6f, s[0].sec, 1e-4f);
        }

        [Test]
        public void Enter_TooShort_IsDiscardedAsNoise()
        {
            var log = new SegmentDwellLog();
            log.Enter(1, 0, 0f);
            log.Enter(1, 1, 0.01f);
            Assert.AreEqual(0, log.PendingCount);
        }

        [Test]
        public void Reset_DropsPartialSegmentButKeepsConfirmed()
        {
            var log = new SegmentDwellLog();
            log.Enter(1, 0, 0f);
            log.Enter(1, 1, 4f);      // 確定 1 本
            log.Reset();              // ラン開始（計時中の 1 は捨てる）
            log.Enter(1, 0, 30f);     // 新しいランの最初の区間
            var s = log.TakePending();
            Assert.AreEqual(1, s.Length, "確定済みは残り、計時中だけが捨てられる");
            Assert.AreEqual(0, s[0].camera);
            Assert.AreEqual(4f, s[0].sec, 1e-4f);
        }

        [Test]
        public void PutBack_RestoresOrderAheadOfNewer()
        {
            var log = new SegmentDwellLog();
            log.Enter(1, 0, 0f);
            log.Enter(1, 1, 3f);
            var first = log.TakePending();          // 送信失敗したつもり
            log.Enter(1, 2, 8f);                    // その間に新しい滞在が確定
            log.PutBack(first);
            var all = log.TakePending();
            Assert.AreEqual(2, all.Length);
            Assert.AreEqual(0, all[0].camera, "古い方が先");
            Assert.AreEqual(1, all[1].camera);
        }

        [Test]
        public void Capacity_DropsOldestSamples()
        {
            var log = new SegmentDwellLog(3);
            float t = 0f;
            for (int i = 0; i < 6; i++) { log.Enter(1, i, t); t += 2f; }
            log.Enter(1, 99, t);
            var s = log.TakePending();
            Assert.AreEqual(3, s.Length);
            Assert.AreEqual(3, s[0].camera, "溢れたら古い方から捨てる");
            Assert.AreEqual(5, s[2].camera, "最後に確定したのは cam5（99 はまだ滞在中）");
        }
    }
}
