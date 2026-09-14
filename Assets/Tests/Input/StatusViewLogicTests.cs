#nullable enable
using FixedCamVr.Input;
using NUnit.Framework;

namespace FixedCamVr.Input.Tests
{
    /// <summary>
    /// ステータス表示（右 B）は<b>押しているあいだだけ</b>出る（2026-09-14）。
    ///
    /// ⚠ <b>秒数を直に書かない。</b> 定数から逆算する — 直書きすると、値を変えるたびに
    /// テストの方を直すことになり「変えたのに落ちない」テストになる
    /// （<c>VisitorMarkHoldLogicTests</c> と同じ約束）。
    /// </summary>
    public sealed class StatusViewLogicTests
    {
        // 90Hz の 1 フレーム。実機と同じ刻みで積む。
        private const float Dt = 1f / 90f;

        private static void Hold(StatusViewLogic logic, float seconds, bool held = true)
        {
            int frames = (int)System.Math.Round(seconds / Dt);
            for (int i = 0; i < frames; i++) logic.Tick(held, Dt);
        }

        [Test]
        public void Default_IsHidden()
        {
            var logic = new StatusViewLogic();

            Assert.That(logic.Visible, Is.False, "押していないあいだは出さない（体験者の視界を守る）");
            Assert.That(logic.Alpha01, Is.EqualTo(0f));
            Assert.That(logic.JustShown, Is.False);
        }

        [Test]
        public void Press_ShowsImmediately_AndJustShownFiresOnce()
        {
            var logic = new StatusViewLogic();

            logic.Tick(true, Dt);
            Assert.That(logic.Visible, Is.True, "押した瞬間から出る（不表示の頭は作らない）");
            Assert.That(logic.Alpha01, Is.GreaterThan(0f));
            Assert.That(logic.JustShown, Is.True);

            // 押しっぱなしにしても 2 回目は出ない（振動が鳴りっぱなしにならない）。
            Hold(logic, 1f);
            Assert.That(logic.JustShown, Is.False);
        }

        [Test]
        public void FadeIn_ReachesFullAtFadeInSec()
        {
            var logic = new StatusViewLogic();

            Hold(logic, StatusViewLogic.FadeInSec * 0.5f);
            Assert.That(logic.Alpha01, Is.GreaterThan(0.3f).And.LessThan(0.8f), "途中は中間の濃さ");

            Hold(logic, StatusViewLogic.FadeInSec);
            Assert.That(logic.Alpha01, Is.EqualTo(1f), "立ち上がりきったら 1");
        }

        [Test]
        public void Release_StaysVisibleThroughGrace_ThenHides()
        {
            var logic = new StatusViewLogic();
            Hold(logic, StatusViewLogic.FadeInSec * 2f);

            // 猶予の手前（指がわずかに浮いた程度）ではまだ消えない。
            logic.Tick(false, StatusViewLogic.ReleaseGraceSec - 0.01f);
            Assert.That(logic.Visible, Is.True);
            Assert.That(logic.Alpha01, Is.GreaterThan(0f).And.LessThan(1f), "猶予の間に薄れていく");

            logic.Tick(false, 0.02f);
            Assert.That(logic.Visible, Is.False, "猶予を過ぎたら消える");
            Assert.That(logic.Alpha01, Is.EqualTo(0f));
        }

        [Test]
        public void Release_LongerThanGrace_HidesInOneTick()
        {
            var logic = new StatusViewLogic();
            Hold(logic, StatusViewLogic.FadeInSec * 2f);

            logic.Tick(false, StatusViewLogic.ReleaseGraceSec + 0.01f);
            Assert.That(logic.Visible, Is.False);
            Assert.That(logic.Alpha01, Is.EqualTo(0f));
        }

        [Test]
        public void PressAgainDuringGrace_DoesNotFireJustShown()
        {
            var logic = new StatusViewLogic();
            Hold(logic, StatusViewLogic.FadeInSec * 2f);

            logic.Tick(false, StatusViewLogic.ReleaseGraceSec * 0.5f);
            Assert.That(logic.Visible, Is.True);

            // 面は消えていないので「出た」縁ではない（振動を二重に鳴らさない）。
            logic.Tick(true, Dt);
            Assert.That(logic.JustShown, Is.False);
            Assert.That(logic.Visible, Is.True);

            Hold(logic, StatusViewLogic.FadeInSec);
            Assert.That(logic.Alpha01, Is.EqualTo(1f), "押し直したら立ち上がりへ戻る");
        }

        [Test]
        public void ZeroDelta_DoesNotBreak()
        {
            var logic = new StatusViewLogic();

            logic.Tick(true, 0f);
            Assert.That(logic.Visible, Is.True, "dt=0 のフレームでも押した事実は届く");
            Assert.That(logic.Alpha01, Is.EqualTo(0f));

            Hold(logic, StatusViewLogic.FadeInSec);
            logic.Tick(false, 0f);
            Assert.That(logic.Visible, Is.True, "dt=0 では猶予が進まない");

            // 負の dt も 0 として扱う（復帰直後の飛びで猶予が巻き戻らない）。
            logic.Tick(false, -1f);
            Assert.That(logic.Visible, Is.True);
        }

        [Test]
        public void Reset_ClearsEverything()
        {
            var logic = new StatusViewLogic();
            Hold(logic, StatusViewLogic.FadeInSec * 2f);
            Assert.That(logic.Visible, Is.True);

            logic.Reset();
            Assert.That(logic.Visible, Is.False);
            Assert.That(logic.Alpha01, Is.EqualTo(0f));
            Assert.That(logic.JustShown, Is.False);
        }
    }
}
