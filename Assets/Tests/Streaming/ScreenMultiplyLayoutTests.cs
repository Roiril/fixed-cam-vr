#nullable enable

using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    public class ScreenMultiplyLayoutTests
    {
        private static ScreenSeat[] Seats => ScreenMultiplyLayout.Seats;

        [Test]
        public void FirstSeat_LeadsTheOrder_AndIsAboveTheMainScreen()
        {
            Assert.That(Seats[0].rank, Is.EqualTo(0f));
            Assert.That(Seats.Skip(1).All(s => s.rank > 0f), Is.True);
            // メインは水平 ±30.6°・上端 +10.4°。最初の 1 枚はその外の視界に出る。
            float bottom = Seats[0].elDeg - Seats[0].widthDeg / CrtReferenceScreen.CaseAspect * 0.5f;
            Assert.That(bottom, Is.GreaterThan(10.4f));
        }

        [Test]
        public void AllSeats_AreBehindTheMainScreen()
        {
            // メインは 2m。前に出ると画面を隠す。
            Assert.That(Seats.Min(s => s.distM), Is.GreaterThan(2.3f));
        }

        [Test]
        public void FarTier_ContinuesIntoTheFog_AndStaysDimmerThanTheNearOnes()
        {
            ScreenSeat[] far = Seats.Where(s => ScreenMultiplyLayout.IsFar(s)).ToArray();
            ScreenSeat[] near = Seats.Where(s => !ScreenMultiplyLayout.IsFar(s)).ToArray();
            Assert.That(far.Length, Is.GreaterThan(20), "霧の奥の段が無い");
            Assert.That(near.Length, Is.GreaterThan(60));
            // 奥の段は手前の段より小さく見える。
            Assert.That(far.Max(s => s.widthDeg), Is.LessThan(near.Min(s => s.widthDeg)));
            // 霧を抜けて届く光は、奥の段の平均が手前の段の平均の 1/4 以下。
            float farLight = far.Average(s => ScreenFogLogic.Transmit(s.distM));
            float nearLight = near.Average(s => ScreenFogLogic.Transmit(s.distM));
            Assert.That(farLight, Is.LessThan(nearLight * 0.25f));
            // 席の数は、シェーダへ配る表に収まる。
            Assert.That(Seats.Length, Is.LessThanOrEqualTo(ScreenMultiplyLayout.MaxSeats));
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
            }
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
        }

        [Test]
        public void Meshes_HaveAVertexPerSeatPart()
        {
            Mesh surfaces = ScreenMultiplyLayout.BuildSurfaces();
            Mesh glow = ScreenMultiplyLayout.BuildGlow();
            try
            {
                Assert.That(surfaces.vertexCount, Is.EqualTo(Seats.Length * 20));
                Assert.That(glow.vertexCount, Is.EqualTo(Seats.Length * 4));
            }
            finally
            {
                Object.DestroyImmediate(surfaces);
                Object.DestroyImmediate(glow);
            }
        }
    }
}
