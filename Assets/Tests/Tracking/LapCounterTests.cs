#nullable enable
using FixedCamVr.Streaming;
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

        // ---- 切替の出どころ（source）ゲート ----
        // LapCounter（MonoBehaviour）は director.SwitchCommitted の source==Zone だけを Feed する。
        // 手動 / Web 固定 / 外部（Manual/Override/External）は Feed しない。その振る舞いを純ロジックで固定する
        // （LapCounter.OnSwitchCommitted の 1 行ゲートをこのヘルパで模す）。

        /// <summary>source==Zone のときだけ Feed する（LapCounter.OnSwitchCommitted のゲート模擬）。</summary>
        private static bool FeedIfZone(LapCounterLogic l, int camera, bool isZone)
            => isZone && l.Feed(camera);

        [Test]
        public void OnlyZoneSwitchesAdvance_ManualAndOverrideIgnored()
        {
            var l = Make(0, 1, 2);
            FeedIfZone(l, 1, isZone: true);   // 体験者のゾーン進行 0→1
            FeedIfZone(l, 0, isZone: false);  // スタッフ手動でカメラ 0 へ（無視）
            FeedIfZone(l, 2, isZone: false);  // Web override でカメラ 2 へ（無視）
            // 進行ポインタは Zone だけで進む（手動 / override では動かない）。
            Assert.That(l.Position, Is.EqualTo(1));
            Assert.That(l.CurrentLap, Is.EqualTo(1));
            FeedIfZone(l, 2, isZone: true);   // ゾーン進行 1→2
            Assert.That(FeedIfZone(l, 0, isZone: true), Is.True); // 2→0 で 1 周完了
            Assert.That(l.CurrentLap, Is.EqualTo(2));
        }

        [Test]
        public void ManualCycling_NeverAdvancesLap()
        {
            var l = Make(0, 1, 2);
            // スタッフが手動でカメラを何度も巡回（source=Manual）→ 一切カウントしない。
            for (int i = 0; i < 10; i++)
            {
                FeedIfZone(l, 1, isZone: false);
                FeedIfZone(l, 2, isZone: false);
                FeedIfZone(l, 0, isZone: false);
            }
            Assert.That(l.CurrentLap, Is.EqualTo(1));
            Assert.That(l.Position, Is.EqualTo(0));
        }

        [Test]
        public void ManualDetour_ThenZoneForward_CountsExactlyOneLap()
        {
            var l = Make(0, 1, 2);
            FeedIfZone(l, 1, isZone: true);   // ゾーン進行 0→1
            // スタッフが手動でカメラ 1→0→2→1 と往復（未 Feed）。進行ポインタは pos=1 に据え置き。
            FeedIfZone(l, 0, isZone: false);
            FeedIfZone(l, 2, isZone: false);
            FeedIfZone(l, 1, isZone: false);
            Assert.That(l.Position, Is.EqualTo(1));
            // 体験者が続けてゾーン進行 2→0 → ちょうど 1 周ぶんだけ前進する（手動往復は透過）。
            Assert.That(FeedIfZone(l, 2, isZone: true), Is.False); // pos 1→2
            Assert.That(FeedIfZone(l, 0, isZone: true), Is.True);  // pos 2→0（lap 2）
            Assert.That(l.CurrentLap, Is.EqualTo(2));
        }

        // ---- インサート source のゲート（LapCounter.OnSwitchCommitted の実 source 判定を固定） ----
        // タイムラインのインサートショットは SwitchSource.Insert で切り替わるが、周回には数えない。
        // FeedIfSource は LapCounter.OnSwitchCommitted の「source==Zone のみ Feed」を実 enum で模す。

        /// <summary>source==Zone のときだけ Feed する（LapCounter.OnSwitchCommitted のゲート・実 enum 版）。</summary>
        private static bool FeedIfSource(LapCounterLogic l, int camera, CameraSwitchDirector.SwitchSource source)
            => source == CameraSwitchDirector.SwitchSource.Zone && l.Feed(camera);

        [Test]
        public void InsertSourceSwitches_NeverAdvanceLap()
        {
            var l = Make(0, 1, 2);
            FeedIfSource(l, 1, CameraSwitchDirector.SwitchSource.Zone);   // 体験者ゾーン進行 0→1
            // exit インサート差し込み（別カメラへ Insert source で切替）→ 数えない。
            FeedIfSource(l, 2, CameraSwitchDirector.SwitchSource.Insert);
            // インサート復帰（Insert source）→ 数えない。
            FeedIfSource(l, 0, CameraSwitchDirector.SwitchSource.Insert);
            Assert.That(l.Position, Is.EqualTo(1));
            Assert.That(l.CurrentLap, Is.EqualTo(1));
            // 体験者ゾーン進行だけで 1 周完了する（インサートは透過）。
            FeedIfSource(l, 2, CameraSwitchDirector.SwitchSource.Zone);   // 1→2
            Assert.That(FeedIfSource(l, 0, CameraSwitchDirector.SwitchSource.Zone), Is.True); // 2→0 で lap 2
            Assert.That(l.CurrentLap, Is.EqualTo(2));
        }
    }
}
