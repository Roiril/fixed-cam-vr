#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 3 周目の長押しの間だけ人形の下からライブが透ける量（<see cref="LiveBleedLogic"/>）。
    /// 目標 0.5×進み具合へ寄り、離すと 0 へ戻り、報告の成立で即 0。旗が無い（働かない）間は常に 0。
    /// </summary>
    public sealed class LiveBleedLogicTests
    {
        private const float Dt = 1f / 72f;

        [Test]
        public void Holding_ApproachesHalfOfProgress_AndNeverExceedsMax()
        {
            var b = new LiveBleedLogic();
            for (int i = 1; i <= 72; i++)
            {
                float v = b.Tick(Dt, i / 72f, armed: true);
                Assert.That(v, Is.InRange(0f, LiveBleedLogic.MaxBleed));
            }
            for (int i = 0; i < 72; i++) b.Tick(Dt, 1f, armed: true);
            Assert.That(b.Value, Is.EqualTo(0.5f).Within(0.005f));

            var half = new LiveBleedLogic();
            for (int i = 0; i < 72 * 2; i++) half.Tick(Dt, 0.4f, armed: true);
            Assert.That(half.Value, Is.EqualTo(0.2f).Within(0.005f), "目標は 0.5 × 進み具合");
        }

        [Test]
        public void OneSecondHold_IsAlreadyBleedingWhenTheMarkFires()
        {
            // 1 秒の長押し（VisitorMarkHoldLogic.DefaultHoldSec）で進み具合が 0→1。成立の瞬間に見えている
            // （解析器の合格線 0.05 を大きく越える）。
            var b = new LiveBleedLogic();
            for (int i = 1; i <= 72; i++) b.Tick(Dt, i / 72f, armed: true);
            Assert.That(b.Value, Is.GreaterThan(0.3f));
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
        public void Collapse_IsImmediateZero_UntilReleased_ThenBleedsAgain()
        {
            var b = new LiveBleedLogic();
            for (int i = 0; i < 72; i++) b.Tick(Dt, 1f, armed: true);
            b.Collapse();
            Assert.That(b.Value, Is.EqualTo(0f), "成立で即 0");
            for (int i = 0; i < 72; i++)
                Assert.That(b.Tick(Dt, 1f, armed: true), Is.EqualTo(0f), "押し続けていても透けない");
            b.Tick(Dt, 0f, armed: true);   // 離した
            Assert.That(b.Collapsed, Is.False);
            for (int i = 0; i < 36; i++) b.Tick(Dt, 1f, armed: true);
            Assert.That(b.Value, Is.GreaterThan(0.3f), "押し直せばまた透ける");
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
            Assert.That(shortMax, Is.GreaterThan(0f).And.LessThan(0.15f));

            for (int i = 0; i < 72 * 2; i++) b.Tick(Dt, 0f, armed: true);
            for (int i = 1; i <= 72; i++) b.Tick(Dt, i / 72f, armed: true);
            float before = b.Value;
            b.Collapse();                                                    // 成立
            for (int i = 0; i < 20; i++) b.Tick(Dt, 1f, armed: true);         // 押したまま
            Assert.That(b.HoldCount, Is.EqualTo(1), "離すまでは数えない");
            b.Tick(Dt, 0f, armed: true);
            Assert.That(b.HoldCount, Is.EqualTo(2));
            Assert.That(b.LastHoldMax, Is.EqualTo(before).Within(1e-6f), "畳む前に出た最大が残る");
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
        public void Reset_ClearsValue_ButKeepsTheObservationCount()
        {
            var b = new LiveBleedLogic();
            for (int i = 0; i < 72; i++) b.Tick(Dt, 1f, armed: true);
            b.Tick(Dt, 0f, armed: true);
            b.Collapse();
            b.Reset();
            Assert.That(b.Value, Is.EqualTo(0f));
            Assert.That(b.Collapsed, Is.False);
            Assert.That(b.HoldCount, Is.EqualTo(1), "増分で読むので戻さない");
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
