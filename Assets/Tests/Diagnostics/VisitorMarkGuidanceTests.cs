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
        /// <summary>
        /// ⚠ <c>Line(progress, confirming)</c> は<b>体験者が選んだ言語</b>を読む（2026-09-03）。
        /// 前のテストが回した言語が残っていると、逐語の突き合わせが理由なく落ちる。
        /// </summary>
        [SetUp]
        public void ResetLanguage() => FixedCamVr.Streaming.ShowLanguage.Reset();

        /// <summary>
        /// <b>どの言語でも「見出し ＋ ゲージ」の 2 行</b>で、ゲージは 1 文字も変わらない。
        /// ⚠ 見出しだけが訳される — ゲージは記号なので訳す物が無い。ここが崩れると、
        /// 言語を変えた瞬間に下段のレイアウトだけ別物になる。
        /// </summary>
        [Test]
        public void EveryLanguage_KeepsTheSameShape()
        {
            string jaBar = VisitorMarkGuidance.Line(0.5f, false, FixedCamVr.Streaming.ShowLang.Ja)
                                              .Split('\n')[1];
            foreach (var lang in FixedCamVr.Streaming.ShowLanguage.All)
            {
                string[] lines = VisitorMarkGuidance.Line(0.5f, false, lang).Split('\n');
                Assert.That(lines.Length, Is.EqualTo(2), $"{lang}");
                Assert.That(lines[0],
                            Is.EqualTo($"<size={VisitorMarkGuidance.LabelPercent}%>"
                                     + $"{VisitorMarkGuidance.HoldingHeadOf(lang)}</size>"), $"{lang}");
                Assert.That(lines[1], Is.EqualTo(jaBar), $"{lang} でゲージが変わっている");
                Assert.That(VisitorMarkGuidance.HoldingHeadOf(lang), Is.Not.Empty, $"{lang}");
                // 押していないときは、どの言語でも空（下段は状態しか持たない）。
                Assert.That(VisitorMarkGuidance.Line(0f, false, lang), Is.Empty, $"{lang}");
            }
        }

        /// <summary>
        /// ⚠⚠ <b>押していないときは何も出さない</b>（2026-08-16・<c>canon/LEDGER.md</c> 0065）。
        /// 下段は<b>状態</b>だけを持ち、指示は持たない。押し方は①の連絡が 1 度だけ言う。
        ///
        /// これが破れると、面が開くたび（＝ 押している最中にも）「押せ」と出続ける状態へ戻る。
        /// </summary>
        [Test]
        public void Idle_ShowsNothing()
        {
            Assert.That(VisitorMarkGuidance.Line(0f, confirming: false), Is.Empty);
        }

        /// <summary>
        /// ⚠ <b>下段に入力機器の名前を出さない。</b> 装置の面にキー名が出ると、
        /// 調査の記録ではなくゲームの操作説明に見える（同 0065）。
        /// </summary>
        [Test]
        public void NoKeyNames_InAnyLine()
        {
            foreach (float p in new[] { 0f, 0.01f, 0.5f, 1f })
            {
                string s = VisitorMarkGuidance.Line(p, false);
                Assert.That(s, Does.Not.Contain("X").And.Not.Contain("Y"), $"進捗 {p}");
                Assert.That(s, Does.Not.Contain("ボタン"), $"進捗 {p}");
            }
        }

        [Test]
        public void Holding_LabelIsSmallerThanGauge()
        {
            string[] lines = VisitorMarkGuidance.Line(0.5f, confirming: false).Split('\n');

            Assert.That(lines.Length, Is.EqualTo(2), "構成は「解析中」＋ゲージの 2 行");
            // 見出しはゲージより小さい（バーの左上に添える）。
            Assert.That(lines[0], Is.EqualTo($"<size={VisitorMarkGuidance.LabelPercent}%>解析中</size>"));
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

        /// <summary>
        /// ⚠⚠ <b>「報告しました」は捨てた</b>（2026-08-16・<c>canon/LEDGER.md</c> 0065）。
        /// 上段の「異常が記録されました」と<b>同じ瞬間に同じことを言っていた</b> —
        /// 二重に言う面は、装置が動揺しているように見える。
        ///
        /// 余韻のあいだ下段が持つのは、押し切ったゲージだけ（＝ 状態）。
        /// </summary>
        [Test]
        public void Confirmed_DoesNotRepeatWhatTheNoticeAlreadySays()
        {
            Assert.That(VisitorMarkGuidance.Line(1f, confirming: true), Does.Not.Contain("報告しました"));
            // 離していれば余韻でも空（下段は状態しか持たない）。
            Assert.That(VisitorMarkGuidance.Line(0f, confirming: true), Is.Empty);
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
                VisitorMarkGuidance.Line(1f, false),
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
