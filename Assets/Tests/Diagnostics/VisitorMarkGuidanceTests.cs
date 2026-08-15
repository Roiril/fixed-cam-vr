#nullable enable
using NUnit.Framework;

namespace FixedCamVr.Diagnostics.Tests
{
    /// <summary>
    /// 体験者の報告ボタンの面に出す文字。判定は <c>canon/LEDGER.md</c> 0050
    /// （「(X,Yで異変を報告) みたいに書いておいてほしい」「報告中 / ゲージ みたいな構成で」）と、
    /// 2026-08-15 の赤入れ（「()で包む必要があるのか」「報告中はバーの左上の端っこに少し小さめに」）。
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
            // 括弧は外し、他の面と同じ `入力：動作` へ揃えた（HmdTextStyle の規約）。
            Assert.That(VisitorMarkGuidance.Line(0f, confirming: false),
                        Is.EqualTo("X／Y：異変を報告"));
        }

        [Test]
        public void Idle_HasNoParentheses()
        {
            // 常設の主操作を括弧で包むと補足に見える（2026-08-15 の赤入れ）。
            string s = VisitorMarkGuidance.Line(0f, false);
            Assert.That(s, Does.Not.Contain("(").And.Not.Contain(")"));
            Assert.That(s, Does.Not.Contain("（").And.Not.Contain("）"));
        }

        [Test]
        public void Holding_LabelIsSmallerThanGauge()
        {
            string[] lines = VisitorMarkGuidance.Line(0.5f, confirming: false).Split('\n');

            Assert.That(lines.Length, Is.EqualTo(2), "構成は「報告中」＋ゲージの 2 行");
            // 見出しはゲージより小さい（バーの左上に添える）。
            Assert.That(lines[0], Is.EqualTo($"<size={VisitorMarkGuidance.LabelPercent}%>報告中</size>"));
            Assert.That(VisitorMarkGuidance.LabelPercent, Is.LessThan(100));
        }

        [Test]
        public void Gauge_FillsFromEmptyToFull()
        {
            // ゲージは「全塗り 1 種類を明暗で分ける」（░ の網目は 1.5° では潰れて文字列に見える）。
            Assert.That(Cells(VisitorMarkGuidance.Line(0.1f, false)), Is.EqualTo(1));
            Assert.That(Cells(VisitorMarkGuidance.Line(0.5f, false)), Is.EqualTo(5));
            Assert.That(Cells(VisitorMarkGuidance.Line(1f, false)), Is.EqualTo(VisitorMarkGuidance.Slots));
        }

        /// <summary>ゲージの外形（目盛の総数）は進捗によらず一定 ＝ 1 本の帯として読める。</summary>
        [Test]
        public void Gauge_KeepsItsOutline_AtEveryProgress()
        {
            foreach (float p in new[] { 0.01f, 0.3f, 0.7f, 1f })
            {
                string bar = VisitorMarkGuidance.Line(p, false).Split('\n')[1];
                Assert.That(Count(bar, '█'), Is.EqualTo(VisitorMarkGuidance.Slots), $"進捗 {p}");
            }
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

        // 進んだ分の目盛数（進んだ側の色で塗られたセルだけ数える）。
        private static int Cells(string composed)
        {
            string bar = composed.Split('\n')[1];
            string open = "<color=#" + FixedCamVr.Tracking.RegistrationGuidance.FilledHex + ">";
            int i = bar.IndexOf(open, System.StringComparison.Ordinal);
            if (i < 0) return 0;
            i += open.Length;
            int close = bar.IndexOf("</color>", i, System.StringComparison.Ordinal);
            return close < 0 ? 0 : close - i;
        }

        private static int Count(string s, char c)
        {
            int n = 0;
            foreach (char x in s) if (x == c) n++;
            return n;
        }
    }
}
