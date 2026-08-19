#nullable enable
using System.Collections.Generic;
using FixedCamVr.Streaming;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// <b>入れ替わりのほどけの決めごとを機械で守る。</b> 数値の良し悪しではなく、
    /// ユーザーの言葉（`canon/LEDGER.md` 0089 / 0090）がそのまま段になっていることを固定する:
    /// 「体験者を徐々にオーバーレイするノイズを走らせ、ノイズが人型になって体験者はノイズに覆われて
    /// 見えなくなり、その後にノイズの人型が徐々に人形サイズになり、ノイズが晴れたら人形になる」
    /// ＋「線に分解されて人形へ吸い込まれるような」。
    /// </summary>
    public class SwapMorphLogicTests
    {
        private const float Dt = 1f / 60f;
        private const float HumanH = 1.70f;
        private const float DollH = 0.40f;

        private static List<SwapMorphLogic.Sample> RunAll(SwapMorphLogic.Dir dir, float sec = 2.6f)
        {
            var logic = new SwapMorphLogic();
            bool toDoll = dir == SwapMorphLogic.Dir.ToDoll;
            logic.Begin(dir, sec, toDoll ? HumanH : DollH, toDoll ? DollH : HumanH);
            var all = new List<SwapMorphLogic.Sample>();
            for (int i = 0; i < 10000 && logic.Active; i++) all.Add(logic.Tick(Dt));
            return all;
        }

        private static readonly SwapMorphLogic.Dir[] BothWays =
            { SwapMorphLogic.Dir.ToDoll, SwapMorphLogic.Dir.ToHuman };

        [Test]
        public void Unravel_ThenDrawIn_ThenFade_InThatOrder()
        {
            List<SwapMorphLogic.Sample> all = RunAll(SwapMorphLogic.Dir.ToDoll);
            int covered = all.FindIndex(s => s.justCovered);
            Assert.Greater(covered, 0, "ほどけ切る前にほどける段が要る");

            // ほどける段: 背丈は動かない（人型が固まる前に縮み始めない）。
            for (int i = 0; i < covered; i++)
                Assert.AreEqual(HumanH, all[i].heightM, 1e-3f, $"ほどける段 i={i} で背丈が動いた");

            // ほどけ切った後にだけ縮む。
            int shrank = all.FindIndex(s => s.heightM < HumanH - 1e-3f);
            Assert.Greater(shrank, covered, "ほどけ切る前に縮み始めている（体験者が縮んで見える）");

            // 実体が出るのは縮み切った後。
            int settled = all.FindIndex(s => s.real > 0.001f);
            Assert.Greater(settled, shrank, "縮む前に実体が出ている");
            Assert.AreEqual(DollH, all[settled].heightM, 1e-3f, "晴れ始めた時点で人形の大きさでない");
        }

        [Test]
        public void Knot_IsFullyOpaque_WhenTheScreenSwitches()
        {
            // ⚠⚠ **ここが「体験者が見えなくなる」の実体。** もつれが 1 でなければ、
            //    差し替えの瞬間に映像の中の体験者が糸の隙間から見えている。
            foreach (SwapMorphLogic.Dir dir in BothWays)
            {
                SwapMorphLogic.Sample at = RunAll(dir).Find(s => s.justCovered);
                Assert.GreaterOrEqual(at.cover, 0.999f, $"{dir}: 前線がほどけ切っていない");
                Assert.GreaterOrEqual(at.knot, 0.999f, $"{dir}: もつれが下を隠し切っていない");
            }
        }

        [Test]
        public void JustCovered_FiresExactlyOnce()
        {
            foreach (SwapMorphLogic.Dir dir in BothWays)
                Assert.AreEqual(1, RunAll(dir).FindAll(s => s.justCovered).Count,
                                $"{dir}: 画面の差し替えは 1 回でなければならない");
        }

        [Test]
        public void JustSettling_FiresOnce_AfterTheFigureFinishedShrinking()
        {
            // ⚠⚠ 姿を人形へ替えてよい唯一の瞬間 — **縮み切っていて、まだもつれに覆われている**。
            //    ほどけ切った瞬間（justCovered）に替えると、**縮んでいる間ずっと人形の形**になる。
            //    人形を人の背丈へ引き伸ばしても人型には見えない（2026-08-19 に絵で確かめた）。
            foreach (SwapMorphLogic.Dir dir in BothWays)
            {
                List<SwapMorphLogic.Sample> all = RunAll(dir);
                Assert.AreEqual(1, all.FindAll(s => s.justSettling).Count, $"{dir}");

                int settling = all.FindIndex(s => s.justSettling);
                int covered = all.FindIndex(s => s.justCovered);
                Assert.Greater(settling, covered, $"{dir}: ほどけ切る前に姿を替えたら見えてしまう");
                Assert.Greater(all[settling].knot, 0.3f, $"{dir}: もつれが薄いので姿の差し替えが見える");

                float endH = dir == SwapMorphLogic.Dir.ToDoll ? DollH : HumanH;
                Assert.AreEqual(endH, all[settling].heightM, 1e-2f,
                                $"{dir}: 縮み切る前に姿を替えている（形が変わったことが見える）");
            }
        }

        [Test]
        public void Thread_RisesThenIsDrawnIn_AndNeverVanishesMidway()
        {
            // 「線に分解されて人形へ吸い込まれる」— 吸い込まれる段で糸が消えると
            // 「集まって人形になった」ではなく「消えてから人形が出た」に見える。
            foreach (SwapMorphLogic.Dir dir in BothWays)
            {
                List<SwapMorphLogic.Sample> all = RunAll(dir);
                int covered = all.FindIndex(s => s.justCovered);
                int settling = all.FindIndex(s => s.justSettling);

                Assert.AreEqual(1f, all[covered].thread, 1e-2f, $"{dir}: ほどけ切った所で糸が出切っていない");
                Assert.AreEqual(SwapMorphLogic.ThreadAtPull, all[settling].thread, 1e-2f,
                                $"{dir}: 吸い込み切った所で糸が残っていない");
                Assert.AreEqual(0f, all[all.Count - 1].thread, 1e-3f, $"{dir}: 最後まで糸が残っている");

                // 単調: ほどける段で増え、それ以降は減るだけ（行きつ戻りつしない）。
                for (int i = 1; i <= covered; i++)
                    Assert.GreaterOrEqual(all[i].thread, all[i - 1].thread - 1e-4f, $"{dir} i={i}");
                for (int i = covered + 1; i < all.Count; i++)
                    Assert.LessOrEqual(all[i].thread, all[i - 1].thread + 1e-4f, $"{dir} i={i}");
            }
        }

        [Test]
        public void ToDoll_StartsInvisible_AndEndsAsTheDoll()
        {
            List<SwapMorphLogic.Sample> all = RunAll(SwapMorphLogic.Dir.ToDoll);
            // ⚠ 始まりは CG が 1 画素も出てはいけない（映像の中の本物の体験者が立っている）。
            Assert.AreEqual(0f, all[0].real, 1e-3f, "始まりに CG の実体が出ている");
            Assert.Less(all[0].knot, 0.05f, "始まりからもつれが濃い");

            SwapMorphLogic.Sample last = all[all.Count - 1];
            Assert.IsTrue(last.justFinished);
            Assert.AreEqual(DollH, last.heightM, 1e-3f);
            Assert.AreEqual(1f, last.real, 1e-3f, "人形が実体として残っていない");
            Assert.AreEqual(0f, last.knot, 1e-3f, "もつれが残っている");
        }

        [Test]
        public void ToHuman_StartsAsTheDoll_AndEndsWithNothing()
        {
            List<SwapMorphLogic.Sample> all = RunAll(SwapMorphLogic.Dir.ToHuman);
            // ⚠ 始まりは人形がふつうに立っている（ほどけるのはその人形）。
            Assert.AreEqual(1f, all[0].real, 5e-2f, "始まりに人形が出ていない");

            // 実体はほどけ切る**前に**消える（縁が残ると隠し切ったように見えない）。
            int covered = all.FindIndex(s => s.justCovered);
            Assert.AreEqual(0f, all[covered].real, 1e-3f, "ほどけ切った所で人形の実体が残っている");

            SwapMorphLogic.Sample last = all[all.Count - 1];
            Assert.IsTrue(last.justFinished);
            Assert.AreEqual(HumanH, last.heightM, 1e-3f);
            Assert.AreEqual(0f, last.real, 1e-3f, "人へ戻る向きで CG を実体化させてはいけない");
            Assert.AreEqual(0f, last.knot, 1e-3f, "もつれが残っている");
        }

        [Test]
        public void Height_MovesGeometrically_NotLinearly()
        {
            // 中点で等比なら幾何平均（√(1.70×0.40) = 0.825）。線形なら 1.05 で、
            // 前半に一気に小さくなって後半が動かない ＝「縮んだ」ではなく「落ちた」に見える。
            var logic = new SwapMorphLogic();
            logic.Begin(SwapMorphLogic.Dir.ToDoll, 2.6f, HumanH, DollH);
            float mid = logic.LerpHeight(0.5f);
            Assert.AreEqual(Mathf.Sqrt(HumanH * DollH), mid, 1e-3f);
            Assert.Greater(Mathf.Abs(Mathf.Lerp(HumanH, DollH, 0.5f) - mid), 0.1f,
                           "線形補間と見分けが付かない");
        }

        [Test]
        public void Height_IsMonotone_AndStaysInsideTheEnds()
        {
            foreach (SwapMorphLogic.Dir dir in BothWays)
            {
                List<SwapMorphLogic.Sample> all = RunAll(dir);
                float lo = Mathf.Min(HumanH, DollH), hi = Mathf.Max(HumanH, DollH);
                bool toDoll = dir == SwapMorphLogic.Dir.ToDoll;
                for (int i = 1; i < all.Count; i++)
                {
                    Assert.GreaterOrEqual(all[i].heightM, lo - 1e-3f);
                    Assert.LessOrEqual(all[i].heightM, hi + 1e-3f);
                    // 行きつ戻りつしない（人型が伸びたり縮んだりすると「入れ替わり」に読めない）。
                    if (toDoll) Assert.LessOrEqual(all[i].heightM, all[i - 1].heightM + 1e-4f);
                    else Assert.GreaterOrEqual(all[i].heightM, all[i - 1].heightM - 1e-4f);
                }
            }
        }

        [Test]
        public void Ground_IsZeroWhileTheFigureIsThread()
        {
            // 糸のもつれは光を遮らない。ほどけているあいだは影も接地影も出ない。
            foreach (SwapMorphLogic.Dir dir in BothWays)
            {
                List<SwapMorphLogic.Sample> all = RunAll(dir);
                int covered = all.FindIndex(s => s.justCovered);
                Assert.AreEqual(0f, all[covered].ground, 1e-3f, $"{dir}: ほどけ切った所で影が残っている");
            }
        }

        [Test]
        public void Ground_ReturnsOnlyWhenTheDollBecomesReal()
        {
            // 人形になる向きは実体が出てくる晴れる段で影が戻る。
            List<SwapMorphLogic.Sample> toDoll = RunAll(SwapMorphLogic.Dir.ToDoll);
            Assert.AreEqual(1f, toDoll[toDoll.Count - 1].ground, 1e-3f, "人形が出たのに影が無い");

            // 人へ戻る向きは戻らない（戻る先はライブ映像で、本物の影が最初から写っている）。
            List<SwapMorphLogic.Sample> toHuman = RunAll(SwapMorphLogic.Dir.ToHuman);
            Assert.AreEqual(0f, toHuman[toHuman.Count - 1].ground, 1e-3f,
                            "人へ戻る向きで CG の影が戻ると、誰も居ない床に影だけが出る");
        }

        [Test]
        public void Cover_IsMonotone_AndStaysAtOneOnceUnravelled()
        {
            // 「徐々にオーバーレイする」ので、ほどける途中で前線が戻ってはいけない。
            // ほどけ切った後も 1 のまま（戻すと体の下半分が唐突に実体へ復帰する）。
            foreach (SwapMorphLogic.Dir dir in BothWays)
            {
                List<SwapMorphLogic.Sample> all = RunAll(dir);
                for (int i = 1; i < all.Count; i++)
                    Assert.GreaterOrEqual(all[i].cover, all[i - 1].cover - 1e-4f, $"{dir} i={i} で前線が戻った");
                Assert.AreEqual(1f, all[all.Count - 1].cover, 1e-3f, $"{dir}: 最後に前線が 1 でない");
            }
        }

        [Test]
        public void Cancel_StopsImmediately_AndNeverCovers()
        {
            var logic = new SwapMorphLogic();
            logic.Begin(SwapMorphLogic.Dir.ToDoll, 2.6f, HumanH, DollH);
            for (int i = 0; i < 10; i++) logic.Tick(Dt);
            logic.Cancel();
            Assert.IsFalse(logic.Active);
            SwapMorphLogic.Sample s = logic.Tick(Dt);
            Assert.IsFalse(s.active);
            Assert.IsFalse(s.justCovered, "畳んだ後に差し替えが走ると、糸が無い所で画面が変わる");
        }

        [Test]
        public void TotalSec_IsClamped()
        {
            var logic = new SwapMorphLogic();
            logic.Begin(SwapMorphLogic.Dir.ToDoll, 0.05f, HumanH, DollH);
            int n = 0;
            while (logic.Active && n < 10000) { logic.Tick(Dt); n++; }
            Assert.GreaterOrEqual(n * Dt, SwapMorphLogic.MinTotalSec - Dt,
                                  "短すぎる尺は 3 段が読めないので下限へ丸める");

            logic.Begin(SwapMorphLogic.Dir.ToDoll, 99f, HumanH, DollH);
            n = 0;
            while (logic.Active && n < 10000) { logic.Tick(Dt); n++; }
            Assert.LessOrEqual(n * Dt, SwapMorphLogic.MaxTotalSec + Dt);
        }

        [Test]
        public void HumanHeight_FallsBackWhenTheHeadIsNotTracked()
        {
            Assert.AreEqual(SwapMorphLogic.FallbackHumanHeightM,
                            SwapMorphLogic.HumanHeightFrom(false, 1.5f), 1e-4f);
        }

        [Test]
        public void HumanHeight_UsesTheSameEstimateAsTheArms()
        {
            // ⚠ 腕の写像（ActorArmLogic）と同じ推定でなければ、糸の人型の背丈と腕の長さが食い違う。
            const float head = 1.55f;
            Assert.AreEqual(Cg.ActorArmLogic.EstimateHeightM(head),
                            SwapMorphLogic.HumanHeightFrom(true, head), 1e-4f);
        }

        [Test]
        public void HumanHeight_IsClampedToPlausibleBodies()
        {
            Assert.AreEqual(SwapMorphLogic.MinHumanHeightM,
                            SwapMorphLogic.HumanHeightFrom(true, 0.1f), 1e-3f);
            Assert.AreEqual(SwapMorphLogic.MaxHumanHeightM,
                            SwapMorphLogic.HumanHeightFrom(true, 9f), 1e-3f);
        }

        [Test]
        public void PhaseFractions_LeaveRoomForAllThree()
        {
            float clear = 1f - SwapMorphLogic.RiseFrac - SwapMorphLogic.MorphFrac;
            Assert.Greater(SwapMorphLogic.RiseFrac, 0.1f);
            Assert.Greater(SwapMorphLogic.MorphFrac, 0.1f);
            Assert.Greater(clear, 0.1f, "晴れる段が短すぎると「糸が晴れた」に読めない");
        }
    }
}
