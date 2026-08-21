#nullable enable
using System.Collections.Generic;
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// <b>持続の覆い</b>（`canon/LEDGER.md` 0102）の決めごとを機械で守る。
    ///
    /// ユーザーの言葉は「3-A に入るとき、最初から右半分の体験者は黒いノイズに包まれており」。
    /// そこから設計が言い切ったのは 3 つで、どれも**外すと沈黙して壊れる**:
    ///
    /// 1. 包んでいるあいだ<b>段は 1 ミリも進まない</b>（覆いが薄まらない・晴れない）
    /// 2. 包んでいるあいだ<b>縁は 1 つも立たない</b>（画面の差し替えは次のカットの仕事）
    /// 3. 引き継いだ入れ替わりは<b>ほどける段を飛ばす</b>が、
    ///    <b>画面の差し替えの縁（<see cref="SwapMorphLogic.Sample.justSwapScreen"/>）は必ず立つ</b>
    ///
    /// ⚠⚠ 3 の後半がこの機構でいちばん壊れやすい。「もう覆い切っている」を素直に
    /// <c>_covered = true</c> で表すと <c>justCovered</c> が永久に立たず、
    /// **人 → 人形の画面差し替えが 1 度も起きない**（覆いの下で何も入れ替わらない）。
    /// </summary>
    public class SwapVeilHoldTests
    {
        private const float Dt = 1f / 60f;
        private const float HumanH = 1.70f;
        private const float DollH = 0.40f;

        [Test]
        public void Hold_KeepsTheVeilFullyClosed_NoMatterHowLongItWaits()
        {
            var logic = new SwapMorphLogic();
            logic.BeginHold(HumanH);

            // 実機の凍結は 1.2 秒だが、体験者が線を越えるまで待つ区間は何秒でも続きうる。
            for (int i = 0; i < 600; i++)
            {
                SwapMorphLogic.Sample s = logic.Tick(Dt);
                Assert.IsTrue(s.active, "包んでいるあいだ覆いは走っている");
                Assert.AreEqual(1f, s.cover, 1e-4f, "ほどけは完了したまま");
                Assert.AreEqual(1f, s.knot, 1e-4f, "もつれは下を隠したまま（薄まらない）");
                Assert.AreEqual(1f, s.thread, 1e-4f, "糸の広がりも保つ");
                Assert.AreEqual(0f, s.real, 1e-4f, "実体は 1 画素も出さない（黒だけ）");
                Assert.AreEqual(HumanH, s.heightM, 1e-3f, "背丈は映像の中の人のまま");
                Assert.AreEqual(0f, s.ground, 1e-4f, "糸のもつれは光を遮らない");
            }
            Assert.IsTrue(logic.Held);
            Assert.IsTrue(logic.Active);
        }

        [Test]
        public void Hold_NeverFiresAnyEdge()
        {
            var logic = new SwapMorphLogic();
            logic.BeginHold(HumanH);
            for (int i = 0; i < 600; i++)
            {
                SwapMorphLogic.Sample s = logic.Tick(Dt);
                Assert.IsFalse(s.justCovered, "包んだままの間に覆い切りの縁を立てない");
                Assert.IsFalse(s.justSwapScreen,
                    "画面の差し替えは次のカット（swap）の仕事。ここで立つと凍結中に画が変わる");
                Assert.IsFalse(s.justSettling);
                Assert.IsFalse(s.justFinished);
            }
        }

        [Test]
        public void Hold_FollowsTheVisitorHeight()
        {
            var logic = new SwapMorphLogic();
            logic.BeginHold(HumanH);
            logic.Tick(Dt);
            logic.UpdateHoldHeight(1.50f);
            Assert.AreEqual(1.50f, logic.Tick(Dt).heightM, 1e-3f);
            // マスクを引く枠も同じ背丈（映像の中の人を差分で拾う相手）。
            Assert.AreEqual(1.50f, logic.MaskHeightM, 1e-3f);
        }

        [Test]
        public void ContinuingFromHold_SkipsTheUnravelStage()
        {
            var logic = new SwapMorphLogic();
            logic.Begin(SwapMorphLogic.Dir.ToDoll, 2.6f, HumanH, DollH, startCovered: true);
            SwapMorphLogic.Sample first = logic.Tick(Dt);

            Assert.IsFalse(logic.Held, "引き継いだら保持ではなく進行");
            Assert.AreEqual(1f, first.cover, 1e-4f, "1 フレーム目からほどけ切っている");
            Assert.Less(first.heightM, HumanH, "縮む段が始まっている（背丈が動く）");
        }

        [Test]
        public void ContinuingFromHold_StillSwapsTheScreen_OnTheFirstFrame()
        {
            var logic = new SwapMorphLogic();
            logic.Begin(SwapMorphLogic.Dir.ToDoll, 2.6f, HumanH, DollH, startCovered: true);
            SwapMorphLogic.Sample first = logic.Tick(Dt);

            // ⚠⚠ ここが要。`_covered` を true で始めると永久に立たず、
            //   覆いの下で何も入れ替わらない（画は凍結のまま最後まで進む）。
            Assert.IsTrue(first.justCovered, "覆い切りの縁は 1 フレーム目に立つ");
            Assert.IsTrue(first.justSwapScreen, "人 → 人形の画面差し替えはその縁で起きる");
        }

        [Test]
        public void ContinuingFromHold_RunsTheRemainingStagesOnce_AndFinishes()
        {
            var logic = new SwapMorphLogic();
            logic.Begin(SwapMorphLogic.Dir.ToDoll, 2.6f, HumanH, DollH, startCovered: true);
            var all = new List<SwapMorphLogic.Sample>();
            for (int i = 0; i < 10000 && logic.Active; i++) all.Add(logic.Tick(Dt));

            Assert.IsTrue(all[all.Count - 1].justFinished, "必ず終わる");
            Assert.AreEqual(1, all.FindAll(s => s.justSwapScreen).Count, "差し替えは 1 回だけ");
            Assert.AreEqual(1, all.FindAll(s => s.justSettling).Count, "晴れる段へ入るのも 1 回だけ");
            Assert.AreEqual(DollH, all[all.Count - 1].heightM, 1e-2f, "人形の背丈で終わる");

            // ほどける段（全体の 32%）を飛ばしたぶんだけ短い。
            float sec = all.Count * Dt;
            float expected = 2.6f * (1f - SwapMorphLogic.RiseFrac);
            Assert.That(sec, Is.EqualTo(expected).Within(0.1f),
                "縮む段から始まるので、ほどける段のぶん短く終わる");
        }

        [Test]
        public void Cancel_ClearsTheHold()
        {
            var logic = new SwapMorphLogic();
            logic.BeginHold(HumanH);
            logic.Tick(Dt);
            logic.Cancel();

            Assert.IsFalse(logic.Held);
            Assert.IsFalse(logic.Active);
            Assert.IsFalse(logic.Tick(Dt).active);
        }

        [Test]
        public void Hold_DoesNotDisturbAPlainSwap()
        {
            // 保持を使っていない入れ替わりは 1 ビットも変わらない（既定の引数）。
            var logic = new SwapMorphLogic();
            logic.Begin(SwapMorphLogic.Dir.ToDoll, 2.6f, HumanH, DollH);
            var all = new List<SwapMorphLogic.Sample>();
            for (int i = 0; i < 10000 && logic.Active; i++) all.Add(logic.Tick(Dt));

            Assert.AreEqual(0f, all[0].cover, 0.05f, "ほどける段から始まる");
            Assert.That(all.Count * Dt, Is.EqualTo(2.6f).Within(0.1f), "尺は変わらない");
        }

        [Test]
        public void WaveKeepsTravelling_WhenTheSwapTakesOverFromTheHold()
        {
            // 山の走った距離を 0 へ戻すと、覆いが続いているのに波だけがその 1 フレームで
            // 体の外へ飛んで湧き直す（＝ 引き継ぎの縁で波が切れて見える）。
            var wave = new SwapWaveLogic();
            wave.Begin(SwapMorphLogic.Dir.ToDoll, SwapMorphLogic.DefaultTotalSec);
            for (int i = 0; i < 60; i++)
                wave.Tick(Dt, SwapMorphLogic.RiseFrac, 1f, 1f);
            float travelled = wave.TravelFig;
            Assert.Greater(travelled, 0f);

            wave.Begin(SwapMorphLogic.Dir.ToDoll, SwapMorphLogic.DefaultTotalSec, keepTravel: true);
            Assert.AreEqual(travelled, wave.TravelFig, 1e-4f, "引き継ぎでは山の位置を保つ");

            wave.Begin(SwapMorphLogic.Dir.ToDoll, SwapMorphLogic.DefaultTotalSec);
            Assert.AreEqual(0f, wave.TravelFig, 1e-4f, "ふつうの始まりでは 0 から");
        }
    }
}
