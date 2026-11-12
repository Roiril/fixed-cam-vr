#nullable enable

using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    public class ScreenMultiplyLayoutTests
    {
        private static ScreenSeat[] Seats => ScreenMultiplyLayout.Seats;

        // make-screen-wall.py の BAND_DELAY の最大（霧の奥の段）と RANK_JITTER の幅。
        private const float MaxBandDelay = 0.18f;
        private const float RankJitterSpan = 0.06f;

        private static float Wrap(float deg) => Mathf.Repeat(deg + 180f, 360f) - 180f;

        /// <summary>0 番との方位差 0..180。</summary>
        private static float AzFromFirst(in ScreenSeat s) => Mathf.Abs(Wrap(s.azDeg - Seats[0].azDeg));

        [Test]
        public void FirstSeat_LeadsTheOrder_AndIsAboveTheMainScreen()
        {
            Assert.That(Seats[0].rank, Is.EqualTo(0f));
            Assert.That(Seats.Skip(1).All(s => s.rank > 0f), Is.True);
            // メインは水平 ±30.6°・上端 +10.4°。最初の 1 枚はその外の視界（右上）に出る。
            float bottom = Seats[0].elDeg - Seats[0].widthDeg / CrtReferenceScreen.CaseAspect * 0.5f;
            Assert.That(bottom, Is.GreaterThan(10.4f));
            Assert.That(Seats[0].azDeg, Is.GreaterThan(0f));
            Assert.That(Seats[0].band, Is.EqualTo(ScreenMultiplyLayout.BandUpper));
        }

        [Test]
        public void AllSeats_AreBehindTheMainScreen()
        {
            // メインは 2m。前に出ると画面を隠す。
            Assert.That(Seats.Min(s => s.distM), Is.GreaterThan(2.3f));
            Assert.That(Seats.Length, Is.LessThanOrEqualTo(ScreenMultiplyLayout.MaxSeats));
        }

        [Test]
        public void Faces_LookAtTheHead_WithOnlyAHandPlacedRoll()
        {
            foreach (ScreenSeat s in Seats)
            {
                Assert.That(s.tiltYawDeg, Is.EqualTo(0f));
                Assert.That(s.tiltPitchDeg, Is.EqualTo(0f));
                Assert.That(Mathf.Abs(s.rollDeg), Is.LessThanOrEqualTo(1.2f + 1e-4f));
            }
        }

        [Test]
        public void Faces_DoNotOverlap()
        {
            // 角度の上で、手前の段どうし・霧の奥の段どうしは重ならない（霧の奥は手前の面の後ろへ少し隠れてよい）。
            for (int i = 0; i < Seats.Length; i++)
            for (int j = i + 1; j < Seats.Length; j++)
            {
                ScreenSeat a = Seats[i], b = Seats[j];
                if (ScreenMultiplyLayout.IsFar(a) != ScreenMultiplyLayout.IsFar(b)) continue;
                float dx = Mathf.Abs(Wrap(a.azDeg - b.azDeg)) * Mathf.Cos((a.elDeg + b.elDeg) * 0.5f * Mathf.Deg2Rad);
                float dy = Mathf.Abs(a.elDeg - b.elDeg);
                bool overlap = dx < (a.widthDeg + b.widthDeg) * 0.5f - 0.05f
                               && dy < (a.widthDeg + b.widthDeg) / CrtReferenceScreen.CaseAspect * 0.5f - 0.05f;
                Assert.That(overlap, Is.False, $"席 {i} と {j} が重なる");
            }
        }

        [Test]
        public void Bands_WrapTheHead_AndLeaveTheMainScreenClear()
        {
            ScreenSeat[] eye = Seats.Where(s => s.band == ScreenMultiplyLayout.BandEye).ToArray();
            ScreenSeat[] upper = Seats.Where(s => s.band == ScreenMultiplyLayout.BandUpper).ToArray();
            ScreenSeat[] top = Seats.Where(s => s.band == ScreenMultiplyLayout.BandTop).ToArray();
            Assert.That(eye.Length, Is.GreaterThan(10));
            Assert.That(upper.Length, Is.GreaterThan(10));
            Assert.That(top.Length, Is.InRange(10, 14));
            // 目の高さの帯はメインの後ろ（方位 ±26°）を空ける。
            foreach (ScreenSeat s in eye)
            {
                float halfAz = s.widthDeg * 0.5f / Mathf.Cos(s.elDeg * Mathf.Deg2Rad);
                Assert.That(Mathf.Abs(s.azDeg) - halfAz, Is.GreaterThanOrEqualTo(26f - 0.05f));
            }
            // 帯 0 と 1 は 360° を巻く（前後左右に面がある）。最上の帯は方位 ±110° 以内。
            foreach (ScreenSeat[] band in new[] { eye, upper })
            foreach (float az in new[] { 90f, -90f, 160f, -160f })
                Assert.That(band.Min(s => Mathf.Abs(Wrap(s.azDeg - az))), Is.LessThan(25f), $"方位 {az} に面が無い");
            Assert.That(top.All(s => Mathf.Abs(s.azDeg) <= 110f), Is.True);
        }

        [Test]
        public void SameWidth_NeverThreeInARow()
        {
            foreach (int band in new[] { ScreenMultiplyLayout.BandEye, ScreenMultiplyLayout.BandUpper })
            {
                float[] w = Seats.Where(s => s.band == band).OrderBy(s => Mathf.Repeat(s.azDeg, 360f))
                                 .Select(s => s.widthDeg).ToArray();
                Assert.That(w.Distinct().Count(), Is.EqualTo(3), "幅は 3 種類を混ぜる");
                for (int i = 0; i + 2 < w.Length; i++)
                    Assert.That(w[i] == w[i + 1] && w[i + 1] == w[i + 2], Is.False, $"帯 {band} で同じ幅が 3 枚続く");
            }
        }

        [Test]
        public void FarTier_ContinuesIntoTheFog_AndStaysDimmerThanTheNearOnes()
        {
            ScreenSeat[] far = Seats.Where(s => ScreenMultiplyLayout.IsFar(s)).ToArray();
            ScreenSeat[] near = Seats.Where(s => !ScreenMultiplyLayout.IsFar(s)).ToArray();
            Assert.That(far.Length, Is.GreaterThan(20), "霧の奥の段が無い");
            Assert.That(near.Length, Is.GreaterThan(30));
            Assert.That(far.All(s => s.band == ScreenMultiplyLayout.BandFar), Is.True);
            // 奥の段は手前の段より小さく見える。
            Assert.That(far.Max(s => s.widthDeg), Is.LessThan(near.Min(s => s.widthDeg)));
            // 霧を抜けて届く光は、奥の段の平均が手前の段の平均の 1/4 以下。
            float farLight = far.Average(s => ScreenFogLogic.Transmit(s.distM));
            float nearLight = near.Average(s => ScreenFogLogic.Transmit(s.distM));
            Assert.That(farLight, Is.LessThan(nearLight * 0.25f));
        }

        [Test]
        public void Fog_LeavesTheMainScreenClear_AndEatsTheDistance()
        {
            Assert.That(ScreenFogLogic.Transmit(2f), Is.EqualTo(1f), "メインスクリーンの距離で霧が掛かっている");
            Assert.That(ScreenFogLogic.Transmit(4f), Is.LessThan(ScreenFogLogic.Transmit(3f)));
            Assert.That(ScreenFogLogic.Transmit(10f), Is.InRange(0.01f, 0.12f), "いちばん奥が見えないか、明るすぎる");
        }

        [Test]
        public void SeatOpen_MatchesTheEyeClock()
        {
            Assert.That(ScreenMultiplyLayout.SeatOpen(0, 0.7f, 0f, AnomalyEyesLogic.SwarmSpan), Is.EqualTo(0.7f));
            for (int i = 1; i < Seats.Length; i += 7)
                Assert.That(ScreenMultiplyLayout.SeatOpen(i, 1f, 0.5f, AnomalyEyesLogic.SwarmSpan),
                            Is.EqualTo(AnomalyEyesLogic.EyeOpen(0.5f, Seats[i].rank)).Within(1e-5f));
        }

        [Test]
        public void Rank_Delay_Tint_StayInTheirRanges()
        {
            foreach (ScreenSeat s in Seats)
            {
                Assert.That(s.rank, Is.InRange(0f, 1f));
                Assert.That(s.delay, Is.InRange(0, ScreenMultiplyLayout.DelaySlots - 1));
                Assert.That(s.tint, Is.InRange(0, ScreenMultiplyLayout.Tints.Length - 1));
                Assert.That(s.band, Is.InRange(0, ScreenMultiplyLayout.BandCount - 1));
            }
        }

        [Test]
        public void Rank_GrowsWithTheAzimuthFromTheFirst()
        {
            // 波は 0 番から左右へ広がる。帯の遅れとゆらぎの幅を超えて遠い面は、必ず後に点く。
            for (int i = 1; i < Seats.Length; i++)
            for (int j = 1; j < Seats.Length; j++)
            {
                float da = AzFromFirst(Seats[i]) / 180f - AzFromFirst(Seats[j]) / 180f;
                bool sameBand = Seats[i].band == Seats[j].band;
                float margin = (sameBand ? RankJitterSpan : MaxBandDelay + RankJitterSpan) + 0.01f;
                if (da > margin)
                    Assert.That(Seats[i].rank, Is.GreaterThan(Seats[j].rank), $"席 {i} が席 {j} より先に点く");
            }
            // 左右の波は真後ろの側で出会う（いちばん遅い面は 0 番の反対側）。
            Assert.That(Seats.Where(s => s.rank > 0.9f).All(s => AzFromFirst(s) > 120f), Is.True);
        }

        [Test]
        public void Delay_GrowsWithTheAzimuthFromTheFirst()
        {
            // 0 番から方位が離れるほど前の画（180° で 15 枚前）。隣の面で 1〜2 枚ずつ渡る。
            foreach (ScreenSeat s in Seats)
                Assert.That(s.delay, Is.EqualTo(15f * AzFromFirst(s) / 180f).Within(0.51f));
            for (int i = 0; i < Seats.Length; i++)
            for (int j = 0; j < Seats.Length; j++)
                if (AzFromFirst(Seats[i]) > AzFromFirst(Seats[j]))
                    Assert.That(Seats[i].delay, Is.GreaterThanOrEqualTo(Seats[j].delay));
        }

        [Test]
        public void Drift_TurnsNeighbouringBandsOppositeWays_Slowly()
        {
            float eye = ScreenMultiplyLayout.DriftRateDegPerSec(ScreenMultiplyLayout.BandEye);
            float upper = ScreenMultiplyLayout.DriftRateDegPerSec(ScreenMultiplyLayout.BandUpper);
            float top = ScreenMultiplyLayout.DriftRateDegPerSec(ScreenMultiplyLayout.BandTop);
            Assert.That(Mathf.Sign(eye), Is.Not.EqualTo(Mathf.Sign(upper)), "目の高さと上の帯が同じ向きに回る");
            Assert.That(Mathf.Sign(upper), Is.Not.EqualTo(Mathf.Sign(top)), "上と最上の帯が同じ向きに回る");
            foreach (float r in new[] { eye, upper, top })
            {
                Assert.That(r, Is.Not.EqualTo(0f));
                Assert.That(Mathf.Abs(r), Is.LessThanOrEqualTo(1.2f), "酔う速さ");
            }
            Assert.That(ScreenMultiplyLayout.DriftRateDegPerSec(ScreenMultiplyLayout.BandFar), Is.EqualTo(0f));
        }

        [Test]
        public void Drift_StopsUnderHorrorRelief_AndMovesTheSeat()
        {
            bool before = HorrorRelief.Enabled;
            try
            {
                HorrorRelief.Select(false);
                ScreenSeat first = Seats[0];
                float deg = ScreenMultiplyLayout.DriftDeg(first.band, 10f);
                Assert.That(deg, Is.EqualTo(ScreenMultiplyLayout.DriftRateDegPerSec(first.band) * 10f).Within(1e-4f));
                Vector3 moved = ScreenMultiplyLayout.DriftedCenter(first, 10f);
                float az0 = Mathf.Atan2(first.Center.x, first.Center.z) * Mathf.Rad2Deg;
                float az1 = Mathf.Atan2(moved.x, moved.z) * Mathf.Rad2Deg;
                Assert.That(Mathf.DeltaAngle(az0, az1), Is.EqualTo(deg).Within(1e-3f), "方位が回った角だけ動いていない");
                Assert.That(moved.y, Is.EqualTo(first.Center.y).Within(1e-4f), "鉛直軸のまわりに回っていない");

                HorrorRelief.Select(true);
                Assert.That(ScreenMultiplyLayout.DriftDeg(first.band, 10f), Is.EqualTo(0f));
                Assert.That(ScreenMultiplyLayout.DriftedCenter(first, 10f), Is.EqualTo(first.Center));
            }
            finally
            {
                HorrorRelief.Select(before);
            }
        }

        [Test]
        public void Tint_StartsWarm_AndSettlesToTheSeatColour()
        {
            Assert.That(ScreenMultiplyLayout.TintAt(0, 0f), Is.EqualTo(ScreenMultiplyLayout.WarmTint));
            Assert.That(ScreenMultiplyLayout.TintAt(0, ScreenMultiplyLayout.WarmSettleSec),
                        Is.EqualTo(ScreenMultiplyLayout.Tints[0]));
            Assert.That(ScreenMultiplyLayout.TintAt(1, 9f), Is.EqualTo(ScreenMultiplyLayout.Tints[1]));
            // 席の色に暖色は無い（暖かいのは波の先頭だけ）。
            foreach (Color c in ScreenMultiplyLayout.Tints) Assert.That(c.b, Is.GreaterThanOrEqualTo(c.r));
        }

        [Test]
        public void CountOpen_FollowsTheClock_OneThenAFewThenAll()
        {
            Assert.That(ScreenMultiplyLayout.CountOpen(0f, 0f), Is.EqualTo(0));
            Assert.That(ScreenMultiplyLayout.CountOpen(1f, 0f), Is.EqualTo(1));
            int ripple = ScreenMultiplyLayout.CountOpen(1f, AnomalyEyesLogic.SwarmRippleField);
            Assert.That(ripple, Is.InRange(2, 8), "さざめきは数枚");
            Assert.That(ScreenMultiplyLayout.CountOpen(1f, 1f), Is.EqualTo(Seats.Length));
        }

        [Test]
        public void NewestOpen_IsTheLastOneLit()
        {
            Assert.That(ScreenMultiplyLayout.NewestOpen(0f), Is.EqualTo(0));
            int last = System.Array.FindIndex(Seats, s => s.rank == Seats.Max(x => x.rank));
            Assert.That(ScreenMultiplyLayout.NewestOpen(1f), Is.EqualTo(last));
            // 最後に点くのは真後ろの側。
            Assert.That(AzFromFirst(Seats[last]), Is.GreaterThan(140f));
        }

        [Test]
        public void Meshes_HaveAVertexPerSeatPart_AndCarryTheBand()
        {
            Mesh surfaces = ScreenMultiplyLayout.BuildSurfaces();
            Mesh glow = ScreenMultiplyLayout.BuildGlow();
            try
            {
                Assert.That(surfaces.vertexCount, Is.EqualTo(Seats.Length * 20));
                Assert.That(glow.vertexCount, Is.EqualTo(Seats.Length * 4));
                var uv3 = new System.Collections.Generic.List<Vector4>();
                surfaces.GetUVs(3, uv3);
                Assert.That(uv3.Count, Is.EqualTo(surfaces.vertexCount), "帯の番号が頂点に無い");
                // 1 席 20 頂点（箱 16 ＋ 面 4）。面の頂点は陰り 1、帯は席の帯。
                for (int s = 0; s < Seats.Length; s++)
                {
                    Assert.That(uv3[s * 20 + 16].x, Is.EqualTo((float)Seats[s].band));
                    Assert.That(uv3[s * 20 + 16].y, Is.EqualTo(1f));
                }
                glow.GetUVs(3, uv3);
                Assert.That(uv3.Count, Is.EqualTo(glow.vertexCount));
            }
            finally
            {
                Object.DestroyImmediate(surfaces);
                Object.DestroyImmediate(glow);
            }
        }
    }
}
