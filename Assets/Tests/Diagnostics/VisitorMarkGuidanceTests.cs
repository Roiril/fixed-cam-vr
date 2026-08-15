#nullable enable
using NUnit.Framework;

namespace FixedCamVr.Diagnostics.Tests
{
    /// <summary>
    /// 体験者の報告ボタンの面に出す文字。判定は <c>canon/LEDGER.md</c> 0046
    /// （「(X,Yで異変を報告) みたいに書いておいてほしい」「報告中 / ゲージ みたいな構成で」）。
    ///
    /// ⚠ <b>文言そのものを固定する</b>のは、ここを変えたら
    /// <c>.\tools\unity.ps1 menu hud-font</c> の再実行が要るため（静的ベイクなので、
    /// 黙って足した字は実機で豆腐になる）。
    /// </summary>
    public sealed class VisitorMarkGuidanceTests
    {
        [Test]
        public void Idle_ShowsHowToPress()
        {
            Assert.That(VisitorMarkGuidance.Line(0f, confirming: false),
                        Is.EqualTo("(X,Yで異変を報告)"));
        }

        [Test]
        public void Holding_ShowsHeadAndGauge()
        {
            string[] lines = VisitorMarkGuidance.Line(0.5f, confirming: false).Split('\n');

            Assert.That(lines.Length, Is.EqualTo(2), "構成は「報告中」＋ゲージの 2 行");
            Assert.That(lines[0], Is.EqualTo("報告中"));
            Assert.That(lines[1].Length, Is.EqualTo(VisitorMarkGuidance.Slots));
            Assert.That(lines[1], Is.EqualTo("█████░░░░░"));
        }

        [Test]
        public void Gauge_FillsFromEmptyToFull()
        {
            Assert.That(VisitorMarkGuidance.Line(0.1f, false).Split('\n')[1], Is.EqualTo("█░░░░░░░░░"));
            Assert.That(VisitorMarkGuidance.Line(1f, false).Split('\n')[1], Is.EqualTo("██████████"));
        }

        /// <summary>余韻は長押し中の表示より強い（発火した瞬間はゲージも 1 なので、順序が要る）。</summary>
        [Test]
        public void Confirmed_WinsOverGauge()
        {
            Assert.That(VisitorMarkGuidance.Line(1f, confirming: true), Is.EqualTo("報告しました"));
        }

        /// <summary>
        /// ⚠ 正誤を返さない。装置が「何が異変か」を判定した瞬間に、この作品の恐怖の前提
        ///（装置は正直に映すだけ）が壊れる。
        /// </summary>
        [Test]
        public void NoJudgement_InAnyLine()
        {
            foreach (string line in new[]
            {
                VisitorMarkGuidance.Line(0f, false),
                VisitorMarkGuidance.Line(0.5f, false),
                VisitorMarkGuidance.Line(1f, true),
            })
            {
                Assert.That(line, Does.Not.Contain("正").And.Not.Contain("誤"));
                Assert.That(line, Does.Not.Contain("正解").And.Not.Contain("不正解"));
            }
        }
    }
}
