#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 「体験中に踏まれる区間はどれか」の契約。
    ///
    /// ⚠ <b>期待値は卓の <c>run-model.test.mjs</c> / <c>tools/analyze-xp-log.py</c> と同じ値を
    /// ハードコードしてある。</b> 3 者が同じ式を持つ設計なので、片方だけ直すとここが落ちる
    /// （沈黙して食い違うのを防ぐ）。
    ///
    /// 周回は進行ポインタ方式で <c>order[0]</c> へ戻った時に上がるので、<c>lap = totalLaps + 1</c> の
    /// <c>order[0]</c>（＝帰りの A）は構造的に必ず踏む。体験は「元の位置に戻って終わる」ので、
    /// この 1 区間だけが追加で到達可能。
    /// </summary>
    public sealed class ShowRunReachTests
    {
        private static readonly int[] Order = { 0, 1, 2 };

        [Test]
        public void NormalLapsAreAllReachable()
        {
            Assert.That(ShowRunReach.IsSegmentReachable(1, 0, 3, Order), Is.True);
            Assert.That(ShowRunReach.IsSegmentReachable(3, 1, 3, Order), Is.True);
            Assert.That(ShowRunReach.IsSegmentReachable(3, 2, 3, Order), Is.True);
        }

        [Test]
        public void ReturnStartSegmentIsReachable()
        {
            Assert.That(ShowRunReach.IsSegmentReachable(4, 0, 3, Order), Is.True,
                "帰りの A（体験の最後の区間）");
        }

        [Test]
        public void OtherSegmentsOfTheReturnLapAreNot()
        {
            Assert.That(ShowRunReach.IsSegmentReachable(4, 1, 3, Order), Is.False);
            Assert.That(ShowRunReach.IsSegmentReachable(4, 2, 3, Order), Is.False);
        }

        [Test]
        public void BeyondTheReturnLapIsNot()
        {
            Assert.That(ShowRunReach.IsSegmentReachable(5, 0, 3, Order), Is.False);
            Assert.That(ShowRunReach.IsSegmentReachable(0, 0, 3, Order), Is.False);
        }

        [Test]
        public void UnauthoredCourseFallsBackToReachable()
        {
            // 順路が無ければ判定できない。著作を黙って殺さない側へ倒す。
            Assert.That(ShowRunReach.IsSegmentReachable(4, 2, 3, null), Is.True);
            Assert.That(ShowRunReach.IsSegmentReachable(4, 2, 3, new int[0]), Is.True);
        }

        [Test]
        public void ReturnSegmentFollowsCourseOrderNotCameraZero()
        {
            int[] order = { 2, 0, 1 };
            Assert.That(ShowRunReach.IsSegmentReachable(4, 2, 3, order), Is.True);
            Assert.That(ShowRunReach.IsSegmentReachable(4, 0, 3, order), Is.False);
        }

        [Test]
        public void InvalidTotalLapsFallsBackToDefault()
        {
            Assert.That(ShowRunReach.IsSegmentReachable(4, 0, 0, Order), Is.True);
            Assert.That(ShowRunReach.IsSegmentReachable(5, 0, 0, Order), Is.False);
        }

        [Test]
        public void IsReturnSegment_OnlyTheLastOne()
        {
            Assert.That(ShowRunReach.IsReturnSegment(4, 0, 3, Order), Is.True);
            Assert.That(ShowRunReach.IsReturnSegment(3, 0, 3, Order), Is.False);
            Assert.That(ShowRunReach.IsReturnSegment(4, 1, 3, Order), Is.False);
        }

        // 実機の判定（固定値の ShowRunDefaults.IsClosingSegment）と、卓と突き合わせる式
        // （IsReturnSegment）は、既定の 3 周・順路 A→B→C で同じ区間を指す。どちらかを変えたらここで割れる。
        [Test]
        public void ClosingSegment_MatchesReturnSegment_ForTheDefaultShow()
        {
            for (int lap = 1; lap <= 5; lap++)
                for (int cam = 0; cam < 3; cam++)
                    Assert.That(ShowRunDefaults.IsClosingSegment(lap, cam),
                        Is.EqualTo(ShowRunReach.IsReturnSegment(lap, cam, ShowRunDefaults.TotalLaps, Order)),
                        $"lap={lap} cam={cam}");
            Assert.That(ShowRunDefaults.IsClosingSegment(4, 0), Is.True, "最後の A は区間の周 4・カメラ A");
        }
    }
}
