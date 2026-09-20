#nullable enable
using System.Reflection;
using FixedCamVr.Streaming;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Diagnostics.Tests
{
    /// <summary>
    /// 憑依の出し方（<c>canon/LEDGER.md</c> 0230）を<b>実物の面</b>（TextMeshPro の実メッシュ・実際の打鍵と音の数え）で見る。
    /// 全文が一気に出て（打鍵 0）→ 読ませる → 左から同位置の赤い嘘と人形へ塗り替わる。
    /// </summary>
    public sealed class CommsPanelPossessionTests
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
            _go = new GameObject("[Test] Possession");
            _panel = _go.AddComponent<CommsPanel>();
            Call("Awake");
            Assert.IsTrue(_panel.IsBuilt, "The actual panel must build for this check");
            var sound = _go.GetComponent<TypeAudioCue>();
            if (!sound.HasClips)
                typeof(TypeAudioCue).GetMethod("Awake", Private)!.Invoke(sound, null);
            Assert.IsTrue(sound.HasClips);
            var sweep = _go.GetComponent<CurseSweepAudioCue>();
            if (!sweep.HasClips)
                typeof(CurseSweepAudioCue).GetMethod("Awake", Private)!.Invoke(sweep, null);
            Assert.IsTrue(sweep.HasClips, "乱れの音源（sfx_glitch）を掴めること");
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
        private TMPro.TMP_Text Text() => (TMPro.TMP_Text)typeof(CommsPanel).GetField("_text", Private)!.GetValue(_panel);
        private TMPro.TMP_Text LieText() => (TMPro.TMP_Text)typeof(CommsPanel).GetField("_lieText", Private)!.GetValue(_panel);
        private static int VisibleCount(TMPro.TMP_Text text)
        {
            int n = 0;
            for (int i = 0; i < text.textInfo.characterCount; i++)
                if (text.textInfo.characterInfo[i].isVisible) n++;
            return n;
        }
        private static Color32 VertexColor(TMPro.TMP_Text text, int charIndex)
        {
            var ch = text.textInfo.characterInfo[charIndex];
            return text.textInfo.meshInfo[ch.materialReferenceIndex].colors32[ch.vertexIndex];
        }
        private float ReadSec() => CommsPossessionLogic.ReadSecFor(Text().textInfo.characterCount, ShowLanguage.Current);

        [Test]
        public void TheTruthAppearsAllAtOnce_WithoutKeystrokes()
        {
            int hits = _panel.TypedCount;
            _panel.Deliver(CommsNotice.Takeover);
            Assert.IsTrue(_panel.TakeoverVisible);
            Assert.AreEqual(0, _panel.NoticeChars, "一気に出るので鳴るはずの打鍵は 0");
            Assert.AreEqual(1, _panel.PossessedCount);
            Assert.AreEqual(1, _panel.LieCount);
            Advance(CommsPanelLogic.InSec + CommsPossessionLogic.ShowSec + 0.05f);
            Assert.AreEqual(CommsPossessionPhase.Shown, _panel.PossessionPhase);
            Assert.AreEqual(Text().textInfo.characterCount, _panel.VisibleChars, "全文が出ている");
            Assert.AreEqual(hits, _panel.TypedCount, "打鍵は 1 発も鳴らない");
            Assert.GreaterOrEqual(_panel.AppliedGlyph, 0.99f);
            Assert.AreEqual(0, _panel.CorruptedChars, "読ませているあいだ 1 字も切れない");
            Assert.AreEqual(0f, _panel.AppliedCurse, "出た初めは通常の面（斑 0）");
            Assert.AreEqual(0f, _panel.AppliedFaceMix, "顔はスイのまま");
            Assert.AreEqual(0f, _panel.AppliedSweep);
            Assert.AreEqual(0, _panel.LieChars, "前線が来る前は赤い嘘を出さない");
        }

        [Test]
        public void TruthAndLieAreClippedByTheSameLeftToRightFront()
        {
            _panel.Deliver(CommsNotice.Takeover);
            Advance(CommsPanelLogic.InSec + CommsPossessionLogic.ShowSec + 0.3f);
            var text = Text();
            string truth = CommsPanel.NoticeText(CommsNotice.Takeover, ShowLanguage.Current);
            string lie = CommsPanel.TakeoverLieText(ShowLanguage.Current);
            float readSec = ReadSec();
            Assert.AreEqual(truth, text.text);
            Assert.AreEqual(lie, LieText().text);
            Assert.AreEqual(text.transform.localPosition, LieText().transform.localPosition,
                            "真実と嘘は同じ位置に重なる");
            Assert.Contains(Resources.Load<TMPro.TMP_FontAsset>("Fonts/JapaneseHud SDF"),
                            LieText().font.fallbackFontAssetTable,
                            "怖い書体に欠けた Latin は通常本文の書体へ戻す");
            Assert.AreEqual(0, _panel.LieChars);
            int truthVisible = VisibleCount(text);
            Color32 first = VertexColor(text, 0);
            Assert.AreEqual(209, first.r, "象牙のまま（赤は使わない）");
            Assert.AreEqual(199, first.g);
            Assert.AreEqual(184, first.b);
            Assert.AreEqual(255, first.a);

            Advance(readSec - 0.3f + CommsPossessionLogic.SweepSec * 0.5f);
            Assert.AreEqual(CommsPossessionPhase.Sweep, _panel.PossessionPhase);
            Assert.AreEqual(truth, Text().text, "真実の文字列は途中で差し替えない");
            Assert.AreEqual(lie, LieText().text);
            Assert.Greater(_panel.LieChars, 0, "左側には赤い嘘が出る");
            Assert.Greater(_panel.CorruptedChars, 0, "同じ左側から真実が消える");
            Color32 red = VertexColor(LieText(), 0);
            Assert.AreEqual(255, red.r);
            Assert.Less(red.g, 64);
            Assert.Less(red.b, 40);

            for (int frame = 0; frame < 60 && _panel.PossessionPhase != CommsPossessionPhase.Cursed; frame++)
            {
                _logic.Tick(1f / 30f);
                Apply();
            }
            text = Text();
            Assert.AreEqual(CommsPossessionPhase.Cursed, _panel.PossessionPhase);
            Assert.AreEqual(truth, text.text, "完了後も真実 TMP の文字列は保つ");
            Assert.AreEqual(VisibleCount(LieText()), _panel.LieChars, "嘘の行は全字が出ている");
            Assert.AreEqual(truthVisible, _panel.CorruptedChars, "上書きされた真実の字の数");
            Assert.AreEqual(255, VertexColor(LieText(), LieText().textInfo.characterCount - 1).a,
                            "嘘の尾は読める");
            Assert.AreEqual(1f, _panel.AppliedSweep);
            Assert.AreEqual(1f, _panel.AppliedCurse, "塗り替わった後は全面");
            Assert.AreEqual(1f, _panel.AppliedFaceMix, "顔は完全に人形");
            Assert.AreEqual(1, _panel.SweepCount);
        }

        [Test]
        public void TheSweepReplacesTheLeftSideBeforeTheRightSide()
        {
            _panel.Deliver(CommsNotice.Takeover);
            var text = Text();
            var info = text.textInfo;
            Advance(CommsPanelLogic.InSec + CommsPossessionLogic.ShowSec + ReadSec() - 1f / 30f);
            int first = -1, last = -1;
            for (int i = 0; i < info.characterCount; i++)
                if (info.characterInfo[i].isVisible) { if (first < 0) first = i; last = i; }
            Assert.Greater(last, first);
            int firstCutLeft = -1, firstCutRight = -1;
            for (int frame = 0; frame < 60; frame++)
            {
                _logic.Tick(1f / 30f);
                Apply();
                if (VertexColor(text, first).a == 0 && firstCutLeft < 0) firstCutLeft = frame;
                if (VertexColor(text, last).a == 0 && firstCutRight < 0) firstCutRight = frame;
                if (_panel.PossessionPhase == CommsPossessionPhase.Cursed) break;
            }
            Assert.GreaterOrEqual(firstCutLeft, 0);
            Assert.GreaterOrEqual(firstCutRight, 0);
            Assert.Less(firstCutLeft, firstCutRight, "左の字が右の字より先に置換される");
            Assert.AreEqual(CommsPossessionPhase.Cursed, _panel.PossessionPhase);
            Assert.AreEqual(VisibleCount(text), _panel.CorruptedChars);
        }

        [Test]
        public void CursedReplyStartsAsTheRedLieWithoutSweepOrKeystrokes()
        {
            _panel.SetDecayForPreview(CommsCurseLogic.PossessedLevel, 0f);
            int hits = _panel.TypedCount;
            _panel.Deliver(CommsNotice.MarkLogged);
            Assert.AreEqual(CommsDelivery.Cursed, _logic.Delivery);
            Apply();
            Assert.AreEqual(1f, _panel.AppliedFaceMix, "開く最初のフレームから人形側");
            Assert.AreEqual(0, _panel.LieChars, "枠が開く前に本文だけ先行しない");
            Advance(CommsPanelLogic.InSec + _logic.TypeSec + 0.1f);
            Assert.AreEqual(Text().textInfo.characterCount, _panel.VisibleChars);
            Assert.AreEqual(hits, _panel.TypedCount);
            Assert.AreEqual(CommsPanel.TakeoverLieText(ShowLanguage.Current), LieText().text);
            Assert.AreEqual(VisibleCount(LieText()), _panel.LieChars);
            Assert.AreEqual(1f, _panel.AppliedFaceMix, "最初から人形");
            Assert.AreEqual(1f, _panel.AppliedCurse);
            Assert.AreEqual(0, _panel.SweepSfxCount);

            _panel.SetDecayForPreview(CommsInvasionLogic.FirstPovLevel, 0f);
            hits = _panel.TypedCount;
            _panel.Deliver(CommsNotice.MarkLogged);
            Assert.AreEqual(CommsDelivery.Typed, _logic.Delivery);
            Advance(CommsPanelLogic.InSec + _logic.TypeSec + 0.1f);
            Assert.AreEqual(_panel.NoticeChars, _panel.TypedCount - hits, "侵食度 0.25 の返事は 1 字 1 発のまま");
            Assert.Greater(_panel.NoticeChars, 0);
        }

        [Test]
        public void SweepSoundFiresOnce_OnTheFirstFrameOfTheFront()
        {
            _panel.Deliver(CommsNotice.Takeover);
            Assert.AreEqual(0, _panel.SweepSfxCount);
            Advance(CommsPanelLogic.InSec + CommsPossessionLogic.ShowSec + ReadSec() - 1f / 30f);
            Assert.AreEqual(CommsPossessionPhase.Shown, _panel.PossessionPhase);
            Assert.AreEqual(0, _panel.SweepSfxCount, "降り始める前は鳴らない");
            Advance(2f / 30f);
            Assert.AreEqual(CommsPossessionPhase.Sweep, _panel.PossessionPhase);
            Assert.AreEqual(1, _panel.SweepSfxCount, "前線が降り始めたコマで 1 発");
            Advance(CommsPossessionLogic.SweepSec + CommsPanelLogic.HoldSec + 0.5f);
            Assert.AreEqual(1, _panel.SweepSfxCount, "同じ連絡では二度鳴らない");

            _panel.Deliver(CommsNotice.MarkLogged);   // 侵食度 1 の普通の返事（最初から乗っ取り後）
            Advance(CommsPanelLogic.InSec + _logic.TypeSec + 0.1f);
            Assert.AreEqual(1, _panel.SweepSfxCount, "通常の Cursed 報告では前線音を鳴らさない");
            Assert.AreEqual(1, _panel.SweepCount, "Sweep は Takeover の一度だけ");
        }

        [TestCase("OnRunRestarted")]
        [TestCase("OnDisable")]
        public void InterruptionClearsTheLieAndTheNextNoticeIsClean(string interruption)
        {
            _panel.Deliver(CommsNotice.Takeover);
            Advance(1.5f);
            Call(interruption);
            Assert.IsFalse(_panel.TakeoverVisible);
            Assert.AreEqual(CommsPossessionPhase.Off, _panel.PossessionPhase);
            Assert.AreEqual(0f, _panel.AppliedGlyph);
            Assert.AreEqual(0f, _panel.AppliedSweep);
            _panel.SetDecayForPreview(0f, 0f);
            _panel.Deliver(CommsNotice.Greeting);
            Advance(2.5f);
            Assert.AreEqual(1f, _panel.AppliedGlyph);
            Assert.AreEqual(0, _panel.CorruptedChars);
            Assert.AreEqual(CommsPossessionPhase.Off, _panel.PossessionPhase);
        }

        [Test]
        public void StopNoticeRemainsReadableAtFullInvasion()
        {
            _panel.Deliver(CommsNotice.Halt);
            Advance(1f);
            Assert.AreEqual(1f, _panel.InvasionProgress);
            Assert.AreEqual(0, _panel.CorruptedChars);
            Assert.Greater(_panel.VisibleChars, 0);
            Assert.AreEqual(CommsPossessionPhase.Off, _panel.PossessionPhase);
        }

        [Test]
        public void PromptAtFullInvasionIsTypedAndClean()
        {
            int hits = _panel.TypedCount;
            _panel.Deliver(CommsNotice.Prompt);
            Assert.AreEqual(CommsDelivery.Typed, _logic.Delivery, "続く警告は侵食度に関わらず打つ");
            Advance(CommsPanelLogic.InSec + 0.5f);
            Assert.Greater(_panel.TypedCount, hits);
            Assert.AreEqual(0, _panel.CorruptedChars);
            Assert.AreEqual(0f, _panel.AppliedSweep);
            Assert.AreEqual(0f, _panel.AppliedCurse, "エージェントが復帰して助ける段は斑 0");
        }

        [Test]
        public void HoldingAfterPossessionKeepsTheGaugeButNotThePreviousReply()
        {
            _panel.Deliver(CommsNotice.MarkLogged);
            Advance(CommsPanelLogic.InSec + _logic.TypeSec + .1f);
            _panel.SetControllerState(true, true);
            _panel.SetMarkState(.6f, false);
            _logic.SetGuideWanted(true);
            Advance(.5f);
            var hint = (TMPro.TMP_Text)typeof(CommsPanel).GetField("_hint", Private)!.GetValue(_panel);
            Assert.IsNotEmpty(_panel.HintBody);
            Assert.IsTrue(hint.gameObject.activeSelf);
            Assert.AreEqual((int)UnityEngine.Rendering.CompareFunction.Always,
                hint.fontMaterial.GetInt("_StencilComp"), "操作ゲージを乗っ取りのマスクで消さない");
            Assert.AreEqual(0, _panel.LieChars);
            Assert.AreEqual(0, _panel.VisibleChars);
            _panel.SetMarkState(0f, false);
            _logic.SetGuideWanted(false);
            Apply();
            Assert.AreEqual(0, _panel.LieChars);
            Assert.AreEqual(0, _panel.VisibleChars);
        }

        [Test]
        public void HoldingReportDuringTheLieDoesNotCutIt()
        {
            _panel.Deliver(CommsNotice.Takeover);
            Advance(CommsPanelLogic.InSec + 0.3f);
            _panel.SetMarkState(0.5f, true);
            _logic.SetGuideWanted(true);
            Advance(0.5f);
            Assert.AreEqual(CommsStage.Type, _logic.Stage, "出る → 読ませる → 塗り替わる は途中で退かない");
            Assert.AreEqual(CommsPossessionPhase.Shown, _panel.PossessionPhase);
            Assert.AreEqual("", _panel.HintBody, "Takeover 中は解析中とゲージを出さない");
            Advance(ReadSec() + CommsPossessionLogic.SweepSec + 0.2f);
            Assert.AreEqual(1, _panel.SweepCount, "塗り替わり切るまで見せる");
            Assert.AreEqual(CommsPossessionPhase.Cursed, _panel.PossessionPhase);
        }
    }
}
