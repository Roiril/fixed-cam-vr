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
            foreach (ShowLang lang in ShowLanguage.All)
            {
                string f = TitleNotice.FooterFor(lang);
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
            foreach ((ShowLang lang, string mine, string[] others) in cases)
            {
                string f = TitleNotice.FooterFor(lang);
                StringAssert.Contains(mine, f, $"[{lang}] 次にすることが自分の言語で無い");
                foreach (string other in others)
                    StringAssert.DoesNotContain(other, f, $"[{lang}] 他の言語の行まで出ている");
            }
        }

        /// <summary>案内の行も枠に入る（本文より小さいので入る数が違う）。</summary>
        [Test]
        public void FooterLines_FitTheFrame()
        {
            foreach (ShowLang lang in ShowLanguage.All)
            foreach (string line in TitleNotice.FooterFor(lang).Split('\n'))
                Assert.LessOrEqual(FullWidth(line), MaxFullWidthPerFooterLine,
                                   $"[{ShowLanguage.Code(lang)}] 案内の「{line}」が 1 行に入らない");
        }

        /// <summary>
        /// <b>案内は 4 行</b>（切り替え方 3 行 ＋ 空行 ＋ 次にすること 1 行）。
        /// ⚠ 行が増えると塊が縦に伸びて、上下の端を読むのに首を振ることになる
        /// （縦の実測は <c>TitleNoticeLayoutTests.TheWholeStack_FitsInTheView</c>）。
        /// </summary>
        [Test]
        public void Footer_KeepsItsFourLines()
        {
            foreach (ShowLang lang in ShowLanguage.All)
            {
                string[] lines = TitleNotice.FooterFor(lang).Split('\n');
                Assert.AreEqual(5, lines.Length, $"[{lang}] 切り替え方 3 ＋ 空行 ＋ 次にすること");
                Assert.That(lines[3], Is.Empty, $"[{lang}] 次にすることは空行で離す");
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
