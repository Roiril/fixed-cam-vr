#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// YawFollowLogic（スクリーン水平追従の緩急）の検証。
    /// deadzone 内不動・枠端トレール・速度上限（スナップ禁止）・再ロック合流・逆走ブーストを固定する。
    /// </summary>
    public sealed class YawFollowLogicTests
    {
        private const float Deadzone = 10f;
        private const float Smooth = 0.3f;
        private const float MaxSpeed = 110f;
        private const float CatchUpThreshold = 45f;
        private const float CatchUpBoost = 2f;
        private const float Dt = 0.1f;

        private static float Step(YawFollowLogic l, float headYaw,
            float maxSpeed = MaxSpeed, float boost = CatchUpBoost)
            => l.Step(headYaw, Dt, Deadzone, Smooth, maxSpeed, CatchUpThreshold, boost);

        [Test]
        public void Step_WithinDeadzone_NoMovement()
        {
            var l = new YawFollowLogic();
            l.Reseat(0f);
            // |e|=5 <= deadzone 10 → 目標は現在ヨー → 動かない。
            for (int i = 0; i < 10; i++) Step(l, 5f);
            Assert.That(l.CurrentYaw, Is.EqualTo(0f).Within(1e-3f));
        }

        [Test]
        public void Step_BeyondDeadzone_TrailsDeadzoneBehindHead()
        {
            var l = new YawFollowLogic();
            l.Reseat(0f);
            // 静止した頭 30° に対し、スクリーンは deadzone(10°) だけ手前 = 20° に収束する。
            for (int i = 0; i < 120; i++) Step(l, 30f);
            Assert.That(l.CurrentYaw, Is.EqualTo(20f).Within(0.5f));
        }

        [Test]
        public void Step_LargeJump_IsRateLimitedNotSnapped()
        {
            var l = new YawFollowLogic();
            l.Reseat(0f);
            float y = Step(l, 170f);
            // 1 フレームで 170° へスナップしない（速度上限で寄せる）。
            Assert.That(Mathf.Abs(y), Is.LessThan(MaxSpeed * Dt * 1.5f));
            Assert.That(Mathf.Abs(y), Is.GreaterThan(0f)); // 動いてはいる
        }

        [Test]
        public void Step_LargeJump_EventuallyConvergesDeadzoneBehind()
        {
            var l = new YawFollowLogic();
            l.Reseat(0f);
            for (int i = 0; i < 400; i++) Step(l, 170f);
            Assert.That(l.CurrentYaw, Is.EqualTo(160f).Within(1f)); // 170 - deadzone
        }

        [Test]
        public void Reseat_MergesFromCurrent_NoSnapToFarHead()
        {
            var l = new YawFollowLogic();
            l.Reseat(45f);
            // 遠い頭 200° でも、再ロック直後は現在 45° から緩やかに寄せる（瞬間ジャンプしない）。
            float y = Step(l, 200f);
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(y, 45f)), Is.LessThan(MaxSpeed * Dt * 1.5f));
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(y, 200f)), Is.GreaterThan(100f));
        }

        [Test]
        public void Step_CatchUpBoost_MovesFasterForLargeError()
        {
            var noBoost = new YawFollowLogic(); noBoost.Reseat(0f);
            var boosted = new YawFollowLogic(); boosted.Reseat(0f);
            // e=170 > threshold(45) → boosted は速度上限が 2 倍 → 1 フレームで多く進む。
            float a = Mathf.Abs(Step(noBoost, 170f, boost: 1f));
            float b = Mathf.Abs(Step(boosted, 170f, boost: 2f));
            Assert.That(b, Is.GreaterThan(a));
        }

        [Test]
        public void Step_Unseeded_SeedsToHeadOnFirstCall()
        {
            var l = new YawFollowLogic();
            Assert.That(l.Seeded, Is.False);
            float y = Step(l, 90f);
            Assert.That(l.Seeded, Is.True);
            // 種 = headYaw なので e=0 → 90° のまま。
            Assert.That(y, Is.EqualTo(90f).Within(1e-3f));
        }

        [Test]
        public void Step_ZeroDt_NoMovement()
        {
            var l = new YawFollowLogic();
            l.Reseat(30f);
            float y = l.Step(90f, 0f, Deadzone, Smooth, MaxSpeed, CatchUpThreshold, CatchUpBoost);
            Assert.That(y, Is.EqualTo(30f).Within(1e-4f));
        }
    }
}
