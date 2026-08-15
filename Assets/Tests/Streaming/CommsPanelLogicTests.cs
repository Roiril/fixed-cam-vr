#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 上司からの連絡（第 2 の面）の段。**読まなくても必ず引く**ことと、
    /// **枠が開いてから打ち、消してから畳む**という順序を固定する（`canon/LEDGER.md` 0053）。
    ///
    /// ⚠ 順序が崩れると、畳む枠から文字がはみ出す / 開き切る前に文字が枠の外へ出る、
    /// という**絵からしか分からない壊れ方**をする。だからここで数値として押さえておく。
    /// </summary>
    public sealed class CommsPanelLogicTests
    {
        private const float Dt = 1f / 72f;
        private const int Chars = 26;   // 現行の文面（11 + 改行 + 14）

        private static void Advance(CommsPanelLogic l, float sec)
        {
            int n = (int)(sec / Dt);
            for (int i = 0; i < n; i++) l.Tick(Dt);
        }

        /// <summary>
        /// その段へ入るまで進める。⚠ <b>秒で足し算しない</b> — 段が変わるたびに
        /// その刻みの余りが捨てられる（`_elapsed = 0`）ので、遷移の数だけ 1 フレームずつ遅れる。
        /// </summary>
        private static void AdvanceUntil(CommsPanelLogic l, CommsStage stage)
        {
            for (int i = 0; i < 5000 && l.Stage != stage; i++) l.Tick(Dt);
            Assert.AreEqual(stage, l.Stage, "その段へ到達しなかった");
        }

        private static CommsPanelLogic Started()
        {
            var l = new CommsPanelLogic();
            l.Begin(Chars);
            return l;
        }

        [Test]
        public void ItAlwaysClosesByItself_WithoutAnyRead()
        {
            // ⚠ **本編の進行を既読待ちにしない。** 体験者が持つ入力は左 X（記録）だけで、
            //    既読の操作を作ると「記録した」と「読んだ」が混ざる。
            var l = Started();
            Assert.IsTrue(l.Active);

            Advance(l, CommsPanelLogic.InSec + l.TypeSec + CommsPanelLogic.HoldSec
                     + CommsPanelLogic.OutSec + 0.5f);
            Assert.AreEqual(CommsStage.Off, l.Stage, "誰も読まなくても引くこと");
            Assert.AreEqual(0f, l.Weights.panel, 0.001f);
            Assert.AreEqual(0f, l.Weights.open, 0.001f);
        }

        [Test]
        public void TheFrameOpensBeforeAnyLetterIsTyped()
        {
            // 枠が開き切る前に文字が出ると、開いていない側へ字がはみ出す。
            var l = Started();
            for (float t = 0f; t < CommsPanelLogic.InSec - Dt; t += Dt)
            {
                l.Tick(Dt);
                Assert.AreEqual(0f, l.Weights.reveal, 0.0001f, $"t={t:0.00} で字が出ている");
                Assert.Less(l.Weights.open, 1f);
            }
        }

        [Test]
        public void TheFrameOpensFromZero_AndReachesFull()
        {
            var l = Started();
            Assert.AreEqual(0f, l.Weights.open, 0.001f, "始まりは左端に畳まれている");

            AdvanceUntil(l, CommsStage.Type);
            Assert.AreEqual(1f, l.Weights.open, 0.001f, "打ち始めるときには開き切っている");
        }

        [Test]
        public void LettersComeOutOneByOne_LinearlyInTime()
        {
            // ⚠ 打つところを均さない（smoothstep を掛けると打鍵の間隔が伸び縮みする）。
            var l = Started();
            AdvanceUntil(l, CommsStage.Type);

            float half = l.TypeSec * 0.5f;
            Advance(l, half);
            Assert.AreEqual(0.5f, l.Weights.reveal, 0.06f, "半分の時刻で半分ほど出ている");

            AdvanceUntil(l, CommsStage.Hold);
            Assert.AreEqual(1f, l.Weights.reveal, 0.001f);
        }

        [Test]
        public void TypingTakesLonger_WhenThereAreMoreLetters()
        {
            var shortMsg = new CommsPanelLogic();
            shortMsg.Begin(8);
            var longMsg = new CommsPanelLogic();
            longMsg.Begin(40);
            Assert.Less(shortMsg.TypeSec, longMsg.TypeSec, "文面が伸びれば打つ時間も伸びる");
            Assert.GreaterOrEqual(shortMsg.TypeSec, CommsPanelLogic.MinTypeSec);
            Assert.LessOrEqual(longMsg.TypeSec, CommsPanelLogic.MaxTypeSec);
        }

        [Test]
        public void TheTextIsGoneBeforeTheFrameFolds()
        {
            // 引くときは文字が先に消える。⚠ 逆にすると、畳む枠から文字がはみ出して
            //    「潰された」に見える（同時に消すと「電源が落ちた」に見える）。
            var l = Started();
            AdvanceUntil(l, CommsStage.Out);

            // 枠が畳まれ始める時点では、文字はもう消えている。
            Advance(l, CommsPanelLogic.OutSec * CommsPanelLogic.FoldStartAt);
            Assert.AreEqual(0f, l.Weights.glyph, 0.02f, "畳み始めに文字が残っている");
            Assert.AreEqual(1f, l.Weights.open, 0.02f, "文字が消える前に畳み始めている");
        }

        [Test]
        public void BeginAgain_RestartsFromTheHead()
        {
            // 2 通目が来たら頭から出し直す（重ねない）。
            var l = Started();
            AdvanceUntil(l, CommsStage.Hold);

            l.Begin(Chars);
            Assert.AreEqual(CommsStage.In, l.Stage);
            Assert.AreEqual(0f, l.Weights.open, 0.001f);
            Assert.AreEqual(0f, l.Weights.reveal, 0.001f);
        }

        [Test]
        public void NoLetters_SkipsTheTypingStage()
        {
            // 文字数が取れない構成（フォント解決に失敗した等）でも段で止まらない。
            var l = new CommsPanelLogic();
            l.Begin(0);
            AdvanceUntil(l, CommsStage.Hold);
        }
    }
}
