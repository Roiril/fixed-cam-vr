using FixedCamVr.Diagnostics;
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Tests.Diagnostics
{
    /// <summary>終幕の観測記録。結果と報告回数は別々の入力として扱う。</summary>
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
        public void EveryLanguage_HasDistinctOutcomeTextAndRemovalInstruction()
        {
            foreach (ShowLang lang in Languages)
            {
                foreach (ShowEndingOutcome outcome in Outcomes)
                {
                    string composed = OutroReportText.Compose(0, 8, outcome, lang);
                    StringAssert.Contains(OutroReportText.Title(outcome, lang), composed);
                    StringAssert.Contains(OutroReportText.Body(outcome, lang), composed);
                    StringAssert.Contains(OutroReportText.Footer(lang), composed);
                    Assert.That(OutroReportText.Footer(lang), Is.Not.Empty, $"{lang}: 装置を外す指示が無い");
                }

                Assert.That(OutroReportText.Title(Outcomes[0], lang),
                    Is.Not.EqualTo(OutroReportText.Title(Outcomes[1], lang)));
                Assert.That(OutroReportText.Title(Outcomes[0], lang),
                    Is.Not.EqualTo(OutroReportText.Title(Outcomes[2], lang)));
                Assert.That(OutroReportText.Title(Outcomes[1], lang),
                    Is.Not.EqualTo(OutroReportText.Title(Outcomes[2], lang)));
                Assert.That(OutroReportText.Body(Outcomes[0], lang),
                    Is.Not.EqualTo(OutroReportText.Body(Outcomes[1], lang)));
                Assert.That(OutroReportText.Body(Outcomes[0], lang),
                    Is.Not.EqualTo(OutroReportText.Body(Outcomes[2], lang)));
                Assert.That(OutroReportText.Body(Outcomes[1], lang),
                    Is.Not.EqualTo(OutroReportText.Body(Outcomes[2], lang)));
            }

            StringAssert.Contains("装置を外してください", OutroReportText.Footer(ShowLang.Ja));
            StringAssert.Contains("remove the headset", OutroReportText.Footer(ShowLang.En));
            StringAssert.Contains("Retirez le casque", OutroReportText.Footer(ShowLang.Fr));
        }

        [TestCase(0, 8, "0 / 8")]
        [TestCase(12, 8, "12 / 8")]
        [TestCase(123, 8, "123 / 8")]
        [TestCase(999, 8, "999 / 8")]
        public void Ratio_PreservesReportsEvenAboveTotal(int reports, int total, string expected)
        {
            Assert.That(OutroReportText.Ratio(reports, total), Is.EqualTo(expected));
            foreach (ShowLang lang in Languages)
                StringAssert.Contains(expected,
                    OutroReportText.Compose(reports, total, ShowEndingOutcome.Released, lang));
        }

        [Test]
        public void Ratio_UsesDashForUnknownTotal_AndClampsNegativeInputs()
        {
            Assert.That(OutroReportText.Ratio(0, 0), Is.EqualTo("0 / —"));
            Assert.That(OutroReportText.Ratio(12, 0), Is.EqualTo("12 / —"));
            Assert.That(OutroReportText.Ratio(-1, -8), Is.EqualTo("0 / —"));
            Assert.That(OutroReportText.Ratio(-1, 8), Is.EqualTo("0 / 8"));
            foreach (ShowLang lang in Languages)
                StringAssert.Contains("0 / —",
                    OutroReportText.Compose(-1, 0, ShowEndingOutcome.Interrupted, lang));
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
                StringAssert.Contains(OutroReportText.Body(outcome, lang), composed);
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
    }
}
