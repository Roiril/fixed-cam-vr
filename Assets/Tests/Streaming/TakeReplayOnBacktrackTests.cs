#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// <b>引き返したときに、途中で切れた演出をどうするか</b>（2026-08-17 ユーザー指定）。
    ///
    ///   異変を報告済み   → 再演出は無し
    ///   異変を報告していない → 最初から再演出
    ///
    /// つまり <c>once</c> が数えるのは「始めた回数」ではなく「<b>決着した回数</b>」
    /// （<see cref="TakeRunnerLogic.Outcome"/>）。旧実装は <c>StartTake</c> の時点で
    /// 発火済みが立っていたので、1 カット目の途中で区間を出た演出は二度と出なかった。
    ///
    /// ⚠ 区間キーの周が引き返しで戻ること（<c>LapCounterLogic.SegmentLap</c>）が前提。
    ///   そちらは <c>LapCounterTests</c> が固定する。ここは「同じ区間へ戻ったら」から先だけを見る。
    /// </summary>
    public sealed class TakeReplayOnBacktrackTests
    {
        // yield = 体験者が区間を移ったら打ち切る（3 周目の演出がこれ）。
        private static TakeRunnerLogic.Def Yielding(int lap, int cam, params float[] steps)
            => new()
            {
                lap = lap, camera = cam, onExit = false, offsetSec = 0f,
                skipWhenMissed = true, once = true, maxDurationSec = 0f,
                yieldOnZoneChange = true, stepDurSec = steps,
            };

        // hold = 画面を持ったまま次の区間へ行ける（1〜2 周目・帰りの A の演出がこれ）。
        private static TakeRunnerLogic.Def Holding(int lap, int cam, params float[] steps)
            => new()
            {
                lap = lap, camera = cam, onExit = false, offsetSec = 0f,
                skipWhenMissed = false, once = true, maxDurationSec = 0f,
                yieldOnZoneChange = false, stepDurSec = steps,
            };

        private static TakeRunnerLogic Make(params TakeRunnerLogic.Def[] defs)
        {
            var l = new TakeRunnerLogic();
            l.SetDefs(defs);
            return l;
        }

        private static void Enter(TakeRunnerLogic l, int lap, int cam, float now,
                                  bool hadPrev = false, int prevLap = 0, int prevCam = 0)
            => l.OnZoneCommitted(lap, cam, hadPrev, prevLap, prevCam, now);

        // ---- 途中で切れた演出は、その区間へ戻れば頭から出し直す ----

        [Test]
        public void Interrupted_AndNotReported_ReplaysFromTheFirstStep()
        {
            var l = Make(Yielding(3, 2, 5f, 5f));
            Enter(l, 3, 2, 0f);
            Assert.That(l.Tick(0f, 2).stepIndex, Is.EqualTo(0), "1 カット目が始まる");

            // 体験者が隣の区間へ出た → yield で打ち切り（未報告）。
            TakeRunnerLogic.Decision cut = l.OnZoneCommitted(3, 1, true, 3, 2, 2f);
            Assert.That(cut.action, Is.EqualTo(TakeRunnerLogic.Action.EndTake));
            Assert.That(cut.reason, Is.EqualTo(TakeRunnerLogic.EndReason.Yielded));
            Assert.That(l.OutcomeOf(0), Is.EqualTo(TakeRunnerLogic.Outcome.Unresolved),
                "体験者の歩きで切れて報告もされていないので、まだ決着していない");

            // 引き返して同じ区間へ戻る。
            l.OnZoneCommitted(3, 2, true, 3, 1, 4f);
            TakeRunnerLogic.Decision again = l.Tick(4f, 2);
            Assert.That(again.action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep));
            Assert.That(again.takeStarted, Is.True);
            Assert.That(again.stepIndex, Is.EqualTo(0), "続きではなく最初のカットから");
            Assert.That(l.ReplayCount, Is.EqualTo(1));
        }

        [Test]
        public void Interrupted_ButReported_DoesNotReplay()
        {
            var l = Make(Yielding(3, 2, 5f, 5f));
            Enter(l, 3, 2, 0f);
            l.Tick(0f, 2);

            l.NotifyMarkPressed(1f);   // 異変を見て報告した（dismissible ではないので消えはしない）
            l.OnZoneCommitted(3, 1, true, 3, 2, 2f);
            Assert.That(l.OutcomeOf(0), Is.EqualTo(TakeRunnerLogic.Outcome.Settled),
                "報告済みなら決着（ユーザー指定「異変を報告済み → 再演出は無し」）");

            l.OnZoneCommitted(3, 2, true, 3, 1, 4f);
            Assert.That(l.Tick(4f, 2).action, Is.EqualTo(TakeRunnerLogic.Action.None));
            Assert.That(l.ReplayCount, Is.EqualTo(0));
        }

        [Test]
        public void Completed_NeverReplays_EvenAfterBacktracking()
        {
            // 「すでに通り過ぎた演出は戻ってきてももう出さない」（ユーザー指定）。
            var l = Make(Yielding(1, 2, 1f));
            Enter(l, 1, 2, 0f);
            l.Tick(0f, 2);
            Assert.That(l.Tick(1f, 2).reason, Is.EqualTo(TakeRunnerLogic.EndReason.Completed));
            Assert.That(l.OutcomeOf(0), Is.EqualTo(TakeRunnerLogic.Outcome.Settled));

            l.OnZoneCommitted(2, 0, true, 1, 2, 2f);      // 次の区間へ
            l.OnZoneCommitted(1, 2, true, 2, 0, 4f);      // 引き返して 1 周目 C へ戻る
            Assert.That(l.Tick(4f, 2).action, Is.EqualTo(TakeRunnerLogic.Action.None));
            Assert.That(l.ReplayCount, Is.EqualTo(0));
        }

        [Test]
        public void Watchdog_DoesNotReplay()
        {
            // 壊れて打ち切られたものを出し直すと、同じ所でもう一度止まる。
            var l = Make(new TakeRunnerLogic.Def
            {
                lap = 1, camera = 0, offsetSec = 0f, once = true, maxDurationSec = 2f,
                yieldOnZoneChange = true, stepDurSec = new[] { TakeRunnerLogic.WaitClipEnd },
            });
            Enter(l, 1, 0, 0f);
            l.Tick(0f, 0);
            Assert.That(l.Tick(2f, 0).reason, Is.EqualTo(TakeRunnerLogic.EndReason.Watchdog));
            Assert.That(l.OutcomeOf(0), Is.EqualTo(TakeRunnerLogic.Outcome.Settled));
        }

        [Test]
        public void OperatorAbort_DoesNotReplay()
        {
            // 卓の「■ 画面を取り返す」は人の判断。戻ってきたからといって出し直さない。
            var l = Make(Yielding(1, 0, 5f));
            Enter(l, 1, 0, 0f);
            l.Tick(0f, 0);
            l.AbortActive();
            Assert.That(l.OutcomeOf(0), Is.EqualTo(TakeRunnerLogic.Outcome.Settled));

            l.OnZoneCommitted(1, 1, true, 1, 0, 2f);
            l.OnZoneCommitted(1, 0, true, 1, 1, 4f);
            Assert.That(l.Tick(4f, 0).action, Is.EqualTo(TakeRunnerLogic.Action.None));
        }

        [Test]
        public void Replay_IsCapped_SoBoundaryPacingDoesNotLoopTheFirstStep()
        {
            // ゾーン確定は dwell 0.5 秒。境界で往復されると 1 カット目だけが繰り返される。
            var l = Make(Yielding(3, 2, 5f, 5f));
            float t = 0f;
            for (int i = 0; i <= TakeRunnerLogic.MaxReplays; i++)
            {
                l.OnZoneCommitted(3, 2, i > 0, 3, 1, t);
                Assert.That(l.Tick(t, 2).action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep),
                    $"{i + 1} 回目は始まる");
                t += 1f;
                l.OnZoneCommitted(3, 1, true, 3, 2, t);   // すぐ出ていく
                t += 1f;
            }
            Assert.That(l.OutcomeOf(0), Is.EqualTo(TakeRunnerLogic.Outcome.Settled),
                "出し直せる回数を使い切ったら決着させる");
            l.OnZoneCommitted(3, 2, true, 3, 1, t);
            Assert.That(l.Tick(t, 2).action, Is.EqualTo(TakeRunnerLogic.Action.None));
            Assert.That(l.ReplayCount, Is.EqualTo(TakeRunnerLogic.MaxReplays));
        }

        // ---- 画面を持ったまま引き返しても二重に始まらない ----

        [Test]
        public void HoldingTake_ReenteringItsOwnSegment_DoesNotStartTwice()
        {
            // policy=hold は画面を持ったまま次の区間へ行ける。そこから引き返して自分の区間へ戻ると、
            // 未決着のまま自分自身が武装されて、終わった次の Tick で二重に始まりうる。
            var l = Make(Holding(2, 0, 10f));
            Enter(l, 2, 0, 0f);
            Assert.That(l.Tick(0f, 0).takeStarted, Is.True);

            l.OnZoneCommitted(1, 2, true, 2, 0, 1f);      // 引き返して 1 周目 C へ（演出は続く）
            l.OnZoneCommitted(2, 0, true, 1, 2, 2f);      // また 2 周目 A へ戻る
            Assert.That(l.ArmedCount, Is.EqualTo(0), "走行中の演出を自分で武装しない");

            Assert.That(l.Tick(10f, 0).action, Is.EqualTo(TakeRunnerLogic.Action.EndTake));
            Assert.That(l.Tick(10.1f, 0).action, Is.EqualTo(TakeRunnerLogic.Action.None),
                "完走したので出し直さない");
        }

        // ---- ラン境界 ----

        [Test]
        public void ResetRun_ClearsOutcomes_SoTheNextVisitorSeesEverything()
        {
            var l = Make(Yielding(3, 2, 5f));
            Enter(l, 3, 2, 0f);
            l.Tick(0f, 2);
            l.OnZoneCommitted(3, 1, true, 3, 2, 1f);
            Assert.That(l.OutcomeOf(0), Is.EqualTo(TakeRunnerLogic.Outcome.Unresolved));

            l.ResetRun();
            Assert.That(l.OutcomeOf(0), Is.EqualTo(TakeRunnerLogic.Outcome.None));
            Assert.That(l.ReplayCount, Is.EqualTo(0));

            Enter(l, 3, 2, 10f);
            Assert.That(l.Tick(10f, 2).takeStarted, Is.True);
            Assert.That(l.ReplayCount, Is.EqualTo(0), "新しい体験者の 1 回目は再演ではない");
        }

        // ---- 報告の帰属 ----

        [Test]
        public void MarkAfterTheTakeEnded_DoesNotCount_SoTheCommsAnswerAndTheReplayAgree()
        {
            // 連絡の面が出す答えは NotifyMarkPressed の戻り値（＝ ShowControlClient.LastMarkResolved）。
            // 終わった後の押下に猶予を作ると、同じ 1 回の押下について
            // 画は「異常は検出されませんでした」・機械は「報告済み」と別のことを言うことになる。
            var l = Make(Yielding(3, 2, 5f, 5f));
            Enter(l, 3, 2, 0f);
            l.Tick(0f, 2);
            l.OnZoneCommitted(3, 1, true, 3, 2, 2f);      // 打ち切り（未報告）
            l.NotifyMarkPressed(2.3f);                     // 押し切ったのは区間を出た後

            Assert.That(l.OutcomeOf(0), Is.EqualTo(TakeRunnerLogic.Outcome.Unresolved),
                "走行中でない報告はその演出への報告として数えない");
            l.OnZoneCommitted(3, 2, true, 3, 1, 4f);
            Assert.That(l.Tick(4f, 2).takeStarted, Is.True);
        }
    }
}
