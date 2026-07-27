#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// SpotTriggerLogic（位置トリガーの円の内外・滞在計時）の検証。
    /// 契約は <c>.claude/plans/2026-07-27_position-trigger.md</c> §3.3。
    /// </summary>
    public sealed class SpotTriggerLogicTests
    {
        private const float Dt = 1f / 60f;

        private static SpotTriggerLogic Make(params SpotTriggerLogic.Spot[] spots)
        {
            var l = new SpotTriggerLogic();
            l.SetSpots(spots);
            return l;
        }

        [Test]
        public void EntersWhenWithinRadius_AndReportsZeroSecOnEntryFrame()
        {
            var l = Make(SpotTriggerLogic.Spot.At(0.5f, -0.5f, 0.25f));

            l.Tick(0f, 0f, Dt);
            Assert.That(l.IsInside(0), Is.False, "円の外");

            l.Tick(0.5f, -0.6f, Dt);   // 中心から 0.1m
            Assert.That(l.IsInside(0), Is.True);
            Assert.That(l.InsideSecOf(0), Is.EqualTo(0f), "入った瞬間の滞在は 0（hold 0 の演出はこれで発火する）");
        }

        [Test]
        public void InsideSec_AccumulatesContinuously_AndResetsOnLeaving()
        {
            var l = Make(SpotTriggerLogic.Spot.At(0f, 0f, 0.25f));
            l.Tick(0f, 0f, Dt);                       // 進入（0）
            for (int i = 0; i < 30; i++) l.Tick(0f, 0f, Dt);   // 0.5s
            Assert.That(l.InsideSecOf(0), Is.EqualTo(0.5f).Within(0.01f));

            l.Tick(2f, 0f, Dt);
            Assert.That(l.IsInside(0), Is.False);
            Assert.That(l.InsideSecOf(0), Is.EqualTo(0f), "出たら計り直し");
        }

        [Test]
        public void ExitUsesHysteresis_SoJitterAtTheEdgeDoesNotResetTheStay()
        {
            var l = Make(SpotTriggerLogic.Spot.At(0f, 0f, 0.25f));
            l.Tick(0.2f, 0f, Dt);                     // 進入
            l.Tick(0.2f, 0f, Dt);
            Assert.That(l.InsideSecOf(0), Is.GreaterThan(0f));

            // 半径より外（0.27m）だが ExitMarginM(0.08) の内 → まだ「中」扱いで計時が続く。
            l.Tick(0.27f, 0f, Dt);
            Assert.That(l.IsInside(0), Is.True);
            Assert.That(l.InsideSecOf(0), Is.GreaterThan(0f), "縁の揺れで滞在が 0 に戻らない");

            // 半径 + マージンを超えたら出る。
            l.Tick(0.34f, 0f, Dt);
            Assert.That(l.IsInside(0), Is.False);
        }

        [Test]
        public void EnteringRequiresTheRadius_NotTheHysteresisBand()
        {
            var l = Make(SpotTriggerLogic.Spot.At(0f, 0f, 0.25f));
            l.Tick(0.28f, 0f, Dt);
            Assert.That(l.IsInside(0), Is.False, "入る判定は素の半径（ヒステリシスは出る側だけ）");
        }

        [Test]
        public void DiscontinuousDt_RestartsTheStay()
        {
            // HMD 着脱・アプリ復帰で「ずっと立っていた」と誤認しない（滞在は数え直す）。
            var l = Make(SpotTriggerLogic.Spot.At(0f, 0f, 0.25f));
            l.Tick(0f, 0f, Dt);
            for (int i = 0; i < 30; i++) l.Tick(0f, 0f, Dt);
            Assert.That(l.InsideSecOf(0), Is.GreaterThan(0.4f));

            l.Tick(0f, 0f, 3f);   // 3 秒ぶんの飛び
            Assert.That(l.IsInside(0), Is.True, "円の中に居ることは変わらない");
            Assert.That(l.InsideSecOf(0), Is.EqualTo(0f), "滞在計時だけやり直す");
        }

        [Test]
        public void UndefinedSpot_IsNeverInside()
        {
            // 演出が参照しているのに layout に円が無い枠（発火しないことが仕様）。
            var l = Make(SpotTriggerLogic.Spot.Undefined);
            l.Tick(0f, 0f, Dt);
            Assert.That(l.IsInside(0), Is.False);
            Assert.That(l.Count, Is.EqualTo(1), "枠（スロット）自体は保たれる = index が動かない");
        }

        [Test]
        public void ZeroRadius_IsClampedToMinimum_NotAnUnreachablePoint()
        {
            var l = Make(SpotTriggerLogic.Spot.At(0f, 0f, 0f));
            l.Tick(0.04f, 0f, Dt);
            Assert.That(l.IsInside(0), Is.True, $"半径 0 は {SpotTriggerLogic.MinRadiusM}m へクランプ");
        }

        [Test]
        public void Reset_ClearsStayWithoutLosingSlots()
        {
            var l = Make(SpotTriggerLogic.Spot.At(0f, 0f, 0.25f), SpotTriggerLogic.Spot.At(1f, 0f, 0.25f));
            l.Tick(0f, 0f, Dt);
            l.Tick(0f, 0f, Dt);
            Assert.That(l.IsInside(0), Is.True);

            l.Reset();
            Assert.That(l.IsInside(0), Is.False);
            Assert.That(l.InsideSecOf(0), Is.EqualTo(0f));
            Assert.That(l.Count, Is.EqualTo(2));
        }

        [Test]
        public void OutOfRangeQueries_AreSafe()
        {
            var l = Make(SpotTriggerLogic.Spot.At(0f, 0f, 0.25f));
            Assert.That(l.IsInside(-1), Is.False);
            Assert.That(l.IsInside(9), Is.False);
            Assert.That(l.InsideSecOf(9), Is.EqualTo(0f));
        }

        [Test]
        public void SetSpots_ReplacesGeometryAndClearsStay()
        {
            var l = Make(SpotTriggerLogic.Spot.At(0f, 0f, 0.25f));
            l.Tick(0f, 0f, Dt);
            l.Tick(0f, 0f, Dt);
            Assert.That(l.IsInside(0), Is.True);

            // 卓が円を動かした（走行中の layout 更新）→ 計り直す。
            l.SetSpots(new[] { SpotTriggerLogic.Spot.At(1f, 1f, 0.25f) });
            Assert.That(l.IsInside(0), Is.False);
            l.Tick(1f, 1f, Dt);
            Assert.That(l.IsInside(0), Is.True);
        }

        [Test]
        public void HeightIsIgnored_ByConstruction()
        {
            // Tick は XZ しか受け取らない（しゃがみ・身長で発火が変わらないことを型で保証する）。
            var l = Make(SpotTriggerLogic.Spot.At(0f, 0f, 0.25f));
            l.Tick(0.1f, 0.1f, Dt);
            Assert.That(l.IsInside(0), Is.True);
        }
    }
}
