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
        /// 全体は <b>本文 ＋ 空行 ＋ 並び ＋ 切り替え方</b>。
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
                Assert.AreEqual(2, tail.Length, $"{lang}: 並び 1 行 ＋ 切り替え方 1 行");
                StringAssert.Contains("］", tail[0], $"{lang}: 並びの行に囲みが無い");
                Assert.That(tail[1], Is.Not.Empty, $"{lang}: 切り替え方の行が空");
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
