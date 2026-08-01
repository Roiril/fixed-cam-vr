#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// YawFollowLogic（スクリーン水平追従の緩急）の検証。
    ///
    /// <b>deadzone（動き出す閾値）と trail（止まる位置）は別物</b>という 2026-08-01 の分離を固定する。
    /// 旧実装は 1 つの値を両方に使っていたため、スクリーンは<b>常に頭の 10° 手前で止まり、
    /// 正面へ一度も到達しなかった</b>（ユーザー指摘「頭の前までぎりぎり到達しないとかはやめて」）。
    /// 速度上限（スナップ禁止）・再ロック合流・逆走ブーストは従来どおり。
    /// </summary>
    public sealed class YawFollowLogicTests
    {
        private const float Deadzone = 10f;
        private const float Smooth = 0.3f;
        private const float MaxSpeed = 110f;
        private const float CatchUpThreshold = 45f;
        private const float CatchUpBoost = 2f;
        private const float Dt = 0.1f;

        /// <summary>スクリーン（ScreenAnchor）の設定 — 頭の正面ちょうどを目指す。</summary>
        private static float StepToFront(YawFollowLogic l, float headYaw,
            float maxSpeed = MaxSpeed, float boost = CatchUpBoost)
            => l.Step(headYaw, Dt, ScreenDeadzone, 0f, Smooth, maxSpeed, CatchUpThreshold, boost);

        private const float ScreenDeadzone = 0.5f;

        /// <summary>HUD（StatusHud）の設定 — 動き出す閾値と止まる位置が同じ値。</summary>
        private static float StepTrailing(YawFollowLogic l, float headYaw)
            => l.Step(headYaw, Dt, Deadzone, Deadzone, Smooth, MaxSpeed, CatchUpThreshold, CatchUpBoost);

        // ---- スクリーン（trail = 0）: 頭の正面まで行って止まる ----

        [Test]
        public void Step_NoTrail_ReachesHeadYawExactly()
        {
            var l = new YawFollowLogic();
            l.Reseat(0f);
            for (int i = 0; i < 200; i++) StepToFront(l, 30f);
            // **ここが本題**。旧実装は 20°（= 30 − deadzone）で止まっていた。
            Assert.That(l.CurrentYaw, Is.EqualTo(30f).Within(1e-3f),
                "スクリーンが頭の正面へ到達していない（trail が効いている？）");
        }

        [Test]
        public void Step_NoTrail_DoesNotStallInsideDeadzoneWhileMoving()
        {
            var l = new YawFollowLogic();
            l.Reseat(0f);
            // 頭は deadzone の外（5° > 0.5°）。動き出したら deadzone を無視して到達しきること。
            for (int i = 0; i < 200; i++) StepToFront(l, 5f);
            Assert.That(l.CurrentYaw, Is.EqualTo(5f).Within(1e-3f));
        }

        [Test]
        public void Step_NoTrail_MicroJitterDoesNotMoveScreen()
        {
            var l = new YawFollowLogic();
            l.Reseat(0f);
            // deadzone(0.5°) 内の震えでは動かない。動くと視界の中で画が常に微動する。
            for (int i = 0; i < 50; i++) StepToFront(l, (i % 2 == 0) ? 0.3f : -0.3f);
            Assert.That(l.CurrentYaw, Is.EqualTo(0f).Within(1e-3f));
        }

        [Test]
        public void Step_NoTrail_SettlesAndStops()
        {
            var l = new YawFollowLogic();
            l.Reseat(0f);
            for (int i = 0; i < 200; i++) StepToFront(l, 30f);
            float settled = l.CurrentYaw;
            // 到達後は微動しない（指数収束の裾を引かない）。
            for (int i = 0; i < 50; i++) StepToFront(l, 30f);
            Assert.That(l.CurrentYaw, Is.EqualTo(settled).Within(1e-6f));
        }

        // ---- HUD（trail = deadzone）: 従来どおり手前で止まる ----

        [Test]
        public void Step_WithTrail_WithinDeadzone_NoMovement()
        {
            var l = new YawFollowLogic();
            l.Reseat(0f);
            // |e|=5 <= deadzone 10 → 動かない。
            for (int i = 0; i < 10; i++) StepTrailing(l, 5f);
            Assert.That(l.CurrentYaw, Is.EqualTo(0f).Within(1e-3f));
        }

        [Test]
        public void Step_WithTrail_BeyondDeadzone_TrailsBehindHead()
        {
            var l = new YawFollowLogic();
            l.Reseat(0f);
            // 静止した頭 30° に対し、パネルは trail(10°) だけ手前 = 20° に収束する。
            for (int i = 0; i < 120; i++) StepTrailing(l, 30f);
            Assert.That(l.CurrentYaw, Is.EqualTo(20f).Within(0.5f));
        }

        [Test]
        public void Step_WithTrail_LargeJump_EventuallyConvergesTrailBehind()
        {
            var l = new YawFollowLogic();
            l.Reseat(0f);
            for (int i = 0; i < 400; i++) StepTrailing(l, 170f);
            Assert.That(l.CurrentYaw, Is.EqualTo(160f).Within(1f)); // 170 - trail
        }

        // ---- 共通（どちらの設定でも守ること）----

        [Test]
        public void Step_LargeJump_IsRateLimitedNotSnapped()
        {
            var l = new YawFollowLogic();
            l.Reseat(0f);
            float y = StepToFront(l, 170f);
            // 1 フレームで 170° へスナップしない（速度上限で寄せる）。
            Assert.That(Mathf.Abs(y), Is.LessThan(MaxSpeed * Dt * 1.5f));
            Assert.That(Mathf.Abs(y), Is.GreaterThan(0f)); // 動いてはいる
        }

        [Test]
        public void Reseat_MergesFromCurrent_NoSnapToFarHead()
        {
            var l = new YawFollowLogic();
            l.Reseat(45f);
            // 遠い頭 200° でも、再ロック直後は現在 45° から緩やかに寄せる（瞬間ジャンプしない）。
            float y = StepToFront(l, 200f);
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(y, 45f)), Is.LessThan(MaxSpeed * Dt * 1.5f));
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(y, 200f)), Is.GreaterThan(100f));
        }

        [Test]
        public void Step_CatchUpBoost_MovesFasterForLargeError()
        {
            var noBoost = new YawFollowLogic(); noBoost.Reseat(0f);
            var boosted = new YawFollowLogic(); boosted.Reseat(0f);
            // e=170 > threshold(45) → boosted は速度上限が 2 倍 → 1 フレームで多く進む。
            float a = Mathf.Abs(StepToFront(noBoost, 170f, boost: 1f));
            float b = Mathf.Abs(StepToFront(boosted, 170f, boost: 2f));
            Assert.That(b, Is.GreaterThan(a));
        }

        [Test]
        public void Step_Unseeded_SeedsToHeadOnFirstCall()
        {
            var l = new YawFollowLogic();
            Assert.That(l.Seeded, Is.False);
            float y = StepToFront(l, 90f);
            Assert.That(l.Seeded, Is.True);
            // 種 = headYaw なので e=0 → 90° のまま。
            Assert.That(y, Is.EqualTo(90f).Within(1e-3f));
        }

        [Test]
        public void Step_ZeroDt_NoMovement()
        {
            var l = new YawFollowLogic();
            l.Reseat(30f);
            float y = l.Step(90f, 0f, Deadzone, Deadzone, Smooth, MaxSpeed, CatchUpThreshold, CatchUpBoost);
            Assert.That(y, Is.EqualTo(30f).Within(1e-4f));
        }

        [Test]
        public void Step_DeadzoneSmallerThanTrail_IsRaisedToTrail()
        {
            // deadzone < trail は「到達した瞬間にまた動き出す」状態なので、内部で trail まで引き上げる。
            var l = new YawFollowLogic();
            l.Reseat(0f);
            for (int i = 0; i < 400; i++) l.Step(30f, Dt, 1f, 10f, Smooth, MaxSpeed, CatchUpThreshold, CatchUpBoost);
            float settled = l.CurrentYaw;
            Assert.That(settled, Is.EqualTo(20f).Within(0.5f));
            for (int i = 0; i < 50; i++) l.Step(30f, Dt, 1f, 10f, Smooth, MaxSpeed, CatchUpThreshold, CatchUpBoost);
            Assert.That(l.CurrentYaw, Is.EqualTo(settled).Within(1e-4f), "到達後に振動している");
        }
    }
}
