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

        [TestCase(false)]
        [TestCase(true)]
        public void SuccessfulReportIsVisibleAfterEitherKindOfLie(bool repeat)
        {
            _panel.Deliver(repeat ? CommsNotice.TakeoverLie : CommsNotice.Takeover);
            Advance(repeat ? 1f : 7f);
            _panel.SetDecayForPreview(0f, 0f);
            _panel.Deliver(CommsNotice.MarkLogged);
            Advance(1.4f);
            Assert.AreEqual(CommsTakeoverPhase.Off, _panel.TakeoverPhase);
            Assert.AreEqual(1f, _panel.AppliedGlyph);
            Assert.AreEqual(0, _panel.CorruptedChars);
            Assert.Greater(_panel.VisibleChars, 0);
        }

        [Test]
        public void CompletedPanelRemainsHiddenWithoutReenteringTruth()
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
            Assert.Greater(_panel.AppliedTakeoverLie, 0f);
            Call(interruption);
            Assert.AreEqual(CommsTakeoverPhase.Off, _panel.TakeoverPhase);
            Assert.AreEqual(0f, _panel.AppliedGlyph);
            Assert.AreEqual(0f, _panel.AppliedTakeoverLie);
            _panel.SetDecayForPreview(0f, 0f);
            _panel.Deliver(CommsNotice.Greeting);
            Advance(2.5f);
            Assert.AreEqual(1f, _panel.AppliedGlyph);
            Assert.AreEqual(0, _panel.CorruptedChars);
        }
    }
}
