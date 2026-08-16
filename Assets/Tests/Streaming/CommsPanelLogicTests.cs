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

        /// <summary>
        /// 打つ速さは<b>そのまま打鍵音の間隔になる</b>（`canon/LEDGER.md` 0056）。
        /// 詰めすぎると 1 発ずつが分かれて聞こえず、カタカタではなく連続音になる。
        ///
        /// ⚠ 60ms は素材の実測から来ている — もらった録音は押し込みの 76.9ms 後に戻りが来て、
        /// 切り出しは 60ms（<c>tools/ingest-sounds.py</c> の <c>CUT_BODY</c>）。
        /// ここを下回ると<b>前の打鍵が鳴り終わる前に次が重なる</b>。
        /// </summary>
        [Test]
        public void TypingSpeed_KeepsKeystrokesApart()
        {
            float stepSec = 1f / CommsPanelLogic.CharsPerSec;
            Assert.GreaterOrEqual(stepSec, 0.060f,
                "打鍵が重なる速さ（1 発 60ms + 間隔）。速くするなら素材を切り直すこと");
            Assert.LessOrEqual(stepSec, 0.120f, "遅すぎると読み終わる前に焦れる");
        }

        /// <summary>
        /// <b>打鍵音の変種の数と、焼いてある音源の本数を突き合わせる。</b>
        ///
        /// ⚠ 片方だけ増減すると**沈黙して食い違う** — 多いと無い音を掴もうとして
        /// その回だけ鳴らず、少ないと焼いた音の一部が一生鳴らない。どちらも実機で
        /// 気づけない（音は録画に映らない）。
        /// </summary>
        [Test]
        public void TypeSfx_VariantCount_MatchesTheBakedClips()
        {
            string dir = System.IO.Path.Combine(UnityEngine.Application.dataPath,
                                                "Resources", "Sound");
            Assert.IsTrue(System.IO.Directory.Exists(dir), $"音の置き場が無い: {dir}");
            int baked = System.IO.Directory.GetFiles(dir, "sfx_type_*.wav").Length;
            Assert.AreEqual(TypeAudioCue.VariantCount, baked,
                "TypeAudioCue.VariantCount と tools/ingest-sounds.py の CUTS が食い違っている");
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
