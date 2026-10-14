#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 同じ面のまま次の文面へ差し替える段（<see cref="CommsStage.Swap"/>・2026-10-14）と、
    /// 押し始めで走っている連絡を片づける消え方（<see cref="CommsStage.Guide"/>）。
    ///
    /// ⚠ 2026-10-14 まで、差し替えは 1 フレームだった（旧文面が消え、枠の丈が縮み、新しい 1 字目が出る、が
    /// 同じコマ）。押して片づけるときも文面は 1 フレームで消えていた。
    /// ここでは「字が見えているあいだは枠を動かさない」「濃さは 1 フレームで落とさない」を数値で押さえる。
    /// </summary>
    public sealed class CommsSwapLogicTests
    {
        private const float Dt = 1f / 72f;   // Quest の表示 1 フレーム
        private const int Chars = 26;

        private static float Visible(CommsPanelLogic l) => l.Weights.glyph * l.Weights.reveal;

        // 見た目の明るさ（暗い地の上の文字。アルファは線形光で掛かる）。
        private static float Perceived(CommsPanelLogic l)
            => (float)System.Math.Pow(Visible(l), 1.0 / PerceptualFade.DisplayGamma);

        private static CommsPanelLogic Holding(bool persistent = false)
        {
            var l = new CommsPanelLogic();
            l.Begin(Chars, CommsDelivery.Typed, persistent);
            for (int i = 0; i < 5000 && l.Stage != CommsStage.Hold; i++) l.Tick(Dt);
            Assert.AreEqual(CommsStage.Hold, l.Stage, "読ませる段へ到達しなかった");
            return l;
        }

        [Test]
        public void Swap_KeepsTheBoxStill_UntilTheOldTextIsGone()
        {
            var l = Holding();
            l.Begin(12);
            Assert.AreEqual(CommsStage.Swap, l.Stage);
            Assert.IsTrue(l.SwapClearing);

            float prev = Perceived(l);
            Assert.AreEqual(1f, prev, 1e-4f, "届いた瞬間に旧文面が消えている（1 フレームの入れ替わり）");
            for (int i = 0; i < 500 && l.Stage == CommsStage.Swap; i++)
            {
                l.Tick(Dt);
                float now = Perceived(l);
                if (now > 0f)
                {
                    Assert.IsTrue(l.SwapClearing, "字が見えているのに枠を動かし始めている");
                    Assert.AreEqual(0f, l.BodyBandBlend, 1e-6f, "字が見えているのに枠を動かしている");
                }
                Assert.LessOrEqual(prev - now, 0.20f, $"見た目が 1 フレームで {prev - now:0.00} 落ちた");
                Assert.LessOrEqual(now, prev + 1e-6f, "消えている途中で濃くなった");
                prev = now;
            }
            Assert.AreEqual(CommsStage.Type, l.Stage);
        }

        [Test]
        public void Swap_MovesTheBoxSmoothly_ThenTypesTheFirstLetter()
        {
            var l = Holding();
            l.Begin(12);
            float sec = 0f, prevBlend = 0f;
            bool committedSeen = false;
            while (l.Stage == CommsStage.Swap && sec < 2f)
            {
                l.Tick(Dt);
                sec += Dt;
                if (!l.SwapClearing) committedSeen = true;
                if (l.Stage != CommsStage.Swap) break;
                Assert.AreEqual(0f, l.Weights.reveal * (l.SwapClearing ? 0f : 1f), 1e-6f,
                                "枠を動かしているあいだに新しい字が出ている");
                float blend = l.BodyBandBlend;
                Assert.GreaterOrEqual(blend, prevBlend - 1e-6f, "枠が戻った");
                Assert.LessOrEqual(blend - prevBlend, 0.15f, $"枠が 1 フレームで {blend - prevBlend:0.00} 動いた");
                prevBlend = blend;
            }
            Assert.IsTrue(committedSeen, "旧文面が消え切った縁が来なかった");
            Assert.AreEqual(CommsStage.Type, l.Stage);
            Assert.AreEqual(1f, l.BodyBandBlend, 1e-6f, "1 字目の前に枠が移り切っていない");
            Assert.AreEqual(CommsPanelLogic.SwapTextOutSec + CommsPanelLogic.SwapBandSec, sec, Dt + 1e-4f,
                            "差し替えの尺");
            l.Tick(Dt);
            Assert.Greater(l.Weights.reveal, 0f, "移り切った次のフレームで 1 字目が出ていない");
        }

        [Test]
        public void Swap_CommitEdge_FiresOnce_AndSwitchesTheDeliveryThen()
        {
            var l = Holding();
            l.Begin(9, CommsDelivery.Fade);
            Assert.AreEqual(CommsDelivery.Fade, l.Delivery, "届いた連絡の出方を返すこと");
            int edges = 0;
            for (int i = 0; i < 200 && l.Stage != CommsStage.Hold; i++)
            {
                bool clearing = l.SwapClearing;
                l.Tick(Dt);
                if (l.TakeSwapCommit())
                {
                    edges++;
                    Assert.IsTrue(clearing, "旧文面が消え切る縁より後で縁が立った");
                    Assert.AreEqual(0f, Visible(l), 1e-6f, "旧文面が見えているのに差し替えの縁が立った");
                    Assert.AreEqual(CommsPanelLogic.FadeInSec, l.TypeSec, 1e-6f, "次の連絡の尺へ入れ替わっていない");
                }
            }
            Assert.AreEqual(1, edges, "差し替えの縁は 1 回だけ");
            Assert.AreEqual(CommsStage.Hold, l.Stage);
            Assert.AreEqual(CommsDelivery.Fade, l.Delivery);
        }

        [Test]
        public void RetimeSwap_UsesTheLengthOfTheBuiltText()
        {
            ShowLang previous = ShowLanguage.Current;
            ShowLanguage.Select(ShowLang.Ja);
            try
            {
                var l = Holding();
                l.Begin(5);   // 実行体は Begin の時点では次の文面を組めない（旧文面の字数を渡す）
                for (int i = 0; i < 100 && !l.TakeSwapCommit(); i++) l.Tick(Dt);
                l.RetimeSwap(36);
                Assert.AreEqual(36f / CommsPanelLogic.CharsPerSec, l.TypeSec, 1e-4f);
            }
            finally
            {
                ShowLanguage.Select(previous);
            }
        }

        [Test]
        public void Takeover_NeverSwaps_AndKeepsItsOwnClock()
        {
            var l = Holding();
            Assert.IsFalse(l.WouldSwap(CommsDelivery.Possessed));
            l.Begin(10, CommsDelivery.Possessed);
            Assert.AreEqual(CommsStage.Intrusion, l.Stage, "乗っ取りは差し替えずに侵入から");
            Assert.AreEqual(0f, l.TakeoverElapsedSec, 1e-6f);
            for (int i = 0; i < 72; i++) l.Tick(Dt);
            Assert.AreEqual(1f, l.TakeoverElapsedSec, Dt * 0.5f, "乗っ取りの時計が狂った");
        }

        [Test]
        public void BeginDuringTheSwap_RestartsWithoutShowingAnything()
        {
            var l = Holding();
            l.Begin(12);
            for (int i = 0; i < 100 && l.SwapClearing; i++) l.Tick(Dt);
            l.Tick(Dt);   // 枠を動かしている途中
            Assert.AreEqual(CommsStage.Swap, l.Stage);
            Assert.IsTrue(l.TakeSwapCommit());

            l.Begin(20);   // さらに次の連絡
            Assert.AreEqual(CommsStage.Swap, l.Stage);
            for (int i = 0; i < 200 && l.Stage == CommsStage.Swap; i++)
            {
                Assert.AreEqual(0f, Visible(l), 1e-6f, "差し替えの途中で何かが光った");
                l.Tick(Dt);
            }
            Assert.AreEqual(CommsStage.Type, l.Stage);
        }

        [Test]
        public void ReleasingTheOnboarding_DuringTheSwap_FoldsWithoutTyping()
        {
            var l = Holding(persistent: true);
            l.Begin(12, CommsDelivery.Typed, persistent: true);
            Assert.AreEqual(CommsStage.Swap, l.Stage);
            l.ReleasePersistent();
            Assert.AreEqual(CommsStage.Out, l.Stage);
            for (int i = 0; i < 500 && l.Stage != CommsStage.Off; i++)
            {
                l.Tick(Dt);
                Assert.AreNotEqual(CommsStage.Type, l.Stage, "畳むはずの導入の文面を打っている");
            }
            Assert.AreEqual(CommsStage.Off, l.Stage);
        }

        [Test]
        public void PressingDuringHold_FadesTheText_ThenShrinksTheBox()
        {
            var l = Holding();
            l.SetGuideWanted(true);
            Assert.AreEqual(CommsStage.Guide, l.Stage);
            float prev = Perceived(l);
            Assert.AreEqual(1f, prev, 1e-4f, "押した瞬間に文面が消えている（1 フレーム）");
            float sec = 0f;
            for (int i = 0; i < 72; i++)
            {
                l.Tick(Dt);
                sec += Dt;
                float now = Perceived(l);
                Assert.LessOrEqual(prev - now, 0.25f, $"見た目が 1 フレームで {prev - now:0.00} 落ちた");
                if (now > 0f)
                    Assert.AreEqual(1f, l.Weights.body, 1e-4f, "字が残っているのに枠の丈が縮んでいる");
                prev = now;
            }
            Assert.AreEqual(0f, prev, 1e-6f);
            Assert.Less(l.Weights.body, 0.5f, "文面が消えた後に丈が縮んでいない");
        }

        [Test]
        public void TextFadeDurations_StayShort()
        {
            // 押した手応えを遅らせない。差し替えは読ませ終わった文面を引きずらない。
            Assert.That(CommsPanelLogic.GuideTextOutSec, Is.InRange(0.05f, 0.1f));
            Assert.That(CommsPanelLogic.SwapTextOutSec, Is.InRange(0.1f, 0.2f));
            Assert.That(CommsPanelLogic.SwapTextOutSec + CommsPanelLogic.SwapBandSec, Is.LessThanOrEqualTo(0.4f));
        }
    }
}
