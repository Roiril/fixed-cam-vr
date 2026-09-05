#nullable enable
using FixedCamVr.Diagnostics;
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Diagnostics.Tests
{
    /// <summary>
    /// <b>体験前の注意書き</b>と、そこに載っている<b>言語の選択</b>
    /// （2026-09-03 ユーザー指定「言語選択をできるようにしてほしい。日本語、英語、フランス語の
    /// 3 種類で。最初の注意書きが表示されている間に、体験者がもつコントローラーから
    /// 切り替えできるように」）。
    ///
    /// ⚠ ここが守るのは<b>実機でしか出ない壊れ方</b>:
    /// 行が枠を超えて首を振らないと読めなくなる／選んでいる言語が分からない／
    /// 訳し忘れて 1 言語だけ日本語のまま出る。
    /// どれも <c>menu text-audit</c> の絵に写らない（絵は大きさとはみ出し専用）。
    ///
    /// ⚠ 文言を変えたら <c>.\tools\unity.ps1 menu hud-font</c> を再実行する
    /// （静的ベイクなので、ここに無い字は実機で豆腐になる）。
    /// </summary>
    public sealed class TitleNoticeTextTests
    {
        /// <summary>面が持つ 2 つの状態（ホラー軽減モードの外 / 中）。<b>両方測る</b>。</summary>
        private static readonly bool[] BothReliefStates = { false, true };

        [SetUp]
        public void ResetRelief() => HorrorRelief.Reset();

        [TearDown]
        public void RestoreRelief() => HorrorRelief.Reset();

        /// <summary>
        /// 1 行に入る全角の数（枠 1.70m ÷ 2.6m 先で 1 文字 1.8°）。
        /// ⚠ 机上の目安で、実測は <c>menu text-audit -Set lang=…</c>。
        /// </summary>
        private const float MaxFullWidthPerLine = 20f;

        /// <summary>
        /// 小さな案内（1.5°）の 1 行に入る全角の数。本文より字が小さいぶん多く入る
        /// （20 × 1.8 ÷ 1.5 ＝ 24）。⚠ <b>本文の物差しで測ると理由なく落ちる</b>。
        /// </summary>
        private const float MaxFullWidthPerFooterLine = 24f;

        /// <summary>物差しは全面で 1 つ（<see cref="HmdTextStyle.LineWidth"/>）。
        /// ⚠ 別々に数えると、片方だけアクセント付きを全角と数えて理由なく落ちる。</summary>
        private static float FullWidth(string line) => HmdTextStyle.LineWidth(line);

        [Test]
        public void EveryLine_FitsTheFrame()
        {
            foreach (ShowLang lang in ShowLanguage.All)
            foreach (string line in TitleNotice.ComposeFor(lang).Split('\n'))
                Assert.LessOrEqual(FullWidth(line), MaxFullWidthPerLine,
                                   $"[{ShowLanguage.Code(lang)}]「{line}」が 1 行に入らない"
                                   + "（折り返して行が増え、塊が視界の上下へはみ出す）");
        }

        /// <summary>
        /// <b>3 つの名前を常に全部出す。</b> 次の言語だけを出す形にすると、
        /// いま何が選べるのかが分からない（体験者は 1 度も押さずに諦める）。
        /// </summary>
        [Test]
        public void EveryLanguage_ListsAllThreeNames()
        {
            foreach (ShowLang lang in ShowLanguage.All)
            {
                string all = TitleNotice.ComposeFor(lang);
                StringAssert.Contains("日本語", all, $"{lang}");
                StringAssert.Contains("English", all, $"{lang}");
                StringAssert.Contains("Français", all, $"{lang}");
            }
        }

        /// <summary>
        /// ⚠⚠ <b>いま選んでいるものが 1 つだけ囲まれている。</b> この面は <c>richText</c> を
        /// 切ってあり白 1 色しか出せないので、<b>括弧だけが「選ばれている」の唯一の手掛かり</b>。
        /// 0 個だとどれを選んでいるか分からず、2 個だと 2 つ選べるように見える。
        /// </summary>
        [Test]
        public void ExactlyOneEntry_IsMarked()
        {
            foreach (ShowLang lang in ShowLanguage.All)
            {
                string all = TitleNotice.ComposeFor(lang);
                int open = Count(all, '［');
                Assert.AreEqual(1, open, $"{lang}: 囲みが {open} 個");
                Assert.AreEqual(1, Count(all, '］'), $"{lang}: 閉じ括弧の数");
            }
        }

        /// <summary>
        /// 訳し忘れの検出。<b>3 つの本文がどれも違う</b>ことだけを見る
        /// （中身の正しさは人が読むしかないが、<b>丸ごとコピーした</b>のは機械で分かる）。
        /// </summary>
        [Test]
        public void EveryLanguage_HasItsOwnBody()
        {
            Assert.AreNotEqual(TitleNotice.BodyFor(ShowLang.Ja), TitleNotice.BodyFor(ShowLang.En));
            Assert.AreNotEqual(TitleNotice.BodyFor(ShowLang.En), TitleNotice.BodyFor(ShowLang.Fr));
            Assert.AreNotEqual(TitleNotice.BodyFor(ShowLang.Fr), TitleNotice.BodyFor(ShowLang.Ja));
            foreach (ShowLang lang in ShowLanguage.All)
                Assert.That(TitleNotice.BodyFor(lang), Is.Not.Empty, $"{lang}");
        }

        /// <summary>
        /// <b>安全の掲示は言語で内容を変えない。</b> 日本語が言っている 2 つ
        /// （① 怖い表現が入っている ② 気分が悪くなったら外してスタッフへ）は、
        /// どの言語でも<b>空行で分けた 2 つの塊</b>として出る。
        /// ⚠ 片方だけ訳し落とすと、その言語の体験者だけ逃げ方を知らないまま入る。
        /// </summary>
        [Test]
        public void EveryLanguage_KeepsBothHalvesOfTheNotice()
        {
            foreach (ShowLang lang in ShowLanguage.All)
            {
                string[] blocks = TitleNotice.BodyFor(lang).Split(new[] { "\n\n" },
                                                                  System.StringSplitOptions.None);
                Assert.AreEqual(2, blocks.Length, $"{lang}: 空行で分かれた 2 つの塊");
                foreach (string b in blocks) Assert.That(b.Trim(), Is.Not.Empty, $"{lang}");
            }
        }

        /// <summary>
        /// 本文と選択のあいだは空行 1 つで分ける（掲示と操作が地続きに読めないように）。
        /// 全体は <b>本文 ＋ 空行 ＋ 並び</b>（切り替え方は小さな案内が持つ）。
        /// </summary>
        [Test]
        public void Compose_PutsTheChooserBelowTheNotice()
        {
            foreach (ShowLang lang in ShowLanguage.All)
            {
                string all = TitleNotice.ComposeFor(lang);
                string body = TitleNotice.BodyFor(lang);
                StringAssert.StartsWith(body + "\n\n", all, $"{lang}");
                string[] tail = all.Substring(body.Length + 2).Split('\n');
                Assert.AreEqual(1, tail.Length, $"{lang}: 並びの 1 行だけ");
                StringAssert.Contains("］", tail[0], $"{lang}: 並びの行に囲みが無い");
            }
        }

        // --- 小さな案内（2026-09-04・0147）------------------------------------------

        /// <summary>
        /// ⚠⚠ <b>切り替え方は 3 言語ぶん出す。</b> 選択中の言語だけで書くと、
        /// <b>それを読めない人には切り替え方が届かない</b>（＝ 日本語のまま始めるしかない）。
        /// これがこの案内の存在理由そのものなので、機械で持つ。
        /// </summary>
        [Test]
        public void Footer_ExplainsSwitchingInEveryLanguage()
        {
            foreach (bool relief in BothReliefStates)
            foreach (ShowLang lang in ShowLanguage.All)
            {
                string f = TitleNotice.FooterFor(lang, relief);
                StringAssert.Contains("ボタン", f, $"[{lang}] 日本語で切り替え方が書かれていない");
                StringAssert.Contains("button", f, $"[{lang}] English で切り替え方が書かれていない");
                StringAssert.Contains("Appuyez", f, $"[{lang}] Français で切り替え方が書かれていない");
            }
        }

        /// <summary>
        /// <b>選んだ後どうするかは、選んでいる言語で 1 行だけ</b>（0151 で 3 言語 → 1 行へ整理）。
        /// この行を読むのは言語を選んだ後なので、そのとき選ばれているのは<b>読める言語</b>。
        /// ⚠ 3 言語ぶん並べると、誰にとっても 2 行が読めない字の壁になる。
        /// </summary>
        [Test]
        public void Footer_TellsWhatToDoNext_InTheChosenLanguage()
        {
            (ShowLang lang, string mine, string[] others)[] cases =
            {
                (ShowLang.Ja, "スタッフにお声がけください", new[] { "staff member", "personnel" }),
                (ShowLang.En, "tell a staff member", new[] { "スタッフにお声がけ", "prévenez le personnel" }),
                (ShowLang.Fr, "prévenez le personnel", new[] { "スタッフにお声がけ", "tell a staff member" }),
            };
            foreach (bool relief in BothReliefStates)
            foreach ((ShowLang lang, string mine, string[] others) in cases)
            {
                string f = TitleNotice.FooterFor(lang, relief);
                StringAssert.Contains(mine, f, $"[{lang}] 次にすることが自分の言語で無い");
                foreach (string other in others)
                    StringAssert.DoesNotContain(other, f, $"[{lang}] 他の言語の行まで出ている");
            }
        }

        /// <summary>案内の行も枠に入る（本文より小さいので入る数が違う）。</summary>
        [Test]
        public void FooterLines_FitTheFrame()
        {
            foreach (bool relief in BothReliefStates)
            foreach (ShowLang lang in ShowLanguage.All)
            foreach (string line in TitleNotice.FooterFor(lang, relief).Split('\n'))
                Assert.LessOrEqual(FullWidth(line), MaxFullWidthPerFooterLine,
                                   $"[{ShowLanguage.Code(lang)}/軽減{relief}] "
                                   + $"案内の「{line}」が 1 行に入らない");
        }

        /// <summary>
        /// <b>案内は 5 行</b>（切り替え方 3 行 ＋ 空行 ＋ ホラー軽減 ＋ 次にすること）。
        /// ⚠ 行が増えると塊が縦に伸びて、上下の端を読むのに首を振ることになる
        /// （縦の実測は <c>TitleNoticeLayoutTests.TheWholeStack_FitsInTheView</c>）。
        /// ⚠ <b>下の 2 行は空行で離さない</b> — どちらも選んでいる言語なので 1 つの塊。
        /// 離すと 0151 で減らした「字の壁」がまた立つ。
        /// </summary>
        [Test]
        public void Footer_KeepsItsSixLines()
        {
            foreach (bool relief in BothReliefStates)
            foreach (ShowLang lang in ShowLanguage.All)
            {
                string[] lines = TitleNotice.FooterFor(lang, relief).Split('\n');
                Assert.AreEqual(6, lines.Length,
                                $"[{lang}] 切り替え方 3 ＋ 空行 ＋ ホラー軽減 ＋ 次にすること");
                Assert.That(lines[3], Is.Empty, $"[{lang}] 下の 2 行は空行で離す");
                Assert.That(lines[4], Is.Not.Empty, $"[{lang}] ホラー軽減の行が無い");
            }
        }

        // --- ホラー軽減モード（2026-09-05・0154）--------------------------------------

        /// <summary>
        /// ⚠⚠ <b>各言語で書く</b>（ユーザー指定「各言語で、ボタン長押しするとホラー軽減モードに
        /// 入れますと書き入れといてほしい」）。訳し忘れると、その言語の体験者だけ
        /// <b>逃げ道があることを知らないまま怖い体験に入る</b>。
        /// </summary>
        [Test]
        public void Relief_IsWrittenInEveryLanguage()
        {
            foreach (bool relief in BothReliefStates)
            {
                Assert.AreNotEqual(TitleNotice.ReliefLineOf(ShowLang.Ja, relief),
                                   TitleNotice.ReliefLineOf(ShowLang.En, relief));
                Assert.AreNotEqual(TitleNotice.ReliefLineOf(ShowLang.En, relief),
                                   TitleNotice.ReliefLineOf(ShowLang.Fr, relief));
                Assert.AreNotEqual(TitleNotice.ReliefLineOf(ShowLang.Fr, relief),
                                   TitleNotice.ReliefLineOf(ShowLang.Ja, relief));
                foreach (ShowLang lang in ShowLanguage.All)
                    Assert.That(TitleNotice.ReliefLineOf(lang, relief), Is.Not.Empty, $"{lang}");
            }
        }

        /// <summary>
        /// <b>入る前は「どうすれば入れるか」と「何が起きるか」の両方を言う</b>
        /// （ユーザー指定「ホラー軽減モード：既存の音が1/2になり、陽気なBGMが流れます と簡単に説明を」）。
        /// ⚠ 入り方だけだと、体験者は何が起きるか分からないまま押すか押さないかを決めることになる。
        /// </summary>
        [Test]
        public void ReliefLine_Off_SaysHowToEnterAndWhatHappens()
        {
            (ShowLang lang, string how, string what)[] cases =
            {
                (ShowLang.Ja, "長押し", "陽気な曲"),
                (ShowLang.En, "Hold", "cheery music"),
                (ShowLang.Fr, "Maintenez", "musique gaie"),
            };
            foreach ((ShowLang lang, string how, string what) in cases)
            {
                string line = TitleNotice.ReliefLineOf(lang, on: false);
                StringAssert.Contains(how, line, $"[{lang}] 入り方が書かれていない");
                StringAssert.Contains(what, line, $"[{lang}] 何が起きるかが書かれていない");
            }
        }

        /// <summary>
        /// ⚠⚠ <b>入っているあいだは、入っていると分かる。</b> この面は <c>richText</c> を
        /// 切ってあり白 1 色しか出せないので、<b>囲み（［］/ []）だけが状態の手掛かり</b>。
        /// 分からないと体験者は効くまで押し続ける（＝ 何度も出入りする）。
        /// ⚠ <b>出方も同じ行に書く。</b> 誤って入った人の出口が、この 1 行しかない。
        /// </summary>
        [Test]
        public void ReliefLine_On_ShowsTheStateAndTheWayOut()
        {
            (ShowLang lang, string mark, string out_)[] cases =
            {
                (ShowLang.Ja, "［", "長押し"),
                (ShowLang.En, "[", "Hold"),
                (ShowLang.Fr, "[", "Maintenez"),
            };
            foreach ((ShowLang lang, string mark, string out_) in cases)
            {
                string on = TitleNotice.ReliefLineOf(lang, on: true);
                StringAssert.Contains(mark, on, $"[{lang}] 入っている印が無い");
                StringAssert.Contains(out_, on, $"[{lang}] 出方が書かれていない");
                StringAssert.DoesNotContain(mark, TitleNotice.ReliefLineOf(lang, on: false),
                                            $"[{lang}] 入っていないのに印が出ている");
            }
        }

        /// <summary>
        /// ⚠⚠ <b>軽減の囲みを本文側へ持ち込まない。</b> 本文（<see cref="TitleNotice.ComposeFor"/>）の
        /// ［］は<b>いま選んでいる言語</b>の印で、<c>ExactlyOneEntry_IsMarked</c> が 1 個であることを
        /// 守っている。軽減の状態を同じ面へ足すと、そこが 2 個になって
        /// <b>どちらが言語の選択か分からなくなる</b>。
        /// </summary>
        [Test]
        public void Relief_DoesNotTouchTheLanguageChooser()
        {
            foreach (ShowLang lang in ShowLanguage.All)
            {
                HorrorRelief.Select(false);
                string off = TitleNotice.ComposeFor(lang);
                HorrorRelief.Select(true);
                Assert.AreEqual(off, TitleNotice.ComposeFor(lang),
                                $"[{lang}] 軽減モードで本文か言語の並びが変わっている");
            }
        }

        private static int Count(string s, char c)
        {
            int n = 0;
            foreach (char x in s) if (x == c) n++;
            return n;
        }
    }
}
