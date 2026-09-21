#nullable enable
using System.Collections.Generic;
using System.Reflection;
using FixedCamVr.Diagnostics;
using FixedCamVr.Streaming;
using NUnit.Framework;
using TMPro;
using UnityEngine;

namespace FixedCamVr.Tests.Diagnostics
{
    /// <summary>結果カードの 7 フィールドを実際の TMP メッシュで検査する。</summary>
    public sealed class OutroReportLayoutTests
    {
        private readonly List<GameObject> _spawned = new List<GameObject>();
        private static readonly ShowLang[] Languages = { ShowLang.Ja, ShowLang.En, ShowLang.Fr };
        private static readonly ShowEndingOutcome[] Outcomes =
        {
            ShowEndingOutcome.Released, ShowEndingOutcome.Trapped, ShowEndingOutcome.Interrupted
        };

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in _spawned)
                if (go != null) Object.DestroyImmediate(go);
            _spawned.Clear();
        }

        private OutroReport SpawnReport()
        {
            var go = new GameObject("[Test] OutroReport");
            _spawned.Add(go);
            var report = go.AddComponent<OutroReport>();
            // EditMode では Awake が自動実行されないので、生成を明示的に起こす。
            MethodInfo? awake = typeof(OutroReport).GetMethod("Awake",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(awake, Is.Not.Null);
            awake!.Invoke(report, null);
            Assert.That(report.IsBuilt, Is.True, "結果カードのフォントまたはシェーダが欠けている");
            return report;
        }

        [Test]
        public void BuildsSevenTopLeftFields_AndStartsWithNothingTyped()
        {
            OutroReport report = SpawnReport();
            string[] names = { "Archive", "Outcome", "Message", "MeasureLabel", "Measure", "MeasureNote", "Exit" };
            Assert.That(report.Fields.Count, Is.EqualTo(names.Length));
            for (int i = 0; i < names.Length; i++)
            {
                Assert.That(report.Fields[i].name, Is.EqualTo(names[i]));
                Assert.That(report.Fields[i].alignment, Is.EqualTo(TextAlignmentOptions.TopLeft));
                Assert.That(report.Fields[i].maxVisibleCharacters, Is.Zero);
            }
            Assert.That(report.VisibleChars, Is.Zero);
        }

        [Test]
        public void EveryLanguageAndOutcome_FitsPanelWithoutTextOverlap_AndHasFontGlyphs()
        {
            OutroReport report = SpawnReport();
            foreach (ShowLang lang in Languages)
            foreach (ShowEndingOutcome outcome in Outcomes)
            {
                report.PresentPreview(999, 8, outcome, lang);
                var rects = new Rect[report.Fields.Count];
                for (int i = 0; i < report.Fields.Count; i++)
                {
                    TMP_Text field = report.Fields[i];
                    Assert.That(field.alignment, Is.EqualTo(TextAlignmentOptions.TopLeft),
                        $"{lang}/{outcome}/{field.name}");
                    Assert.That(field.font, Is.Not.Null, $"{lang}/{outcome}/{field.name}: font が無い");
                    foreach (char ch in field.text)
                    {
                        if (char.IsWhiteSpace(ch)) continue;
                        Assert.That(field.font.HasCharacter(ch), Is.True,
                            $"{lang}/{outcome}/{field.name}: 欠字 U+{(int)ch:X4} '{ch}'");
                    }
                    field.ForceMeshUpdate(true, true);
                    rects[i] = InkRectInCard(field);
                    Assert.That(rects[i].xMin, Is.GreaterThanOrEqualTo(-OutroReport.PanelWidth / 2 - .003f),
                        $"{lang}/{outcome}/{field.name}: 左にはみ出す");
                    Assert.That(rects[i].xMax, Is.LessThanOrEqualTo(OutroReport.PanelWidth / 2 + .003f),
                        $"{lang}/{outcome}/{field.name}: 右にはみ出す");
                    Assert.That(rects[i].yMin, Is.GreaterThanOrEqualTo(-OutroReport.PanelHeight / 2 - .003f),
                        $"{lang}/{outcome}/{field.name}: 下にはみ出す");
                    Assert.That(rects[i].yMax, Is.LessThanOrEqualTo(OutroReport.PanelHeight / 2 + .003f),
                        $"{lang}/{outcome}/{field.name}: 上にはみ出す");
                }
                for (int i = 0; i < rects.Length; i++)
                for (int j = i + 1; j < rects.Length; j++)
                    Assert.That(HasInkOverlap(rects[i], rects[j]), Is.False,
                        $"{lang}/{outcome}: {report.Fields[i].name} {rects[i]} と {report.Fields[j].name} {rects[j]} が重なる");
            }
        }

        [Test]
        public void PartialReveal_DoesNotMoveFieldsOrAlreadyVisibleGlyphs()
        {
            OutroReport report = SpawnReport();
            report.PresentPreview(123, 8, ShowEndingOutcome.Trapped, ShowLang.Ja);
            var positions = new Vector3[report.Fields.Count];
            for (int i = 0; i < positions.Length; i++)
                positions[i] = report.Fields[i].transform.localPosition;
            TMP_Text first = report.Fields[0];
            first.ForceMeshUpdate(true, true);
            Vector3 firstGlyph = first.textInfo.characterInfo[0].bottomLeft;

            report.PresentPreview(123, 8, ShowEndingOutcome.Trapped, ShowLang.Ja, characters: 3);
            Assert.That(report.VisibleChars, Is.GreaterThan(0));
            Assert.That(report.VisibleChars, Is.LessThan(report.ReportChars));
            for (int i = 0; i < positions.Length; i++)
                Assert.That(Vector3.Distance(report.Fields[i].transform.localPosition, positions[i]),
                    Is.LessThan(.0001f), $"{report.Fields[i].name}: 部分表示で位置が動いた");
            first.ForceMeshUpdate(true, true);
            Assert.That(Vector3.Distance(first.textInfo.characterInfo[0].bottomLeft, firstGlyph),
                Is.LessThan(.0001f), "部分表示で先頭の字が動いた");
        }

        [Test]
        public void ReportChars_EqualsActualDrawableGlyphs()
        {
            OutroReport report = SpawnReport();
            foreach (ShowLang lang in Languages)
            foreach (ShowEndingOutcome outcome in Outcomes)
            {
                report.PresentPreview(123, 8, outcome, lang);
                int drawable = 0;
                foreach (TMP_Text field in report.Fields)
                {
                    field.ForceMeshUpdate(true, true);
                    for (int c = 0; c < field.textInfo.characterCount; c++)
                        if (field.textInfo.characterInfo[c].isVisible) drawable++;
                }
                Assert.That(report.ReportChars, Is.EqualTo(drawable), $"{lang}/{outcome}");
                Assert.That(report.VisibleChars, Is.EqualTo(drawable), $"{lang}/{outcome}: 全文表示");
            }
        }

        [Test]
        public void NextPreview_ReplacesPreviousOutcomeAndCapturedValues()
        {
            OutroReport report = SpawnReport();
            report.PresentPreview(999, 8, ShowEndingOutcome.Trapped, ShowLang.Ja);
            string previous = report.CurrentBody;
            report.PresentPreview(0, 0, ShowEndingOutcome.Released, ShowLang.En);

            Assert.That(report.CapturedReports, Is.Zero);
            Assert.That(report.CapturedTotal, Is.Zero);
            Assert.That(report.CapturedOutcome, Is.EqualTo(ShowEndingOutcome.Released));
            Assert.That(report.CurrentBody, Is.Not.EqualTo(previous));
            Assert.That(report.CurrentBody, Is.EqualTo(
                OutroReportText.Compose(0, 0, ShowEndingOutcome.Released, ShowLang.En)));
            Assert.That(report.Fields[1].text, Is.EqualTo(OutroReportText.Title(ShowEndingOutcome.Released, ShowLang.En)));
            Assert.That(report.Fields[2].text, Is.EqualTo(OutroReportText.Body(ShowEndingOutcome.Released, ShowLang.En)));
            Assert.That(report.Fields[4].text, Is.EqualTo("0 / —"));
            Assert.That(report.VisibleChars, Is.EqualTo(report.ReportChars));

            report.PresentPreview(-2, -5, ShowEndingOutcome.Interrupted, ShowLang.Fr);
            Assert.That(report.CapturedReports, Is.Zero);
            Assert.That(report.CapturedTotal, Is.Zero);
            Assert.That(report.Fields[1].text, Is.EqualTo(OutroReportText.Title(ShowEndingOutcome.Interrupted, ShowLang.Fr)));
            Assert.That(report.Fields[4].text, Is.EqualTo("0 / —"));
        }

        private static Rect InkRectInCard(TMP_Text field)
        {
            Bounds ink = field.textBounds;
            float scale = field.transform.localScale.x;
            Vector3 pos = field.transform.localPosition;
            return Rect.MinMaxRect(pos.x + ink.min.x * scale, pos.y + ink.min.y * scale,
                pos.x + ink.max.x * scale, pos.y + ink.max.y * scale);
        }

        private static bool HasInkOverlap(Rect a, Rect b)
        {
            const float margin = .002f;
            return a.xMin < b.xMax - margin && a.xMax > b.xMin + margin
                && a.yMin < b.yMax - margin && a.yMax > b.yMin + margin;
        }
    }
}
