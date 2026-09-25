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
    /// <summary>エンドロール冒頭の左右配置と、全文同時表示を検査する。</summary>
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
            MethodInfo? awake = typeof(OutroReport).GetMethod("Awake",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(awake, Is.Not.Null);
            awake!.Invoke(report, null);
            Assert.That(report.IsBuilt, Is.True, "エンド画面のフォント、シェーダ、ロゴのいずれかが欠けている");
            return report;
        }

        [Test]
        public void BuildsWideTwoColumnLead_AndStartsHidden()
        {
            OutroReport report = SpawnReport();
            Assert.That(OutroReport.PanelWidth / OutroReport.PanelHeight, Is.GreaterThan(1.8f));
            Assert.That(OutroReport.PhotoWidth / OutroReport.PhotoHeight, Is.EqualTo(16f / 9f).Within(.0001f));
            Assert.That(report.Fields.Count, Is.EqualTo(2));
            Assert.That(report.Fields[0].name, Is.EqualTo("Outcome"));
            Assert.That(report.Fields[1].name, Is.EqualTo("ReportCount"));
            Assert.That(report.Fields[0].alignment, Is.EqualTo(TextAlignmentOptions.Top));
            Assert.That(report.Fields[1].alignment, Is.EqualTo(TextAlignmentOptions.Top));
            Assert.That(report.AppliedAlpha, Is.Zero);
            Assert.That(report.VisibleChars, Is.Zero);
            Assert.That(report.TypeSfxBuilt, Is.False);
            Assert.That(report.TypedCount, Is.Zero);

            Transform logo = FindDescendant(report.transform, "MawarimiLogo");
            Transform photo = FindDescendant(report.transform, "EndingPhoto");
            Assert.That(logo.localPosition.x, Is.LessThan(0));
            Assert.That(photo.localPosition.x, Is.GreaterThan(0));
            Assert.That(photo.localScale.x / photo.localScale.y, Is.EqualTo(16f / 9f).Within(.0001f));
        }

        [Test]
        public void EveryLanguageAndOutcome_FitsLeftColumnAndShowsAllGlyphsTogether()
        {
            OutroReport report = SpawnReport();
            foreach (ShowLang lang in Languages)
            foreach (ShowEndingOutcome outcome in Outcomes)
            {
                report.PresentPreview(999, 8, outcome, lang, characters: 1);
                var rects = new Rect[report.Fields.Count];
                for (int i = 0; i < report.Fields.Count; i++)
                {
                    TMP_Text field = report.Fields[i];
                    Assert.That(field.font, Is.Not.Null, $"{lang}/{outcome}/{field.name}: font が無い");
                    foreach (char ch in field.text)
                    {
                        if (char.IsWhiteSpace(ch)) continue;
                        Assert.That(field.font.HasCharacter(ch), Is.True,
                            $"{lang}/{outcome}/{field.name}: 欠字 U+{(int)ch:X4} '{ch}'");
                    }
                    field.ForceMeshUpdate(true, true);
                    rects[i] = InkRectInCard(field);
                    Assert.That(rects[i].xMin, Is.GreaterThanOrEqualTo(-OutroReport.PanelWidth / 2 - .003f));
                    Assert.That(rects[i].xMax, Is.LessThanOrEqualTo(.16f),
                        $"{lang}/{outcome}/{field.name}: 写真列へはみ出す");
                    Assert.That(rects[i].yMin, Is.GreaterThanOrEqualTo(-OutroReport.PanelHeight / 2 - .003f));
                    Assert.That(rects[i].yMax, Is.LessThanOrEqualTo(OutroReport.PanelHeight / 2 + .003f));
                    Assert.That(field.maxVisibleCharacters, Is.EqualTo(int.MaxValue));
                }
                Assert.That(HasInkOverlap(rects[0], rects[1]), Is.False, $"{lang}/{outcome}: 文字が重なる");
                Assert.That(report.VisibleChars, Is.EqualTo(report.ReportChars),
                    $"{lang}/{outcome}: タイプ表示が残っている");
                Assert.That(report.AppliedAlpha, Is.EqualTo(1));
            }
        }

        [Test]
        public void SetEndingImages_SelectsOnlyNormalEndingImage()
        {
            OutroReport report = SpawnReport();
            var released = new Texture2D(16, 9);
            var trapped = new Texture2D(32, 18);
            try
            {
                report.SetEndingImages(released, trapped);
                report.PresentPreview(12, 8, ShowEndingOutcome.Released, ShowLang.Ja);
                Assert.That(report.CurrentEndingImage, Is.SameAs(released));
                Assert.That(report.PhotoVisible, Is.True);

                report.PresentPreview(12, 8, ShowEndingOutcome.Trapped, ShowLang.Ja);
                Assert.That(report.CurrentEndingImage, Is.SameAs(trapped));
                Assert.That(report.PhotoVisible, Is.True);

                report.PresentPreview(12, 8, ShowEndingOutcome.Interrupted, ShowLang.Ja);
                Assert.That(report.CurrentEndingImage, Is.Null);
                Assert.That(report.PhotoVisible, Is.False);
                Assert.That(report.Fields[0].text, Is.EqualTo("中断"));
            }
            finally
            {
                Object.DestroyImmediate(released);
                Object.DestroyImmediate(trapped);
            }
        }

        [Test]
        public void NextPreview_ReplacesOutcomeAndPreservesDiagnostics()
        {
            OutroReport report = SpawnReport();
            report.PresentPreview(999, 8, ShowEndingOutcome.Trapped, ShowLang.Ja);
            string previous = report.CurrentBody;
            report.PresentPreview(-2, -5, ShowEndingOutcome.Released, ShowLang.En);

            Assert.That(report.CapturedReports, Is.Zero);
            Assert.That(report.CapturedTotal, Is.Zero);
            Assert.That(report.CapturedOutcome, Is.EqualTo(ShowEndingOutcome.Released));
            Assert.That(report.CurrentBody, Is.Not.EqualTo(previous));
            Assert.That(report.CurrentBody, Is.EqualTo(
                OutroReportText.Compose(0, 0, ShowEndingOutcome.Released, ShowLang.En)));
            Assert.That(report.Fields[0].text, Is.EqualTo("Return End"));
            Assert.That(report.Fields[1].text, Is.EqualTo("Reports 0"));
            Assert.That(report.VisibleChars, Is.EqualTo(report.ReportChars));
        }

        private static Transform FindDescendant(Transform root, string name)
        {
            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
                if (child.name == name) return child;
            Assert.Fail($"{name} が無い");
            return root;
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
