#nullable enable
using NUnit.Framework;
using TableDuoVr.Net;
using UnityEngine;

namespace TableDuoVr.Tests
{
    /// <summary>
    /// PenGripLogic（ペン/消しゴム保持中の姿勢合成）の EditMode テスト。GameObject 生成なしの純関数のみ。
    /// gripBack=0.025 / tipDistance=0.0923 / 俯角 +15° / 下向きクランプ 80°。
    /// </summary>
    public class PenGripLogicTests
    {
        private const float GripBack = 0.025f;
        private const float Tip = 0.0923f;
        private const float ExtraPitch = 15f;
        private const float MaxDown = 80f;

        [Test]
        public void ComputePenDir_HorizontalHand_AddsExtraPitchDown()
        {
            // 手首→ピンチが水平（+Z）→ 俯角 15° だけ下を向く
            var dir = PenGripLogic.ComputePenDir(
                new Vector3(0f, 1f, 0f), new Vector3(0f, 1f, 0.1f), ExtraPitch, MaxDown, Vector3.forward, out bool deg);
            Assert.IsFalse(deg);
            Assert.That(dir.magnitude, Is.EqualTo(1f).Within(1e-4f));
            Assert.That(dir.y, Is.EqualTo(-Mathf.Sin(ExtraPitch * Mathf.Deg2Rad)).Within(1e-4f));
            Assert.That(dir.z, Is.EqualTo(Mathf.Cos(ExtraPitch * Mathf.Deg2Rad)).Within(1e-4f));
            Assert.That(dir.x, Is.EqualTo(0f).Within(1e-4f));
        }

        [Test]
        public void ComputePenDir_SteepDown_ClampsToMaxDown()
        {
            // ピンチ点が手首の真下近く → 俯角が急。+15° してから 80° にクランプ
            var dir = PenGripLogic.ComputePenDir(
                new Vector3(0f, 1f, 0f), new Vector3(0f, 0.5f, 0.02f), ExtraPitch, MaxDown, Vector3.forward, out _);
            Assert.That(dir.magnitude, Is.EqualTo(1f).Within(1e-4f));
            Assert.That(dir.y, Is.EqualTo(-Mathf.Sin(MaxDown * Mathf.Deg2Rad)).Within(1e-3f));
        }

        [Test]
        public void ComputePenDir_WristEqualsPinch_IsDegenerateAndFinite()
        {
            var p = new Vector3(0.3f, 0.9f, -0.1f);
            var dir = PenGripLogic.ComputePenDir(p, p, ExtraPitch, MaxDown, Vector3.forward, out bool deg);
            Assert.IsTrue(deg);
            Assert.That(dir.magnitude, Is.EqualTo(1f).Within(1e-4f), "縮退でも単位ベクトルを返す");
            Assert.IsFalse(float.IsNaN(dir.x) || float.IsNaN(dir.y) || float.IsNaN(dir.z));
            // fallback=+Z を俯角 15° 下へ倒した向き
            Assert.That(dir.y, Is.EqualTo(-Mathf.Sin(ExtraPitch * Mathf.Deg2Rad)).Within(1e-4f));
        }

        [Test]
        public void ComposeTipPose_NoClamp_OriginIsPinchMinusBackAlongDir()
        {
            // 面が十分下 → クランプ無し。原点 = ピンチ点 + dir·(gripBack − tipDistance)
            var pinch = new Vector3(0f, 1f, 0.1f);
            var dir = new Vector3(0f, -Mathf.Sin(15f * Mathf.Deg2Rad), Mathf.Cos(15f * Mathf.Deg2Rad));
            var pos = PenGripLogic.ComposeTipPose(pinch, dir, GripBack, Tip, 0f, out bool clamped);
            Assert.IsFalse(clamped);
            var expected = pinch + dir * (GripBack - Tip);
            Assert.That((pos - expected).magnitude, Is.LessThan(1e-4f));
            // ペン先 = 原点 + dir·tipDistance = ピンチ点 + dir·gripBack
            var tip = pos + dir * Tip;
            Assert.That((tip - (pinch + dir * GripBack)).magnitude, Is.LessThan(1e-4f));
        }

        [Test]
        public void ComposeTipPose_TipBelowSurface_LiftsSoTipSitsOnSurface()
        {
            // ペン先が面より下 → 全体を持ち上げてペン先を面高へ載せる
            var pinch = new Vector3(0f, 0.05f, 0.02f);
            var dir = new Vector3(0f, -0.7f, 0.714f).normalized; // 下向き
            const float surfaceY = 0.05f;
            var pos = PenGripLogic.ComposeTipPose(pinch, dir, GripBack, Tip, surfaceY, out bool clamped);
            Assert.IsTrue(clamped);
            var tip = pos + dir * Tip;
            Assert.That(tip.y, Is.EqualTo(surfaceY).Within(1e-4f), "クランプ後はペン先が面高に載る");
        }

        [Test]
        public void ComposePenPose_WristEqualsPinch_ReturnsFinite()
        {
            var p = new Vector3(0.1f, 0.8f, 0.2f);
            var pos = PenGripLogic.ComposePenPose(p, p, GripBack, Tip, 0f, ExtraPitch, MaxDown, out var fwd, out _);
            Assert.IsFalse(float.IsNaN(pos.x) || float.IsNaN(pos.y) || float.IsNaN(pos.z));
            Assert.That(fwd.magnitude, Is.EqualTo(1f).Within(1e-4f));
        }

        [Test]
        public void ComposeFlatPose_PlacesUnderPinch_YawOnlyForward()
        {
            var wrist = new Vector3(0f, 1f, 0f);
            var pinch = new Vector3(0.1f, 0.9f, 0.05f);
            const float holdDrop = 0.02f;
            const float surfaceY = 0.5f;
            var pos = PenGripLogic.ComposeFlatPose(wrist, pinch, holdDrop, surfaceY, Vector3.forward, out var fwd);
            Assert.That(pos.x, Is.EqualTo(pinch.x).Within(1e-5f));
            Assert.That(pos.z, Is.EqualTo(pinch.z).Within(1e-5f));
            Assert.That(pos.y, Is.EqualTo(pinch.y - holdDrop).Within(1e-5f));
            Assert.That(fwd.y, Is.EqualTo(0f).Within(1e-5f), "yaw のみ（水平）");
            Assert.That(fwd.magnitude, Is.EqualTo(1f).Within(1e-4f));
        }

        [Test]
        public void ComposeFlatPose_BottomClampedToSurface()
        {
            var wrist = new Vector3(0f, 1f, 0f);
            var pinch = new Vector3(0f, 0.51f, 0f);
            var pos = PenGripLogic.ComposeFlatPose(wrist, pinch, 0.02f, 0.5f, Vector3.forward, out _);
            Assert.That(pos.y, Is.EqualTo(0.5f).Within(1e-5f), "底面が面を割らない");
        }
    }
}
