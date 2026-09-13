#nullable enable
using System.Reflection;
using FixedCamVr.Streaming;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Diagnostics.Tests
{
    public sealed class CommsPanelTakeoverTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private GameObject? _go;
        private CommsPanel _panel = null!;
        private CommsPanelLogic _logic = null!;
        private ShowLang _previousLanguage;

        [SetUp]
        public void Setup()
        {
            _previousLanguage = ShowLanguage.Current;
            ShowLanguage.Select(ShowLang.Ja);
            _go = new GameObject("[Test] Takeover");
            _panel = _go.AddComponent<CommsPanel>();
            Call("Awake");
            Assert.IsTrue(_panel.IsBuilt, "The actual panel must build for this check");
            _logic = (CommsPanelLogic)typeof(CommsPanel).GetField("_logic", Private)!.GetValue(_panel);
            _panel.SetDecayForPreview(1f, 0f);
        }

        [TearDown]
        public void Teardown()
        {
            if (_go != null) Object.DestroyImmediate(_go);
            ShowLanguage.Select(_previousLanguage);
        }

        private void Call(string method) => typeof(CommsPanel).GetMethod(method, Private)!.Invoke(_panel, null);
        private void Apply() => typeof(CommsPanel).GetMethod("Apply", Private)!
            .Invoke(_panel, new object[] { _logic.Weights });
        private void Advance(float seconds)
        {
            for (int i = 0; i < Mathf.CeilToInt(seconds * 30f); i++)
            { _logic.Tick(1f / 30f); Apply(); }
        }

        [TestCase(1f)]
        [TestCase(3f)]
        public void SuccessfulReportIsVisibleDuringAndAfterSeizure(float elapsed)
        {
            _panel.Deliver(CommsNotice.Takeover);
            Advance(elapsed);
            _panel.SetDecayForPreview(0f, 0f);
            _panel.Deliver(CommsNotice.MarkLogged);
            Advance(1.4f);
            Assert.AreEqual(CommsTakeoverPhase.Off, _panel.TakeoverPhase);
            Assert.AreEqual(1f, _panel.AppliedGlyph);
            Assert.AreEqual(0, _panel.CorruptedChars);
            Assert.Greater(_panel.VisibleChars, 0);
        }

        [Test]
        public void CompletedPanelRemainsHiddenWithoutReenteringOutput()
        {
            _panel.Deliver(CommsNotice.Takeover);
            Advance(10f);
            Assert.AreEqual(1, _panel.TakeoverCompletedCount);
            Assert.AreEqual(CommsTakeoverPhase.Off, _panel.TakeoverPhase);
            Assert.AreEqual(0f, _panel.AppliedGlyph);
            Apply();
            Assert.AreEqual(CommsTakeoverPhase.Off, _panel.TakeoverPhase);
        }

        [TestCase("OnRunRestarted")]
        [TestCase("OnDisable")]
        public void InterruptionClearsTheShownLieAndNextNotice(string interruption)
        {
            _panel.Deliver(CommsNotice.Takeover);
            Advance(5f);
            Assert.AreEqual(1, _panel.TakeoverCutCount);
            Call(interruption);
            Assert.AreEqual(CommsTakeoverPhase.Off, _panel.TakeoverPhase);
            Assert.AreEqual(0f, _panel.AppliedGlyph);
            Assert.AreEqual(0f, _panel.AppliedTakeoverCollapse);
            _panel.SetDecayForPreview(0f, 0f);
            _panel.Deliver(CommsNotice.Greeting);
            Advance(2.5f);
            Assert.AreEqual(1f, _panel.AppliedGlyph);
            Assert.AreEqual(0, _panel.CorruptedChars);
        }

        [Test]
        public void HoldingReportAfterCaptureCannotRestoreTheFace()
        {
            _panel.Deliver(CommsNotice.Takeover);
            Advance(4f);
            _logic.SetGuideWanted(true);
            Advance(1f);
            Assert.AreEqual(0f, _panel.AppliedGlyph);
            Assert.AreEqual(0f, _panel.AppliedFace);
            Assert.AreEqual(1, _panel.TakeoverCompletedCount);
        }

        [TestCase(ShowLang.Ja)]
        [TestCase(ShowLang.En)]
        [TestCase(ShowLang.Fr)]
        public void OneSentenceIsErasedWhileTheTailIsStillBeingGenerated(ShowLang lang)
        {
            ShowLanguage.Select(lang);
            _panel.Deliver(CommsNotice.Takeover);
            var text = (TMPro.TMP_Text)typeof(CommsPanel).GetField("_text", Private)!.GetValue(_panel);
            string original = text.text;
            int total = text.textInfo.characterCount, lastShown = 0, overlappingGrowth = 0;
            bool seized = false;
            for (int i = 0; i < 160; i++)
            {
                Advance(1f / 30f);
                Assert.AreEqual(original, text.text, "A second sentence must never replace the original");
                Assert.Less(_panel.VisibleChars, total, "The original must never finish");
                if (_panel.CorruptedChars > 0 && _panel.VisibleChars > lastShown) overlappingGrowth++;
                if (_panel.TakeoverCutCount > 0)
                {
                    seized = true;
                    Assert.AreEqual(0f, _panel.AppliedGlyph);
                }
                lastShown = _panel.VisibleChars;
            }
            Assert.GreaterOrEqual(overlappingGrowth, 2, "Erasure and new output must coexist");
            Assert.IsTrue(seized);
            Assert.AreEqual(0f, _panel.AppliedFace);
            Assert.AreEqual(1, _panel.TakeoverCutCount);
        }
    }
}
