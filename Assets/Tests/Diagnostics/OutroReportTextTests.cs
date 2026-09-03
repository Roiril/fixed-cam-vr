using FixedCamVr.Diagnostics;
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Tests.Diagnostics
{
    /// <summary>
    /// 終幕の報告の文言を固定する（<c>canon/LEDGER.md</c> 0048・ユーザーが書いた 4 行）。
    ///
    /// ⚠ 文言を変えたら <c>.\tools\unity.ps1 menu hud-font</c> を再実行する
    /// （静的ベイクなので、ここに無い文字は実機で豆腐になる）。
    /// </summary>
    public sealed class OutroReportTextTests
    {
        /// <summary>
        /// ⚠ <c>Compose(int)</c> は<b>体験者が選んだ言語</b>を読む（2026-09-03）。
        /// 前のテストが回した言語が残っていると、下の逐語の突き合わせが理由なく落ちる。
        /// </summary>
        [SetUp]
        public void ResetLanguage() => ShowLanguage.Reset();

        /// <summary>
        /// <b>どの言語でも構成は 5 行</b>（数の行 ＋ 空行 ＋ 結び 3 行）。
        /// ⚠ 行を足しも減らしもしない — 構成は <c>canon/LEDGER.md</c> 0048 が書いたもので、
        /// 訳すのは中身だけ。1 行減らすと、その言語の体験者だけ別の終わり方になる。
        /// </summary>
        [Test]
        public void EveryLanguage_KeepsTheFiveLineShape()
        {
            foreach (ShowLang lang in ShowLanguage.All)
            {
                string[] lines = OutroReportText.Compose(3, lang).Split('\n');
                Assert.AreEqual(5, lines.Length, $"{lang}: 数の行 + 空行 + 結び 3 行");
                Assert.AreEqual("", lines[1], $"{lang}: 2 行目は空行");
                foreach (int i in new[] { 0, 2, 3, 4 })
                    Assert.That(lines[i], Is.Not.Empty, $"{lang}: {i} 行目が空");
                StringAssert.Contains(OutroReportText.CountOf(3, lang), lines[0], $"{lang}: 数が出ていない");
            }
        }

        /// <summary>
        /// 数字の形は言語で分ける。<b>全角は日本語だけ</b>（紙の依頼書と揃えるための形なので、
        /// Latin の文に混ぜるとその 1 文字だけ倍幅になって桁がずれる）。
        /// </summary>
        [Test]
        public void FullWidthDigits_AreJapaneseOnly()
        {
            Assert.AreEqual("１２", OutroReportText.CountOf(12, ShowLang.Ja));
            Assert.AreEqual("12", OutroReportText.CountOf(12, ShowLang.En));
            Assert.AreEqual("12", OutroReportText.CountOf(12, ShowLang.Fr));
            // 負の数はどの言語でも 0（押していない体験者に「−１」を出さない）。
            foreach (ShowLang lang in ShowLanguage.All)
                Assert.That(OutroReportText.CountOf(-3, lang), Is.EqualTo(lang == ShowLang.Ja ? "０" : "0"));
        }

        /// <summary>
        /// 打ち切るまでの尺（言語ごと）。<b>速さは装置の声なので言語で変えない</b>ので、
        /// Latin は文字数のぶんそのまま長くなる（同じ内容で日本語のおよそ 2 倍）。
        /// ⚠ 上限を分けてあるのはそのため。ここを日本語の 6 秒で縛ると、
        /// <b>4 行を削るしかなくなる</b> ＝ 言語で終わり方が変わる。
        /// </summary>
        [Test]
        public void EveryLanguage_TypesWithinAReadableSpan()
        {
            foreach (ShowLang lang in ShowLanguage.All)
            {
                float sec = KeystrokesOf(OutroReportText.Compose(0, lang)) / CommsPanelLogic.CharsPerSec;
                float max = lang == ShowLang.Ja ? 6f : 8f;
                Assert.That(sec, Is.GreaterThan(2f), $"{lang}: 速すぎると「一気に出た」に見える");
                Assert.That(sec, Is.LessThan(max), $"{lang}: 打ち切るまで {sec:F1}s は長い — 文言を詰める");
            }
        }

        [Test]
        public void Compose_KeepsTheAuthoredFourLines()
        {
            string s = OutroReportText.Compose(3);
            string[] lines = s.Split('\n');
            Assert.AreEqual(5, lines.Length, "数の行 + 空行 + 結び 3 行");
            Assert.AreEqual("報告した怪異の数：３", lines[0]);
            Assert.AreEqual("", lines[1]);
            Assert.AreEqual("十分なデータが取れました。", lines[2]);
            Assert.AreEqual("調査完了です。", lines[3]);
            Assert.AreEqual("装置を外してください。", lines[4]);
        }

        [Test]
        public void FullWidth_UsesFullWidthDigits()
        {
            // 紙の依頼書（観測者番号 ０３７）と同じ形。半角が混ざると装置の声が 2 つに割れる。
            Assert.AreEqual("０", OutroReportText.FullWidth(0));
            Assert.AreEqual("７", OutroReportText.FullWidth(7));
            Assert.AreEqual("１０", OutroReportText.FullWidth(10));
            Assert.AreEqual("１０３", OutroReportText.FullWidth(103));
        }

        [Test]
        public void FullWidth_NeverShowsANegativeCount()
        {
            // 押していない体験者に「−１」を出さない（回数の供給が壊れても文面は成立する）。
            Assert.AreEqual("０", OutroReportText.FullWidth(-1));
            Assert.AreEqual("０", OutroReportText.FullWidth(int.MinValue));
        }

        /// <summary>
        /// 打鍵は<b>改行では鳴らさない</b>（<c>canon/LEDGER.md</c> 0063）。
        /// <c>maxVisibleCharacters</c> は改行も 1 文字として数えるので、鳴らすと
        /// 「字が出ていないのに 1 発鳴る」が起きる。実行体（<c>OutroReport</c>）は TMP に
        /// 測らせるが、<b>期待値はここが持つ</b> — テレメトリの <c>repChars</c> と対で読む値。
        /// </summary>
        [Test]
        public void Compose_KeystrokesExcludeTheFourNewlines()
        {
            string s = OutroReportText.Compose(0);
            int newlines = 0;
            foreach (char c in s)
                if (c == '\n') newlines++;
            Assert.AreEqual(4, newlines, "数の行 + 空行 + 結び 3 行 ＝ 改行 4 つ");
            Assert.AreEqual(s.Length - 4, KeystrokesOf(s));
            Assert.AreEqual(41, KeystrokesOf(s), "打鍵の数（数が 1 桁のとき）");
        }

        /// <summary>
        /// 打ち切るまでの尺。<b>速さは連絡の面と同じ</b>（同じ装置の印字なので、
        /// 違う速さで打つと別の装置が 2 台あるように聞こえる）。
        ///
        /// ⚠ ここが伸びると、報告が出たまま体験者が待たされる。文言を足すときは実尺を見る。
        /// </summary>
        [Test]
        public void Compose_TypesWithinAReadableSpan()
        {
            float sec = KeystrokesOf(OutroReportText.Compose(0)) / CommsPanelLogic.CharsPerSec;
            Assert.That(sec, Is.GreaterThan(2f), "速すぎると「一気に出た」に見える");
            Assert.That(sec, Is.LessThan(6f), $"打ち切るまで {sec:F1}s は長い — 文言を詰める");
        }

        private static int KeystrokesOf(string s)
        {
            int n = 0;
            foreach (char c in s)
                if (c != '\n') n++;
            return n;
        }

        [Test]
        public void Compose_HasNoHalfWidthDigits()
        {
            foreach (int n in new[] { 0, 1, 9, 12, 250 })
            {
                string s = OutroReportText.Compose(n);
                foreach (char c in s)
                    Assert.IsFalse(c >= '0' && c <= '9', $"半角数字が混ざっている: {s}");
            }
        }
    }
}
