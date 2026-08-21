#nullable enable
using FixedCamVr.Streaming;
using FixedCamVr.Streaming.Cg;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// <b>黒い波が体験者に応える</b>ときの決めごとを機械で守る（設計 E / F）。
    /// 「かっこいいか」は測れないが、**壊れ方**は測れる —
    /// 静止で死なない / 復帰の飛びで全開にならない / 手が取れなければ 0 へ落ちる。
    /// </summary>
    public class SwapEnergyLogicTests
    {
        private const float Dt = 1f / 90f;

        private static ShowBodyInput Body(Vector3 head, Vector3? left = null, Vector3? right = null)
            => new ShowBodyInput(true, head, 0f,
                                 left.HasValue, left ?? Vector3.zero,
                                 right.HasValue, right ?? Vector3.zero);

        private static SwapEnergyLogic Walk(float speed, float sec, out float lastEnergy)
        {
            var e = new SwapEnergyLogic();
            Vector3 p = Vector3.zero;
            int n = Mathf.RoundToInt(sec / Dt);
            for (int i = 0; i < n; i++)
            {
                p += new Vector3(speed * Dt, 0f, 0f);
                e.Tick(Dt, Body(p + Vector3.up * 1.6f));
            }
            lastEnergy = e.Energy01;
            return e;
        }

        [Test]
        public void StandingStill_IsNotDead()
        {
            var e = new SwapEnergyLogic();
            for (int i = 0; i < 300; i++) e.Tick(Dt, Body(new Vector3(0f, 1.6f, 0f)));

            Assert.AreEqual(0f, e.Energy01, 1e-3f, "立ち止まっていればエネルギーは 0");
            // 0089 の恐怖は「静かに蠢き続ける」こと。振幅を 0 にすると死んで見える。
            Assert.AreEqual(SwapEnergyLogic.AmpFloor, e.AmpMul, 1e-4f,
                            "静止でも振幅は下限（0.55）まで残る");
            Assert.AreEqual(SwapEnergyLogic.SpeedFloor, e.SpeedMul, 1e-4f);
        }

        [Test]
        public void WalkingFullSpeed_ReachesFullEnergy()
        {
            Walk(SwapEnergyLogic.BodyFullSpeed, 4f, out float e);
            Assert.Greater(e, 0.95f, "1.2 m/s で歩いたらエネルギーはほぼ全開");
            Walk(SwapEnergyLogic.BodyFullSpeed * 3f, 4f, out float fast);
            Assert.AreEqual(1f, fast, 1e-3f, "上は 1 で頭打ち（走っても荒れ続けない）");
        }

        [Test]
        public void Smoothing_LagsByHalfLife()
        {
            // 半減期 0.8 秒 ＝ 0.8 秒歩いた時点でおよそ半分。**遅れは演出**なので固定する。
            Walk(SwapEnergyLogic.BodyFullSpeed, SwapEnergyLogic.BodyHalfLifeSec, out float half);
            Assert.AreEqual(0.5f, half, 0.08f, "半減期ぶん歩いたらおよそ半分");
        }

        [Test]
        public void LongFrame_DoesNotSpikeEnergy()
        {
            // アプリの復帰・トラッキングの飛び。**立ち止まっている体験者が全開になってはいけない。**
            var e = new SwapEnergyLogic();
            e.Tick(Dt, Body(new Vector3(0f, 1.6f, 0f)));
            e.Tick(SwapEnergyLogic.MaxDtSec + 0.5f, Body(new Vector3(9f, 1.6f, 9f)));
            Assert.AreEqual(0f, e.Energy01, 1e-3f, "不連続のフレームでは速さを測らない");
        }

        [Test]
        public void Teleport_IsClampedBySpeedCeiling()
        {
            // recenter でワールドが飛ぶと数十 m/s が出る。切らないと半減期ぶん全開が居座る。
            var e = new SwapEnergyLogic();
            e.Tick(Dt, Body(new Vector3(0f, 1.6f, 0f)));
            e.Tick(Dt, Body(new Vector3(50f, 1.6f, 0f)));
            Assert.LessOrEqual(e.Energy01, 1f);
            float afterTeleport = e.Energy01;
            for (int i = 0; i < 300; i++) e.Tick(Dt, Body(new Vector3(50f, 1.6f, 0f)));
            Assert.Less(e.Energy01, afterTeleport, "止まれば下がる（飛びが居座らない）");
        }

        [Test]
        public void OnlyTheWavingHand_GetsHot()
        {
            var e = new SwapEnergyLogic();
            Vector3 head = new Vector3(0f, 1.6f, 0f);
            Vector3 left = new Vector3(-0.26f, 1.3f, 0f);
            for (int i = 0; i < 180; i++)
            {
                float t = i * Dt;
                Vector3 right = new Vector3(0.26f + Mathf.Sin(t * 10f) * 0.3f, 1.3f, 0f);
                e.Tick(Dt, Body(head, left, right));
            }
            Assert.Greater(e.RightHot01, 0.5f, "振った手は熱くなる");
            Assert.AreEqual(0f, e.LeftHot01, 1e-3f, "振っていない手は反応しない");
        }

        [Test]
        public void LostHand_FallsBackToZero()
        {
            // 手が取れない（コントローラ持ち・トラッキング切れ）→ 0 ＝ 設計 A〜D の絵へ自然に落ちる。
            var e = new SwapEnergyLogic();
            Vector3 head = new Vector3(0f, 1.6f, 0f);
            for (int i = 0; i < 180; i++)
            {
                float t = i * Dt;
                e.Tick(Dt, Body(head, null, new Vector3(Mathf.Sin(t * 10f) * 0.3f, 1.3f, 0f)));
            }
            Assert.Greater(e.RightHot01, 0.3f);

            for (int i = 0; i < 200; i++) e.Tick(Dt, Body(head));
            Assert.Less(e.RightHot01, 0.02f, "取れなくなったら 0 へ落ちる");
        }

        [Test]
        public void Reset_DoesNotCarryTheLastVisitor()
        {
            Walk(SwapEnergyLogic.BodyFullSpeed, 3f, out float e1);
            Assert.Greater(e1, 0.9f);

            var e = new SwapEnergyLogic();
            Vector3 p = Vector3.zero;
            for (int i = 0; i < 270; i++)
            {
                p += new Vector3(SwapEnergyLogic.BodyFullSpeed * Dt, 0f, 0f);
                e.Tick(Dt, Body(p + Vector3.up * 1.6f));
            }
            e.Reset();
            Assert.AreEqual(0f, e.Energy01, 1e-4f, "次の体験者へ速さを持ち越さない");
            Assert.AreEqual(SwapEnergyLogic.AmpFloor, e.AmpMul, 1e-4f);
        }

        [Test]
        public void NeutralEnergy_MeansEveryMultiplierIsOne()
        {
            // 入力を読まない構成（Editor プレビューの `-Set layers=` で E を切る）で使う中立値。
            Assert.AreEqual(1f, SwapEnergyLogic.NeutralEnergy, 1e-6f);
        }
    }
}
