#nullable enable
using NUnit.Framework;
using TableDuoVr.Hands;
using UnityEngine;

namespace TableDuoVr.Tests
{
    /// <summary>
    /// リターゲット幾何（HandRetarget.Solve / WristFrame・public static・純）を固定する。
    /// Solve は式A（親相対バインド差分）の代数恒等、WristFrame は指方向×甲法線フレームの正規直交性と
    /// 左右で cross 順が入れ替わる掌性・退化フォールバックを pin する。
    /// </summary>
    public class HandRetargetGeometryTests
    {
        private const float Tol = 1e-3f;

        private static void AssertVec(Vector3 expected, Vector3 actual, string msg)
            => Assert.Less(Vector3.Distance(expected, actual), Tol, $"{msg} 期待={expected} 実際={actual}");

        // --- Solve ---

        [Test]
        public void Solve_EqualsLiveTimesInverseBindTimesVarBind()
        {
            var live = Quaternion.Euler(10f, 20f, 30f);
            var bind = Quaternion.Euler(5f, 15f, 25f);
            var varBind = Quaternion.Euler(40f, 50f, 60f);

            var expected = live * Quaternion.Inverse(bind) * varBind;
            var got = HandRetarget.Solve(live, bind, varBind);
            Assert.Less(Quaternion.Angle(expected, got), 0.01f, "Solve = live * inv(bind) * varBind");
        }

        [Test]
        public void Solve_BindEqualsVarBind_ReturnsLive()
        {
            var live = Quaternion.Euler(10f, 20f, 30f);
            var bind = Quaternion.Euler(5f, 15f, 25f);
            var got = HandRetarget.Solve(live, bind, bind);
            Assert.Less(Quaternion.Angle(live, got), 0.01f, "bind==varBind なら live");
        }

        // --- WristFrame ---

        [Test]
        public void WristFrame_OrthonormalWithForwardAlongMiddle()
        {
            var wrist = Vector3.zero;
            var index1 = new Vector3(1f, 0f, 0f);
            var middle1 = new Vector3(0f, 0f, 1f);
            var pinky1 = new Vector3(-1f, 0f, 0f);

            Quaternion q = HandRetarget.WristFrame(wrist, index1, middle1, pinky1, true);
            Vector3 fwd = q * Vector3.forward;
            Vector3 up = q * Vector3.up;

            AssertVec(new Vector3(0f, 0f, 1f), fwd, "fwd = (middle-wrist) 正規化");
            Assert.Less(Mathf.Abs(Vector3.Dot(fwd, up)), Tol, "fwd ⟂ up（正規直交）");
            Assert.AreEqual(1f, fwd.magnitude, Tol);
            Assert.AreEqual(1f, up.magnitude, Tol);
        }

        [Test]
        public void WristFrame_HandednessFlipsUpCrossOrder()
        {
            var wrist = Vector3.zero;
            var index1 = new Vector3(1f, 0f, 0f);
            var middle1 = new Vector3(0f, 0f, 1f);
            var pinky1 = new Vector3(-1f, 0f, 0f);

            Vector3 upR = HandRetarget.WristFrame(wrist, index1, middle1, pinky1, true) * Vector3.up;
            Vector3 upL = HandRetarget.WristFrame(wrist, index1, middle1, pinky1, false) * Vector3.up;

            AssertVec(new Vector3(0f, -1f, 0f), upR, "右手 up = cross(fwd, lateral)");
            AssertVec(new Vector3(0f, 1f, 0f), upL, "左手 up = cross(lateral, fwd)（符号反転）");
        }

        [Test]
        public void WristFrame_ForwardDegenerateReturnsIdentity()
        {
            var wrist = Vector3.zero;
            // middle==wrist → fwd 退化
            Quaternion q = HandRetarget.WristFrame(wrist, new Vector3(1, 0, 0), wrist, new Vector3(-1, 0, 0), true);
            Assert.Less(Quaternion.Angle(Quaternion.identity, q), 0.01f, "fwd 退化 → identity");
        }

        [Test]
        public void WristFrame_LateralDegenerateFallsBackToUp()
        {
            var wrist = Vector3.zero;
            var middle1 = new Vector3(0f, 0f, 1f);
            // index1==pinky1 → lateral 退化 → up フォールバック Vector3.up
            var same = new Vector3(1f, 0f, 0f);
            Quaternion q = HandRetarget.WristFrame(wrist, same, middle1, same, true);
            Quaternion expected = Quaternion.LookRotation(new Vector3(0f, 0f, 1f), Vector3.up);
            Assert.Less(Quaternion.Angle(expected, q), 0.01f, "lateral 退化 → up フォールバック");
        }
    }
}
