#nullable enable
using FixedCamVr.Streaming.Cg;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// CG 人形を実映像に重ねるための**像空間の対応付け**を固定する。
    ///
    /// ここが狂うと「姿勢を完璧に測っても人形が合わない」状態になる。実際、2026-07-27 の監査では
    /// (1) 水平画角を Unity の垂直 FOV へそのまま渡していた（4:3 で約 25% ずれ）
    /// (2) 光の向きがワールド固定で、トラッキング原点の向き次第で部屋に対する陰影が変わっていた
    /// の 2 件が確定した。両方ともここで数値として固定する。
    /// 設計の正本: .claude/plans/2026-07-27_cg-compositing-rebuild.md
    /// </summary>
    public sealed class CgProjectionTests
    {
        // ---- 水平 FOV → 垂直 FOV ----

        [Test]
        public void Fov_SquareAspect_IsIdentity()
        {
            // 1:1 なら水平と垂直は同じ。変換式の恒等元。
            Assert.AreEqual(70f, ShowCgLayer.HorizontalToVerticalFovDeg(70f, 1f), 1e-3f);
        }

        [Test]
        public void Fov_FourThree_NarrowsVertically()
        {
            // 4:3 で水平 90° → 垂直 2*atan(tan(45°)/1.3333) = 73.74°
            float v = ShowCgLayer.HorizontalToVerticalFovDeg(90f, 4f / 3f);
            Assert.AreEqual(73.7398f, v, 1e-3f);
            Assert.Less(v, 90f, "横長の画では垂直画角は水平より狭い");
        }

        [Test]
        public void Fov_RoundTrip_MatchesTangentRelation()
        {
            // 定義そのもの: tan(h/2) = aspect * tan(v/2)
            const float aspect = 16f / 9f;
            float v = ShowCgLayer.HorizontalToVerticalFovDeg(100f, aspect);
            float lhs = Mathf.Tan(100f * 0.5f * Mathf.Deg2Rad);
            float rhs = aspect * Mathf.Tan(v * 0.5f * Mathf.Deg2Rad);
            Assert.AreEqual(lhs, rhs, 1e-4f);
        }

        [Test]
        public void Fov_WiderAspect_GivesNarrowerVertical()
        {
            // 同じレンズ（水平画角一定）でも、横長の画ほど垂直画角は狭くなる。
            float v43 = ShowCgLayer.HorizontalToVerticalFovDeg(80f, 4f / 3f);
            float v169 = ShowCgLayer.HorizontalToVerticalFovDeg(80f, 16f / 9f);
            Assert.Less(v169, v43);
        }

        [Test]
        public void Fov_DegenerateAspect_DoesNotExplode()
        {
            // アスペクトが未確定（映像未受信）でも NaN / 無限大を出さない。
            float v = ShowCgLayer.HorizontalToVerticalFovDeg(70f, 0f);
            Assert.IsFalse(float.IsNaN(v));
            Assert.IsFalse(float.IsInfinity(v));
        }

        // ---- course 空間の光の向き → ワールド ----

        [Test]
        public void Light_StraightUp_IsWorldUp()
        {
            Vector3 d = ShowCgLayer.CourseLightDirToWorld(0f, 90f, 0f);
            Assert.AreEqual(0f, d.x, 1e-3f);
            Assert.AreEqual(1f, d.y, 1e-3f);
            Assert.AreEqual(0f, d.z, 1e-3f);
        }

        [Test]
        public void Light_ZeroYaw_PointsAlongCoursePlusZ()
        {
            // 方位角 0・仰角 0 = course の +Z 方向から光が来る（yaw の基準を固定する）。
            Vector3 d = ShowCgLayer.CourseLightDirToWorld(0f, 0f, 0f);
            Assert.AreEqual(0f, d.x, 1e-3f);
            Assert.AreEqual(0f, d.y, 1e-3f);
            Assert.AreEqual(1f, d.z, 1e-3f);
        }

        [Test]
        public void Light_YawIncreases_TowardPlusX()
        {
            // +90° で +X。卓のフロアマップの向きハンドル（sin/cos）と同じ規約。
            Vector3 d = ShowCgLayer.CourseLightDirToWorld(90f, 0f, 0f);
            Assert.AreEqual(1f, d.x, 1e-3f);
            Assert.AreEqual(0f, d.z, 1e-3f);
        }

        [Test]
        public void Light_FollowsCourseYaw()
        {
            // **これが修正の核心**: 部屋（course）が実空間で回っていれば、光もその分だけ回る。
            // 旧実装はワールド固定で、登録のたびに部屋に対する光の向きが変わっていた。
            Vector3 a = ShowCgLayer.CourseLightDirToWorld(30f, 20f, 0f);
            Vector3 b = ShowCgLayer.CourseLightDirToWorld(30f, 20f, 45f);
            Vector3 expected = Quaternion.Euler(0f, 45f, 0f) * a;
            Assert.AreEqual(expected.x, b.x, 1e-3f);
            Assert.AreEqual(expected.y, b.y, 1e-3f);
            Assert.AreEqual(expected.z, b.z, 1e-3f);
        }

        [Test]
        public void Light_IsAlwaysNormalized()
        {
            foreach (float pitch in new[] { -80f, 0f, 45f, 89f })
                foreach (float yaw in new[] { -170f, 0f, 123f })
                {
                    Vector3 d = ShowCgLayer.CourseLightDirToWorld(yaw, pitch, 17f);
                    Assert.AreEqual(1f, d.magnitude, 1e-3f, $"yaw={yaw} pitch={pitch}");
                }
        }

        [Test]
        public void Light_ExtremePitch_IsClamped()
        {
            // 真上（90°超）を渡しても縮退して NaN にならない。
            Vector3 d = ShowCgLayer.CourseLightDirToWorld(0f, 180f, 0f);
            Assert.IsFalse(float.IsNaN(d.x) || float.IsNaN(d.y) || float.IsNaN(d.z));
            Assert.AreEqual(1f, d.magnitude, 1e-3f);
        }
    }
}
