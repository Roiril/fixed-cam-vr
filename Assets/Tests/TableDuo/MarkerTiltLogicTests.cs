#nullable enable
using NUnit.Framework;
using TableDuoVr.Net;
using UnityEngine;

namespace TableDuoVr.Tests
{
    /// <summary>
    /// MarkerTiltLogic（ペン保持中の傾き計算）の EditMode テスト。GameObject 生成なしの純関数のみ。
    /// tipDistance は実運用のペン先距離 0.0923m、上限 45°。
    /// </summary>
    public class MarkerTiltLogicTests
    {
        private const float Tip = 0.0923f;
        private const float MaxPitch = 45f;

        [Test]
        public void ComputeAllowedPitch_AtSurface_IsZero()
        {
            // 保持点が面と同じ高さ = ペン先を下げられない = 水平
            float p = MarkerTiltLogic.ComputeAllowedPitchDeg(0.75f, 0.75f, Tip, MaxPitch);
            Assert.That(p, Is.EqualTo(0f).Within(1e-4f));
        }

        [Test]
        public void ComputeAllowedPitch_BelowSurface_IsZero()
        {
            // 面より低く持っている（clamp01 が負を 0 にする）
            float p = MarkerTiltLogic.ComputeAllowedPitchDeg(0.70f, 0.75f, Tip, MaxPitch);
            Assert.That(p, Is.EqualTo(0f).Within(1e-4f));
        }

        [Test]
        public void ComputeAllowedPitch_HighEnough_ClampsToMax()
        {
            // 保持点が面から tipDistance 以上高い = asin(1)=90° だが上限 45° にクランプ
            float p = MarkerTiltLogic.ComputeAllowedPitchDeg(0.75f + Tip + 0.05f, 0.75f, Tip, MaxPitch);
            Assert.That(p, Is.EqualTo(MaxPitch).Within(1e-4f));
        }

        [Test]
        public void ComputeAllowedPitch_MidHeight_MatchesAsin()
        {
            // ratio = 0.5 → asin(0.5) = 30°（上限 45 未満なのでそのまま返る）
            float heldY = 0.75f + Tip * 0.5f;
            float p = MarkerTiltLogic.ComputeAllowedPitchDeg(heldY, 0.75f, Tip, MaxPitch);
            Assert.That(p, Is.EqualTo(30f).Within(0.01f));
        }

        [Test]
        public void ComputeAllowedPitch_MonotonicallyIncreasesWithHeight()
        {
            float prev = -1f;
            for (float h = 0f; h <= Tip; h += Tip / 20f)
            {
                float p = MarkerTiltLogic.ComputeAllowedPitchDeg(0.75f + h, 0.75f, Tip, MaxPitch);
                Assert.That(p, Is.GreaterThanOrEqualTo(prev - 1e-4f), $"h={h} で単調増加が崩れた");
                prev = p;
            }
        }

        [Test]
        public void SmoothPitch_ConvergesToTarget()
        {
            float cur = 0f;
            const float target = 40f;
            for (int i = 0; i < 200; i++) cur = MarkerTiltLogic.SmoothPitch(cur, target, 1f / 60f);
            Assert.That(cur, Is.EqualTo(target).Within(0.01f), "十分な反復で target へ収束するはず");
        }

        [Test]
        public void SmoothPitch_ZeroDt_HoldsCurrent()
        {
            Assert.That(MarkerTiltLogic.SmoothPitch(12f, 40f, 0f), Is.EqualTo(12f).Within(1e-6f));
        }

        [Test]
        public void ComposeTiltedForward_HorizontalForward_TiltsDownByPitch()
        {
            // forward=+Z 水平・pitch 30° → dir=(0,-sin30,cos30)、heading=+Z
            var dir = MarkerTiltLogic.ComposeTiltedForward(Vector3.forward, 30f, Vector3.right, out var heading);
            Assert.That(heading, Is.EqualTo(Vector3.forward).Using(Vec3Comparer));
            Assert.That(dir.x, Is.EqualTo(0f).Within(1e-4f));
            Assert.That(dir.y, Is.EqualTo(-Mathf.Sin(30f * Mathf.Deg2Rad)).Within(1e-4f));
            Assert.That(dir.z, Is.EqualTo(Mathf.Cos(30f * Mathf.Deg2Rad)).Within(1e-4f));
        }

        [Test]
        public void ComposeTiltedForward_ZeroPitch_LiesFlatAlongHeading()
        {
            var dir = MarkerTiltLogic.ComposeTiltedForward(Vector3.forward, 0f, Vector3.right, out var heading);
            Assert.That(dir.y, Is.EqualTo(0f).Within(1e-4f), "俯角 0 なら水平（寝る）");
            Assert.That(dir, Is.EqualTo(heading).Using(Vec3Comparer));
        }

        [Test]
        public void ComposeTiltedForward_DegenerateForward_UsesLastHeading()
        {
            // 真上向き（XZ 射影が縮退）→ heading は lastHeading フォールバック
            var last = new Vector3(1f, 0f, 0f);
            var dir = MarkerTiltLogic.ComposeTiltedForward(Vector3.up, 20f, last, out var heading);
            Assert.That(heading, Is.EqualTo(last).Using(Vec3Comparer));
            float cos = Mathf.Cos(20f * Mathf.Deg2Rad);
            Assert.That(dir.x, Is.EqualTo(cos * last.x).Within(1e-4f));
            Assert.That(dir.z, Is.EqualTo(cos * last.z).Within(1e-4f));
        }

        private static readonly System.Collections.Generic.IEqualityComparer<Vector3> Vec3Comparer =
            new Vec3Approx();

        private sealed class Vec3Approx : System.Collections.Generic.IEqualityComparer<Vector3>
        {
            public bool Equals(Vector3 a, Vector3 b) => (a - b).sqrMagnitude < 1e-8f;
            public int GetHashCode(Vector3 v) => v.GetHashCode();
        }
    }
}
