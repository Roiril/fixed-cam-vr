#nullable enable
using System.Collections.Generic;
using FixedCamVr.Streaming;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// <b>入れ替わりのノイズの決めごとを機械で守る。</b> 数値の良し悪しではなく、
    /// ユーザーの言葉（2026-08-19・`canon/LEDGER.md` 0089）がそのまま段になっていることを固定する:
    /// 「体験者を徐々にオーバーレイするノイズを走らせ、ノイズが人型になって体験者はノイズに覆われて
    /// 見えなくなり、その後にノイズの人型が徐々に人形サイズになり、ノイズが晴れたら人形になる」。
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

        [Test]
        public void Rise_ThenMorph_ThenClear_InThatOrder()
        {
            List<SwapMorphLogic.Sample> all = RunAll(SwapMorphLogic.Dir.ToDoll);
            int covered = all.FindIndex(s => s.justCovered);
            Assert.Greater(covered, 0, "覆い切る前に湧く段が要る");

            // 湧く段: 背丈は動かない（人型がまだ体験者の大きさで固まる前に縮み始めない）。
            for (int i = 0; i < covered; i++)
                Assert.AreEqual(HumanH, all[i].heightM, 1e-3f, $"湧く段 i={i} で背丈が動いた");

            // 覆い切った後にだけ縮む。
            int shrank = all.FindIndex(s => s.heightM < HumanH - 1e-3f);
            Assert.Greater(shrank, covered, "覆い切る前に縮み始めている（体験者が縮んで見える）");

            // 晴れる段は縮み切った後。
            int cleared = all.FindIndex(s => s.solid > 0.001f);
            Assert.Greater(cleared, shrank, "縮む前に実体が出ている");
            Assert.AreEqual(DollH, all[cleared].heightM, 1e-3f, "晴れ始めた時点で人形の大きさでない");
        }

        [Test]
        public void JustSettling_FiresOnce_AfterTheFigureFinishedShrinking()
        {
            // ⚠⚠ 姿を人形へ替えてよい唯一の瞬間 — **縮み切っていて、まだ砂に覆われている**。
            //    覆い切った瞬間（justCovered）に替えると、**縮んでいる間ずっと人形の形**になる。
            //    人形を人の背丈へ引き伸ばしても人型には見えない（2026-08-19 に絵で確かめた）。
            foreach (SwapMorphLogic.Dir dir in new[] { SwapMorphLogic.Dir.ToDoll, SwapMorphLogic.Dir.ToHuman })
            {
                List<SwapMorphLogic.Sample> all = RunAll(dir);
                Assert.AreEqual(1, all.FindAll(s => s.justSettling).Count, $"{dir}");

                int settling = all.FindIndex(s => s.justSettling);
                int covered = all.FindIndex(s => s.justCovered);
                Assert.Greater(settling, covered, $"{dir}: 覆い切る前に姿を替えたら見えてしまう");
                Assert.GreaterOrEqual(all[settling].cover, 0.999f, $"{dir}: 砂が覆い切っていない");

                float endH = dir == SwapMorphLogic.Dir.ToDoll ? DollH : HumanH;
                Assert.AreEqual(endH, all[settling].heightM, 1e-2f,
                                $"{dir}: 縮み切る前に姿を替えている（形が変わったことが見える）");
            }
        }

        [Test]
        public void JustCovered_FiresExactlyOnce()
        {
            foreach (SwapMorphLogic.Dir dir in new[] { SwapMorphLogic.Dir.ToDoll, SwapMorphLogic.Dir.ToHuman })
            {
                List<SwapMorphLogic.Sample> all = RunAll(dir);
                Assert.AreEqual(1, all.FindAll(s => s.justCovered).Count,
                                $"{dir}: 画面の差し替えは 1 回でなければならない");
            }
        }

        [Test]
        public void Covered_MeansFullyOpaque()
        {
            List<SwapMorphLogic.Sample> all = RunAll(SwapMorphLogic.Dir.ToDoll);
            SwapMorphLogic.Sample at = all.Find(s => s.justCovered);
            Assert.GreaterOrEqual(at.cover, 0.999f,
                "覆い切っていないのに差し替えると、体験者が砂の下ではなく画の中で消える");
        }

        [Test]
        public void ToDoll_EndsAsTheDoll()
        {
            List<SwapMorphLogic.Sample> all = RunAll(SwapMorphLogic.Dir.ToDoll);
            SwapMorphLogic.Sample last = all[all.Count - 1];
            Assert.IsTrue(last.justFinished);
            Assert.AreEqual(DollH, last.heightM, 1e-3f);
            Assert.AreEqual(1f, last.solid, 1e-3f, "砂が実体（人形）へ戻り切っていない");
            Assert.AreEqual(1f, last.cover, 1e-3f, "人形になる向きでは砂は引かない（実体へ寄る）");
        }

        [Test]
        public void ToHuman_EndsWithNothing()
        {
            List<SwapMorphLogic.Sample> all = RunAll(SwapMorphLogic.Dir.ToHuman);
            SwapMorphLogic.Sample last = all[all.Count - 1];
            Assert.IsTrue(last.justFinished);
            Assert.AreEqual(HumanH, last.heightM, 1e-3f);
            Assert.AreEqual(0f, last.cover, 1e-3f, "砂が引き切っていない（下のライブ映像が出ない）");
            Assert.AreEqual(0f, last.solid, 1e-3f, "人へ戻る向きで人形を実体化させてはいけない");
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
            foreach (SwapMorphLogic.Dir dir in new[] { SwapMorphLogic.Dir.ToDoll, SwapMorphLogic.Dir.ToHuman })
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
        public void Ground_IsZeroWhileTheFigureIsSand()
        {
            // 砂の人型は光を遮らない。覆われているあいだは影も接地影も出ない。
            foreach (SwapMorphLogic.Dir dir in new[] { SwapMorphLogic.Dir.ToDoll, SwapMorphLogic.Dir.ToHuman })
            {
                List<SwapMorphLogic.Sample> all = RunAll(dir);
                int covered = all.FindIndex(s => s.justCovered);
                Assert.AreEqual(0f, all[covered].ground, 1e-3f, $"{dir}: 覆い切った所で影が残っている");
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
        public void Cover_IsMonotoneUntilCovered()
        {
            // 「徐々にオーバーレイする」ので、湧く途中で砂が引いてはいけない。
            List<SwapMorphLogic.Sample> all = RunAll(SwapMorphLogic.Dir.ToDoll);
            int covered = all.FindIndex(s => s.justCovered);
            for (int i = 1; i <= covered; i++)
                Assert.GreaterOrEqual(all[i].cover, all[i - 1].cover - 1e-4f, $"i={i} で砂が引いた");
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
            Assert.IsFalse(s.justCovered, "畳んだ後に差し替えが走ると、砂が無い所で画面が変わる");
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
            // ⚠ 腕の写像（ActorArmLogic）と同じ推定でなければ、砂の人型の背丈と腕の長さが食い違う。
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
            Assert.Greater(clear, 0.1f, "晴れる段が短すぎると「砂が晴れた」に読めない");
        }
    }
}
