#nullable enable
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Tracking.Tests
{
    /// <summary>
    /// <see cref="HoldAverageSampler"/> の計時・平均・早離し中断を dt 注入で決定的に固定する純ロジック回帰。
    /// production（CourseRegistrationController.UpdateMarkSampling）と同一経路（Tick）を叩く。
    /// </summary>
    public sealed class HoldAverageSamplerTests
    {
        // Down エッジで開始し held を n フレーム進める共通駆動（dt 一定）。最後の Tick の結果を返す。
        private static HoldAverageSampler.Result Drive(
            HoldAverageSampler s, Vector3 pointer, float dt, int heldFrames, out Vector3 avg)
        {
            s.Tick(markDown: true, markHeld: true, dt: 0f, pointer: pointer, out avg); // 開始（現フレームを 1 サンプル目に）
            HoldAverageSampler.Result r = HoldAverageSampler.Result.None;
            for (int i = 0; i < heldFrames; i++)
                r = s.Tick(markDown: false, markHeld: true, dt: dt, pointer: pointer, out avg);
            return r;
        }

        [Test]
        public void ReachesThreshold_Captured_ReturnsAverageOfPointers()
        {
            var s = new HoldAverageSampler(0.3f);

            // 開始点(0,0,0) → held 3 フレームで (3,0,0) を加算（dt 0.1 × 3 = 0.3 で到達）。
            s.Tick(markDown: true, markHeld: true, dt: 0f, pointer: new Vector3(0f, 0f, 0f), out _);
            var r1 = s.Tick(markDown: false, markHeld: true, dt: 0.1f, pointer: new Vector3(3f, 0f, 0f), out _);
            var r2 = s.Tick(markDown: false, markHeld: true, dt: 0.1f, pointer: new Vector3(3f, 0f, 0f), out _);
            var r3 = s.Tick(markDown: false, markHeld: true, dt: 0.1f, pointer: new Vector3(3f, 0f, 0f), out Vector3 avg);

            Assert.That(r1, Is.EqualTo(HoldAverageSampler.Result.None));
            Assert.That(r2, Is.EqualTo(HoldAverageSampler.Result.None));
            Assert.That(r3, Is.EqualTo(HoldAverageSampler.Result.Captured), "0.3s 到達で Captured");
            // 平均 = {(0),(3),(3),(3)} / 4 = 2.25（4 サンプル: 開始 1 + held 3）。
            Assert.That(avg.x, Is.EqualTo(2.25f).Within(1e-5f), "複数 pointer の平均位置を返す");
            Assert.That(s.Active, Is.False, "Captured 後は非アクティブ");
        }

        [Test]
        public void ReleaseBeforeThreshold_Aborted_NoCapture()
        {
            var s = new HoldAverageSampler(0.5f);
            s.Tick(markDown: true, markHeld: true, dt: 0f, pointer: Vector3.one, out _);
            s.Tick(markDown: false, markHeld: true, dt: 0.1f, pointer: Vector3.one, out _); // time 0.1 < 0.5

            var r = s.Tick(markDown: false, markHeld: false, dt: 0.1f, pointer: Vector3.one, out Vector3 avg);

            Assert.That(r, Is.EqualTo(HoldAverageSampler.Result.Aborted), "しきい値未満の早離しは Aborted");
            Assert.That(avg, Is.EqualTo(Vector3.zero), "中断時は位置を採らない（avg=0）");
            Assert.That(s.Active, Is.False);
        }

        [Test]
        public void HeldWithoutDown_DoesNotStart_None()
        {
            var s = new HoldAverageSampler(0.5f);
            // markDown なしの markHeld=true は開始しない。
            var r = s.Tick(markDown: false, markHeld: true, dt: 0.1f, pointer: Vector3.one, out Vector3 avg);
            Assert.That(r, Is.EqualTo(HoldAverageSampler.Result.None));
            Assert.That(s.Active, Is.False, "Down が無いので開始しない");
            Assert.That(avg, Is.EqualTo(Vector3.zero));
        }

        [Test]
        public void Progress01_MonotonicZeroToOne_ZeroWhenInactive()
        {
            var s = new HoldAverageSampler(0.4f);
            Assert.That(s.Progress01, Is.EqualTo(0f), "非アクティブは 0");

            s.Tick(markDown: true, markHeld: true, dt: 0f, pointer: Vector3.zero, out _);
            float p0 = s.Progress01; // time 0

            s.Tick(markDown: false, markHeld: true, dt: 0.1f, pointer: Vector3.zero, out _);
            float p1 = s.Progress01; // 0.1/0.4 = 0.25
            s.Tick(markDown: false, markHeld: true, dt: 0.1f, pointer: Vector3.zero, out _);
            float p2 = s.Progress01; // 0.5

            Assert.That(p0, Is.EqualTo(0f).Within(1e-5f));
            Assert.That(p1, Is.EqualTo(0.25f).Within(1e-5f));
            Assert.That(p2, Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(p1, Is.GreaterThan(p0));
            Assert.That(p2, Is.GreaterThan(p1));

            // 到達で非アクティブ → 0 へ戻る。
            s.Tick(markDown: false, markHeld: true, dt: 0.1f, pointer: Vector3.zero, out _);
            s.Tick(markDown: false, markHeld: true, dt: 0.1f, pointer: Vector3.zero, out _);
            Assert.That(s.Progress01, Is.EqualTo(0f), "Captured 後は非アクティブ＝0");
        }

        [Test]
        public void Reset_ThenRestart_Works()
        {
            var s = new HoldAverageSampler(0.2f);
            var r = Drive(s, new Vector3(5f, 0f, 0f), dt: 0.1f, heldFrames: 2, out _);
            Assert.That(r, Is.EqualTo(HoldAverageSampler.Result.Captured));

            s.Reset();
            Assert.That(s.Active, Is.False);
            Assert.That(s.Progress01, Is.EqualTo(0f));

            // Reset 後に再開して再度キャプチャできる。
            var r2 = Drive(s, new Vector3(7f, 0f, 0f), dt: 0.1f, heldFrames: 2, out Vector3 avg2);
            Assert.That(r2, Is.EqualTo(HoldAverageSampler.Result.Captured), "Reset 後に再開できる");
            Assert.That(avg2.x, Is.EqualTo(7f).Within(1e-5f));
        }
    }
}
