#nullable enable
using FixedCamVr.Tracking;
using NUnit.Framework;

namespace FixedCamVr.Tracking.Tests
{
    /// <summary>
    /// LapCounterLogic（周回カウントの純ロジック）の検証。
    /// order 巡回・順方向前進のみ・逆走/行き来/スキップ不進行・複数周・order 変更を固定する。
    /// MonoBehaviour（registry 購読・scheduler 橋渡し）はテスト対象外。
    /// </summary>
    public sealed class LapCounterTests
    {
        private static LapCounterLogic Make(params int[] order)
        {
            var l = new LapCounterLogic();
            l.SetOrder(order);
            return l;
        }

        [Test]
        public void Initial_LapIsOne_PositionZero()
        {
            var l = Make(0, 1, 2);
            Assert.That(l.CurrentLap, Is.EqualTo(1));
            Assert.That(l.Position, Is.EqualTo(0));
        }

        [Test]
        public void Forward_OneFullLap_IncrementsToTwoOnReturnToStart()
        {
            var l = Make(0, 1, 2);
            // 0(=start) からスタート。1→2→0 と順方向に踏破して start へ戻ると lap 2。
            Assert.That(l.Feed(1), Is.False); // pos 0→1
            Assert.That(l.Feed(2), Is.False); // pos 1→2
            Assert.That(l.Feed(0), Is.True);  // pos 2→0（1 周完了）
            Assert.That(l.CurrentLap, Is.EqualTo(2));
            Assert.That(l.Position, Is.EqualTo(0));
        }

        [Test]
        public void SameCamera_DoesNotAdvance()
        {
            var l = Make(0, 1, 2);
            // 現在 pos=0（cam 0）。同じ cam 0 が来ても期待次 = cam 1 と不一致 → 不進行。
            Assert.That(l.Feed(0), Is.False);
            Assert.That(l.Position, Is.EqualTo(0));
            Assert.That(l.CurrentLap, Is.EqualTo(1));
        }

        [Test]
        public void Skip_DoesNotAdvance()
        {
            var l = Make(0, 1, 2);
            // pos=0 で期待次 = cam 1。cam 2 へスキップしても前進しない。
            Assert.That(l.Feed(2), Is.False);
            Assert.That(l.Position, Is.EqualTo(0));
        }

        [Test]
        public void Reverse_DoesNotAdvance_NorDecrement()
        {
            var l = Make(0, 1, 2);
            l.Feed(1); // pos 0→1
            l.Feed(2); // pos 1→2
            Assert.That(l.Position, Is.EqualTo(2));
            // 逆走: pos=2 で期待次 = cam 0（wrap）。cam 1 へ戻っても前進せず、pos も減らない。
            Assert.That(l.Feed(1), Is.False);
            Assert.That(l.Position, Is.EqualTo(2));
            Assert.That(l.CurrentLap, Is.EqualTo(1));
        }

        [Test]
        public void BackAndForth_AroundBoundary_DoesNotCount()
        {
            var l = Make(0, 1, 2);
            l.Feed(1);                 // pos 0→1
            Assert.That(l.Feed(0), Is.False); // 戻り（期待次=cam2）→ 不進行
            Assert.That(l.Feed(1), Is.False); // 期待次=cam2、cam1 は同一位置カメラ → 不進行
            Assert.That(l.Position, Is.EqualTo(1));
            Assert.That(l.CurrentLap, Is.EqualTo(1));
            // 正しい順方向に戻れば進む
            Assert.That(l.Feed(2), Is.False); // pos 1→2
            Assert.That(l.Feed(0), Is.True);  // 1 周完了
            Assert.That(l.CurrentLap, Is.EqualTo(2));
        }

        [Test]
        public void MultipleLaps_CountUp()
        {
            var l = Make(0, 1, 2);
            for (int lap = 2; lap <= 4; lap++)
            {
                l.Feed(1);
                l.Feed(2);
                Assert.That(l.Feed(0), Is.True, $"周回 {lap - 1} 完了で lap {lap} になるはず");
                Assert.That(l.CurrentLap, Is.EqualTo(lap));
            }
        }

        [Test]
        public void SetOrder_ResetsProgressAndLap()
        {
            var l = Make(0, 1, 2);
            l.Feed(1);
            l.Feed(2);
            l.Feed(0); // lap 2
            Assert.That(l.CurrentLap, Is.EqualTo(2));

            // order 差し替え → スタート状態へリセット
            l.SetOrder(new[] { 2, 1, 0 });
            Assert.That(l.CurrentLap, Is.EqualTo(1));
            Assert.That(l.Position, Is.EqualTo(0));

            // 新 order の順方向: 2(start)→1→0→2 で lap 2
            Assert.That(l.Feed(1), Is.False); // pos 0→1
            Assert.That(l.Feed(0), Is.False); // pos 1→2
            Assert.That(l.Feed(2), Is.True);  // pos 2→0（新 start=cam2 へ復帰）
            Assert.That(l.CurrentLap, Is.EqualTo(2));
        }

        [Test]
        public void Reset_KeepsOrder_ResetsLapAndPosition()
        {
            var l = Make(0, 1, 2);
            l.Feed(1);
            l.Feed(2);
            l.Feed(0); // lap 2, pos 0
            l.Feed(1); // pos 1
            l.Reset();
            Assert.That(l.CurrentLap, Is.EqualTo(1));
            Assert.That(l.Position, Is.EqualTo(0));
            Assert.That(l.Order, Is.EqualTo(new[] { 0, 1, 2 }));
        }

        [Test]
        public void EmptyOrder_FeedIsNoOp()
        {
            var l = new LapCounterLogic();
            Assert.That(l.Feed(0), Is.False);
            Assert.That(l.CurrentLap, Is.EqualTo(1));
        }
    }
}
