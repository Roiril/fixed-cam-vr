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
            var sound = _go.GetComponent<TypeAudioCue>();
            if (!sound.HasClips)
                typeof(TypeAudioCue).GetMethod("Awake", Private)!.Invoke(sound, null);
            Assert.IsTrue(sound.HasClips);
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
        public void OneSentenceDeformsWhileTheTailIsStillBeingGenerated(ShowLang lang)
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
            Assert.GreaterOrEqual(overlappingGrowth, 2, "Deformation and new output must coexist");
            Assert.IsTrue(seized);
            Assert.AreEqual(0f, _panel.AppliedFace);
            Assert.AreEqual(1, _panel.TakeoverCutCount);
        }

        [Test]
        public void ActualGlyphMeshStretchesAndStopsDuringResistance()
        {
            _panel.Deliver(CommsNotice.Takeover);
            var text = (TMPro.TMP_Text)typeof(CommsPanel).GetField("_text", Private)!.GetValue(_panel);
            _logic.Tick(CommsPanelLogic.InSec);
            _logic.Tick(_logic.TypeSec * 0.15f);
            Apply();
            var ch = text.textInfo.characterInfo[0];
            Vector3[] original = (Vector3[])text.textInfo.meshInfo[ch.materialReferenceIndex].vertices.Clone();
            _logic.Tick(_logic.TypeSec * 0.60f);
            Apply();
            Assert.IsTrue(_panel.TakeoverResistance);
            Vector3[] held = (Vector3[])text.textInfo.meshInfo[ch.materialReferenceIndex].vertices.Clone();
            Color32[] heldColors = (Color32[])text.textInfo.meshInfo[ch.materialReferenceIndex].colors32.Clone();
            Assert.Less(held[ch.vertexIndex].y, original[ch.vertexIndex].y,
                "The first glyph's lower edge must actually move");
            Assert.AreEqual(original[ch.vertexIndex + 1], held[ch.vertexIndex + 1],
                "The upper edge stays anchored");
            int hits = _panel.TypedCount;
            int generated = _panel.VisibleChars;
            _logic.Tick(_logic.TypeSec * 0.06f);
            Apply();
            CollectionAssert.AreEqual(held, text.textInfo.meshInfo[ch.materialReferenceIndex].vertices);
            CollectionAssert.AreEqual(heldColors, text.textInfo.meshInfo[ch.materialReferenceIndex].colors32);
            Assert.AreEqual(hits, _panel.TypedCount);
            Assert.AreEqual(generated, _panel.VisibleChars);
            _logic.Tick(_logic.TypeSec * 0.14f);
            Apply();
            Assert.Greater(_panel.VisibleChars, generated);
            Assert.Greater(_panel.TypedCount, hits);
            _logic.Tick(_logic.TypeSec * 0.05f);
            Apply();
            Assert.AreEqual(0f, _panel.AppliedPanelAlpha);
            Assert.AreEqual(0f, _panel.AppliedFace);
            Assert.AreEqual(0f, _panel.AppliedGlyph);
        }

        [TestCase(ShowLang.Ja)]
        [TestCase(ShowLang.En)]
        [TestCase(ShowLang.Fr)]
        public void MultipleGlyphsInOneFrameKeepEveryKeystroke(ShowLang lang)
        {
            ShowLanguage.Select(lang);
            _panel.Deliver(CommsNotice.Takeover);
            _logic.Tick(CommsPanelLogic.InSec);
            Apply();
            int startHits = _panel.TypedCount;
            for (int i = 0; i < 22; i++)
            {
                _logic.Tick(0.1f);
                Apply();
            }
            Assert.AreEqual(_panel.NoticeChars, _panel.TypedCount - startHits);
            Assert.AreEqual(1, _panel.TakeoverCutCount);
        }
    }
}
