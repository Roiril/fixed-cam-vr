using FixedCamVr.Diagnostics;
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
