#nullable enable
using FixedCamVr.Input;
using NUnit.Framework;

namespace FixedCamVr.Input.Tests
{
    /// <summary>
    /// 体験者の報告ボタン（左 X / 左 Y）の長押し。
    /// 判定は <c>canon/LEDGER.md</c> 0050（「長押し 2s で報告できるように」）と
    /// 0059（2026-08-16 に半分の 1.0 秒へ）。
    ///
    /// ⚠ <b>秒数を直に書かない。</b> 閾値から逆算する（<see cref="Full"/> / <see cref="Short"/>）
    /// — 直書きすると、閾値を変えるたびに**テストの方を直すことになり、
    /// 「変えたのに落ちない」テストになる**。
    /// </summary>
    public sealed class VisitorMarkHoldLogicTests
    {
        // 90Hz の 1 フレーム。実機と同じ刻みで積む。
        private const float Dt = 1f / 90f;

        /// <summary>確実に発火する長さ（閾値 ＋ 1 フレーム余分）。</summary>
        private const float Full = VisitorMarkHoldLogic.DefaultHoldSec + 0.05f;

        /// <summary>発火しない長さ（閾値の 3/4）。ゲージは動くが届かない。</summary>
        private const float Short = VisitorMarkHoldLogic.DefaultHoldSec * 0.75f;

        private static bool Hold(VisitorMarkHoldLogic logic, float seconds, bool held = true)
        {
            bool fired = false;
            int frames = (int)System.Math.Round(seconds / Dt);
            for (int i = 0; i < frames; i++)
                if (logic.Tick(Dt, held)) fired = true;
            return fired;
        }

        [Test]
        public void ShortPress_DoesNotReport_AndGaugeReturnsToZero()
        {
            var logic = new VisitorMarkHoldLogic();

            Assert.That(Hold(logic, Short), Is.False, "閾値に届かない押しで報告が通ってはいけない");
            Assert.That(logic.Progress01, Is.GreaterThan(0.6f).And.LessThan(1f));

            // 離す
            Assert.That(logic.Tick(Dt, false), Is.False);
            Assert.That(logic.Progress01, Is.EqualTo(0f), "離したらゲージは 0 へ戻る");
            Assert.That(logic.Confirming, Is.False);
        }

        [Test]
        public void HoldToThreshold_ReportsExactlyOnce_EvenIfKeptHeld()
        {
            var logic = new VisitorMarkHoldLogic();

            Assert.That(Hold(logic, Full), Is.True, "閾値ちょうどで 1 回発火する");
            Assert.That(logic.Progress01, Is.EqualTo(1f));
            Assert.That(logic.Confirming, Is.True, "発火直後は「報告しました」の余韻が立つ");

            // 押しっぱなしのまま更に 3 秒 — 2 回目は出ない
            Assert.That(Hold(logic, 3f), Is.False, "押しっぱなしで連射になってはいけない");
        }

        [Test]
        public void ReleaseThenPressAgain_CanReportAgain()
        {
            var logic = new VisitorMarkHoldLogic();
            Assert.That(Hold(logic, Full), Is.True);

            // 離す → 押し直す
            Hold(logic, 0.3f, held: false);
            Assert.That(logic.Progress01, Is.EqualTo(0f));
            Assert.That(Hold(logic, Full), Is.True, "離して押し直せば何度でも報告できる");
        }

        [Test]
        public void ConfirmWindow_ExpiresOnItsOwn()
        {
            var logic = new VisitorMarkHoldLogic();
            Hold(logic, Full);
            Assert.That(logic.Confirming, Is.True);

            Hold(logic, VisitorMarkHoldLogic.DefaultConfirmSec + 0.1f, held: false);
            Assert.That(logic.Confirming, Is.False, "余韻は自分で消える（消す操作を作らない）");
        }

        /// <summary>
        /// ⚠ 起動直後・復帰直後は dt が数秒飛ぶ。そのフレームに握っていても発火してはいけない
        /// （握った瞬間に「閾値ぶん」が積まれる）。
        /// </summary>
        [Test]
        public void HugeDeltaTime_CannotFireInOneFrame()
        {
            var logic = new VisitorMarkHoldLogic();

            Assert.That(logic.Tick(5f, true), Is.False, "1 フレームの dt は MaxStepSec で切る");
            Assert.That(logic.Progress01,
                Is.EqualTo(VisitorMarkHoldLogic.MaxStepSec / VisitorMarkHoldLogic.DefaultHoldSec).Within(1e-4f));
        }

        [Test]
        public void Takeover_CancelsPartialHoldAndRequiresReleaseAfterItEnds()
        {
            var logic = new VisitorMarkHoldLogic();
            Hold(logic, 0.8f);
            for (int i = 0; i < 300; i++)
            {
                Assert.IsFalse(logic.Tick(0.03f, true, blocked: true));
                Assert.AreEqual(0f, logic.Progress01);
                Assert.IsFalse(logic.Confirming);
            }
            for (int i = 0; i < 80; i++) Assert.IsFalse(logic.Tick(0.03f, true));
            Assert.IsFalse(logic.Tick(0.03f, false));
            Assert.IsFalse(logic.Tick(0.25f, true));
            Assert.IsFalse(logic.Tick(0.25f, true));
            Assert.IsFalse(logic.Tick(0.25f, true));
            Assert.IsTrue(logic.Tick(0.25f, true));
        }

        [Test]
        public void Takeover_DiscardsConfirmationAndRapidPresses()
        {
            var logic = new VisitorMarkHoldLogic();
            Hold(logic, Full);
            Assert.IsTrue(logic.Confirming);
            for (int i = 0; i < 100; i++)
                Assert.IsFalse(logic.Tick(0.03f, i % 2 == 0, blocked: true));
            Assert.IsFalse(logic.Confirming);
            Assert.AreEqual(0f, logic.Progress01);
        }

        [Test]
        public void Reset_ClearsHoldAndConfirm()
        {
            var logic = new VisitorMarkHoldLogic();
            Hold(logic, Full);

            logic.Reset();

            Assert.That(logic.Progress01, Is.EqualTo(0f));
            Assert.That(logic.Confirming, Is.False);
            Assert.That(logic.Holding, Is.False);
        }
    }
}
