#nullable enable
using NUnit.Framework;
using TableDuoVr.Net;
using UnityEngine;

namespace TableDuoVr.Tests
{
    /// <summary>OneEuroFilter（float / Vector3）の EditMode テスト。静止収束・高速追従・dt≤0 ガード。</summary>
    public class OneEuroFilterTests
    {
        private const float Dt = 1f / 60f;

        [Test]
        public void Float_FirstCall_ReturnsInput()
        {
            var f = new OneEuroFloatFilter();
            Assert.That(f.Filter(3.5f, Dt), Is.EqualTo(3.5f).Within(1e-6f));
            Assert.IsTrue(f.Initialized);
        }

        [Test]
        public void Float_ConstantInput_ConvergesToValue()
        {
            var f = new OneEuroFloatFilter();
            f.Filter(0f, Dt);
            float last = 0f;
            for (int i = 0; i < 300; i++) last = f.Filter(10f, Dt);
            Assert.That(last, Is.EqualTo(10f).Within(0.05f), "一定入力なら値へ収束する");
        }

        [Test]
        public void Float_FastMotion_TracksWithoutLargeLag()
        {
            // 速い一定速度のランプに対し、cutoff が上がって追従遅れが小さく収まる
            var f = new OneEuroFloatFilter();
            float x = 0f;
            float outv = 0f;
            for (int i = 0; i < 120; i++)
            {
                x += 0.05f;              // 3 m/s 相当（60fps）
                outv = f.Filter(x, Dt);
            }
            Assert.That(Mathf.Abs(x - outv), Is.LessThan(0.05f), "定常ランプでの遅れは 1 フレーム分程度に収まる");
        }

        [Test]
        public void Float_NonPositiveDt_HoldsLastValue()
        {
            var f = new OneEuroFloatFilter();
            f.Filter(5f, Dt);           // init → 5
            Assert.That(f.Filter(100f, 0f), Is.EqualTo(5f).Within(1e-6f), "dt=0 は直近値を保持");
            Assert.That(f.Filter(100f, -1f), Is.EqualTo(5f).Within(1e-6f), "dt<0 も保持");
        }

        [Test]
        public void Float_Uninitialized_NonPositiveDt_SeedsWithInput()
        {
            var f = new OneEuroFloatFilter();
            Assert.That(f.Filter(7f, 0f), Is.EqualTo(7f).Within(1e-6f), "未初期化 + dt≤0 は入力で初期化");
        }

        [Test]
        public void Float_Reset_ReinitializesOnNextFilter()
        {
            var f = new OneEuroFloatFilter();
            for (int i = 0; i < 50; i++) f.Filter(10f, Dt);
            f.Reset();
            Assert.That(f.Filter(-4f, Dt), Is.EqualTo(-4f).Within(1e-6f), "Reset 後の初回は入力を返す");
        }

        [Test]
        public void Vector3_ConstantInput_ConvergesPerComponent()
        {
            var f = new OneEuroFilter();
            var target = new Vector3(1f, -2f, 3f);
            f.Filter(Vector3.zero, Dt);
            Vector3 last = Vector3.zero;
            for (int i = 0; i < 300; i++) last = f.Filter(target, Dt);
            Assert.That((last - target).magnitude, Is.LessThan(0.05f));
        }

        [Test]
        public void Vector3_NonPositiveDt_HoldsLast()
        {
            var f = new OneEuroFilter();
            var v = new Vector3(2f, 2f, 2f);
            f.Filter(v, Dt);
            var held = f.Filter(new Vector3(9f, 9f, 9f), 0f);
            Assert.That((held - v).magnitude, Is.LessThan(1e-5f));
        }
    }
}
