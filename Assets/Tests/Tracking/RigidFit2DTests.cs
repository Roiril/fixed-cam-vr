#nullable enable
using FixedCamVr.Tracking;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Tracking.Tests
{
    /// <summary>
    /// RigidFit2D（course XZ ↔ world XZ の 2D Procrustes 剛体フィット）の純ロジック検証。
    /// N=2 で旧 2 点解と一致・N=3 の完全一致データで残差 0・1 点を故意にズラして該当残差が突出・
    /// 平行移動/回転の合成復元・退化（全点同一）で失敗、を固定する。
    /// </summary>
    public sealed class RigidFit2DTests
    {
        private const float Eps = 1e-3f;

        // CourseFrame と同じ順変換（w = origin + Euler(0,yaw,0)·course）で world 点を生成する。
        private static Vector2 Forward(Vector2 course, Vector2 originXZ, float yawDeg)
        {
            Vector3 r = Quaternion.Euler(0f, yawDeg, 0f) * new Vector3(course.x, 0f, course.y);
            return new Vector2(originXZ.x + r.x, originXZ.y + r.z);
        }

        // 旧 CourseRegistrationController の 2 点解を再現（現行一致の基準）。
        private static (Vector2 origin, float yaw) OldSolve2(Vector2 c1, Vector2 c2, Vector2 w1, Vector2 w2)
        {
            Vector2 dW = w2 - w1, dC = c2 - c1;
            float cross = dC.x * dW.y - dC.y * dW.x;
            float dot = dC.x * dW.x + dC.y * dW.y;
            float yaw = -Mathf.Atan2(cross, dot) * Mathf.Rad2Deg;
            Quaternion rot = Quaternion.Euler(0f, yaw, 0f);
            Vector3 r1 = rot * new Vector3(c1.x, 0f, c1.y);
            Vector3 r2 = rot * new Vector3(c2.x, 0f, c2.y);
            Vector2 o1 = new(w1.x - r1.x, w1.y - r1.z);
            Vector2 o2 = new(w2.x - r2.x, w2.y - r2.z);
            return ((o1 + o2) * 0.5f, yaw);
        }

        [Test]
        public void TwoPoints_MatchesLegacySolve_AndRecoversKnownTransform()
        {
            Vector2 c1 = new(-0.5f, 0.5f), c2 = new(0.5f, 0.5f);
            Vector2 origin = new(1.3f, -0.7f);
            float yaw = 37f;
            Vector2 w1 = Forward(c1, origin, yaw), w2 = Forward(c2, origin, yaw);

            var fit = RigidFit2D.Solve(new[] { c1, c2 }, new[] { w1, w2 });
            var (oldO, oldY) = OldSolve2(c1, c2, w1, w2);

            Assert.That(fit.ok, Is.True);
            // 現行 2 点解と一致
            Assert.That(Mathf.DeltaAngle(fit.yawDeg, oldY), Is.EqualTo(0f).Within(Eps));
            Assert.That((fit.originXZ - oldO).magnitude, Is.LessThan(Eps));
            // 既知変換を復元
            Assert.That(Mathf.DeltaAngle(fit.yawDeg, yaw), Is.EqualTo(0f).Within(Eps));
            Assert.That((fit.originXZ - origin).magnitude, Is.LessThan(Eps));
            Assert.That(fit.maxResidualM, Is.LessThan(Eps));
        }

        [Test]
        public void ThreePoints_ExactData_ZeroResidual_RecoversTransform()
        {
            var course = new[] { new Vector2(-0.6f, 0.4f), new Vector2(0.5f, 0.5f), new Vector2(0.1f, -0.7f) };
            Vector2 origin = new(-2.0f, 3.5f);
            float yaw = -115f;
            var world = new Vector2[course.Length];
            for (int i = 0; i < course.Length; i++) world[i] = Forward(course[i], origin, yaw);

            var fit = RigidFit2D.Solve(course, world);

            Assert.That(fit.ok, Is.True);
            Assert.That(Mathf.DeltaAngle(fit.yawDeg, yaw), Is.EqualTo(0f).Within(Eps));
            Assert.That((fit.originXZ - origin).magnitude, Is.LessThan(Eps));
            Assert.That(fit.maxResidualM, Is.LessThan(1e-4f));
            Assert.That(fit.rmsResidualM, Is.LessThan(1e-4f));
            Assert.That(fit.residualsM.Length, Is.EqualTo(3));
        }

        [Test]
        public void OneOffsetPoint_IsFlaggedAsWorst_WithSpikeResidual()
        {
            var course = new[] { new Vector2(-0.6f, 0.4f), new Vector2(0.5f, 0.5f), new Vector2(0.1f, -0.7f) };
            Vector2 origin = new(0.2f, 0.1f);
            float yaw = 20f;
            var world = new Vector2[course.Length];
            for (int i = 0; i < course.Length; i++) world[i] = Forward(course[i], origin, yaw);
            // 点 1（index 1）を故意に 0.3m ずらす
            world[1] += new Vector2(0.3f, 0f);

            var fit = RigidFit2D.Solve(course, world);

            Assert.That(fit.ok, Is.True);
            Assert.That(fit.worstIndex, Is.EqualTo(1));
            Assert.That(fit.residualsM[1], Is.EqualTo(fit.maxResidualM).Within(1e-6f));
            Assert.That(fit.residualsM[1], Is.GreaterThan(fit.residualsM[0]));
            Assert.That(fit.residualsM[1], Is.GreaterThan(fit.residualsM[2]));
            Assert.That(fit.maxResidualM, Is.GreaterThan(0.1f)); // ズレが残差として突出する
        }

        [Test]
        public void PureTranslation_NoRotation_IsRecovered()
        {
            var course = new[] { new Vector2(-0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -0.5f) };
            Vector2 origin = new(4.2f, -1.1f);
            var world = new Vector2[course.Length];
            for (int i = 0; i < course.Length; i++) world[i] = Forward(course[i], origin, 0f);

            var fit = RigidFit2D.Solve(course, world);

            Assert.That(fit.ok, Is.True);
            Assert.That(Mathf.DeltaAngle(fit.yawDeg, 0f), Is.EqualTo(0f).Within(Eps));
            Assert.That((fit.originXZ - origin).magnitude, Is.LessThan(Eps));
            Assert.That(fit.maxResidualM, Is.LessThan(1e-4f));
        }

        [Test]
        public void PureRotation_AboutOrigin_IsRecovered()
        {
            var course = new[] { new Vector2(-0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.2f, -0.6f) };
            float yaw = 90f;
            var world = new Vector2[course.Length];
            for (int i = 0; i < course.Length; i++) world[i] = Forward(course[i], Vector2.zero, yaw);

            var fit = RigidFit2D.Solve(course, world);

            Assert.That(fit.ok, Is.True);
            Assert.That(Mathf.DeltaAngle(fit.yawDeg, yaw), Is.EqualTo(0f).Within(Eps));
            Assert.That(fit.originXZ.magnitude, Is.LessThan(Eps));
            Assert.That(fit.maxResidualM, Is.LessThan(1e-4f));
        }

        [Test]
        public void DegenerateCoincidentCoursePoints_ReturnsFailure()
        {
            var course = new[] { new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f) };
            var world = new[] { new Vector2(0f, 0f), new Vector2(1f, 2f), new Vector2(3f, -1f) };

            var fit = RigidFit2D.Solve(course, world);

            Assert.That(fit.ok, Is.False);
            Assert.That(fit.worstIndex, Is.EqualTo(-1));
        }

        [Test]
        public void TooFewPoints_OrLengthMismatch_ReturnsFailure()
        {
            Assert.That(RigidFit2D.Solve(new[] { Vector2.zero }, new[] { Vector2.zero }).ok, Is.False);
            Assert.That(RigidFit2D.Solve(
                new[] { Vector2.zero, Vector2.one },
                new[] { Vector2.zero }).ok, Is.False);
        }
    }
}
