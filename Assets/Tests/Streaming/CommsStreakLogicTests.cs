#nullable enable
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// ③a / ③b の縦の筋の場（<see cref="CommsStreakLogic"/>）。量が 0 なら何も無く、量とともに増え、
    /// 顔の側に多く字の所に少ないことを数える（<c>canon/LEDGER.md</c> 0356 / 0358）。
    /// </summary>
    public sealed class CommsStreakLogicTests
    {
        // ③b（日本語 3 行）の面の矩形。幅は顔の枠を含む全幅、字の帯は左端から 0.194m。
        private const float W = 0.955f, H = 0.20f;

        private static float Cover(float reach, float x0, float x1, float y0, float y1)
        {
            int hit = 0, n = 0;
            for (float y = y0; y <= y1; y += 0.004f)
            for (float x = x0; x <= x1; x += 0.0011f)
            {
                n++;
                if (CommsStreakLogic.CoveredByFixedLayer(x, y, 0f, 0f, W, H, reach)) hit++;
            }
            return hit / (float)n;
        }

        [Test]
        public void NothingAtZero_AndItOnlyGrows()
        {
            Assert.AreEqual(0f, Cover(0f, -W / 2, W / 2, -H / 2, H / 2), 1e-6f);
            float prev = 0f;
            for (float r = 0.05f; r <= 1.0001f; r += 0.05f)
            {
                float c = Cover(r, -W / 2, W / 2, -H / 2, H / 2);
                Assert.GreaterOrEqual(c, prev - 1e-4f, $"量 {r:F2} で人形の面が引いた");
                prev = c;
            }
        }

        [Test]
        public void JustAppeared_MostOfThePanelIsTaken_AndSettled_ItHasReceded()
        {
            float start = Cover(CommsRecoveryLogic.ResidueStart, -W / 2, W / 2, -H / 2, H / 2);
            float settled = Cover(CommsRecoveryLogic.ResidueFloor, -W / 2, W / 2, -H / 2, H / 2);
            // 列の 3 割強はスイの側の光で覆わないので、止まっている層だけなら 0.27 前後（2026-11-14 実測）。
            Assert.Greater(start, 0.20f, "現れた直後は広く人形の面");
            Assert.Greater(settled, 0.02f, "落ち着いても人形の面は残る");
            Assert.Less(settled, start * 0.6f, "スイが押し返した分だけ引いている");
        }

        [Test]
        public void Settled_TheFaceSideIsTakenMoreThanTheTextColumn()
        {
            float reach = CommsRecoveryLogic.ResidueFloor;
            float face = Cover(reach, -W / 2 + 0.022f, -W / 2 + 0.172f, -H / 2, H / 2);
            float text = Cover(reach, -W / 2 + 0.22f * W, -W / 2 + 0.62f * W, -H / 2, H / 2);
            Assert.Greater(face, text, "顔の側に多く、字の所は少ない");
        }

        [Test]
        public void TheLiveLayer_Reshuffles_AndOutsideThePanelIsNeverTaken()
        {
            int differ = 0;
            for (float x = -W / 2; x <= W / 2; x += 0.0011f)
            {
                bool a = CommsStreakLogic.Covered(x, H / 2 - 0.01f, 0f, 0f, W, H, 0.9f, 0.1f);
                bool b = CommsStreakLogic.Covered(x, H / 2 - 0.01f, 0f, 0f, W, H, 0.9f, 3.1f);
                if (a != b) differ++;
            }
            Assert.Greater(differ, 0, "組み替わる層は時計で変わる");
            Assert.AreNotEqual(CommsStreakLogic.Epoch(0.1f), CommsStreakLogic.Epoch(3.1f));
            Assert.IsFalse(CommsStreakLogic.Covered(0f, H / 2 + 0.01f, 0f, 0f, W, H, 1f, 0f));
            Assert.IsFalse(CommsStreakLogic.Covered(-W / 2 - 0.01f, 0f, 0f, 0f, W, H, 1f, 0f));
        }
    }
}
