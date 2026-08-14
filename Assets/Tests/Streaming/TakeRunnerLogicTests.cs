#nullable enable
using System.Collections.Generic;
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// TakeRunnerLogic（演出の判定・計時）の検証。設計 §6.3 の実行セマンティクス 8 項を固定する。
    /// InsertLogic（1 区間 1 本・単一カット）の後継で、多段カットと ifMissed が新しい。
    /// </summary>
    public sealed class TakeRunnerLogicTests
    {
        private static TakeRunnerLogic.Def Enter(int lap, int cam, float offset, params float[] steps)
            => new()
            {
                lap = lap, camera = cam, onExit = false, offsetSec = offset,
                skipWhenMissed = false, once = true, maxDurationSec = 0f, stepDurSec = steps,
            };

        private static TakeRunnerLogic.Def Exit(int lap, int cam, params float[] steps)
            => new()
            {
                lap = lap, camera = cam, onExit = true, offsetSec = 0f,
                skipWhenMissed = false, once = true, maxDurationSec = 0f, stepDurSec = steps,
            };

        private static TakeRunnerLogic Make(params TakeRunnerLogic.Def[] defs)
        {
            var l = new TakeRunnerLogic();
            l.SetDefs(defs);
            return l;
        }

        // 区間 (lap,cam) へ進入させる（初回は hadPrev=false）。
        private static TakeRunnerLogic.Decision Enter(TakeRunnerLogic l, int lap, int cam, float now,
            bool hadPrev = false, int prevLap = 0, int prevCam = 0)
            => l.OnZoneCommitted(lap, cam, hadPrev, prevLap, prevCam, now);

        // ---- 基本（enter / 多段カット） ----

        [Test]
        public void Enter_NoOffset_FiresOnNextTick()
        {
            var l = Make(Enter(1, 0, 0f, 2f));
            Assert.That(Enter(l, 1, 0, 0f).action, Is.EqualTo(TakeRunnerLogic.Action.None),
                "進入時点では武装のみ（発火は Tick）");
            TakeRunnerLogic.Decision d = l.Tick(0f, 0);
            Assert.That(d.action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep));
            Assert.That(d.stepIndex, Is.EqualTo(0));
            Assert.That(d.takeStarted, Is.True);
            Assert.That(l.IsActive, Is.True);
        }

        [Test]
        public void Enter_WithOffset_WaitsThenFires()
        {
            var l = Make(Enter(1, 0, 20f, 2f));
            Enter(l, 1, 0, 0f);
            Assert.That(l.Tick(19.9f, 0).action, Is.EqualTo(TakeRunnerLogic.Action.None));
            Assert.That(l.Tick(20f, 0).action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep));
        }

        [Test]
        public void MultiStep_AdvancesInOrder_ThenEnds()
        {
            // 要求の演出形: 4 カット（D 4s → 映像① 8s → 録画B 7s → 録画C 7s）
            var l = Make(Enter(3, 1, 0f, 4f, 8f, 7f, 7f));
            Enter(l, 3, 1, 0f);
            Assert.That(l.Tick(0f, 1).stepIndex, Is.EqualTo(0));
            Assert.That(l.Tick(3.9f, 1).action, Is.EqualTo(TakeRunnerLogic.Action.None));

            TakeRunnerLogic.Decision s1 = l.Tick(4f, 1);
            Assert.That(s1.action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep));
            Assert.That(s1.stepIndex, Is.EqualTo(1));
            Assert.That(s1.takeStarted, Is.False, "2 カット目以降は演出の開始ではない");

            Assert.That(l.Tick(12f, 1).stepIndex, Is.EqualTo(2));
            Assert.That(l.Tick(19f, 1).stepIndex, Is.EqualTo(3));

            TakeRunnerLogic.Decision end = l.Tick(26f, 2);
            Assert.That(end.action, Is.EqualTo(TakeRunnerLogic.Action.EndTake));
            Assert.That(end.returnCamera, Is.EqualTo(2), "復帰先はいま体験者が居るゾーン（再計算）");
            Assert.That(end.forced, Is.False);
            Assert.That(l.IsActive, Is.False);
        }

        [Test]
        public void UntilClipEnd_WaitsForExternalNotify()
        {
            var l = Make(Enter(1, 0, 0f, -1f, 3f));   // 1 カット目 = untilClipEnd
            Enter(l, 1, 0, 0f);
            l.Tick(0f, 0);
            Assert.That(l.Tick(10f, 0).action, Is.EqualTo(TakeRunnerLogic.Action.None),
                "通知が来るまで進まない（watchdog 45s の手前）");
            l.NotifyCurrentStepFinished(10f);
            Assert.That(l.Tick(10f, 0).stepIndex, Is.EqualTo(1));
        }

        [Test]
        public void UntilZoneChange_WaitsForZoneCommit_NotClipEnd()
        {
            var l = Make(Enter(1, 0, 0f, TakeRunnerLogic.WaitZoneChange, 3f));
            Enter(l, 1, 0, 0f);
            l.Tick(0f, 0);

            // 素材が終わっても畳まない（そのモードの意味は「区間に居るあいだ出し続ける」）
            l.NotifyCurrentStepFinished(5f);
            Assert.That(l.Tick(5f, 0).action, Is.EqualTo(TakeRunnerLogic.Action.None),
                "素材の終端では終わらない");

            l.NotifyZoneChanged(8f);
            Assert.That(l.Tick(8f, 0).stepIndex, Is.EqualTo(1), "区間が確定したら次のカットへ");
        }

        [Test]
        public void UntilClipEnd_IsNotFoldedByZoneChange()
        {
            var l = Make(Enter(1, 0, 0f, TakeRunnerLogic.WaitClipEnd, 3f));
            Enter(l, 1, 0, 0f);
            l.Tick(0f, 0);

            l.NotifyZoneChanged(5f);
            Assert.That(l.Tick(5f, 0).action, Is.EqualTo(TakeRunnerLogic.Action.None),
                "素材の終端を待つカットは、区間が変わっても畳まない");

            l.NotifyCurrentStepFinished(6f);
            Assert.That(l.Tick(6f, 0).stepIndex, Is.EqualTo(1));
        }

        [Test]
        public void UntilZoneChange_StillBoundedByWatchdog()
        {
            var l = Make(Enter(1, 0, 0f, TakeRunnerLogic.WaitZoneChange));
            Enter(l, 1, 0, 0f);
            l.Tick(0f, 0);
            // 体験者が動かなければ通知は来ない。上限は watchdog が保証する。
            Assert.That(l.Tick(TakeSchema.DefaultMaxDurationSec + 1f, 0).action,
                Is.EqualTo(TakeRunnerLogic.Action.EndTake));
        }

        // ---- 不変条件 2: 必ず終わる（watchdog） ----

        [Test]
        public void Watchdog_ForcesEnd_WhenClipNeverEnds()
        {
            var l = Make(Enter(1, 0, 0f, -1f));       // 永遠に終わらないカット
            Enter(l, 1, 0, 0f);
            l.Tick(0f, 0);
            Assert.That(l.Tick(44f, 0).action, Is.EqualTo(TakeRunnerLogic.Action.None));
            TakeRunnerLogic.Decision d = l.Tick(TakeSchema.DefaultMaxDurationSec, 2);
            Assert.That(d.action, Is.EqualTo(TakeRunnerLogic.Action.EndTake));
            Assert.That(d.forced, Is.True, "既定 45s の watchdog で強制終了する");
            Assert.That(d.returnCamera, Is.EqualTo(2));
            Assert.That(l.IsActive, Is.False);
        }

        [Test]
        public void Watchdog_RespectsPerTakeOverride()
        {
            var def = Enter(1, 0, 0f, -1f);
            def.maxDurationSec = 5f;
            var l = Make(def);
            Enter(l, 1, 0, 0f);
            l.Tick(0f, 0);
            Assert.That(l.Tick(4.9f, 0).action, Is.EqualTo(TakeRunnerLogic.Action.None));
            Assert.That(l.Tick(5f, 0).forced, Is.True);
        }

        // ---- 不変条件 6: ifMissed ----

        [Test]
        public void FireOnExit_FiresAtExit_WhenWalkedThroughFast()
        {
            // 最大のリスク（§8 論点 5）: enter+20s の山場が、8 秒で通過されても出ないままにならない。
            var l = Make(Enter(3, 1, 20f, 4f));
            Enter(l, 3, 1, 0f);
            Assert.That(l.Tick(8f, 1).action, Is.EqualTo(TakeRunnerLogic.Action.None), "まだ発火時刻ではない");

            TakeRunnerLogic.Decision d = Enter(l, 3, 2, 8f, hadPrev: true, prevLap: 3, prevCam: 1);
            Assert.That(d.action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep),
                "離脱の瞬間に発火する（fireOnExit）");
            Assert.That(d.takeStarted, Is.True);
        }

        [Test]
        public void Skip_DoesNotFireAtExit()
        {
            var def = Enter(3, 1, 20f, 4f);
            def.skipWhenMissed = true;
            var l = Make(def);
            Enter(l, 3, 1, 0f);
            TakeRunnerLogic.Decision d = Enter(l, 3, 2, 8f, hadPrev: true, prevLap: 3, prevCam: 1);
            Assert.That(d.action, Is.EqualTo(TakeRunnerLogic.Action.None), "skip は通り過ぎたら出ない");
            Assert.That(l.Tick(30f, 2).action, Is.EqualTo(TakeRunnerLogic.Action.None));
        }

        // ---- 不変条件 7: 遅れて別区間で発火しない ----

        [Test]
        public void ArmedTake_NeverFiresInAnotherSegment()
        {
            // 旧 InsertLogic の穴（delay 待ちが別区間で誤爆する）を塞いだことの固定。
            var def = Enter(1, 0, 20f, 4f);
            def.skipWhenMissed = true;    // 離脱時発火もしない設定で「消える」ことを確かめる
            var l = Make(def);
            Enter(l, 1, 0, 0f);
            Enter(l, 1, 1, 5f, hadPrev: true, prevLap: 1, prevCam: 0);   // 区間 B へ移動
            Assert.That(l.ArmedCount, Is.EqualTo(0), "離脱時に武装は必ず決着する");
            for (float t = 6f; t <= 40f; t += 2f)
                Assert.That(l.Tick(t, 1).action, Is.EqualTo(TakeRunnerLogic.Action.None),
                    $"t={t} で別区間の演出が誤爆してはならない");
        }

        [Test]
        public void EnteringSegmentTake_NotArmedAcrossSegments()
        {
            var l = Make(Enter(1, 1, 0f, 3f));
            Enter(l, 1, 0, 0f);                                          // 別区間 A に居る
            Assert.That(l.Tick(5f, 0).action, Is.EqualTo(TakeRunnerLogic.Action.None));
            Enter(l, 1, 1, 5f, hadPrev: true, prevLap: 1, prevCam: 0);   // B へ進入して初めて武装
            Assert.That(l.Tick(5f, 1).action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep));
        }

        // ---- 不変条件 1 / §6.3-1: 同時 1 本・同じ区間に居るあいだは画面を待つ ----

        [Test]
        public void SecondTake_WaitsForTheScreen_ThenFiresInTheSameSegment()
        {
            // 2026-07-27 変更。旧実装は「待たせない」＝ここで捨てていたため、
            // 著作した演出が黙って消えた（ユーザー報告: B の離脱演出中に C へ入ると C の演出が出ない）。
            var a = Enter(1, 0, 0f, 10f);
            var b = Enter(1, 0, 2f, 3f);
            b.skipWhenMissed = true;   // skip でも「画面待ち」では捨てない（skip は体験者が通り過ぎた時の話）
            var l = Make(a, b);
            Enter(l, 1, 0, 0f);
            Assert.That(l.Tick(0f, 0).takeIndex, Is.EqualTo(0), "先に来た方が走る");
            l.Tick(2f, 0);                                   // b の発火時刻を跨ぐ（捨てずに待つ）
            Assert.That(l.ArmedCount, Is.EqualTo(1), "武装したまま画面の空きを待つ");
            Assert.That(l.Tick(10f, 0).action, Is.EqualTo(TakeRunnerLogic.Action.EndTake));
            TakeRunnerLogic.Decision d = l.Tick(10f, 0);
            Assert.That(d.action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep));
            Assert.That(d.takeIndex, Is.EqualTo(1), "画面が空いたら同じ区間で出る");
        }

        [Test]
        public void WaitingTake_IsDroppedAndReported_WhenSegmentEndsWhileScreenBusy()
        {
            // 待っても間に合わなかった場合は捨てる（遅れて別区間で出さない = 不変条件 4）。
            // ただし **黙っては消さない** — 設計 §6.3-1 が約束していた「破棄 + 警告ログ」。
            var a = Enter(1, 0, 0f, 30f);   // 長い演出が画面を占有し続ける
            var b = Enter(1, 0, 2f, 3f);
            var l = Make(a, b);
            var dropped = new List<(int, TakeRunnerLogic.DropReason)>();
            l.TakeDropped = (i, r) => dropped.Add((i, r));

            Enter(l, 1, 0, 0f);
            l.Tick(0f, 0);                  // a 開始
            l.Tick(2f, 0);                  // b は条件を満たすが画面待ち
            Enter(l, 1, 1, 5f, hadPrev: true, prevLap: 1, prevCam: 0);   // 画面が塞がったまま離脱

            Assert.That(l.ArmedCount, Is.EqualTo(0), "離脱で必ず決着する");
            Assert.That(dropped, Is.EqualTo(new[] { (1, TakeRunnerLogic.DropReason.ScreenBusyAtExit) }),
                "出せなかったことを必ず報告する");
        }

        [Test]
        public void LosingTake_IsReported_WhenAnotherWinsAtExit()
        {
            // 離脱時に出られるのは 1 本だけ。選ばれなかった方も報告する。
            var a = Exit(1, 0, 2f);
            var b = Exit(1, 0, 2f);
            var l = Make(a, b);
            var dropped = new List<(int, TakeRunnerLogic.DropReason)>();
            l.TakeDropped = (i, r) => dropped.Add((i, r));

            Enter(l, 1, 0, 0f);
            TakeRunnerLogic.Decision d = Enter(l, 1, 1, 1f, hadPrev: true, prevLap: 1, prevCam: 0);
            Assert.That(d.takeIndex, Is.EqualTo(0), "配列順で先頭が勝つ");
            Assert.That(dropped, Is.EqualTo(new[] { (1, TakeRunnerLogic.DropReason.LostToAnotherTake) }));
        }

        [Test]
        public void OverdueFireOnExit_WhileRunning_StillFiresAtExit()
        {
            var a = Enter(1, 0, 0f, 10f);
            var b = Enter(1, 0, 2f, 3f);        // fireOnExit（既定）
            var l = Make(a, b);
            Enter(l, 1, 0, 0f);
            l.Tick(0f, 0);                      // a 開始
            l.Tick(2f, 0);                      // b は発火時刻超過 → 離脱時へ持ち越し
            l.Tick(10f, 0);                     // a 終了
            TakeRunnerLogic.Decision d = Enter(l, 1, 1, 11f, hadPrev: true, prevLap: 1, prevCam: 0);
            Assert.That(d.action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep));
            Assert.That(d.takeIndex, Is.EqualTo(1));
        }

        [Test]
        public void EnterOrder_ByOffsetThenArrayOrder()
        {
            var late = Enter(1, 0, 5f, 2f);
            var early = Enter(1, 0, 1f, 2f);
            var l = Make(late, early);          // 配列順は late が先
            Enter(l, 1, 0, 0f);
            Assert.That(l.Tick(1f, 0).takeIndex, Is.EqualTo(1), "offsetSec の小さい方が先に走る");
        }

        // ---- exit アンカー ----

        [Test]
        public void ExitTake_FiresOnLeavingSegment()
        {
            var l = Make(Exit(1, 0, 3f));
            Enter(l, 1, 0, 0f);
            Assert.That(l.Tick(5f, 0).action, Is.EqualTo(TakeRunnerLogic.Action.None), "滞在中は出ない");
            TakeRunnerLogic.Decision d = Enter(l, 1, 1, 5f, hadPrev: true, prevLap: 1, prevCam: 0);
            Assert.That(d.action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep));
        }

        [Test]
        public void ExitTake_DoesNotFire_WhenSegmentUnchanged()
        {
            var l = Make(Exit(1, 0, 3f));
            Enter(l, 1, 0, 0f);
            TakeRunnerLogic.Decision d = Enter(l, 1, 0, 5f, hadPrev: true, prevLap: 1, prevCam: 0);
            Assert.That(d.action, Is.EqualTo(TakeRunnerLogic.Action.None));
        }

        // ---- once / ラン ----

        [Test]
        public void Once_FiresOnlyOncePerRun_ThenAgainAfterReset()
        {
            var l = Make(Enter(1, 0, 0f, 1f));
            Enter(l, 1, 0, 0f);
            Assert.That(l.Tick(0f, 0).action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep));
            l.Tick(1f, 0);                                               // 終了
            Enter(l, 1, 1, 2f, hadPrev: true, prevLap: 1, prevCam: 0);   // 出る
            Enter(l, 1, 0, 3f, hadPrev: true, prevLap: 1, prevCam: 1);   // 戻る
            Assert.That(l.Tick(3f, 0).action, Is.EqualTo(TakeRunnerLogic.Action.None), "once はラン内 1 回");

            l.ResetRun();
            Enter(l, 1, 0, 10f);
            Assert.That(l.Tick(10f, 0).action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep),
                "ラン開始でクリアされ再び出る");
        }

        [Test]
        public void NotOnce_FiresEveryVisit()
        {
            var def = Enter(1, 0, 0f, 1f);
            def.once = false;
            var l = Make(def);
            Enter(l, 1, 0, 0f);
            l.Tick(0f, 0);
            l.Tick(1f, 0);
            Enter(l, 1, 1, 2f, hadPrev: true, prevLap: 1, prevCam: 0);
            Enter(l, 1, 0, 3f, hadPrev: true, prevLap: 1, prevCam: 1);
            Assert.That(l.Tick(3f, 0).action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep));
        }

        [Test]
        public void ResetRun_ClearsRunningTake()
        {
            var l = Make(Enter(1, 0, 0f, 10f));
            Enter(l, 1, 0, 0f);
            l.Tick(0f, 0);
            Assert.That(l.IsActive, Is.True);
            l.ResetRun();
            Assert.That(l.IsActive, Is.False, "ラン開始は走行中の演出を必ず畳む（不変条件 8）");
        }

        // ---- 抑止（ライブ卓が最優先） ----

        [Test]
        public void Suppressed_DoesNotFire()
        {
            var l = Make(Enter(1, 0, 0f, 3f));
            l.SetSuppressed(true);
            Enter(l, 1, 0, 0f);
            Assert.That(l.Tick(0f, 0).action, Is.EqualTo(TakeRunnerLogic.Action.None));
        }

        [Test]
        public void Suppressed_AtExit_DoesNotFire()
        {
            var l = Make(Exit(1, 0, 3f));
            l.SetSuppressed(true);
            Enter(l, 1, 0, 0f);
            TakeRunnerLogic.Decision d = Enter(l, 1, 1, 5f, hadPrev: true, prevLap: 1, prevCam: 0);
            Assert.That(d.action, Is.EqualTo(TakeRunnerLogic.Action.None));
        }

        [Test]
        public void EmptySteps_NeverFires()
        {
            var l = Make(Enter(1, 0, 0f));    // カット 0 枚
            Enter(l, 1, 0, 0f);
            Assert.That(l.Tick(5f, 0).action, Is.EqualTo(TakeRunnerLogic.Action.None));
        }

        // ---- 不変条件 5: 横取り中に通過した区間の演出は捨てる ----

        [Test]
        public void SegmentsPassedDuringTake_AreDropped()
        {
            var longTake = Enter(1, 0, 0f, 30f);
            var bTake = Enter(1, 1, 0f, 3f);
            var cTake = Enter(1, 2, 0f, 3f);
            var l = Make(longTake, bTake, cTake);
            Enter(l, 1, 0, 0f);
            l.Tick(0f, 0);                                                // A の長い演出が走る
            Enter(l, 1, 1, 5f, hadPrev: true, prevLap: 1, prevCam: 0);    // 演出中に B を通過
            l.Tick(5f, 1);
            Enter(l, 1, 2, 10f, hadPrev: true, prevLap: 1, prevCam: 1);   // 演出中に C へ
            l.Tick(10f, 2);

            TakeRunnerLogic.Decision end = l.Tick(30f, 2);
            Assert.That(end.action, Is.EqualTo(TakeRunnerLogic.Action.EndTake));
            Assert.That(end.returnCamera, Is.EqualTo(2), "復帰先はいまの C");

            // 不変条件 4 が守るのは「**もう居ない**区間の演出が後から湧かないこと」。
            // いま居る C の演出は画面待ちだっただけなので、空いたら出る（2026-07-27 変更）。
            TakeRunnerLogic.Decision after = l.Tick(31f, 2);
            Assert.That(after.takeIndex, Is.Not.EqualTo(1), "通過した B の演出は後から湧かない");
            Assert.That(after.action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep));
            Assert.That(after.takeIndex, Is.EqualTo(2), "いま居る C の演出は画面が空いて出る");
        }

        // ---- policy: yield（体験者が区間を移ったら打ち切る） ----

        [Test]
        public void Yield_EndsTakeWhenViewerLeavesSegment()
        {
            var def = Enter(1, 0, 0f, 30f);
            def.yieldOnZoneChange = true;
            var l = Make(def);
            Enter(l, 1, 0, 0f);
            l.Tick(0f, 0);
            Assert.That(l.IsActive, Is.True);

            TakeRunnerLogic.Decision d = Enter(l, 1, 1, 5f, hadPrev: true, prevLap: 1, prevCam: 0);
            Assert.That(d.action, Is.EqualTo(TakeRunnerLogic.Action.EndTake), "境界を跨いだら打ち切る");
            Assert.That(d.returnCamera, Is.EqualTo(1), "復帰先は入った先のゾーン");
            Assert.That(d.forced, Is.False, "watchdog ではなく設計どおりの終了");
            Assert.That(l.IsActive, Is.False);
        }

        [Test]
        public void Hold_KeepsRunningWhenViewerLeavesSegment()
        {
            var def = Enter(1, 0, 0f, 30f);   // 既定 = hold
            var l = Make(def);
            Enter(l, 1, 0, 0f);
            l.Tick(0f, 0);
            TakeRunnerLogic.Decision d = Enter(l, 1, 1, 5f, hadPrev: true, prevLap: 1, prevCam: 0);
            Assert.That(d.action, Is.EqualTo(TakeRunnerLogic.Action.None));
            Assert.That(l.IsActive, Is.True, "hold は歩かれても見せ切る");
        }

        /// <summary>
        /// 3 周目の録画再生の契約（2026-08-14）。録画は切り替えの前後（tailSec + postSec）を持つので、
        /// **体験者が前の周より速く歩くと、まだ流し終わっていない**。そのときは切り替えの時点で
        /// 打ち止めて、次の区間の録画を**冒頭から**出す（居ない場所の続きを見せない）。
        /// </summary>
        [Test]
        public void Yield_CutsStillPlayingRecording_AndNextSegmentStartsFromItsHead()
        {
            // 尺 = WaitClipEnd（録画の終端待ち = まだ終わっていない）。
            var a = Enter(3, 0, 0f, TakeRunnerLogic.WaitClipEnd);
            a.yieldOnZoneChange = true;
            var b = Enter(3, 1, 0f, TakeRunnerLogic.WaitClipEnd);
            b.yieldOnZoneChange = true;
            var l = Make(a, b);

            Enter(l, 3, 0, 0f);
            Assert.That(l.Tick(0f, 0).stepIndex, Is.EqualTo(0), "A の録画が流れ始める");
            Assert.That(l.Tick(1f, 0).action, Is.EqualTo(TakeRunnerLogic.Action.None), "まだ流し終わっていない");

            TakeRunnerLogic.Decision cut = Enter(l, 3, 1, 2f, hadPrev: true, prevLap: 3, prevCam: 0);
            Assert.That(cut.action, Is.EqualTo(TakeRunnerLogic.Action.EndTake), "切り替えの時点で打ち止める");
            Assert.That(cut.forced, Is.False);
            Assert.That(cut.chainNext, Is.False,
                "画面を返してから次を出す（返さないと A の録画を持ったまま B の区間へ入る）");

            TakeRunnerLogic.Decision next = l.Tick(2f, 1);
            Assert.That(next.action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep));
            Assert.That(next.takeIndex, Is.EqualTo(1), "次の区間の演出");
            Assert.That(next.stepIndex, Is.EqualTo(0), "その録画は冒頭のカットから");
            Assert.That(next.takeStarted, Is.True);
        }

        [Test]
        public void Yield_ArmsEnteringSegmentTakeAfterAborting()
        {
            var a = Enter(1, 0, 0f, 30f);
            a.yieldOnZoneChange = true;
            var b = Enter(1, 1, 0f, 3f);
            var l = Make(a, b);
            Enter(l, 1, 0, 0f);
            l.Tick(0f, 0);
            Enter(l, 1, 1, 5f, hadPrev: true, prevLap: 1, prevCam: 0);   // 打ち切り
            TakeRunnerLogic.Decision d = l.Tick(5f, 1);
            Assert.That(d.action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep));
            Assert.That(d.takeIndex, Is.EqualTo(1), "打ち切り後、入った区間の演出は通常どおり武装される");
        }

        // ---- 復帰先の再計算 ----

        [Test]
        public void ReturnCamera_IsLatestZone_NotWhereItStarted()
        {
            var l = Make(Enter(1, 0, 0f, 5f));
            Enter(l, 1, 0, 0f);
            l.Tick(0f, 0);
            Assert.That(l.BaseZoneCamera, Is.EqualTo(0));
            TakeRunnerLogic.Decision end = l.Tick(5f, 2);
            Assert.That(end.returnCamera, Is.EqualTo(2), "開始時ゾーン(0)ではなく現在地(2)へ戻る");
        }

        // ---- 開始規則「このラインを通過したら」（at=line・2026-07-27）----
        //   契約: .claude/plans/2026-07-27_position-trigger.md §3
        //   時刻の代わりに床のラインの横断で due になるだけで、武装・決着・once・ifMissed は enter と同じ。
        //   加えて **ラインは担当カメラに紐づく**（別ゾーンのラインを踏んでも発火しない）。

        private static TakeRunnerLogic.Def Line(int lap, int cam, int lineIndex, params float[] steps) => new()
        {
            lap = lap, camera = cam, onExit = false, offsetSec = 0f,
            skipWhenMissed = false, once = true, maxDurationSec = 0f, stepDurSec = steps,
            onLine = true, lineIndex = lineIndex,
        };

        // ライン 1 本ぶんの状態。camera は担当カメラ（-1 = 未指定）。
        //   crossedAtSec は「最後に横切った時刻」。TakeRunnerLogic は CrossLatchSec の猶予で due を見る
        //   （武装がゾーン確定 = dwell 後に起きるため、事象を 1 フレームで消すと入口の線が発火しない）。
        private static LineCrossLogic.State[] NotCrossed(int camera = -1)
            => new[] { new LineCrossLogic.State
            { crossed = false, crossedAtSec = float.NegativeInfinity, camera = camera } };

        /// <summary>時刻 atSec に横切った状態（呼び出し側の Tick の now と同じ値を渡す）。</summary>
        private static LineCrossLogic.State[] Crossed(float atSec, int camera = -1)
            => new[] { new LineCrossLogic.State { crossed = true, crossedAtSec = atSec, camera = camera } };

        [Test]
        public void Line_FiresOnTheFrameItIsCrossed()
        {
            var l = Make(Line(2, 1, 0, 3f));
            Enter(l, 2, 1, 0f);
            Assert.That(l.Tick(1f, 1, NotCrossed(1)).action, Is.EqualTo(TakeRunnerLogic.Action.None),
                "区間に居るだけでは出ない（時刻では due にならない）");
            TakeRunnerLogic.Decision d = l.Tick(2f, 1, Crossed(2f, 1));
            Assert.That(d.action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep));
            Assert.That(d.takeStarted, Is.True);
        }

        [Test]
        public void Line_OfAnotherCamera_NeverFires()
        {
            // ユーザー要求の核: 手違いで別の領域のラインを踏んでも何も起こらない。
            var l = Make(Line(2, 1, 0, 3f));
            Enter(l, 2, 1, 0f);
            Assert.That(l.Tick(1f, 1, Crossed(1f, 0)).action, Is.EqualTo(TakeRunnerLogic.Action.None),
                "カメラ 0 のラインは カメラ 1 の区間の演出を動かさない");
            Assert.That(l.Tick(2f, 1, Crossed(2f, 2)).action, Is.EqualTo(TakeRunnerLogic.Action.None));
            Assert.That(l.Tick(3f, 1, Crossed(3f, 1)).action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep),
                "自分の区間のカメラのラインなら発火する");
        }

        [Test]
        public void Line_WithUnassignedCamera_FiresInAnySegment()
        {
            // camera 未指定（-1）のラインは「区間を限定しない」= 卓が担当を決めていない状態。
            var l = Make(Line(2, 1, 0, 3f));
            Enter(l, 2, 1, 0f);
            Assert.That(l.Tick(1f, 1, Crossed(1f, -1)).action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep));
        }

        [Test]
        public void Line_WithoutCrossState_NeverFires()
        {
            // 位置が取れない環境（HeadCourseXZProvider 未注入）では発火しない = 従来の動きを壊さない。
            var l = Make(Line(2, 1, 0, 3f));
            Enter(l, 2, 1, 0f);
            for (float t = 0f; t < 10f; t += 1f)
                Assert.That(l.Tick(t, 1).action, Is.EqualTo(TakeRunnerLogic.Action.None));
        }

        [Test]
        public void Line_UnknownSlot_NeverFires()
        {
            // layout にラインが無い（実体の無い枠）／範囲外 index。黙って別のラインで出したりしない。
            var l = Make(Line(2, 1, 5, 3f));
            Enter(l, 2, 1, 0f);
            Assert.That(l.Tick(1f, 1, Crossed(1f, 1)).action, Is.EqualTo(TakeRunnerLogic.Action.None));
        }

        [Test]
        public void Line_NeverCrossed_FiresAtExit_WhenFireOnExit()
        {
            var l = Make(Line(2, 1, 0, 3f));      // ifMissed 既定 = fireOnExit
            Enter(l, 2, 1, 0f);
            l.Tick(1f, 1, NotCrossed(1));
            TakeRunnerLogic.Decision d = Enter(l, 2, 2, 5f, hadPrev: true, prevLap: 2, prevCam: 1);
            Assert.That(d.action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep),
                "そのラインを通らないまま離脱したら、離脱の瞬間に出す（時刻トリガーと同じ規則）");
        }

        [Test]
        public void Line_NeverCrossed_IsDropped_WhenSkip()
        {
            // 卓の既定はこちら（場所に意味を持たせた演出を、通らなかったのに出さない）。
            var def = Line(2, 1, 0, 3f);
            def.skipWhenMissed = true;
            var l = Make(def);
            Enter(l, 2, 1, 0f);
            l.Tick(1f, 1, NotCrossed(1));
            Assert.That(Enter(l, 2, 2, 5f, hadPrev: true, prevLap: 2, prevCam: 1).action,
                Is.EqualTo(TakeRunnerLogic.Action.None));
            Assert.That(l.ArmedCount, Is.EqualTo(0), "離脱で必ず決着する（不変条件 7）");
        }

        [Test]
        public void Line_CrossedWhileAnotherTakeRuns_FiresWhenTheScreenFrees()
        {
            // 横断は**事象**で猶予 0.6s しか残らない。走行中に Ready へラッチしておかないと
            // 「別の演出が走っていた」だけで永久に失われる（2026-07-27 の取りこぼし対策）。
            var running = Enter(2, 1, 0f, 10f);
            var line = Line(2, 1, 0, 3f);
            line.skipWhenMissed = true;
            var l = Make(running, line);
            Enter(l, 2, 1, 0f);
            l.Tick(0f, 1, NotCrossed(1));                    // 先の演出が走る
            l.Tick(2f, 1, Crossed(2f, 1));                   // 走行中に横切った → Ready で保持
            Assert.That(l.ArmedCount, Is.EqualTo(1), "横断は覚えておく（猶予切れで消さない）");
            l.Tick(10f, 1, NotCrossed(1));                   // 走行中の演出が終了
            TakeRunnerLogic.Decision d = l.Tick(10f, 1, NotCrossed(1));
            Assert.That(d.action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep),
                "画面が空いたら、同じ区間に居る限り出る");
            Assert.That(d.takeIndex, Is.EqualTo(1));
        }

        [Test]
        public void Line_CrossedWhileAnotherTakeRuns_DroppedAndReported_IfSegmentEndsFirst()
        {
            var running = Enter(2, 1, 0f, 30f);
            var line = Line(2, 1, 0, 3f);
            var l = Make(running, line);
            var dropped = new List<(int, TakeRunnerLogic.DropReason)>();
            l.TakeDropped = (i, r) => dropped.Add((i, r));

            Enter(l, 2, 1, 0f);
            l.Tick(0f, 1, NotCrossed(1));
            l.Tick(2f, 1, Crossed(2f, 1));                   // 走行中に横切った
            Enter(l, 2, 2, 5f, hadPrev: true, prevLap: 2, prevCam: 1);
            Assert.That(dropped, Is.EqualTo(new[] { (1, TakeRunnerLogic.DropReason.ScreenBusyAtExit) }),
                "線を踏んだのに出せなかったことは必ず報告する");
        }

        [Test]
        public void Line_CrossedWhileSuppressed_FiresAtExit_WhenFireOnExit()
        {
            var l = Make(Line(2, 1, 0, 3f));
            l.SetSuppressed(true);
            Enter(l, 2, 1, 0f);
            l.Tick(1f, 1, Crossed(1f, 1));                             // ライブ卓が握っている間は出さない
            Assert.That(l.IsActive, Is.False);
            l.SetSuppressed(false);
            TakeRunnerLogic.Decision d = Enter(l, 2, 2, 5f, hadPrev: true, prevLap: 2, prevCam: 1);
            Assert.That(d.action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep));
        }

        [Test]
        public void Line_FiresAtMostOncePerVisit_EvenWhenNotOnce()
        {
            var def = Line(1, 0, 0, 1f);
            def.once = false;
            var l = Make(def);
            Enter(l, 1, 0, 0f);
            Assert.That(l.Tick(0f, 0, Crossed(0f, 0)).action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep));
            l.Tick(1f, 0, NotCrossed(0));                                 // 終了
            Assert.That(l.Tick(2f, 0, Crossed(2f, 0)).action, Is.EqualTo(TakeRunnerLogic.Action.None),
                "同じ滞在中に往復して踏んでも 1 区間滞在につき 1 回");

            Enter(l, 1, 1, 3f, hadPrev: true, prevLap: 1, prevCam: 0);    // 区間を出て
            Enter(l, 1, 0, 4f, hadPrev: true, prevLap: 1, prevCam: 1);    // 戻ると再武装
            Assert.That(l.Tick(4f, 0, Crossed(4f, 0)).action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep));
        }

        [Test]
        public void Line_Once_DoesNotFireAgainInTheSameRun()
        {
            var l = Make(Line(1, 0, 0, 1f));
            Enter(l, 1, 0, 0f);
            l.Tick(0f, 0, Crossed(0f, 0));
            l.Tick(1f, 0, NotCrossed(0));
            Enter(l, 1, 1, 2f, hadPrev: true, prevLap: 1, prevCam: 0);
            Enter(l, 1, 0, 3f, hadPrev: true, prevLap: 1, prevCam: 1);
            Assert.That(l.Tick(3f, 0, Crossed(3f, 0)).action, Is.EqualTo(TakeRunnerLogic.Action.None));

            l.ResetRun();
            Enter(l, 1, 0, 10f);
            Assert.That(l.Tick(10f, 0, Crossed(10f, 0)).action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep),
                "ラン開始でクリアされ再び出る");
        }

        [Test]
        public void Line_OnlyArmedInItsOwnSegment()
        {
            var l = Make(Line(3, 2, 0, 3f));
            Enter(l, 1, 0, 0f);
            Assert.That(l.Tick(1f, 0, Crossed(1f, 2)).action, Is.EqualTo(TakeRunnerLogic.Action.None),
                "別の周・別のカメラの区間では武装していないので踏んでも出ない");
            Enter(l, 3, 2, 5f, hadPrev: true, prevLap: 1, prevCam: 0);
            Assert.That(l.Tick(5f, 2, Crossed(5f, 2)).action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep));
        }

    }
}
