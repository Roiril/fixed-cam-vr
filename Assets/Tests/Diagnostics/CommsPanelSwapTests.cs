#nullable enable
using System.Reflection;
using FixedCamVr.Streaming;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Diagnostics.Tests
{
    /// <summary>
    /// 同じ面のまま次の文面へ差し替える（<see cref="CommsStage.Swap"/>・2026-10-14）を<b>実物の面</b>で見る。
    /// TextMeshPro の文字列・枠の高さ・打鍵の数えを 90Hz で追い、
    /// 「見えている文面を入れ替えない」「旧文面が見えているあいだ枠を動かさない」
    /// 「1 字目と打鍵の 1 発目は同じフレーム」を確かめる。
    /// </summary>
    public sealed class CommsPanelSwapTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private const float Tick = 1f / 90f;
        // 72Hz の 1 フレームあたりの上限を 90Hz の刻みへ直す係数。
        private const float Per90 = 72f / 90f;
        private GameObject? _go;
        private CommsPanel _panel = null!;
        private CommsPanelLogic _logic = null!;
        private ShowLang _previousLanguage;

        [SetUp]
        public void Setup()
        {
            _previousLanguage = ShowLanguage.Current;
            ShowLanguage.Select(ShowLang.Ja);
            _go = new GameObject("[Test] Swap");
            _panel = _go.AddComponent<CommsPanel>();
            typeof(CommsPanel).GetMethod("Awake", Private)!.Invoke(_panel, null);
            Assert.IsTrue(_panel.IsBuilt, "The actual panel must build for this check");
            var sound = _go.GetComponent<TypeAudioCue>();
            if (!sound.HasClips)
                typeof(TypeAudioCue).GetMethod("Awake", Private)!.Invoke(sound, null);
            Assert.IsTrue(sound.HasClips, "打鍵の音源を掴めること（数えが 0 のままになる）");
            _logic = (CommsPanelLogic)typeof(CommsPanel).GetField("_logic", Private)!.GetValue(_panel);
            _panel.SetDecayForPreview(0f, 0f);
        }

        [TearDown]
        public void Teardown()
        {
            if (_go != null) Object.DestroyImmediate(_go);
            ShowLanguage.Select(_previousLanguage);
        }

        private void Step()
        {
            _logic.Tick(Tick);
            typeof(CommsPanel).GetMethod("Apply", Private)!.Invoke(_panel, new object[] { _logic.Weights });
        }

        private void StepUntil(CommsStage stage)
        {
            for (int i = 0; i < 2000 && _logic.Stage != stage; i++) Step();
            Assert.AreEqual(stage, _logic.Stage, "その段へ到達しなかった");
        }

        private string Text() => ((TMPro.TMP_Text)typeof(CommsPanel).GetField("_text", Private)!.GetValue(_panel)).text;

        private float TextAlpha() => _panel.VisibleChars > 0 && _panel.AppliedGlyph > 0.002f ? _panel.AppliedGlyph : 0f;

        // 見た目の明るさ（暗い地の上の文字。アルファは線形光で掛かる）。
        private static float Perceived(float alpha) => Mathf.Pow(alpha, 1f / PerceptualFade.DisplayGamma);

        [Test]
        public void ChainedNotice_FadesTheOldText_ThenMovesTheBox_ThenTypes()
        {
            // ①（1 行）→ ①b（3 行）。枠の高さが変わる組（本番でも続けて届く）。
            _panel.Deliver(CommsNotice.Begin);
            StepUntil(CommsStage.Hold);
            for (int i = 0; i < 18; i++) Step();
            string oldText = Text();
            float oldBand = _panel.AppliedBodyBandH;
            int hits = _panel.TypedCount;

            _panel.Deliver(CommsNotice.BeginHow);
            Assert.AreEqual(CommsStage.Swap, _logic.Stage);
            Assert.AreEqual(oldText, Text(), "旧文面が消える前に新しい文面を組んでいる");

            float prevAlpha = TextAlpha();
            string prevText = oldText;
            float prevBand = oldBand;
            bool firstLetter = false;
            for (int i = 0; i < 270 && !firstLetter; i++)
            {
                Step();
                string text = Text();
                float alpha = TextAlpha();
                float band = _panel.AppliedBodyBandH;
                int typed = _panel.TypedCount - hits;
                if (text != prevText)
                    Assert.IsTrue(prevAlpha <= 0f && alpha <= 0f,
                                  $"見えている文面を入れ替えた（{prevAlpha:0.00} → {alpha:0.00}）");
                if (alpha > 0f && text == oldText)
                    Assert.AreEqual(oldBand, band, 1e-6f, "旧文面が見えているのに枠が動いた");
                Assert.LessOrEqual(Perceived(prevAlpha) - Perceived(alpha), 0.20f * Per90,
                                   $"見た目が 1 刻みで {Perceived(prevAlpha) - Perceived(alpha):0.00} 落ちた");
                Assert.LessOrEqual(Mathf.Abs(band - prevBand), 0.15f * Per90 * Mathf.Max(oldBand, band),
                                   $"枠が 1 刻みで {band - prevBand:0.000}m 動いた");
                if (text != oldText && _panel.VisibleChars == 0)
                    Assert.AreEqual(0, typed, "字が出る前に打鍵が鳴った");
                if (text != oldText && _panel.VisibleChars > 0)
                {
                    Assert.GreaterOrEqual(typed, 1, "1 字目と同じフレームで打鍵が鳴っていない");
                    firstLetter = true;
                }
                prevAlpha = alpha;
                prevText = text;
                prevBand = band;
            }
            Assert.IsTrue(firstLetter, "新しい文面の 1 字目が出なかった");
            Assert.Greater(Mathf.Abs(_panel.AppliedBodyBandH - oldBand), 0.01f,
                           "前提: 2 通の文面の高さが違う（違わないと枠の動きを確かめられない）");

            StepUntil(CommsStage.Hold);
            Assert.AreEqual(_panel.NoticeChars, _panel.TypedCount - hits, "打鍵は見える字 1 つにつき 1 発");
        }

        [Test]
        public void PressingDuringHold_FadesTheText_BeforeTheBoxShrinks()
        {
            _panel.Deliver(CommsNotice.Greeting);
            StepUntil(CommsStage.Hold);
            _panel.SetControllerState(true, true);
            _panel.SetMarkState(0.3f, confirming: false);
            _logic.SetGuideWanted(true);

            float prevAlpha = 1f;
            for (int i = 0; i < 54; i++)   // 0.6 秒
            {
                Step();
                float alpha = TextAlpha();
                Assert.LessOrEqual(Perceived(prevAlpha) - Perceived(alpha), 0.25f * Per90,
                                   $"見た目が 1 刻みで {Perceived(prevAlpha) - Perceived(alpha):0.00} 落ちた");
                if (alpha > 0f)
                    Assert.GreaterOrEqual(_panel.AppliedBody, 0.999f, "字が残っているのに枠の丈が縮んでいる");
                prevAlpha = alpha;
            }
            Assert.AreEqual(0f, prevAlpha, 1e-6f, "文面が消えていない");
            Assert.AreEqual(0, _panel.VisibleChars);
        }
    }
}
