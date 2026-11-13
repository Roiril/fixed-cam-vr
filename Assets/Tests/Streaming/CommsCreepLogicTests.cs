#nullable enable
using System.IO;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// ③a / ③b の髪の場（<see cref="CommsCreepLogic"/>）。焼いた版（<c>Resources/CommsFx/CurseCreepField.bytes</c>）を
    /// 実際に読んで、0270 の逐語（顔の半分・字に赤が残る）と命令の行が読めることを数える。
    /// </summary>
    public sealed class CommsCreepLogicTests
    {
        // ③b（日本語 3 行）の面の矩形。幅は顔の枠を含む全幅、字の帯は左端から 0.194m。
        private const float W = 0.955f, H = 0.20f;
        private static byte[] Field() => File.ReadAllBytes("Assets/Resources/CommsFx/CurseCreepField.bytes");

        private static float Cover(byte[] f, float reach, float x0, float x1, float y0, float y1)
        {
            int hit = 0, n = 0;
            for (float y = y0; y <= y1; y += 0.004f)
            for (float x = x0; x <= x1; x += 0.004f)
            {
                n++;
                if (CommsCreepLogic.Depth(f, x, y, 0f, 0f, W, H, reach) > 0f) hit++;
            }
            return hit / (float)n;
        }

        [Test]
        public void BakedField_HasTheExpectedSize_AndRootsAtTheBottom()
        {
            byte[] f = Field();
            Assert.AreEqual(CommsCreepLogic.FieldW * CommsCreepLogic.FieldH, f.Length);
            Assert.Less(CommsCreepLogic.SampleField(f, 0.3f, 0f), 0.05f, "根元は到達順 0");
            Assert.Greater(CommsCreepLogic.SampleField(f, 0.3f, 1f), 0.9f, "版の上端には届かない");
        }

        [Test]
        public void NoHairAtZero_EverythingSwallowedAtOne_AndItOnlyGrows()
        {
            byte[] f = Field();
            Assert.AreEqual(0f, Cover(f, 0f, -W / 2, W / 2, -H / 2, H / 2), 1e-6f);
            Assert.Greater(Cover(f, 1f, -W / 2, W / 2, -H / 2, H / 2), 0.98f);
            float prev = 0f;
            for (float r = 0.05f; r <= 1.0001f; r += 0.05f)
            {
                float c = Cover(f, r, -W / 2, W / 2, -H / 2, H / 2);
                Assert.GreaterOrEqual(c, prev - 1e-4f, $"寄り {r:F2} で髪が引いた");
                prev = c;
            }
        }

        [Test]
        public void Settled_HalfTheFaceIsUnderHair_AndTheTextColumnStaysMostlyClear()
        {
            byte[] f = Field();
            float reach = CommsRecoveryLogic.ResidueFloor;
            // 顔の枠（左端から 0.022〜0.172m）。
            float face = Cover(f, reach, -W / 2 + 0.022f, -W / 2 + 0.172f, -0.075f, 0.075f);
            Assert.That(face, Is.InRange(0.30f, 0.70f), "顔は半分ほどが髪の下（0270）");
            // 字の帯。
            float text = Cover(f, reach, -W / 2 + 0.194f, W / 2 - 0.03f, -H / 2 + 0.01f, H / 2 - 0.01f);
            Assert.That(text, Is.InRange(0.02f, 0.30f), "字には髪が掛かるが、読める");
            // 現れた瞬間はほぼ呑まれている。
            Assert.Greater(Cover(f, CommsRecoveryLogic.ResidueStart, -W / 2, W / 2, -H / 2, H / 2), 0.9f);
        }

        [Test]
        public void MissingField_MeansNoHair()
        {
            Assert.Less(CommsCreepLogic.Depth(null, 0f, 0f, 0f, 0f, W, H, 1f), 0f);
            Assert.Less(CommsCreepLogic.Depth(new byte[3], 0f, 0f, 0f, 0f, W, H, 1f), 0f);
        }
    }
}
