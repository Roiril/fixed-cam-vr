#nullable enable
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 4 周目 A の③a / ③b の「何とか戻ってきた」姿（<c>canon/LEDGER.md</c> 0270・<see cref="CommsRecoveryLogic"/>）と、
    /// その時計（<see cref="CommsPanelLogic.RecoverySec"/>）。
    /// </summary>
    public sealed class CommsRecoveryLogicTests
    {
        private const float Dt = 1f / 90f;
        private const float HighRed = CommsRecoveryLogic.ResidueStart * CommsRecoveryLogic.RedPerResidue;
        private const float LowRed = CommsRecoveryLogic.ResidueFloor * CommsRecoveryLogic.RedPerResidue;

        [Test]
        public void Idle_IsTheOrdinaryPanel()
        {
            CommsRecoverySample s = CommsRecoveryLogic.Sample(-1f);
            Assert.IsFalse(s.active);
            Assert.AreEqual(1f, s.power);
            Assert.AreEqual(1f, s.squash);
            Assert.AreEqual(0f, s.tear);
            Assert.AreEqual(0f, s.residue);
            Assert.AreEqual(0f, s.red);
        }

        [Test]
        public void Boot_NeverDimsTheScreen_PowerOnlyRises()
        {
            // ⚠ 明るさを落とさない（3〜30Hz の明滅にしない）。電力は上がる方向にしか動かない。
            float prev = 0f;
            for (float t = 0f; t < 3f; t += Dt)
            {
                float p = CommsRecoveryLogic.Sample(t, promptReveal: -1f).power;
                Assert.GreaterOrEqual(p, prev - 1e-6f, $"t={t:F3} で電力が下がった");
                prev = p;
            }
            // 余震でも電力は下げない。
            for (float r = 0f; r <= 1f; r += 0.01f)
                Assert.AreEqual(1f, CommsRecoveryLogic.Sample(3f, r, 2.5f).power, 1e-6f);
        }

        [Test]
        public void Boot_IsSteppedHolds_ThenSettlesToFull_BeforeTheTextFloatsIn()
        {
            // ホールドフレーム: 段の中では値が動かない。
            Assert.AreEqual(CommsRecoveryLogic.Sample(0.10f).power, CommsRecoveryLogic.Sample(0.20f).power, 1e-6f);
            Assert.AreEqual(CommsRecoveryLogic.Sample(0.10f).squash, CommsRecoveryLogic.Sample(0.20f).squash, 1e-6f);
            // 横線から開く。
            Assert.Less(CommsRecoveryLogic.Sample(0.01f).squash, 0.1f);
            float maxSquash = 0f;
            for (float t = 0f; t < CommsRecoveryLogic.BootSec; t += Dt)
                maxSquash = System.Math.Max(maxSquash, CommsRecoveryLogic.Sample(t).squash);
            Assert.LessOrEqual(maxSquash, 1f, "弱っているので跳ねない");
            // 字が浮かび始める（開く段の終わり）までに通常の電力・高さへ戻る。
            Assert.LessOrEqual(CommsRecoveryLogic.BootSec, CommsPanelLogic.InSec + 1e-6f);
            CommsRecoverySample after = CommsRecoveryLogic.Sample(CommsRecoveryLogic.BootSec + 0.001f);
            Assert.AreEqual(1f, after.power, 1e-6f);
            Assert.AreEqual(1f, after.squash, 1e-6f);
            Assert.AreEqual(0f, after.tear, 1e-6f);
            Assert.IsFalse(after.booting);
        }

        [Test]
        public void Boot_TearsNoHarderThanTheTakeover_ThenFades()
        {
            Assert.AreEqual(CommsRecoveryLogic.BootTearPeak, CommsRecoveryLogic.Sample(0.01f).tear, 1e-6f);
            Assert.LessOrEqual(CommsRecoveryLogic.BootTearPeak, CommsPossessionLogic.TearPeak,
                "乗っ取りより激しいと「もっと壊れた」に読める");
            Assert.Less(CommsRecoveryLogic.Sample(0.40f).tear, CommsRecoveryLogic.Sample(0.25f).tear);
        }

        [Test]
        public void Residue_PushesBack_ButHalfStaysToTheEnd()
        {
            float start = CommsRecoveryLogic.Sample(0f).residue;
            float haltEnd = CommsRecoveryLogic.Sample(CommsRecoveryLogic.HaltEndSec).residue;
            float late = CommsRecoveryLogic.Sample(30f).residue;
            Assert.Greater(start, haltEnd, "スイが押し返す");
            Assert.Greater(haltEnd, late);
            Assert.GreaterOrEqual(late, 0.4f, "半分近くは最後まで人形のまま（0270「半分はまだ侵食されていて」）");
            for (float t = CommsRecoveryLogic.HaltEndSec; t < 30f; t += 0.1f)
                Assert.That(CommsRecoveryLogic.Sample(t).residue, Is.InRange(0.48f, 0.55f),
                    "復帰後の正常表示と赤い人形は約1:1を保つ（2026-10-18）");
            CommsRecoverySample s = CommsRecoveryLogic.Sample(30f);
            Assert.AreEqual(s.residue * CommsRecoveryLogic.RedPerResidue, s.red, 1e-6f,
                "顔と文字は同じ 1 本の量を読む");
        }

        [Test]
        public void Aftershocks_FollowThePromptTyping_AndPushTheDollBack()
        {
            const float typeSec = 2.5f;
            float at = CommsRecoveryLogic.AftershockAtReveal[0];
            float during = at + CommsRecoveryLogic.AftershockSec * 0.5f / typeSec;
            CommsRecoverySample s = CommsRecoveryLogic.Sample(3f, during, typeSec);
            Assert.IsTrue(s.aftershock);
            Assert.AreEqual(CommsRecoveryLogic.AftershockTear, s.tear, 1e-6f);
            Assert.Greater(s.residue, CommsRecoveryLogic.Sample(3f, at - 0.01f, typeSec).residue, "人形が押し返してくる");
            Assert.AreEqual(0f, CommsRecoveryLogic.Sample(3f, at - 0.01f, typeSec).tear, 1e-6f);
            // ③b を打っていない（読ませる段）では起きない — 人形 End の時計が数える段に入れない。
            Assert.AreEqual(0f, CommsRecoveryLogic.Sample(3f, -1f, typeSec).tear, 1e-6f);
            Assert.IsFalse(CommsRecoveryLogic.Sample(3f, -1f, typeSec).aftershock);
        }

        [Test]
        public void Release_ClearsTheResidue_OverTheSameSecondsAsTheScreen()
        {
            Assert.Greater(CommsRecoveryLogic.Sample(5f, -1f, 0f, 0f).residue, 0.4f, "解けた瞬間はまだ残っている");
            Assert.Greater(CommsRecoveryLogic.Sample(5f, -1f, 0f, CommsRecoveryLogic.ReleaseSec * 0.5f).residue, 0f);
            CommsRecoverySample done = CommsRecoveryLogic.Sample(5f, -1f, 0f, CommsRecoveryLogic.ReleaseSec);
            Assert.AreEqual(0f, done.residue, 1e-6f);
            Assert.AreEqual(0f, done.red, 1e-6f);
        }

        [Test]
        public void Red_NeverLandsOnTheCommand()
        {
            // ③b: 最後の行（排除してください / Remove it）は象牙。
            Assert.IsTrue(CommsRecoveryLogic.RedAllowed(isPrompt: true, lineNumber: 0, lastLine: 2));
            Assert.IsFalse(CommsRecoveryLogic.RedAllowed(isPrompt: true, lineNumber: 2, lastLine: 2));
            // ③a: 赤は起動の間だけ字の片側に残り、焼け落ちる。
            Assert.Less(CommsRecoveryLogic.HaltRedCap(0f), 0.6f, "字の片側だけ（遠い側は象牙）");
            Assert.AreEqual(0f, CommsRecoveryLogic.HaltRedCap(CommsRecoveryLogic.HaltRedBurnSec), 1e-6f);
        }

        [Test]
        public void RedOf_IsContinuous_AndRecedes()
        {
            int redAtHigh = 0, redAtLow = 0;
            for (int i = 0; i < 200; i++)
            {
                if (CommsRecoveryLogic.RedOf(i, HighRed) > 0f) redAtHigh++;
                if (CommsRecoveryLogic.RedOf(i, LowRed) > 0f) redAtLow++;
                // 閾値が下がって赤くなる字は無い（引くだけ）。
                Assert.LessOrEqual(CommsRecoveryLogic.RedOf(i, LowRed),
                                   CommsRecoveryLogic.RedOf(i, HighRed) + 1e-6f);
            }
            Assert.Greater(redAtHigh, redAtLow);
            Assert.Greater(redAtLow, 0);
            Assert.Less(redAtHigh, 200, "全部の字を赤くしない");
            Assert.AreEqual(0f, CommsRecoveryLogic.RedOf(3, 0f));
        }

        [Test]
        public void CornerRed_LeavesTheFarSideIvory_UntilTheGlyphIsMostlyRed()
        {
            // 左側（0）から残る字: 左の 2 隅だけが赤い。
            Assert.AreEqual(0.4f, CommsRecoveryLogic.CornerRed(0, 0, 0.4f), 1e-6f);
            Assert.AreEqual(0.4f, CommsRecoveryLogic.CornerRed(0, 1, 0.4f), 1e-6f);
            Assert.AreEqual(0f, CommsRecoveryLogic.CornerRed(0, 2, 0.4f), 1e-6f);
            Assert.AreEqual(0f, CommsRecoveryLogic.CornerRed(0, 3, 0.4f), 1e-6f);
            Assert.AreEqual(1f, CommsRecoveryLogic.CornerRed(0, 2, 1f), 1e-6f);
        }

        [Test]
        public void PanelClock_StartsAtHalt_SurvivesTheChain_StopsWhenFolded()
        {
            var logic = new CommsPanelLogic();
            Assert.Less(logic.RecoverySec, 0f);
            logic.BeginRecovery();
            logic.Begin(9, CommsDelivery.Fade);
            for (int i = 0; i < 30; i++) logic.Tick(Dt);
            float t = logic.RecoverySec;
            Assert.AreEqual(30 * Dt, t, 1e-4f);
            // ③b は同じ面のまま繋ぐ: EnsureRecovery は数え直さない。
            logic.EnsureRecovery();
            Assert.AreEqual(t, logic.RecoverySec, 1e-6f);
            logic.Disable();
            Assert.Less(logic.RecoverySec, 0f, "畳んだら止まる");
            logic.Begin(9, CommsDelivery.Typed);
            Assert.Less(logic.RecoverySec, 0f, "次の連絡へ持ち越さない");
        }

        [Test]
        public void PanelClock_StopsWhenTheNoticeFinishesNaturally()
        {
            var logic = new CommsPanelLogic();
            logic.BeginRecovery();
            logic.Begin(9, CommsDelivery.Fade);
            for (int i = 0; i < 90 * 8; i++) logic.Tick(Dt);
            Assert.IsFalse(logic.Active);
            Assert.Less(logic.RecoverySec, 0f);
            logic.Begin(4, CommsDelivery.Typed);
            Assert.Less(logic.RecoverySec, 0f);
        }
    }
}
