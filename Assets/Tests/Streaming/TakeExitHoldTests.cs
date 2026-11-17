#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// take の <c>exitHoldSec</c>（2 周目 A の人形・LEDGER 0378 の後の案）を固定する。
    /// <c>policy:"yield"</c> の演出が前へ抜けても、その秒までは畳まずに画面を持ち、カットは尺どおり進む。
    /// 引き返し・持っている間の 2 度目の区間移り・旗の無い演出は従来どおりすぐ畳む。次の区間の演出は畳んだ後に始まる。
    /// </summary>
    public sealed class TakeExitHoldTests
    {
        private const float Hold = 3f;

        private static TakeRunnerLogic.Def Take(int camera, float exitHold, params float[] steps)
            => new()
            {
                lap = 2, camera = camera, onExit = false, offsetSec = 0f,
                skipWhenMissed = true, once = true, maxDurationSec = 0f,
                yieldOnZoneChange = true,
                exitHoldSec = exitHold,
                stepDurSec = steps,
            };

        // 演出 0 = 2 周目 A（camera 0）、演出 1 = 2 周目 B（camera 1・入ったら出る）
        private static TakeRunnerLogic StartedInA(float exitHold, params float[] steps)
        {
            var l = new TakeRunnerLogic();
            l.SetDefs(new[] { Take(0, exitHold, steps), Take(1, 0f, 10f) });
            l.OnZoneCommitted(2, 0, true, 1, 2, 0f, forward: true);
            TakeRunnerLogic.Decision d = l.Tick(0f, 0);
            Assert.That(d.action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep), "前提: 2 周目 A の演出が走っていること");
            Assert.That(d.takeIndex, Is.EqualTo(0));
            return l;
        }

        private static TakeRunnerLogic.Decision ExitTo(TakeRunnerLogic l, int cam, float now, bool forward)
        {
            l.NotifyZoneChanged(now);
            return l.OnZoneCommitted(2, cam, true, 2, 0, now, forward);
        }

        [Test]
        public void ForwardExit_KeepsTheScreen_UntilTheHoldEnds_ThenTheNextZoneTakeStarts()
        {
            TakeRunnerLogic l = StartedInA(Hold, 30f);

            Assert.That(ExitTo(l, 1, 2f, forward: true).action, Is.EqualTo(TakeRunnerLogic.Action.None), "前へ抜けても畳まない");
            Assert.That(l.ExitHolding, Is.True);
            Assert.That(l.ExitHoldCount, Is.EqualTo(1));
            Assert.That(l.LastExitHoldTake, Is.EqualTo(0));
            Assert.That(l.Tick(4.9f, 1).action, Is.EqualTo(TakeRunnerLogic.Action.None), "持っている間は 2 周目 B の演出も出ない");
            Assert.That(l.IsActive, Is.True);

            TakeRunnerLogic.Decision end = l.Tick(5f, 1);
            Assert.That(end.action, Is.EqualTo(TakeRunnerLogic.Action.EndTake));
            Assert.That(end.takeIndex, Is.EqualTo(0));
            Assert.That(end.reason, Is.EqualTo(TakeRunnerLogic.EndReason.Yielded));
            Assert.That(end.yieldedForward, Is.True, "前進の yield として数える（追いつきの成立）");
            Assert.That(end.returnCamera, Is.EqualTo(1), "画面は今の区間へ返す");
            Assert.That(end.chainNext, Is.False, "次の演出は別のカメラなので画面を返してから始める");
            Assert.That(l.ExitHolding, Is.False);

            TakeRunnerLogic.Decision next = l.Tick(5.1f, 1);
            Assert.That(next.action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep));
            Assert.That(next.takeIndex, Is.EqualTo(1), "畳んだ後に 2 周目 B の演出が始まる");
        }

        [Test]
        public void ForwardExit_CutsKeepTheirDurations_AndUntilZoneChangeEndsWithTheHold()
        {
            TakeRunnerLogic l = StartedInA(Hold, 1f, 1f, TakeRunnerLogic.WaitZoneChange);
            Assert.That(ExitTo(l, 1, 0.5f, forward: true).action, Is.EqualTo(TakeRunnerLogic.Action.None));

            Assert.That(l.Tick(1f, 1).stepIndex, Is.EqualTo(1), "持っている間もカットは尺どおり進む");
            Assert.That(l.Tick(2f, 1).stepIndex, Is.EqualTo(2), "区間が変わった後に始まった untilZoneChange のカット");
            Assert.That(l.Tick(3.4f, 1).action, Is.EqualTo(TakeRunnerLogic.Action.None), "untilZoneChange は持つ秒まで続く");
            Assert.That(l.Tick(3.5f, 1).reason, Is.EqualTo(TakeRunnerLogic.EndReason.Yielded));
        }

        [Test]
        public void ForwardExit_DuringAnUntilZoneChangeCut_DoesNotEndThatCut()
        {
            TakeRunnerLogic l = StartedInA(Hold, 1f, TakeRunnerLogic.WaitZoneChange);
            Assert.That(l.Tick(1f, 0).stepIndex, Is.EqualTo(1), "前提: CG の人形のカット");

            Assert.That(ExitTo(l, 1, 2f, forward: true).action, Is.EqualTo(TakeRunnerLogic.Action.None));
            Assert.That(l.Tick(2.1f, 1).action, Is.EqualTo(TakeRunnerLogic.Action.None), "区間を出てもカットは終わらない");
            Assert.That(l.Tick(4.9f, 1).action, Is.EqualTo(TakeRunnerLogic.Action.None));
            Assert.That(l.Tick(5f, 1).reason, Is.EqualTo(TakeRunnerLogic.EndReason.Yielded));
        }

        [Test]
        public void TakeThatFinishesDuringTheHold_EndsAsCompleted()
        {
            TakeRunnerLogic l = StartedInA(Hold, 2f);
            Assert.That(ExitTo(l, 1, 1f, forward: true).action, Is.EqualTo(TakeRunnerLogic.Action.None));
            TakeRunnerLogic.Decision end = l.Tick(2f, 1);
            Assert.That(end.reason, Is.EqualTo(TakeRunnerLogic.EndReason.Completed));
            Assert.That(l.Tick(2.1f, 1).takeIndex, Is.EqualTo(1), "終わった後に 2 周目 B の演出が始まる");
        }

        [Test]
        public void BackwardExit_FoldsAtOnce()
        {
            TakeRunnerLogic l = StartedInA(Hold, 30f);
            TakeRunnerLogic.Decision d = ExitTo(l, 2, 2f, forward: false);
            Assert.That(d.action, Is.EqualTo(TakeRunnerLogic.Action.EndTake), "引き返しは従来どおりすぐ畳む");
            Assert.That(d.reason, Is.EqualTo(TakeRunnerLogic.EndReason.Yielded));
            Assert.That(d.yieldedForward, Is.False);
            Assert.That(l.ExitHoldCount, Is.EqualTo(0));
        }

        [Test]
        public void SecondZoneChangeDuringTheHold_FoldsAtOnce()
        {
            TakeRunnerLogic l = StartedInA(Hold, 30f);
            Assert.That(ExitTo(l, 1, 2f, forward: true).action, Is.EqualTo(TakeRunnerLogic.Action.None));
            l.NotifyZoneChanged(3f);
            TakeRunnerLogic.Decision d = l.OnZoneCommitted(2, 0, true, 2, 1, 3f, forward: false);
            Assert.That(d.action, Is.EqualTo(TakeRunnerLogic.Action.EndTake), "持っている間に引き返したら畳む");
            Assert.That(d.reason, Is.EqualTo(TakeRunnerLogic.EndReason.Yielded));
            Assert.That(l.ExitHoldCount, Is.EqualTo(1), "持つのは 1 演出 1 度");
        }

        [Test]
        public void WithoutExitHold_ForwardExitFoldsAtOnce_AndEndsUntilZoneChange()
        {
            TakeRunnerLogic l = StartedInA(0f, 1f, TakeRunnerLogic.WaitZoneChange);
            Assert.That(l.Tick(1f, 0).stepIndex, Is.EqualTo(1));
            TakeRunnerLogic.Decision d = ExitTo(l, 1, 2f, forward: true);
            Assert.That(d.action, Is.EqualTo(TakeRunnerLogic.Action.EndTake), "旗の無い演出は従来どおり");
            Assert.That(d.yieldedForward, Is.True);
            Assert.That(l.ExitHoldCount, Is.EqualTo(0));
        }

        [Test]
        public void ReportDuringTheHold_StillFoldsTheTake()
        {
            var l = new TakeRunnerLogic();
            TakeRunnerLogic.Def a = Take(0, Hold, 30f);
            a.dismissible = true;
            l.SetDefs(new[] { a, Take(1, 0f, 10f) });
            l.OnZoneCommitted(2, 0, true, 1, 2, 0f, forward: true);
            l.Tick(0f, 0);
            Assert.That(ExitTo(l, 1, 5f, forward: true).action, Is.EqualTo(TakeRunnerLogic.Action.None));
            Assert.That(l.NotifyMarkPressed(6f), Is.EqualTo(TakeRunnerLogic.MarkResult.Dismissed));
            Assert.That(l.Tick(6f, 1).reason, Is.EqualTo(TakeRunnerLogic.EndReason.VisitorDismissed));
            Assert.That(l.ExitHolding, Is.False);
        }

        [Test]
        public void ReportDuringTheHold_StartsTheNextZoneTake_WithoutTheDoll()
        {
            var l = new TakeRunnerLogic();
            TakeRunnerLogic.Def a = Take(0, Hold, 30f);
            a.dismissible = true;
            TakeRunnerLogic.Def b = Take(1, 0f, 10f);
            b.skipIfDismissedTake1 = 1; // 2 周目 A（index 0）が報告で畳まれたら人形を出さない（演出は出す・0399）
            l.SetDefs(new[] { a, b });
            l.OnZoneCommitted(2, 0, true, 1, 2, 0f, forward: true);
            l.Tick(0f, 0);

            Assert.That(ExitTo(l, 1, 5f, forward: true).action, Is.EqualTo(TakeRunnerLogic.Action.None));
            Assert.That(l.Tick(5.5f, 1).action, Is.EqualTo(TakeRunnerLogic.Action.None), "前提: 2 周目 B は武装されて画面を待っている");
            Assert.That(l.SkippedByDismissCount, Is.EqualTo(0));

            Assert.That(l.NotifyMarkPressed(6f), Is.EqualTo(TakeRunnerLogic.MarkResult.Dismissed));
            Assert.That(l.Tick(6f, 1).reason, Is.EqualTo(TakeRunnerLogic.EndReason.VisitorDismissed));
            // 2026-11-18 β の実機: 人形が付いて来る間に 2-B で報告した人に子供部屋が出なかった
            TakeRunnerLogic.Decision d = l.Tick(6.1f, 1);
            Assert.That(d.action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep), "2 周目 B の演出は捨てずに出す");
            Assert.That(d.takeIndex, Is.EqualTo(1));
            Assert.That(l.ActiveDollWithheld, Is.True, "人形だけを出さない");
            Assert.That(l.ActiveCleared, Is.True, "人形のカットは最初から消した扱い");
            Assert.That(l.SkippedByDismissCount, Is.EqualTo(1));
            Assert.That(l.LastSkippedByDismissTake, Is.EqualTo(1));
        }

        [Test]
        public void ResetRun_ForgetsTheHold()
        {
            TakeRunnerLogic l = StartedInA(Hold, 30f);
            ExitTo(l, 1, 2f, forward: true);
            l.ResetRun();
            Assert.That(l.ExitHoldCount, Is.EqualTo(0));
            Assert.That(l.LastExitHoldTake, Is.EqualTo(-1));
            Assert.That(l.ExitHolding, Is.False);
        }
    }
}
