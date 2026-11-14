#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 報告でカットを 1 つ進める（step の <c>markAdvance</c>）の判断を固定する。
    /// 演出は畳まず、そのカットだけを今で終え、次の Tick で次のカットへ移る（<see cref="TakeRunnerLogic.MarkResult.Advanced"/>）。
    /// 締めのカット（<see cref="TakeRunnerLogic.WaitMark"/>）の Released と、受け付けない最初の数秒
    /// （<see cref="TakeRunnerLogic.MarkGraceSec"/>）は変えない。
    /// </summary>
    public sealed class TakeMarkAdvanceTests
    {
        private static TakeRunnerLogic.Def Take(bool dismissible, bool[]? advance, params float[] steps)
            => new()
            {
                lap = 2, camera = 0, onExit = false, offsetSec = 0f,
                skipWhenMissed = false, once = true, maxDurationSec = 0f,
                dismissible = dismissible,
                stepDurSec = steps,
                stepMarkAdvance = advance,
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
        public void Report_OnAdvanceCut_EndsOnlyThatCut_AndTheNextCutFollows()
        {
            TakeRunnerLogic l = Started(Take(false, new[] { true, false, false }, 30f, 5f, 5f));
            Assert.That(l.ActiveStepAdvancesOnMark, Is.True);

            Assert.That(l.NotifyMarkPressed(2f), Is.EqualTo(TakeRunnerLogic.MarkResult.Advanced));
            TakeRunnerLogic.Decision d = l.Tick(2f, 0);
            Assert.That(d.action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep), "次の Tick で次のカットへ");
            Assert.That(d.stepIndex, Is.EqualTo(1));
            Assert.That(l.IsActive, Is.True, "演出は畳まない");
            Assert.That(l.DismissCount, Is.EqualTo(0), "畳んだ回数には数えない");
            Assert.That(l.ActiveStepAdvancesOnMark, Is.False, "次のカットは旗を持たない");
        }

        [Test]
        public void Report_OnCutWithoutTheFlag_DoesNothing_WhenTheTakeIsNotDismissible()
        {
            TakeRunnerLogic l = Started(Take(false, new[] { true, false }, 1f, 30f));
            Assert.That(l.Tick(1f, 0).stepIndex, Is.EqualTo(1), "前提: 2 カット目へ進んだ");
            Assert.That(l.NotifyMarkPressed(2f), Is.EqualTo(TakeRunnerLogic.MarkResult.None));
            Assert.That(l.Tick(2.1f, 0).action, Is.EqualTo(TakeRunnerLogic.Action.None), "2 カット目は尺どおり続く");
        }

        [Test]
        public void Report_OnLastAdvanceCut_EndsTheTakeAsCompleted()
        {
            TakeRunnerLogic l = Started(Take(false, new[] { true }, 30f));
            Assert.That(l.NotifyMarkPressed(2f), Is.EqualTo(TakeRunnerLogic.MarkResult.Advanced));
            TakeRunnerLogic.Decision d = l.Tick(2f, 0);
            Assert.That(d.action, Is.EqualTo(TakeRunnerLogic.Action.EndTake));
            Assert.That(d.reason, Is.EqualTo(TakeRunnerLogic.EndReason.Completed), "報告で畳んだのではなく、最後のカットが終わった");
            Assert.That(d.dismissed, Is.False);
        }

        [Test]
        public void Advance_WinsOverDismissible_OnTheFlaggedCut()
        {
            TakeRunnerLogic l = Started(Take(true, new[] { true, false }, 30f, 30f));
            Assert.That(l.NotifyMarkPressed(2f), Is.EqualTo(TakeRunnerLogic.MarkResult.Advanced));
            Assert.That(l.Tick(2f, 0).stepIndex, Is.EqualTo(1));
            Assert.That(l.IsActive, Is.True);

            // 旗の無いカットでは従来どおり畳まれる
            Assert.That(l.NotifyMarkPressed(3f), Is.EqualTo(TakeRunnerLogic.MarkResult.Dismissed));
            Assert.That(l.Tick(3f, 0).reason, Is.EqualTo(TakeRunnerLogic.EndReason.VisitorDismissed));
        }

        [Test]
        public void WaitMarkCut_IsStillReleased_EvenWithTheFlag()
        {
            TakeRunnerLogic l = Started(Take(false, new[] { true }, TakeRunnerLogic.WaitMark));
            Assert.That(l.NotifyMarkPressed(TakeRunnerLogic.MarkGraceSec + 1f), Is.EqualTo(TakeRunnerLogic.MarkResult.Released));
        }

        [Test]
        public void TooEarly_IsUnchanged()
        {
            // 締めのカットを持つ演出の最初の数秒は、markAdvance のカットでも受け付けない。
            TakeRunnerLogic l = Started(Take(false, new[] { true, false }, 30f, TakeRunnerLogic.WaitMark));
            Assert.That(l.IsMarkTooEarly(1f), Is.True);
            Assert.That(l.NotifyMarkPressed(1f), Is.EqualTo(TakeRunnerLogic.MarkResult.None));
            Assert.That(l.NotifyMarkPressed(TakeRunnerLogic.MarkGraceSec + 0.1f), Is.EqualTo(TakeRunnerLogic.MarkResult.Advanced));
        }

        [Test]
        public void AdvanceCut_IsRemovable()
        {
            TakeRunnerLogic l = Started(Take(false, new[] { true, false }, 1f, 30f));
            Assert.That(l.ActiveRemovable, Is.True, "報告が効くカットとして扱う");
            l.Tick(1f, 0);
            Assert.That(l.ActiveRemovable, Is.False, "旗の無いカット・dismissible でない演出");
        }

        [Test]
        public void NoFlags_BehaveAsBefore()
        {
            TakeRunnerLogic l = Started(Take(true, null, 30f, 30f));
            Assert.That(l.ActiveStepAdvancesOnMark, Is.False);
            Assert.That(l.NotifyMarkPressed(2f), Is.EqualTo(TakeRunnerLogic.MarkResult.Dismissed));
        }

        [Test]
        public void Flags_AreBuiltFromTheScript_AndNullWhenNoneIsSet()
        {
            var take = new ShowTakeDef { steps = new[] { new ShowStepDef(), new ShowStepDef { markAdvance = true } } };
            bool[]? flags = TakeRunner.BuildStepMarkAdvance(take);
            Assert.That(flags, Is.EqualTo(new[] { false, true }));
            Assert.That(TakeRunner.BuildStepMarkAdvance(new ShowTakeDef { steps = new[] { new ShowStepDef() } }), Is.Null);

            var parsed = UnityEngine.JsonUtility.FromJson<ShowStepDef>("{\"cg\":\"doll\"}");
            Assert.That(parsed.markAdvance, Is.False, "キーが無ければ進めない");
            Assert.That(parsed.dollLaugh, Is.False, "キーが無ければ笑わない");
        }
    }
}
