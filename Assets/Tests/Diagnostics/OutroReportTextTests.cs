using FixedCamVr.Diagnostics;
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Tests.Diagnostics
{
    /// <summary>エンドロール冒頭の短い結末名と報告数を検査する。</summary>
    public sealed class OutroReportTextTests
    {
        private static readonly ShowLang[] Languages = { ShowLang.Ja, ShowLang.En, ShowLang.Fr };
        private static readonly ShowEndingOutcome[] Outcomes =
        {
            ShowEndingOutcome.Released, ShowEndingOutcome.Trapped, ShowEndingOutcome.Interrupted
        };

        [SetUp]
        public void ResetLanguage() => ShowLanguage.Reset();

        [Test]
        public void JapaneseUsesSpecifiedEndingAndReportLabels()
        {
            Assert.That(OutroReportText.Title(ShowEndingOutcome.Released, ShowLang.Ja), Is.EqualTo("帰還End"));
            Assert.That(OutroReportText.Title(ShowEndingOutcome.Trapped, ShowLang.Ja), Is.EqualTo("人形End"));
            Assert.That(OutroReportText.Title(ShowEndingOutcome.Interrupted, ShowLang.Ja), Is.EqualTo("中断"));
            Assert.That(OutroReportText.CountLine(12, ShowLang.Ja), Is.EqualTo("報告数 １２"));
        }

        [Test]
        public void EnglishAndFrenchKeepDistinctOutcomeAndReportText()
        {
            foreach (ShowLang lang in Languages)
            {
                foreach (ShowEndingOutcome outcome in Outcomes)
                {
                    string composed = OutroReportText.Compose(12, 8, outcome, lang);
                    StringAssert.Contains(OutroReportText.Title(outcome, lang), composed);
                    StringAssert.Contains(OutroReportText.CountLine(12, lang), composed);
                    Assert.That(composed.Split('\n').Length, Is.EqualTo(2));
                    StringAssert.DoesNotContain("/", composed, $"{lang}/{outcome}: 分母を表示している");
                }

                Assert.That(OutroReportText.Title(Outcomes[0], lang),
                    Is.Not.EqualTo(OutroReportText.Title(Outcomes[1], lang)));
                Assert.That(OutroReportText.Title(Outcomes[0], lang),
                    Is.Not.EqualTo(OutroReportText.Title(Outcomes[2], lang)));
                Assert.That(OutroReportText.Title(Outcomes[1], lang),
                    Is.Not.EqualTo(OutroReportText.Title(Outcomes[2], lang)));
            }

            Assert.That(OutroReportText.CountLine(12, ShowLang.En), Is.EqualTo("Reports 12"));
            Assert.That(OutroReportText.CountLine(12, ShowLang.Fr), Is.EqualTo("Signalements 12"));
        }

        [Test]
        public void Compose_DoesNotInferOutcomeFromCounts()
        {
            foreach (ShowLang lang in Languages)
            foreach (ShowEndingOutcome outcome in Outcomes)
            foreach (int reports in new[] { 0, 12, 123, 999 })
            {
                string composed = OutroReportText.Compose(reports, 8, outcome, lang);
                StringAssert.Contains(OutroReportText.Title(outcome, lang), composed);
                foreach (ShowEndingOutcome other in Outcomes)
                {
                    if (other == outcome) continue;
                    Assert.That(composed.Contains(OutroReportText.Title(other, lang)), Is.False,
                        $"{lang}/{outcome}/{reports}: 件数から別の結末を選んだ");
                }
            }
        }

        [Test]
        public void FullWidthDigits_AreJapaneseOnly()
        {
            Assert.That(OutroReportText.CountOf(12, ShowLang.Ja), Is.EqualTo("１２"));
            Assert.That(OutroReportText.CountOf(12, ShowLang.En), Is.EqualTo("12"));
            Assert.That(OutroReportText.CountOf(12, ShowLang.Fr), Is.EqualTo("12"));
            foreach (ShowLang lang in Languages)
                Assert.That(OutroReportText.CountOf(-3, lang),
                    Is.EqualTo(lang == ShowLang.Ja ? "０" : "0"));
            Assert.That(OutroReportText.FullWidth(103), Is.EqualTo("１０３"));
            Assert.That(OutroReportText.FullWidth(int.MinValue), Is.EqualTo("０"));
        }

        [Test]
        public void LegacyProseAndRatioApis_RemainAvailableForDiagnostics()
        {
            foreach (ShowLang lang in Languages)
            {
                Assert.That(OutroReportText.Header(lang), Is.Not.Empty);
                Assert.That(OutroReportText.Body(ShowEndingOutcome.Released, lang), Is.Not.Empty);
                Assert.That(OutroReportText.Note(lang), Is.Not.Empty);
                Assert.That(OutroReportText.Footer(lang), Is.Not.Empty);
            }
            Assert.That(OutroReportText.Ratio(12, 8), Is.EqualTo("12 / 8"));
        }
    }
}
