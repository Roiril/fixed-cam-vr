#nullable enable
using System;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 体験の時計 <see cref="SimTime"/>。固定刻みの進み方は純関数 <see cref="SimTimeLogic"/> で確かめる
    /// （EditMode では <c>Time.frameCount</c> が進まない）。素通しのときは Unity の値をそのまま返すこと。
    /// </summary>
    public sealed class SimTimeTests
    {
        private const float Step = 1f / 60f;

        [TearDown]
        public void TearDown()
        {
            if (SimTime.Fixed) SimTime.Disable();
        }

        // ---- 固定刻みの進み方（純関数） -------------------------------------------------

        [Test]
        public void Logic_StartFrame_IsTheStartValues()
        {
            var logic = new SimTimeLogic(Step, startFrame: 10, startNow: 5f, startRealtime: 7f);
            Assert.AreEqual(5f, logic.NowAt(10), 1e-6f);
            Assert.AreEqual(7f, logic.RealtimeAt(10), 1e-6f);
            Assert.AreEqual(Step, logic.Dt, 1e-9f, "始めたフレームも 0 ではなく 1 刻み");
        }

        [Test]
        public void Logic_OneFrame_AdvancesOneStep()
        {
            var logic = new SimTimeLogic(Step, 10, 5f, 7f);
            Assert.AreEqual(5f + Step, logic.NowAt(11), 1e-6f);
            Assert.AreEqual(7f + Step, logic.RealtimeAt(11), 1e-6f);
            Assert.AreEqual(Step, logic.Dt, 1e-9f);
        }

        [Test]
        public void Logic_SameFrame_ReadsTheSameValue()
        {
            var logic = new SimTimeLogic(Step, 10, 5f, 7f);
            Assert.AreEqual(logic.NowAt(12), logic.NowAt(12));
            Assert.AreEqual(logic.RealtimeAt(12), logic.RealtimeAt(12));
        }

        [Test]
        public void Logic_SkippedFrames_AdvanceByTheSkippedCount()
        {
            // 1 フレーム = 1 刻み。間を読まなくても、毎フレーム読んだときと同じ値になる。
            var logic = new SimTimeLogic(Step, 10, 5f, 7f);
            Assert.AreEqual(3L, logic.StepsAt(13));
            Assert.AreEqual(5f + 3f * Step, logic.NowAt(13), 1e-6f);
            Assert.AreEqual(7f + 3f * Step, logic.RealtimeAt(13), 1e-6f);
        }

        [Test]
        public void Logic_FrameBeforeStart_DoesNotGoBackwards()
        {
            var logic = new SimTimeLogic(Step, 10, 5f, 7f);
            Assert.AreEqual(0L, logic.StepsAt(3));
            Assert.AreEqual(5f, logic.NowAt(3), 1e-6f);
        }

        [Test]
        public void Logic_LongRun_DoesNotDrift()
        {
            // 90fps で 1 時間。積算せず掛け算で出すので誤差が溜まらない。
            const float step90 = 1f / 90f;
            var logic = new SimTimeLogic(step90, 0, 0f, 0f);
            Assert.AreEqual(3600f, logic.NowAt(90 * 3600), 1e-3f);
        }

        [Test]
        public void Logic_RejectsInvalidStep()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new SimTimeLogic(0f, 0, 0f, 0f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new SimTimeLogic(-Step, 0, 0f, 0f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new SimTimeLogic(float.NaN, 0, 0f, 0f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new SimTimeLogic(float.PositiveInfinity, 0, 0f, 0f));
        }

        // ---- 素通し ------------------------------------------------------------------

        [Test]
        public void PassThrough_ReturnsUnityValuesAsIs()
        {
            Assert.IsFalse(SimTime.Fixed);
            Assert.AreEqual(0f, SimTime.StepSec);
            Assert.AreEqual(Time.unscaledTime, SimTime.Now);
            Assert.AreEqual(Time.unscaledDeltaTime, SimTime.Dt);
            float before = Time.realtimeSinceStartup;
            float read = SimTime.Realtime;
            float after = Time.realtimeSinceStartup;
            Assert.That(read, Is.InRange(before, after), "素通しの Realtime は realtimeSinceStartup そのもの");
        }

        [Test]
        public void WallClock_IsRealtimeSinceStartup()
        {
            float before = Time.realtimeSinceStartup;
            float read = WallClock.Now;
            float after = Time.realtimeSinceStartup;
            Assert.That(read, Is.InRange(before, after));
        }

        // ---- 固定刻みの切り替え ----------------------------------------------------------

        [Test]
        public void EnableFixedStep_StartsFromUnityValues_AndDisableRestores()
        {
            float unscaledAtEnable = Time.unscaledTime;
            float realtimeBefore = Time.realtimeSinceStartup;
            SimTime.EnableFixedStep(Step);
            float realtimeAfter = Time.realtimeSinceStartup;

            Assert.IsTrue(SimTime.Fixed);
            Assert.AreEqual(Step, SimTime.StepSec, 1e-9f);
            Assert.AreEqual(Step, SimTime.Dt, 1e-9f);
            Assert.AreEqual(Step, Time.captureDeltaTime, 1e-9f, "scaled 側も同じ刻みに揃える");
            Assert.AreEqual(unscaledAtEnable, SimTime.Now, 1e-6f, "呼んだフレームの Now は呼んだ瞬間の unscaledTime");
            Assert.That(SimTime.Realtime, Is.InRange(realtimeBefore, realtimeAfter));

            // 同じフレームでは実時間が進んでも動かない。
            float realtime = SimTime.Realtime;
            Assert.AreEqual(realtime, SimTime.Realtime);

            SimTime.Disable();
            Assert.IsFalse(SimTime.Fixed);
            Assert.AreEqual(0f, SimTime.StepSec);
            Assert.AreEqual(0f, Time.captureDeltaTime);
            Assert.AreEqual(Time.unscaledTime, SimTime.Now);
        }

        [Test]
        public void EnableFixedStep_Again_ContinuesWithoutRewinding()
        {
            SimTime.EnableFixedStep(Step);
            float now = SimTime.Now;
            float realtime = SimTime.Realtime;
            SimTime.EnableFixedStep(Step * 2f);
            Assert.AreEqual(now, SimTime.Now, 1e-6f);
            Assert.AreEqual(realtime, SimTime.Realtime, 1e-6f);
            Assert.AreEqual(Step * 2f, SimTime.StepSec, 1e-9f);
        }

        [Test]
        public void EnableFixedStep_RejectsInvalidStep_WithoutChangingState()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => SimTime.EnableFixedStep(0f));
            Assert.IsFalse(SimTime.Fixed);
            Assert.AreEqual(0f, Time.captureDeltaTime);
        }
    }
}
