#nullable enable
using FixedCamVr.Streaming.Cg;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 2 ボーン IK（肩→肘→手首）の不変条件。人形の腕が一瞬でも飛ぶ / 消し飛ぶと演出が壊れるので、
    /// 「届く範囲では目標に一致」「届かない範囲では発散せずクランプ」「退化入力で NaN を出さない」を固定する。
    /// </summary>
    public sealed class TwoBoneIkTests
    {
        private const float Upper = 0.30f;
        private const float Lower = 0.25f;

        private static bool IsFinite(Vector3 v)
            => !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z)
                 || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));

        [Test]
        public void 届く目標なら手首が目標に一致する()
        {
            Vector3 root = new Vector3(0.2f, 1.4f, 0f);
            Vector3 target = root + new Vector3(0.1f, -0.35f, 0.15f);   // 0.4m ≒ 届く

            TwoBoneIk.Solve(root, target, root + Vector3.down + Vector3.back, Upper, Lower,
                            out Vector3 joint, out Vector3 end);

            Assert.That(Vector3.Distance(end, target), Is.LessThan(1e-3f));
            Assert.That(Vector3.Distance(root, joint), Is.EqualTo(Upper).Within(1e-3f));
            Assert.That(Vector3.Distance(joint, end), Is.EqualTo(Lower).Within(1e-3f));
        }

        [Test]
        public void 届かない目標はまっすぐ伸ばしてクランプする()
        {
            Vector3 root = Vector3.zero;
            Vector3 target = new Vector3(0f, -3f, 0f);                   // 腕の長さの 5 倍以上

            TwoBoneIk.Solve(root, target, root + Vector3.back, Upper, Lower,
                            out Vector3 joint, out Vector3 end);

            float reach = (Upper + Lower) * TwoBoneIk.MaxReachRatio;
            Assert.That(end.magnitude, Is.EqualTo(reach).Within(1e-3f));
            Assert.That(Vector3.Dot(end.normalized, Vector3.down), Is.GreaterThan(0.99f)); // 目標方向を向く
            Assert.That(Vector3.Distance(root, joint), Is.EqualTo(Upper).Within(1e-3f));
        }

        [Test]
        public void 骨長ゼロでも_NaN_を出さない()
        {
            TwoBoneIk.Solve(Vector3.zero, new Vector3(0.1f, -0.2f, 0f), Vector3.back, 0f, 0f,
                            out Vector3 joint, out Vector3 end);

            Assert.IsTrue(IsFinite(joint));
            Assert.IsTrue(IsFinite(end));
        }

        [Test]
        public void 目標が肩に重なっても_NaN_を出さない()
        {
            Vector3 root = new Vector3(1f, 1f, 1f);

            TwoBoneIk.Solve(root, root, root, Upper, Lower, out Vector3 joint, out Vector3 end);

            Assert.IsTrue(IsFinite(joint));
            Assert.IsTrue(IsFinite(end));
            // 退化時は「腕を下ろす」向きへ倒す（暴れない）。
            Assert.That(end.y, Is.LessThan(root.y));
        }

        [Test]
        public void 肘は_pole_の側へ曲がる()
        {
            Vector3 root = Vector3.zero;
            Vector3 target = new Vector3(0f, -0.4f, 0f);

            TwoBoneIk.Solve(root, target, root + Vector3.back, Upper, Lower, out Vector3 back, out _);
            TwoBoneIk.Solve(root, target, root + Vector3.forward, Upper, Lower, out Vector3 fwd, out _);

            Assert.That(back.z, Is.LessThan(0f));
            Assert.That(fwd.z, Is.GreaterThan(0f));
        }
    }
}
