#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 3 周目の長押しの間だけ人形の下からライブが透ける量（<see cref="LiveBleedLogic"/>）。
    /// 目標 0.85×smoothstep(進み具合/0.6) へ寄り、離すと 0 へ戻り、報告の成立ではその値を 0.5 秒保ってから 0。
    /// 旗が無い（働かない）間は常に 0。
    /// </summary>
    public sealed class LiveBleedLogicTests
    {
        private const float Dt = 1f / 72f;

        // 成立の後に保つフレーム数（0.5 秒 = 72Hz で 36）。保持の時計は 36 フレーム目で 0 を割る。
        private static readonly int HoldFrames = (int)System.Math.Round(LiveBleedLogic.HoldAfterSec / Dt);

        [Test]
        public void Target_ReachesMaxAtSixTenthsOfTheHold_AlongASmoothstep()
        {
            Assert.That(LiveBleedLogic.TargetFor(0f), Is.EqualTo(0f));
            Assert.That(LiveBleedLogic.TargetFor(-1f), Is.EqualTo(0f));
            Assert.That(LiveBleedLogic.TargetFor(float.NaN), Is.EqualTo(0f));
            Assert.That(LiveBleedLogic.TargetFor(0.3f), Is.EqualTo(0.425f).Within(1e-5f), "進み 3 割で最大の半分");
            Assert.That(LiveBleedLogic.TargetFor(LiveBleedLogic.FullAtProgress), Is.EqualTo(LiveBleedLogic.MaxBleed));
            Assert.That(LiveBleedLogic.TargetFor(1f), Is.EqualTo(LiveBleedLogic.MaxBleed));
            float prev = 0f;
            for (int i = 1; i <= 100; i++)
            {
                float v = LiveBleedLogic.TargetFor(i / 100f);
                Assert.That(v, Is.GreaterThanOrEqualTo(prev), "進むほど濃くなる（戻らない）");
                Assert.That(v, Is.InRange(0f, LiveBleedLogic.MaxBleed));
                prev = v;
            }
        }

        [Test]
        public void Holding_ApproachesTheTarget_AndNeverExceedsMax()
        {
            var b = new LiveBleedLogic();
            for (int i = 1; i <= 72; i++)
            {
                float v = b.Tick(Dt, i / 72f, armed: true);
                Assert.That(v, Is.InRange(0f, LiveBleedLogic.MaxBleed));
            }
            for (int i = 0; i < 72; i++) b.Tick(Dt, 1f, armed: true);
            Assert.That(b.Value, Is.EqualTo(LiveBleedLogic.MaxBleed).Within(0.005f));

            var half = new LiveBleedLogic();
            for (int i = 0; i < 72 * 2; i++) half.Tick(Dt, 0.3f, armed: true);
            Assert.That(half.Value, Is.EqualTo(LiveBleedLogic.TargetFor(0.3f)).Within(0.005f), "目標は 0.85 × smoothstep(x/0.6)");
        }

        [Test]
        public void OneSecondHold_IsNearlyFullWellBeforeTheMarkFires()
        {
            // 1 秒の長押し（VisitorMarkHoldLogic.DefaultHoldSec）で進み具合が 0→1。進み 6 割（0.6 秒）で
            // もう 0.7 を越え、成立の瞬間には最大の手前まで来ている。
            var b = new LiveBleedLogic();
            for (int i = 1; i <= 43; i++) b.Tick(Dt, i / 72f, armed: true);
            Assert.That(b.Value, Is.GreaterThan(0.7f), "進み 6 割で濃く見えている");
            for (int i = 44; i <= 72; i++) b.Tick(Dt, i / 72f, armed: true);
            Assert.That(b.Value, Is.GreaterThan(0.8f));
        }

        [Test]
        public void Releasing_ReturnsToExactlyZero()
        {
            var b = new LiveBleedLogic();
            for (int i = 0; i < 72; i++) b.Tick(Dt, 1f, armed: true);
            float prev = b.Value;
            for (int i = 0; i < 72 * 2; i++)
            {
                float v = b.Tick(Dt, 0f, armed: true);
                Assert.That(v, Is.LessThanOrEqualTo(prev), "離したら単調に戻る（行き過ぎない）");
                Assert.That(v, Is.GreaterThanOrEqualTo(0f));
                prev = v;
            }
            Assert.That(b.Value, Is.EqualTo(0f), "シェーダへ正確な 0 を書く");
        }

        [Test]
        public void Collapse_HoldsTheValueForHalfASecond_ThenZeroUntilReleased_ThenBleedsAgain()
        {
            var b = new LiveBleedLogic();
            for (int i = 0; i < 72; i++) b.Tick(Dt, 1f, armed: true);
            float before = b.Value;
            b.Collapse();
            Assert.That(b.Value, Is.EqualTo(before), "成立の瞬間は値を変えない");
            Assert.That(b.HoldingAfterCollapse, Is.True);

            // 0.5 秒（36 フレーム）のうち、最後の 1 フレーム手前までは同じ値
            int holdFrames = HoldFrames;
            for (int i = 0; i < holdFrames - 1; i++)
                Assert.That(b.Tick(Dt, 1f, armed: true), Is.EqualTo(before), "押していても 0.5 秒は保つ");
            for (int i = 0; i < 2; i++) b.Tick(Dt, 1f, armed: true);
            Assert.That(b.Value, Is.EqualTo(0f), "0.5 秒を過ぎたら即 0");
            Assert.That(b.HoldingAfterCollapse, Is.False);
            for (int i = 0; i < 72; i++)
                Assert.That(b.Tick(Dt, 1f, armed: true), Is.EqualTo(0f), "押し続けていても、離すまで透けない");

            b.Tick(Dt, 0f, armed: true);   // 離した
            Assert.That(b.Collapsed, Is.False);
            for (int i = 0; i < 36; i++) b.Tick(Dt, 1f, armed: true);
            Assert.That(b.Value, Is.GreaterThan(0.7f), "押し直せばまた透ける");
        }

        [Test]
        public void ReleasingDuringTheHold_StillHoldsForHalfASecond_AndCountsOneHold()
        {
            var b = new LiveBleedLogic();
            for (int i = 1; i <= 72; i++) b.Tick(Dt, i / 72f, armed: true);
            float before = b.Value;
            b.Collapse();
            for (int i = 0; i < 6; i++) b.Tick(Dt, 1f, armed: true);         // 押したまま少し
            Assert.That(b.HoldCount, Is.EqualTo(0), "離すまでは数えない");

            Assert.That(b.Tick(Dt, 0f, armed: true), Is.EqualTo(before), "保っている間に離しても値は動かない");
            Assert.That(b.HoldCount, Is.EqualTo(1), "1 回の長押しは 1 回");
            Assert.That(b.LastHoldMax, Is.EqualTo(before).Within(1e-6f), "成立の前に出た最大が残る");

            int left = HoldFrames - 7;
            for (int i = 0; i < left - 1; i++)
                Assert.That(b.Tick(Dt, 0f, armed: true), Is.EqualTo(before), "離していても 0.5 秒までは保つ");
            for (int i = 0; i < 2; i++) b.Tick(Dt, 0f, armed: true);
            Assert.That(b.Value, Is.EqualTo(0f), "0.5 秒で即 0（離したあとの追従では戻さない）");
            for (int i = 0; i < 72; i++) b.Tick(Dt, 0f, armed: true);
            Assert.That(b.HoldCount, Is.EqualTo(1), "保ち終わっても数え直さない");
        }

        [Test]
        public void CollapseWithNothingShowing_IsJustZero()
        {
            var b = new LiveBleedLogic();
            b.Collapse();
            Assert.That(b.HoldingAfterCollapse, Is.False, "透けていなければ保つものが無い");
            Assert.That(b.Tick(Dt, 1f, armed: true), Is.EqualTo(0f), "押していても離すまで透けない");
        }

        [Test]
        public void LosingArm_DuringTheHold_SnapsToZero_AndDropsTheHold()
        {
            var b = new LiveBleedLogic();
            for (int i = 0; i < 72; i++) b.Tick(Dt, 1f, armed: true);
            b.Collapse();
            b.Tick(Dt, 1f, armed: true);
            Assert.That(b.Value, Is.GreaterThan(0f));
            Assert.That(b.Tick(Dt, 1f, armed: false), Is.EqualTo(0f), "カットや演出が替わったら保っていても即 0");
            Assert.That(b.HoldingAfterCollapse, Is.False);
            Assert.That(b.HoldCount, Is.EqualTo(1));
            Assert.That(b.Tick(Dt, 1f, armed: true), Is.LessThan(0.2f), "戻ってきても保った値には戻らない（0 から追い直す）");
        }

        [Test]
        public void NotArmed_IsAlwaysZero_AndCountsNoHold()
        {
            var b = new LiveBleedLogic();
            for (int i = 1; i <= 72 * 2; i++)
                Assert.That(b.Tick(Dt, i / 72f, armed: false), Is.EqualTo(0f));
            b.Tick(Dt, 0f, armed: false);
            Assert.That(b.HoldCount, Is.EqualTo(0));
            Assert.That(b.Holding, Is.False);
        }

        [Test]
        public void LosingArm_SnapsToZero_AndEndsTheHold()
        {
            var b = new LiveBleedLogic();
            for (int i = 0; i < 72; i++) b.Tick(Dt, 1f, armed: true);
            float peak = b.Value;
            Assert.That(b.Tick(Dt, 1f, armed: false), Is.EqualTo(0f), "人形のカットを出たら即 0");
            Assert.That(b.HoldCount, Is.EqualTo(1));
            Assert.That(b.LastHoldMax, Is.EqualTo(peak).Within(1e-6f));
        }

        [Test]
        public void EachHold_ReportsItsOwnMax_IncludingTheOneThatFired()
        {
            var b = new LiveBleedLogic();
            for (int i = 0; i < 18; i++) b.Tick(Dt, i / 72f, armed: true);   // 0.25 秒で離す
            b.Tick(Dt, 0f, armed: true);
            Assert.That(b.HoldCount, Is.EqualTo(1));
            float shortMax = b.LastHoldMax;
            Assert.That(shortMax, Is.GreaterThan(0f).And.LessThan(0.2f));

            for (int i = 0; i < 72 * 2; i++) b.Tick(Dt, 0f, armed: true);
            for (int i = 1; i <= 72; i++) b.Tick(Dt, i / 72f, armed: true);
            float before = b.Value;
            b.Collapse();                                                    // 成立
            for (int i = 0; i < 72; i++) b.Tick(Dt, 1f, armed: true);         // 押したまま（保つ 0.5 秒を越える）
            Assert.That(b.HoldCount, Is.EqualTo(1), "離すまでは数えない");
            b.Tick(Dt, 0f, armed: true);
            Assert.That(b.HoldCount, Is.EqualTo(2));
            Assert.That(b.LastHoldMax, Is.EqualTo(before).Within(1e-6f), "畳む前に出た最大が残る");
        }

        [Test]
        public void PressingAgainDuringTheHold_DoesNotCountTheHeldValueAsItsMax()
        {
            var b = new LiveBleedLogic();
            for (int i = 0; i < 72; i++) b.Tick(Dt, 1f, armed: true);
            b.Collapse();
            b.Tick(Dt, 0f, armed: true);                                     // 離した（1 回目を数える）
            for (int i = 0; i < 3; i++) b.Tick(Dt, 0.05f, armed: true);       // 保っている間に押し直す
            for (int i = 0; i < 72; i++) b.Tick(Dt, 0.05f, armed: true);      // 保ち終わって、浅く押したまま
            b.Tick(Dt, 0f, armed: true);
            Assert.That(b.HoldCount, Is.EqualTo(2));
            Assert.That(b.LastHoldMax, Is.LessThan(0.1f), "前の長押しの保った値は、押し直した長押しの最大に入れない");
        }

        [Test]
        public void BadInputs_AreTreatedAsZero_AndCoarseDtStaysInRange()
        {
            var b = new LiveBleedLogic();
            Assert.That(b.Tick(float.NaN, float.NaN, armed: true), Is.EqualTo(0f));
            Assert.That(b.Tick(-1f, -3f, armed: true), Is.EqualTo(0f));
            for (int i = 0; i < 20; i++)
            {
                float v = b.Tick(i % 2 == 0 ? 5f : 0.5f, i < 10 ? 7f : 0f, armed: true);
                Assert.That(float.IsNaN(v), Is.False);
                Assert.That(v, Is.InRange(0f, LiveBleedLogic.MaxBleed));
            }
            Assert.That(b.Value, Is.EqualTo(0f));
        }

        [Test]
        public void Reset_ClearsValueAndTheHold_ButKeepsTheObservationCount()
        {
            var b = new LiveBleedLogic();
            for (int i = 0; i < 72; i++) b.Tick(Dt, 1f, armed: true);
            b.Tick(Dt, 0f, armed: true);
            for (int i = 0; i < 4; i++) b.Tick(Dt, 1f, armed: true);
            b.Collapse();
            Assert.That(b.HoldingAfterCollapse, Is.True);
            b.Reset();
            Assert.That(b.Value, Is.EqualTo(0f));
            Assert.That(b.Collapsed, Is.False);
            Assert.That(b.HoldingAfterCollapse, Is.False, "保っている値も捨てる");
            Assert.That(b.HoldCount, Is.EqualTo(1), "増分で読むので戻さない");
            Assert.That(b.Tick(Dt, 0f, armed: true), Is.EqualTo(0f));
        }

        [Test]
        public void Armed_OnlyForAFlaggedTake_OnAPlateCutWithTheDoll()
        {
            Assert.That(TakeRunner.IsBleedArmed(true, true, TakeSchema.SourcePlate, true), Is.True);
            Assert.That(TakeRunner.IsBleedArmed(true, false, TakeSchema.SourcePlate, true), Is.False, "旗が無い");
            Assert.That(TakeRunner.IsBleedArmed(false, true, TakeSchema.SourcePlate, true), Is.False, "演出が走っていない");
            Assert.That(TakeRunner.IsBleedArmed(true, true, TakeSchema.SourceRec, true), Is.False, "録画のカット");
            Assert.That(TakeRunner.IsBleedArmed(true, true, TakeSchema.SourceLive, true), Is.False, "ライブのカット");
            Assert.That(TakeRunner.IsBleedArmed(true, true, TakeSchema.SourcePlate, false), Is.False, "人形が出ていない");
            Assert.That(TakeRunner.IsBleedArmed(true, true, null, true), Is.False);
        }
    }
}
