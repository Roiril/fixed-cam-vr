#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// AIエージェントからの連絡（第 2 の面）の段。**読まなくても必ず引く**ことと、
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

        /// <summary>
        /// 報告の押し方は<b>この面の下段</b>に出る（`canon/LEDGER.md` 0058）。
        /// 押している最中は面が開き、離せば引く。<b>AIエージェントの文面は 1 字も出ない。</b>
        /// </summary>
        [Test]
        public void HoldingTheReportButton_OpensThePanel_WithoutAnyMessage()
        {
            var l = new CommsPanelLogic();
            Assert.AreEqual(CommsStage.Off, l.Stage);

            l.SetGuideWanted(true);
            Assert.AreEqual(CommsStage.Guide, l.Stage);
            Advance(l, 5f);
            Assert.AreEqual(CommsStage.Guide, l.Stage, "時間では終わらないこと");
            Assert.AreEqual(1f, l.Weights.open, 0.001f);
            Assert.AreEqual(1f, l.Weights.hint, 0.001f, "下段が出ていること");
            Assert.AreEqual(0f, l.Weights.reveal, 0.001f, "AIエージェントの文面は 1 字も出ないこと");
            Assert.AreEqual(0f, l.Weights.body, 0.001f,
                "連絡が無いのに文面のぶんの丈があると、大きな空の箱になる");

            l.SetGuideWanted(false);
            Assert.AreEqual(CommsStage.Out, l.Stage);
            Advance(l, CommsPanelLogic.OutSec + 0.2f);
            Assert.AreEqual(CommsStage.Off, l.Stage);
        }

        /// <summary>
        /// ⚠⚠ <b>押し終わった瞬間に届く②の連絡で、枠が開き直さない。</b>
        /// 素直に In から始めると<b>押すたびに必ず</b>枠が畳まれて開き直る（毎回起きる吃り）。
        /// 受信票はもう出ているので、装置は印字だけすればよい。
        /// </summary>
        [Test]
        public void AMessageThatArrivesWhileHolding_DoesNotReopenTheFrame()
        {
            var l = new CommsPanelLogic();
            l.SetGuideWanted(true);
            Advance(l, 1f);

            l.Begin(Chars);
            // 横は開いたまま。動くのは**丈だけ**（文面の場所が上へ伸びる）。
            Assert.AreEqual(1f, l.Weights.open, 0.001f, "枠が畳まれて開き直していないこと");
            Assert.AreEqual(0f, l.Weights.body, 0.02f, "丈はまだ伸び始めたところ");

            Advance(l, CommsPanelLogic.InSec * 0.5f);
            Assert.AreEqual(1f, l.Weights.open, 0.001f, "横は最後まで動かないこと");
            Assert.Greater(l.Weights.body, 0.2f, "丈が伸びていること");

            AdvanceUntil(l, CommsStage.Type);
            Assert.AreEqual(1f, l.Weights.body, 0.001f, "印字を始めるときには丈が出ていること");
        }

        /// <summary>まだ押しているあいだは、読ませ終わっても引かずに開いたまま残る。</summary>
        [Test]
        public void AfterReading_ItStaysOpen_WhileStillHolding()
        {
            var l = Started();
            l.SetGuideWanted(true);
            AdvanceUntil(l, CommsStage.Hold);
            Advance(l, CommsPanelLogic.HoldSec + 0.2f);
            Assert.AreEqual(CommsStage.Guide, l.Stage, "押している間は引かないこと");

            l.SetGuideWanted(false);
            Assert.AreEqual(CommsStage.Out, l.Stage);
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
            // 2 通目が来たら**文面は頭から**出し直す（重ねない）。
            // ⚠ ただし**枠は畳まない**（2026-08-16・`canon/LEDGER.md` 0058）。受信票はもう出ているので、
            //   装置は次の行を印字するだけ。畳んで開き直すと、続けて届いた 2 通のあいだで吃る
            //   （②は押すたび届くので、実際に起きる）。
            var l = Started();
            AdvanceUntil(l, CommsStage.Hold);

            l.Begin(Chars);
            // ⚠ 枠も丈も出来上がっているので、**開く段は挟まずに印字から**
            //   （2026-08-19・`canon/LEDGER.md` 0096。挟むと何も動かない 0.45 秒だけ面が空になる）。
            Assert.AreEqual(CommsStage.Type, l.Stage);
            Assert.AreEqual(0f, l.Weights.reveal, 0.001f, "文面は 1 字目から出し直すこと");
            Assert.AreEqual(1f, l.Weights.open, 0.001f, "枠は畳まないこと");
            Assert.AreEqual(1f, l.Weights.body, 0.001f, "丈も保つこと");
            // ⚠⚠ 2026-08-16 まで**濃さと下段だけ 0 から張り直していた** — 走行中の面へ 2 通目が
            //    来ると受信票が 0.16 秒だけ黒へ落ちて戻り、下段も 0.34 秒消えて戻っていた。
            //    上の 3 行しか見ていなかったので素通りしていた（画にしか出ない不具合）。
            Assert.AreEqual(1f, l.Weights.panel, 0.001f, "地の濃さも保つこと（黒へ落として戻さない）");
            Assert.AreEqual(1f, l.Weights.hint, 0.001f, "下段も保つこと（消して戻さない）");
        }

        /// <summary>
        /// 読ませる尺は<b>打つ連絡すべてで同じ</b>（2026-08-19・<c>canon/LEDGER.md</c> 0092・
        /// 「出し切った後残す時間は一律 2s」）。役割ごとに分けていた 3 つ（0065）は畳んだ。
        /// ⚠ <b>長い文面ほど画に居る時間は自然に長い</b>（打つ尺が文字数から決まる）。
        /// ⚠⚠ <b>すっと浮かぶ連絡（③a）だけは別</b>（0168）。あの 2 秒は「打っているあいだに
        /// もう読み終わっている」が前提で、<b>打たない連絡にはその前提が無い</b>。
        /// </summary>
        [Test]
        public void HoldSec_IsTheSameForEveryTypedNotice()
        {
            Assert.AreEqual(2.0f, CommsPanelLogic.HoldSec, 0.001f);
            Assert.AreEqual(CommsPanelLogic.HoldSec,
                            CommsPanelLogic.HoldSecFor(CommsDelivery.Typed), 0.001f);
            // 報告 1 回で面が灯る総尺（押し始めから）。**10 秒級に戻さない**。
            float total = VisitorMarkHoldSec
                        + CommsPanelLogic.InSec + CommsPanelLogic.MinTypeSec
                        + CommsPanelLogic.HoldSec + CommsPanelLogic.OutSec;
            Assert.That(total, Is.LessThan(6f), $"報告 1 回に {total:0.0}s は長い");
        }

        /// <summary>報告の長押し（`VisitorMarkHoldLogic.DefaultHoldSec`）。asmdef を跨がないので値を持つ。</summary>
        private const float VisitorMarkHoldSec = 1.0f;

        // ------------------------------------------------------------------ ③a すっと浮かぶ出方

        /// <summary>
        /// ⚠⚠ <b>すっと浮かぶ連絡は 1 字も「打たない」。</b>（2026-09-06・
        /// <c>canon/LEDGER.md</c> 0168・ユーザー指定「カタカタ音無しにすっと出てくる感じで」）
        ///
        /// 打鍵は <c>CommsPanel.Apply</c> が <b>出た字数の増分</b>から鳴らすので、
        /// ここで <c>reveal</c> が段階的に増えると<b>その刻みだけ打鍵が鳴る</b>。
        /// <b>字は最初から全部そこに在って、濃さだけが上がる</b>のが正。
        /// </summary>
        [Test]
        public void TheFadedNotice_HasEveryLetterFromTheFirstFrame()
        {
            var l = new CommsPanelLogic();
            l.Begin(9, CommsDelivery.Fade);
            AdvanceUntil(l, CommsStage.Type);

            Assert.AreEqual(CommsDelivery.Fade, l.Delivery);
            Assert.AreEqual(1f, l.Weights.reveal, 1e-4f,
                            "字が 1 字ずつ出ている（増分で打鍵が鳴ってしまう）");
            Assert.Less(l.Weights.glyph, 1f, "初手から濃さが全開 ＝ 浮かばずに点いている");

            Advance(l, CommsPanelLogic.FadeInSec);
            Assert.AreEqual(1f, l.Weights.glyph, 0.02f, "浮かび切っていない");
            Assert.AreEqual(1f, l.Weights.reveal, 1e-4f);
        }

        /// <summary>
        /// ⚠ <b>浮かぶ尺は 0 でも長くもない。</b> 0 なら「点いた」に見え、長いと
        /// 「打つ間も惜しい一言」という意味が消える。
        /// </summary>
        [Test]
        public void TheFadedNotice_TakesLessTimeOnScreen_ThanTypingTheSameWords()
        {
            const int Nine = 9;   // 「止まってください！」
            float faded = CommsPanelLogic.FadeInSec + CommsPanelLogic.FadeHoldSec;

            var typed = new CommsPanelLogic();
            typed.Begin(Nine);
            float asTyped = typed.TypeSec + CommsPanelLogic.HoldSec;

            Assert.Greater(CommsPanelLogic.FadeInSec, 0f, "1 フレームで出すと「点いた」に見える");
            Assert.Less(faded, asTyped,
                        $"すっと浮かぶ方が長い（{faded:0.00}s / 打つと {asTyped:0.00}s）");
            Assert.Greater(faded, 1f, "読む間が無い");
        }

        /// <summary>浮かび終わったら、打つ連絡と同じ道を通って引く（段は増やしていない）。</summary>
        [Test]
        public void TheFadedNotice_ClosesByItself_LikeAnyOther()
        {
            var l = new CommsPanelLogic();
            l.Begin(9, CommsDelivery.Fade);
            AdvanceUntil(l, CommsStage.Hold);
            Advance(l, CommsPanelLogic.FadeHoldSec + Dt);
            Assert.AreEqual(CommsStage.Out, l.Stage, "読ませ終わっても引かない");

            // ⚠ 次の連絡（③b）は**打つ**方へ戻る。出方を持ち越すと以後ずっと無音になる。
            l.Begin(24);
            Assert.AreEqual(CommsDelivery.Typed, l.Delivery);
        }

        /// <summary>
        /// ⚠⚠ <b>押し始めたら、走っている連絡は片づく</b>（2026-08-16・<c>canon/LEDGER.md</c> 0065）。
        /// ユーザーの「X／Y を押して閉じる」に対する答え — <b>X／Y に 2 つ目の意味を与えずに</b>
        /// 「自分の行為がこの面に効く」を返す（記録の時刻は 1 ビットも濁らない）。
        ///
        /// ⚠ 移る先は <see cref="CommsStage.Out"/> ではなく <see cref="CommsStage.Guide"/> —
        /// 枠は開いたまま、丈だけ縮んで文面が消える。畳んでから開き直すと、
        /// 1 秒後に届く②の連絡で必ず吃る。
        /// </summary>
        [Test]
        public void PressingAgain_ClearsTheRunningNotice_WithoutFoldingTheFrame()
        {
            var l = Started();
            AdvanceUntil(l, CommsStage.Hold);

            l.SetGuideWanted(true);          // 体験者が次の報告を押し始めた

            Assert.AreEqual(CommsStage.Guide, l.Stage, "走っている連絡が片づいていない");
            Assert.AreEqual(0f, l.Weights.reveal, 0.001f, "文面が残っている");
            Assert.AreEqual(1f, l.Weights.open, 0.001f, "枠を畳まないこと（1 秒後に②が来る）");
        }

        /// <summary>
        /// ⚠ 押しっぱなしのあいだ<b>毎フレーム</b>「開きたい」が立つ。縁でしか片づけないので、
        /// 押している最中に届いた②の連絡が<b>その場で消える</b>ことは無い
        /// （消えると、押した手応えが 1 つも返らない）。
        /// </summary>
        [Test]
        public void HoldingDown_DoesNotEatTheNoticeItJustTriggered()
        {
            var l = new CommsPanelLogic();
            l.SetGuideWanted(true);                       // 押し始め
            AdvanceUntil(l, CommsStage.Guide);
            l.SetGuideWanted(true);                       // 押しっぱなし（縁ではない）
            l.Begin(Chars);                               // 長押し成立 → ②が届く
            AdvanceUntil(l, CommsStage.Type);

            l.SetGuideWanted(true);                       // まだ離していない
            Assert.AreEqual(CommsStage.Type, l.Stage, "自分で起こした連絡を自分で消している");
        }

        [Test]
        public void NoLetters_SkipsTheTypingStage()
        {
            // 文字数が取れない構成（フォント解決に失敗した等）でも段で止まらない。
            var l = new CommsPanelLogic();
            l.Begin(0);
            AdvanceUntil(l, CommsStage.Hold);
        }

        /// <summary>
        /// ⚠⚠ <b>連続して言う 2 通は、同じ面のまま繋がる</b>（2026-08-19・
        /// <c>canon/LEDGER.md</c> 0096・ユーザー指定「前の言葉を表示して 2s たったら、
        /// そのスクリーンのまま、次の言葉が始まる。毎回消して表示しなおすのはしない」）。
        ///
        /// 守るのは 2 つ — <b>枠を畳まない</b>（開き 1 のまま）と、<b>開く段を挟まない</b>
        /// （挟むと何も動かない 0.45 秒のあいだ面が空になる ＝ 消えて、待って、また出た、に見える）。
        /// </summary>
        [Test]
        public void TheNextNotice_ContinuesOnTheSameScreen()
        {
            var l = Started();
            AdvanceUntil(l, CommsStage.Out);   // ＝ 読ませ終わった縁
            Assert.IsTrue(l.DoneReading, "読ませ終わりの合図が立っていない");
            Assert.AreEqual(1f, l.Weights.open, 0.001f, "この瞬間はまだ枠が開いていること");

            l.Begin(Chars);                       // 次の連絡が届く

            Assert.AreEqual(CommsStage.Type, l.Stage, "開く段を挟んでいる（面が空になる）");
            Assert.AreEqual(1f, l.Weights.open, 0.001f, "枠を畳んでいる");
            Assert.AreEqual(1f, l.Weights.body, 0.001f, "丈を張り直している");
            Assert.AreEqual(0f, l.Weights.reveal, 0.001f, "前の文面が残っている");
        }

        /// <summary>
        /// ⚠ <b>畳み切った後は、ちゃんと開く段から出す。</b> 上のテストと対で、
        /// 「常に開く段を飛ばす」実装に倒れていないことを固定する。
        /// </summary>
        [Test]
        public void AfterItFolded_TheNextNoticeOpensAgain()
        {
            var l = Started();
            Advance(l, CommsPanelLogic.InSec + l.TypeSec + CommsPanelLogic.HoldSec
                     + CommsPanelLogic.OutSec + 0.5f);
            Assert.AreEqual(CommsStage.Off, l.Stage);

            l.Begin(Chars);
            Assert.AreEqual(CommsStage.In, l.Stage, "畳んだ後は開く段から出すこと");
            Assert.AreEqual(0f, l.Weights.open, 0.001f);
        }
    }
}
