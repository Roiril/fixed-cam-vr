#nullable enable
using System.Reflection;
using FixedCamVr.Streaming;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Diagnostics.Tests
{
    /// <summary>
    /// 憑依の出し方（<c>canon/LEDGER.md</c> 0230）を<b>実物の面</b>（TextMeshPro の実メッシュ・実際の打鍵と音の数え）で見る。
    /// 全文が一気に出て（打鍵 0）→ 読ませて（赤い「異常なし」が読める）→ 上から前線が降りて（上の行が先に切れる）→
    /// 塗り替わり切る（全字が切れ・顔は人形・赤は 0）。
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
        public void TheLieAppearsAllAtOnce_WithoutKeystrokes()
        {
            int hits = _panel.TypedCount;
            _panel.Deliver(CommsNotice.Takeover);
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
        }

        [Test]
        public void RedPrefixIsReadableWhileShown_AndCutAfterTheSweep()
        {
            _panel.Deliver(CommsNotice.Takeover);
            Advance(CommsPanelLogic.InSec + CommsPossessionLogic.ShowSec + 0.3f);
            var text = Text();
            Assert.AreEqual(4, _panel.RedChars, "「異常なし」の 4 文字だけを赤くする");
            for (int i = 0; i < 4; i++)
            {
                Color32 color = VertexColor(text, i);
                Assert.AreEqual(184, color.r, $"{i}文字目 red");
                Assert.AreEqual(48, color.g, $"{i}文字目 green");
                Assert.AreEqual(40, color.b, $"{i}文字目 blue");
                Assert.AreEqual(255, color.a, $"{i}文字目は読める");
            }
            Color32 suffix = VertexColor(text, 4);
            Assert.AreEqual(209, suffix.r, "続く「と」は地の象牙色を保つ");
            Assert.AreEqual(199, suffix.g);
            Assert.AreEqual(184, suffix.b);

            Advance(ReadSec() + CommsPossessionLogic.SweepSec + 0.1f);
            Assert.AreEqual(CommsPossessionPhase.Cursed, _panel.PossessionPhase);
            Assert.AreEqual(1f, _panel.AppliedSweep);
            Assert.AreEqual(1f, _panel.AppliedCurse, "塗り替わった後は全面");
            Assert.AreEqual(1f, _panel.AppliedFaceMix, "顔は完全に人形");
            Assert.AreEqual(VisibleCount(text), _panel.CorruptedChars, "全字が切られている");
            Assert.AreEqual(0, _panel.RedChars, "赤い字も塗り替わって切れる");
            Assert.AreEqual(0, VertexColor(text, 0).a, "切られた字は CPU でも alpha 0");
            Assert.AreEqual(1, _panel.SweepCount);
        }

        [Test]
        public void TheSweepCutsTheTopLineBeforeTheBottomLine()
        {
            _panel.SetDecayForPreview(CommsCurseLogic.PossessedLevel, 0f);
            _panel.Deliver(CommsNotice.BeginHow);   // 3 行
            var text = Text();
            var info = text.textInfo;
            int lastLine = 0;
            for (int i = 0; i < info.characterCount; i++)
                if (info.characterInfo[i].isVisible) lastLine = Mathf.Max(lastLine, info.characterInfo[i].lineNumber);
            Assert.GreaterOrEqual(lastLine, 2, "3 行の文面で見る");

            Advance(CommsPanelLogic.InSec + CommsPossessionLogic.ShowSec + ReadSec() - 1f / 30f);
            int firstCutTop = -1, firstCutBottom = -1;
            for (int frame = 0; frame < 30; frame++)
            {
                _logic.Tick(1f / 30f);
                Apply();
                bool topCut = false, bottomCut = false;
                for (int i = 0; i < info.characterCount; i++)
                {
                    var ch = info.characterInfo[i];
                    if (!ch.isVisible) continue;
                    bool cut = VertexColor(text, i).a == 0;
                    if (ch.lineNumber == 0 && cut) topCut = true;
                    if (ch.lineNumber == lastLine && cut) bottomCut = true;
                }
                if (topCut && firstCutTop < 0) firstCutTop = frame;
                if (bottomCut && firstCutBottom < 0) firstCutBottom = frame;
                if (_panel.PossessionPhase == CommsPossessionPhase.Cursed) break;
            }
            Assert.GreaterOrEqual(firstCutTop, 0, "上の行が切れる");
            Assert.GreaterOrEqual(firstCutBottom, 0, "下の行も最後には切れる");
            Assert.Less(firstCutTop, firstCutBottom, "上の行が下の行より先に切れる（前線は上から降りる）");
            Assert.AreEqual(CommsPossessionPhase.Cursed, _panel.PossessionPhase);
            Assert.AreEqual(VisibleCount(text), _panel.CorruptedChars);
        }

        [Test]
        public void PossessedReplyHasNoKeystrokes_AndTheTypedReplyBelowItDoes()
        {
            _panel.SetDecayForPreview(CommsCurseLogic.PossessedLevel, 0f);
            int hits = _panel.TypedCount;
            _panel.Deliver(CommsNotice.MarkLogged);
            Assert.AreEqual(CommsDelivery.Possessed, _logic.Delivery);
            Advance(CommsPanelLogic.InSec + CommsPossessionLogic.ShowSec + 0.1f);
            Assert.AreEqual(Text().textInfo.characterCount, _panel.VisibleChars);
            Assert.AreEqual(hits, _panel.TypedCount, "侵食度 0.75 の返事は打鍵なし");

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

            _panel.Deliver(CommsNotice.MarkLogged);   // 侵食度 1 の普通の返事（憑依の出し方）
            Advance(CommsPanelLogic.InSec + _logic.TypeSec + 0.1f);
            Assert.AreEqual(2, _panel.SweepSfxCount, "連絡ごとに 1 発");
            Assert.AreEqual(2, _panel.SweepCount);
        }

        [TestCase("OnRunRestarted")]
        [TestCase("OnDisable")]
        public void InterruptionClearsTheLieAndTheNextNoticeIsClean(string interruption)
        {
            _panel.Deliver(CommsNotice.Takeover);
            Advance(1.5f);
            Call(interruption);
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
        public void HoldingReportDuringTheLieDoesNotCutIt()
        {
            _panel.Deliver(CommsNotice.Takeover);
            Advance(CommsPanelLogic.InSec + 0.3f);
            _logic.SetGuideWanted(true);
            Advance(0.5f);
            Assert.AreEqual(CommsStage.Type, _logic.Stage, "出る → 読ませる → 塗り替わる は途中で退かない");
            Assert.AreEqual(CommsPossessionPhase.Shown, _panel.PossessionPhase);
            Advance(ReadSec() + CommsPossessionLogic.SweepSec + 0.2f);
            Assert.AreEqual(1, _panel.SweepCount, "塗り替わり切るまで見せる");
            Assert.AreEqual(CommsPossessionPhase.Cursed, _panel.PossessionPhase);
        }
    }
}
