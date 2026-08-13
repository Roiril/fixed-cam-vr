#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 封印の箱が床に落とす影の形（<see cref="SealedBoxShadowLogic"/>）の検証。
    ///
    /// 出どころは <c>canon/LEDGER.md</c> 0024（「床に影を斜め方向くらいで落とすようにしてほしい。
    /// リアル感がなさすぎる」）。ここで守るのは 4 つ:
    ///   (A) <b>影は斜めに伸びる</b>（真下に隠れたら見えない ＝ 依頼が満たされない）
    ///   (B) <b>板は影を必ず収める</b>（足りないと縁で切れて直線が画に出る）
    ///   (C) 接地点が濃く、先端が薄い（半影は遮蔽物から離れるほど広がる）
    ///   (D) <b>板の外では 0</b>（板を越えて影が続いているように見えない）
    ///
    /// ⚠ <see cref="SealedBoxShadowLogic.Coverage"/> は <c>SealedBoxShadow.shader</c> と<b>同じ式</b>。
    /// ここが通っても、シェーダ側を直し忘れれば画は変わらない（対で直すこと）。
    /// </summary>
    public sealed class SealedBoxShadowLogicTests
    {
        // show.json の床そのもの（歩ける範囲 1.8 四方）＋ 箱の高さ 2.4m。
        private static readonly Vector2 Half = new Vector2(0.9f, 0.9f);
        private const float HeightM = 2.4f;

        private static Vector2 DefaultSweep() => SealedBoxShadowLogic.Sweep(
            SealedBoxShadowLogic.DefaultYawDeg, SealedBoxShadowLogic.DefaultElevationDeg, HeightM);

        // ---- (A) 斜めに伸びる ---------------------------------------------------

        [Test]
        public void Sweep_ReachesBeyondTheFootprint_AndIsDiagonal()
        {
            Vector2 s = DefaultSweep();
            Assert.Greater(s.magnitude, Half.x + 0.5f,
                $"影が footprint の外へ出ていない（長さ {s.magnitude:F2}m）。箱の下に隠れて見えない");
            // 「斜め」＝ どちらの軸にも寄り切っていない。軸に平行だと壁と平行な帯になって
            // 「箱が床に立っている」ではなく「床に黒い帯が引いてある」に見える。
            float ax = Mathf.Abs(s.x), az = Mathf.Abs(s.y);
            Assert.Greater(Mathf.Min(ax, az) / Mathf.Max(ax, az), 0.35f,
                $"斜めになっていない（x {s.x:F2} / z {s.y:F2}）");
        }

        [Test]
        public void Sweep_GetsLongerAsTheLightGetsLower()
        {
            float lowSun = SealedBoxShadowLogic.Sweep(0f, 20f, HeightM).magnitude;
            float highSun = SealedBoxShadowLogic.Sweep(0f, 70f, HeightM).magnitude;
            Assert.Greater(lowSun, highSun);
            // 真上へ振り切っても発散しない（上限を切っていないと影が数十 m になる）。
            Assert.Less(SealedBoxShadowLogic.Sweep(0f, 89f, HeightM).magnitude, 20f);
            Assert.Less(SealedBoxShadowLogic.Sweep(0f, 0f, HeightM).magnitude, 20f,
                "光の高さ 0（真横）でも影の長さが有限であること");
        }

        [Test]
        public void Sweep_YawZeroPointsAlongPlusZ_AndNinetyAlongPlusX()
        {
            // 向きの規約はシェーダ・配置（Euler(90, yaw, 0)）と対。ここが狂うと影だけ別方向へ出る。
            Vector2 z = SealedBoxShadowLogic.Sweep(0f, 45f, 1f);
            Assert.AreEqual(0f, z.x, 1e-4f);
            Assert.Greater(z.y, 0f);

            Vector2 x = SealedBoxShadowLogic.Sweep(90f, 45f, 1f);
            Assert.Greater(x.x, 0f);
            Assert.AreEqual(0f, x.y, 1e-4f);
        }

        // ---- (B) 板は影を必ず収める ---------------------------------------------

        [Test]
        public void QuadCoversTheWholeShadow_IncludingThePenumbra()
        {
            Vector2 s = DefaultSweep();
            Vector2 quad = SealedBoxShadowLogic.QuadSizeM(Half, s);
            Vector2 c = SealedBoxShadowLogic.QuadCenterM(s);

            // 板の縁を 200 点なめて、そこに影が残っていないことを見る。
            // 残っていたら、実機では**そこで影が直線に切れる**。
            for (int i = 0; i < 200; i++)
            {
                float u = -0.5f + i / 199f;
                foreach (Vector2 edge in new[]
                {
                    new Vector2(u * quad.x, -0.5f * quad.y),
                    new Vector2(u * quad.x, 0.5f * quad.y),
                    new Vector2(-0.5f * quad.x, u * quad.y),
                    new Vector2(0.5f * quad.x, u * quad.y),
                })
                {
                    float cov = SealedBoxShadowLogic.Coverage(edge + c, Half, s);
                    Assert.Less(cov, 0.01f, $"板の縁 {edge + c} に影が残っている（cov {cov:F3}）");
                }
            }
        }

        [Test]
        public void QuadGrowsWithTheSweep()
        {
            Vector2 shortS = SealedBoxShadowLogic.Sweep(45f, 70f, HeightM);
            Vector2 longS = SealedBoxShadowLogic.Sweep(45f, 20f, HeightM);
            Vector2 a = SealedBoxShadowLogic.QuadSizeM(Half, shortS);
            Vector2 b = SealedBoxShadowLogic.QuadSizeM(Half, longS);
            Assert.Greater(b.x, a.x);
            Assert.Greater(b.y, a.y);
        }

        // ---- (C) 接地点が濃く、先端が薄い ---------------------------------------

        [Test]
        public void Coverage_IsFullUnderTheBox()
        {
            Vector2 s = DefaultSweep();
            Assert.AreEqual(1f, SealedBoxShadowLogic.Coverage(Vector2.zero, Half, s), 1e-3f,
                "箱の真下が影になっていない");
        }

        [Test]
        public void Coverage_FadesAlongTheSweep()
        {
            Vector2 s = DefaultSweep();
            Vector2 dir = s.normalized;
            // footprint の縁のすぐ外（接地帯）と、掃いた先端の手前。
            float near = SealedBoxShadowLogic.Coverage(dir * (Half.x + 0.05f), Half, s);
            float far = SealedBoxShadowLogic.Coverage(s * 0.92f, Half, s);
            Assert.Greater(near, far, "接地点より先端の方が濃い（半影が逆向き）");
            Assert.Greater(near, 0.6f, "接地帯が薄い（箱が浮いて見える）");
            Assert.Greater(far, 0.05f, "先端で消え切っている（影が短く見える）");
        }

        [Test]
        public void Coverage_IsZeroOnTheOppositeSide()
        {
            Vector2 s = DefaultSweep();
            Vector2 back = -s.normalized * (Half.x + 0.6f);
            Assert.Less(SealedBoxShadowLogic.Coverage(back, Half, s), 0.01f,
                "光が来る側にも影が出ている（両方向に伸びたら光源が 2 つあることになる）");
        }

        [Test]
        public void Coverage_NeverExceedsOne()
        {
            Vector2 s = DefaultSweep();
            for (float x = -4f; x <= 4f; x += 0.11f)
            {
                for (float z = -4f; z <= 4f; z += 0.11f)
                {
                    float cov = SealedBoxShadowLogic.Coverage(new Vector2(x, z), Half, s);
                    Assert.GreaterOrEqual(cov, 0f);
                    Assert.LessOrEqual(cov, 1f);
                }
            }
        }

        // ---- 縮退 ---------------------------------------------------------------

        [Test]
        public void Coverage_DegradesToTheFootprintWhenTheLightIsStraightUp()
        {
            // 掃きが 0 でも 0 除算しない（真上の光 ＝ 箱の下だけが影）。
            var s = Vector2.zero;
            Assert.AreEqual(1f, SealedBoxShadowLogic.Coverage(Vector2.zero, Half, s), 1e-3f);
            Assert.Less(SealedBoxShadowLogic.Coverage(new Vector2(2f, 2f), Half, s), 0.01f);
        }

        [Test]
        public void SdBox_MatchesTheObviousCases()
        {
            var h = new Vector2(1f, 2f);
            Assert.AreEqual(-1f, SealedBoxShadowLogic.SdBox(Vector2.zero, h), 1e-4f);
            Assert.AreEqual(0f, SealedBoxShadowLogic.SdBox(new Vector2(1f, 0f), h), 1e-4f);
            Assert.AreEqual(1f, SealedBoxShadowLogic.SdBox(new Vector2(2f, 0f), h), 1e-4f);
            Assert.AreEqual(Mathf.Sqrt(2f), SealedBoxShadowLogic.SdBox(new Vector2(2f, 3f), h), 1e-4f);
        }
    }
}
