#nullable enable
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 黒へ落とす・黒から上げる曲線（<see cref="PerceptualFade"/>）。端点・単調・見た目の明るさ・
    /// 「黒の直前の 1 刻みで一気に落ちない」ことを固定する。
    /// </summary>
    public sealed class PerceptualFadeTests
    {
        private static float Perceived(float dim) => Mathf.Pow(1f - dim, 1f / PerceptualFade.DisplayGamma);

        [Test]
        public void Darken_Endpoints_AreIdentityAndBlack()
        {
            Assert.AreEqual(0f, PerceptualFade.Darken(0f), 1e-6f);
            Assert.AreEqual(1f, PerceptualFade.Darken(1f), 1e-6f);
            Assert.AreEqual(0f, PerceptualFade.Darken(-3f), 1e-6f, "範囲外は丸める");
            Assert.AreEqual(1f, PerceptualFade.Darken(7f), 1e-6f, "範囲外は丸める");
        }

        [Test]
        public void Darken_IsMonotonic()
        {
            float prev = -1f;
            for (int i = 0; i <= 100; i++)
            {
                float d = PerceptualFade.Darken(i / 100f);
                Assert.GreaterOrEqual(d, prev - 1e-6f, $"level {i / 100f} で戻った");
                prev = d;
            }
        }

        [Test]
        public void Darken_PerceivedBrightness_IsOneMinusLevelSquared()
        {
            foreach (float l in new[] { 0.1f, 0.25f, 0.5f, 0.75f, 0.9f })
                Assert.AreEqual(1f - l * l, Perceived(PerceptualFade.Darken(l)), 1e-4f, $"level {l}");
        }

        [Test]
        public void Darken_LastTickBeforeBlack_IsSmallerThanLinear()
        {
            // 0.07s の暗転を 90Hz で刻むと 6 刻み。最後の 1 刻み（5/6 → 1）で落ちる見た目の明るさ。
            float l = 5f / 6f;
            float linearStep = Perceived(l);                       // 旧: 係数を時間に比例
            float curvedStep = Perceived(PerceptualFade.Darken(l)); // 新
            Assert.Less(curvedStep, linearStep, "黒の直前の落ち込みが線形より小さいこと");
        }

        [Test]
        public void Reveal_Endpoints_AndMonotonic()
        {
            Assert.AreEqual(0f, PerceptualFade.Reveal(0f), 1e-6f);
            Assert.AreEqual(1f, PerceptualFade.Reveal(1f), 1e-6f);
            float prev = -1f;
            for (int i = 0; i <= 100; i++)
            {
                float a = PerceptualFade.Reveal(i / 100f);
                Assert.GreaterOrEqual(a, prev - 1e-6f);
                prev = a;
            }
        }

        [Test]
        public void Reveal_PerceivedBrightness_FollowsSmoothstep()
        {
            foreach (float p in new[] { 0.1f, 0.3f, 0.5f, 0.7f, 0.9f })
            {
                float s = p * p * (3f - 2f * p);
                float perceived = Mathf.Pow(PerceptualFade.Reveal(p), 1f / PerceptualFade.DisplayGamma);
                Assert.AreEqual(s, perceived, 1e-4f, $"progress {p}");
            }
        }
    }
}
