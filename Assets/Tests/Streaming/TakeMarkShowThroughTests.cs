#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 2 周目 A の右の幕（しみ → 膨らむ顔と人形）を全員に見せる仕組み（LEDGER 0390 の後のユーザー指示
    /// 「右のシミのもっこりは全員に見せたい演出」）を固定する。
    ///   ・step の <c>markShowThrough</c>: 報告で畳むとき、そのカットの終わりまでは見せ切る。前のカットで成立したら今で進める
    ///   ・<c>markClear</c>（左のシミを消す報告）は演出の決着に数えない。消した後に引き返しても、戻れば後半を出し直す
    /// </summary>
    public sealed class TakeMarkShowThroughTests
    {
        // 段 0 左のシミ（markClear）・段 1 右のしみ・段 2 膨らむ動画・段 3 止め
        private static TakeRunnerLogic.Def Take(bool yieldOnZoneChange = false)
            => new()
            {
                lap = 2, camera = 0, onExit = false, offsetSec = 0f,
                skipWhenMissed = false, once = true, maxDurationSec = 150f,   // 本番と同じ（既定の 45 秒では止めのカットまで届かない）
                dismissible = true, yieldOnZoneChange = yieldOnZoneChange,
                stepDurSec = new[] { 30f, 30f, 2.3f, 30f },
                stepMarkClear = new[] { true, false, false, false },
            };

        private static TakeRunnerLogic Started(TakeRunnerLogic.Def def)
        {
            var l = new TakeRunnerLogic();
            l.SetDefs(new[] { def });
            l.OnZoneCommitted(2, 0, false, 0, 0, 0f);
            Assert.That(l.Tick(0f, 0).action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep), "前提: 演出が走っていること");
            return l;
        }

        private static ShowStepDef[] Steps()
            => new[] { new ShowStepDef(), new ShowStepDef(), new ShowStepDef { markShowThrough = true }, new ShowStepDef() };

        [Test]
        public void Resolve_PicksTheFirstShowThroughCutFromHere()
        {
            ShowStepDef[] steps = Steps();
            Assert.That(TakeRunner.ResolveShowThroughDismissAfterStep(steps, 0), Is.EqualTo(2));
            Assert.That(TakeRunner.ResolveShowThroughDismissAfterStep(steps, 1), Is.EqualTo(2), "前のカット → 見せ切るカットまで");
            Assert.That(TakeRunner.ResolveShowThroughDismissAfterStep(steps, 2), Is.EqualTo(2), "見せ切るカットの中 → その終わりまで");
            Assert.That(TakeRunner.ResolveShowThroughDismissAfterStep(steps, 3), Is.EqualTo(-1), "見せた後は即畳む（従来どおり）");
            Assert.That(TakeRunner.ResolveShowThroughDismissAfterStep(new[] { new ShowStepDef() }, 0), Is.EqualTo(-1));
            Assert.That(TakeRunner.ResolveShowThroughDismissAfterStep(null, 0), Is.EqualTo(-1));
        }

        [Test]
        public void ReportBeforeTheCut_JumpsToIt_AndFoldsAfterItEnds()
        {
            TakeRunnerLogic l = Started(Take());
            Assert.That(l.Tick(30f, 0).stepIndex, Is.EqualTo(1), "前提: 右のしみのカット");

            // 右のしみの間に報告が成立（段 0 で押し始めて線を越えた人）。線を待たず今で段 2 へ進む
            Assert.That(l.NotifyMarkPressed(31f, dismissAfterStep: 2, endStepNow: true),
                Is.EqualTo(TakeRunnerLogic.MarkResult.Dismissed), "報告は成立した時点で数える");
            TakeRunnerLogic.Decision d = l.Tick(31f, 0);
            Assert.That(d.action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep));
            Assert.That(d.stepIndex, Is.EqualTo(2), "膨らむ動画へ進む");

            Assert.That(l.Tick(32f, 0).action, Is.EqualTo(TakeRunnerLogic.Action.None), "動画の途中では畳まない");
            Assert.That(l.IsActive, Is.True);

            TakeRunnerLogic.Decision end = l.Tick(33.4f, 0);
            Assert.That(end.action, Is.EqualTo(TakeRunnerLogic.Action.EndTake), "動画が終わった縁で畳む");
            Assert.That(end.reason, Is.EqualTo(TakeRunnerLogic.EndReason.VisitorDismissed));
        }

        [Test]
        public void ReportInsideTheCut_KeepsItToTheEnd()
        {
            TakeRunnerLogic l = Started(Take());
            l.Tick(30f, 0);
            Assert.That(l.Tick(60.1f, 0).stepIndex, Is.EqualTo(2), "前提: 膨らむ動画のカット");

            Assert.That(l.NotifyMarkPressed(61f, dismissAfterStep: 2, endStepNow: false),
                Is.EqualTo(TakeRunnerLogic.MarkResult.Dismissed));
            Assert.That(l.Tick(61f, 0).action, Is.EqualTo(TakeRunnerLogic.Action.None), "途中で切らない");
            Assert.That(l.Tick(62.5f, 0).reason, Is.EqualTo(TakeRunnerLogic.EndReason.VisitorDismissed), "終わりで畳む");
        }

        [Test]
        public void ReportAfterTheCut_FoldsAtOnce()
        {
            TakeRunnerLogic l = Started(Take());
            l.Tick(30f, 0);
            l.Tick(60.1f, 0);
            Assert.That(l.Tick(62.5f, 0).stepIndex, Is.EqualTo(3), "前提: 止めのカット");
            Assert.That(l.NotifyMarkPressed(63f), Is.EqualTo(TakeRunnerLogic.MarkResult.Dismissed));
            Assert.That(l.Tick(63f, 0).reason, Is.EqualTo(TakeRunnerLogic.EndReason.VisitorDismissed));
        }

        [Test]
        public void ClearThenBacktrack_IsNotSettled_AndReplaysOnReturn()
        {
            TakeRunnerLogic l = Started(Take(yieldOnZoneChange: true));
            Assert.That(l.NotifyMarkPressed(2f), Is.EqualTo(TakeRunnerLogic.MarkResult.Cleared), "左のシミだけ消す");

            // 右の幕を見る前に引き返す
            TakeRunnerLogic.Decision cut = l.OnZoneCommitted(1, 2, true, 2, 0, 5f);
            Assert.That(cut.reason, Is.EqualTo(TakeRunnerLogic.EndReason.Yielded));
            Assert.That(l.OutcomeOf(0), Is.EqualTo(TakeRunnerLogic.Outcome.Unresolved),
                "素材を消しただけの報告は決着に数えない（右の幕をまだ見ていない）");

            l.OnZoneCommitted(2, 0, true, 1, 2, 8f);
            TakeRunnerLogic.Decision again = l.Tick(8f, 0);
            Assert.That(again.action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep), "戻れば頭から出し直す");
            Assert.That(again.stepIndex, Is.EqualTo(0));
        }

        [Test]
        public void DismissThenBacktrack_IsSettled()
        {
            TakeRunnerLogic l = Started(Take(yieldOnZoneChange: true));
            l.Tick(30f, 0);
            Assert.That(l.NotifyMarkPressed(31f), Is.EqualTo(TakeRunnerLogic.MarkResult.Dismissed));
            l.Tick(31f, 0);
            Assert.That(l.OutcomeOf(0), Is.EqualTo(TakeRunnerLogic.Outcome.Settled), "畳む報告は従来どおり決着");
        }
    }
}
