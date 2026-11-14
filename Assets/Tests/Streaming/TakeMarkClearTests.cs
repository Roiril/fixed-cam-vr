#nullable enable
using System.Collections.Generic;
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 2 周目 A の作り直し（LEDGER 0378）で足した 3 つの判断を固定する。
    ///   ・step の <c>markClear</c>: 報告でカットの素材だけを消し、演出もカットも続ける（左のシミ）
    ///   ・step の <c>markStartAdvance</c>: 報告の長押しを始めたらカットを今で終える（右の幕が伸び出す）
    ///   ・take の <c>skipIfDismissed</c>: 相手の演出が報告で畳まれていたら出ない（2 周目 B の人形）
    /// </summary>
    public sealed class TakeMarkClearTests
    {
        private static TakeRunnerLogic.Def Take(int camera, bool dismissible, bool[]? clear, bool[]? startAdvance,
                                                params float[] steps)
            => new()
            {
                lap = 2, camera = camera, onExit = false, offsetSec = 0f,
                skipWhenMissed = false, once = true, maxDurationSec = 0f,
                dismissible = dismissible,
                stepDurSec = steps,
                stepMarkClear = clear,
                stepMarkStartAdvance = startAdvance,
            };

        private static TakeRunnerLogic Started(TakeRunnerLogic.Def def, float now = 0f)
        {
            var l = new TakeRunnerLogic();
            l.SetDefs(new[] { def });
            l.OnZoneCommitted(2, 0, false, 0, 0, now);
            Assert.That(l.Tick(now, 0).action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep), "前提: 演出が走っていること");
            return l;
        }

        [Test]
        public void Clear_RemovesOnlyTheMaterial_TheTakeAndTheCutGoOn()
        {
            TakeRunnerLogic l = Started(Take(0, true, new[] { true, false }, null, 30f, 30f));
            Assert.That(l.ActiveRemovable, Is.True, "消す前は報告が効くカット");

            Assert.That(l.NotifyMarkPressed(2f), Is.EqualTo(TakeRunnerLogic.MarkResult.Cleared));
            Assert.That(l.ActiveCleared, Is.True);
            Assert.That(l.Tick(2f, 0).action, Is.EqualTo(TakeRunnerLogic.Action.None), "カットは尺どおり続く");
            Assert.That(l.IsActive, Is.True, "演出は畳まない");
            Assert.That(l.DismissCount, Is.EqualTo(0));
            Assert.That(l.ClearCount, Is.EqualTo(1));
            Assert.That(l.ActiveRemovable, Is.False, "消した後は報告が何にも効かない");

            // 消した後の同じカットでの報告は何にも効かない（演出を畳むと右の幕の人形が出なくなる）
            Assert.That(l.NotifyMarkPressed(3f), Is.EqualTo(TakeRunnerLogic.MarkResult.None));
            Assert.That(l.Tick(3f, 0).action, Is.EqualTo(TakeRunnerLogic.Action.None));
            Assert.That(l.IsActive, Is.True);

            // 次のカットでは従来どおり報告で畳まれる。消した印は演出の終わりまで残る
            Assert.That(l.Tick(30f, 0).stepIndex, Is.EqualTo(1));
            Assert.That(l.ActiveCleared, Is.True, "消した印は次のカットでも残る（2 枚目の層を戻さない）");
            Assert.That(l.NotifyMarkPressed(31f), Is.EqualTo(TakeRunnerLogic.MarkResult.Dismissed));
            Assert.That(l.Tick(31f, 0).reason, Is.EqualTo(TakeRunnerLogic.EndReason.VisitorDismissed));
            Assert.That(l.ActiveCleared, Is.False, "演出が終われば落ちる");
        }

        [Test]
        public void Clear_IsResetWhenTheTakeStartsAgain()
        {
            var l = new TakeRunnerLogic();
            l.SetDefs(new[] { Take(0, true, new[] { true }, null, 30f) });
            l.OnZoneCommitted(2, 0, false, 0, 0, 0f);
            l.Tick(0f, 0);
            Assert.That(l.NotifyMarkPressed(2f), Is.EqualTo(TakeRunnerLogic.MarkResult.Cleared));
            l.ResetRun();
            Assert.That(l.ClearCount, Is.EqualTo(0));
            l.OnZoneCommitted(2, 0, false, 0, 0, 40f);
            Assert.That(l.Tick(40f, 0).action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep));
            Assert.That(l.ActiveCleared, Is.False, "新しいランの演出は消していない");
            Assert.That(l.NotifyMarkPressed(42f), Is.EqualTo(TakeRunnerLogic.MarkResult.Cleared));
        }

        [Test]
        public void MarkStart_OnTheFlaggedCut_EndsThatCut()
        {
            TakeRunnerLogic l = Started(Take(0, true, null, new[] { false, true, false }, 1f, 30f, 30f));
            Assert.That(l.NotifyMarkStarted(0.5f), Is.False, "旗の無いカットでは進めない");
            Assert.That(l.Tick(1f, 0).stepIndex, Is.EqualTo(1), "前提: 2 カット目へ進んだ");

            Assert.That(l.NotifyMarkStarted(2f), Is.True);
            TakeRunnerLogic.Decision d = l.Tick(2f, 0);
            Assert.That(d.action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep));
            Assert.That(d.stepIndex, Is.EqualTo(2));
            Assert.That(l.MarkStartAdvanceCount, Is.EqualTo(1));
            Assert.That(l.IsActive, Is.True, "長押しの開始では演出を畳まない");
            Assert.That(l.NotifyMarkStarted(2.5f), Is.False, "進んだ先は旗を持たない");
        }

        [Test]
        public void MarkStart_BeforeTheCutBegan_IsNotCounted()
        {
            TakeRunnerLogic l = Started(Take(0, true, null, new[] { true }, 30f), now: 5f);
            Assert.That(l.NotifyMarkStarted(4f), Is.False);
            Assert.That(l.MarkStartAdvanceCount, Is.EqualTo(0));
        }

        private static TakeRunnerLogic TwoTakes(out TakeRunnerLogic.Def b)
        {
            TakeRunnerLogic.Def a = Take(0, true, null, null, 30f);
            b = Take(1, true, null, null, 5f);
            b.skipIfDismissedTake1 = 1;   // 相手は take 0
            var l = new TakeRunnerLogic();
            l.SetDefs(new[] { a, b });
            return l;
        }

        [Test]
        public void SkipIfDismissed_TheNextTakeDoesNotShow_AfterTheVisitorDismissedTheFirst()
        {
            TakeRunnerLogic l = TwoTakes(out _);
            l.OnZoneCommitted(2, 0, false, 0, 0, 0f);
            Assert.That(l.Tick(0f, 0).takeIndex, Is.EqualTo(0));
            Assert.That(l.NotifyMarkPressed(2f), Is.EqualTo(TakeRunnerLogic.MarkResult.Dismissed));
            l.Tick(2f, 0);
            Assert.That(l.WasDismissed(0), Is.True);

            l.OnZoneCommitted(2, 1, true, 2, 0, 3f, forward: true);
            Assert.That(l.Tick(3f, 1).action, Is.EqualTo(TakeRunnerLogic.Action.None), "2 本目は出ない");
            Assert.That(l.Tick(4f, 1).action, Is.EqualTo(TakeRunnerLogic.Action.None));
            Assert.That(l.SkippedByDismissCount, Is.EqualTo(1));
            Assert.That(l.LastSkippedByDismissTake, Is.EqualTo(1));
        }

        [Test]
        public void SkipIfDismissed_TheNextTakeShows_WhenTheFirstEndedWithoutAReport()
        {
            TakeRunnerLogic l = TwoTakes(out _);
            l.OnZoneCommitted(2, 0, false, 0, 0, 0f);
            l.Tick(0f, 0);
            Assert.That(l.Tick(30f, 0).reason, Is.EqualTo(TakeRunnerLogic.EndReason.Completed));
            Assert.That(l.WasDismissed(0), Is.False);

            l.OnZoneCommitted(2, 1, true, 2, 0, 31f, forward: true);
            TakeRunnerLogic.Decision d = l.Tick(31f, 1);
            Assert.That(d.action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep));
            Assert.That(d.takeIndex, Is.EqualTo(1));
            Assert.That(l.SkippedByDismissCount, Is.EqualTo(0));
        }

        [Test]
        public void SkipIfDismissed_ClearDoesNotCountAsDismissed()
        {
            // 左のシミだけを消した（markClear）のは、演出を畳んだことにならない
            TakeRunnerLogic.Def a = Take(0, true, new[] { true }, null, 30f);
            TakeRunnerLogic.Def b = Take(1, true, null, null, 5f);
            b.skipIfDismissedTake1 = 1;
            var l = new TakeRunnerLogic();
            l.SetDefs(new[] { a, b });
            l.OnZoneCommitted(2, 0, false, 0, 0, 0f);
            l.Tick(0f, 0);
            Assert.That(l.NotifyMarkPressed(2f), Is.EqualTo(TakeRunnerLogic.MarkResult.Cleared));
            l.Tick(30f, 0);
            Assert.That(l.WasDismissed(0), Is.False);
            l.OnZoneCommitted(2, 1, true, 2, 0, 31f, forward: true);
            Assert.That(l.Tick(31f, 1).takeIndex, Is.EqualTo(1), "2 本目は出る");
        }

        [Test]
        public void SkipIfDismissed_IsForgottenOnANewRun()
        {
            TakeRunnerLogic l = TwoTakes(out _);
            l.OnZoneCommitted(2, 0, false, 0, 0, 0f);
            l.Tick(0f, 0);
            l.NotifyMarkPressed(2f);
            l.Tick(2f, 0);
            l.ResetRun();
            Assert.That(l.WasDismissed(0), Is.False);
            l.OnZoneCommitted(2, 1, false, 0, 0, 10f);
            Assert.That(l.Tick(10f, 1).takeIndex, Is.EqualTo(1), "次の体験者には出る");
        }

        [Test]
        public void Script_FlagsAndPartner_AreResolved()
        {
            var a = new ShowTakeDef { id = "L2C0#0", steps = new[] { new ShowStepDef { markClear = true }, new ShowStepDef { markStartAdvance = true } } };
            var b = new ShowTakeDef { id = "L2C1#0", skipIfDismissed = "L2C0#0", steps = new[] { new ShowStepDef() } };
            var c = new ShowTakeDef { id = "L2C2#0", skipIfDismissed = "nope", steps = new[] { new ShowStepDef() } };
            var takes = new List<ShowTakeDef> { a, b, c };
            Assert.That(TakeRunner.BuildStepMarkClear(a), Is.EqualTo(new[] { true, false }));
            Assert.That(TakeRunner.BuildStepMarkStartAdvance(a), Is.EqualTo(new[] { false, true }));
            Assert.That(TakeRunner.BuildStepMarkClear(b), Is.Null);
            Assert.That(TakeRunner.ResolveSkipIfDismissed(takes, 1), Is.EqualTo(1), "相手 L2C0#0 = index 0 → 1");
            Assert.That(TakeRunner.ResolveSkipIfDismissed(takes, 0), Is.EqualTo(0), "空は相手なし");
            Assert.That(TakeRunner.ResolveSkipIfDismissed(takes, 2), Is.EqualTo(0), "未知の id は相手なし");

            var parsedStep = UnityEngine.JsonUtility.FromJson<ShowStepDef>("{\"cg\":\"doll\"}");
            Assert.That(parsedStep.markClear, Is.False, "キーが無ければ消さない");
            Assert.That(parsedStep.markStartAdvance, Is.False, "キーが無ければ進めない");
            var parsedTake = UnityEngine.JsonUtility.FromJson<ShowTakeDef>("{\"id\":\"x\"}");
            Assert.That(parsedTake.skipIfDismissed, Is.EqualTo(""), "キーが無ければ相手なし");
        }
    }
}
