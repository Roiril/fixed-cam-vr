#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 体験 1 回の骨格（導入 → 本編 3 周 → 終了）の検証。
    /// 企画書 3 章「経路を 3 周する／全体は導入を含め 3 分以内／導入で固定視点に慣れてから開始する」。
    ///
    /// ここで固定する要点は 3 つ:
    ///   - 導入はランの第 1 相であって「ラン開始前の待機」ではない
    ///   - 終了の判定は Tick（次フレーム）で行う。周回の変化と同じ同期連鎖で確定させない
    ///   - 終了条件を満たしても走行中の演出は見せ切る（ただし上限つき）
    /// </summary>
    public sealed class ShowRunLogicTests
    {
        private const float IntroMin = 20f;

        private static ShowRunLogic Make(bool intro = true, bool auto = true, int laps = 3,
                                         float hardLimit = 300f)
        {
            var r = new ShowRunLogic();
            r.Configure(intro, IntroMin, auto, laps, hardLimit);
            r.BeginRun();
            return r;
        }

        [Test]
        public void BeginRun_StartsInIntro_WhenIntroEnabled()
        {
            var r = Make();
            Assert.That(r.Phase, Is.EqualTo(ShowPhase.Intro));
            Assert.That(r.GateOpen, Is.False, "導入中は区間進行を下流へ流さない");
        }

        [Test]
        public void BeginRun_GoesStraightToRun_WhenIntroDisabled()
        {
            var r = new ShowRunLogic();
            r.Configure(false, IntroMin, true, 3, 300f);
            Assert.That(r.BeginRun(), Is.EqualTo(ShowRunEvent.RunBegan));
            Assert.That(r.Phase, Is.EqualTo(ShowPhase.Run));
            Assert.That(r.GateOpen, Is.True);
        }

        [Test]
        public void Intro_AdvancesOnlyWhenBothTimeAndPlaceAreSatisfied()
        {
            var r = Make();
            // 時間は足りたが、スタート区間に居ない
            Assert.That(r.Tick(IntroMin + 1f, atStartZone: false, takeRunning: false),
                Is.EqualTo(ShowRunEvent.None));
            Assert.That(r.Phase, Is.EqualTo(ShowPhase.Intro));

            // スタート区間に入った次の Tick で本編へ
            Assert.That(r.Tick(0.1f, atStartZone: true, takeRunning: false),
                Is.EqualTo(ShowRunEvent.RunBegan));
            Assert.That(r.Phase, Is.EqualTo(ShowPhase.Run));
            Assert.That(r.Lap, Is.EqualTo(1));
            Assert.That(r.RunElapsedSec, Is.EqualTo(0f), "本編の経過は導入を含まない");
        }

        [Test]
        public void Intro_DoesNotAdvanceBeforeMinimumTime_EvenAtStartZone()
        {
            var r = Make();
            Assert.That(r.Tick(IntroMin - 1f, atStartZone: true, takeRunning: false),
                Is.EqualTo(ShowRunEvent.None));
            Assert.That(r.Phase, Is.EqualTo(ShowPhase.Intro));
        }

        [Test]
        public void Intro_ManualAdvance_WorksImmediatelyAndIgnoresPlace()
        {
            var r = Make(auto: false);
            r.Tick(1f, atStartZone: false, takeRunning: false);
            r.RequestAdvance();
            Assert.That(r.Tick(0.02f, atStartZone: false, takeRunning: false),
                Is.EqualTo(ShowRunEvent.RunBegan));
        }

        [Test]
        public void Intro_NeverAutoAdvances_WhenAutoDisabled()
        {
            var r = Make(auto: false);
            Assert.That(r.Tick(IntroMin * 5f, atStartZone: true, takeRunning: false),
                Is.EqualTo(ShowRunEvent.None));
            Assert.That(r.Phase, Is.EqualTo(ShowPhase.Intro));
        }

        [Test]
        public void Run_FinishesAfterTotalLaps()
        {
            var r = ToRun();
            r.NotifyLap(2);
            r.NotifyLap(3);
            Assert.That(r.Tick(1f, true, false), Is.EqualTo(ShowRunEvent.None), "3 周目は本編のまま");
            // 3 周目の最後の区間からスタート領域へ戻ると lap=4 になる = 走り切った。
            // ただしそこは**帰りの A**（体験の最後の区間）なので、演出が始まる猶予（endGraceSec）を
            // 必ず待ってから終わる。
            r.NotifyLap(4);
            Assert.That(r.Tick(0.02f, true, false), Is.EqualTo(ShowRunEvent.None), "猶予のあいだは終わらない");
            Assert.That(r.Tick(ShowRunDefaults.EndGraceSec, true, false), Is.EqualTo(ShowRunEvent.RunFinished));
            Assert.That(r.Phase, Is.EqualTo(ShowPhase.Finished));
            Assert.That(r.GateOpen, Is.False);
        }

        [Test]
        public void Run_WaitsGrace_SoTheReturnSegmentTakeCanStart()
        {
            // 帰りの A に入ったフレームで終了条件が立つが、その区間の at:"enter" 演出は
            // まだ武装されただけで走っていない（開始は次フレーム以降の TakeRunner.Update で、
            // スクリプト実行順は未定義）。takeRunning だけを見ると走り出す前に暗転する。
            var r = ToRun();
            r.NotifyLap(4);
            for (int i = 0; i < 10; i++)   // 猶予 3 秒ぶん（0.1s × 10 = 1.0s）はまだ終わらない
                Assert.That(r.Tick(0.1f, true, takeRunning: false), Is.EqualTo(ShowRunEvent.None));
            Assert.That(r.EndHolding, Is.True);

            // 猶予のあいだに演出が始まれば、そのまま見せ切る側へ移る
            Assert.That(r.Tick(0.1f, true, takeRunning: true), Is.EqualTo(ShowRunEvent.None));
            Assert.That(r.Tick(30f, true, takeRunning: true), Is.EqualTo(ShowRunEvent.None),
                "旧実装の上限 12 秒では帰りの A の録画（20〜40 秒）が途中で切れた");
            Assert.That(r.Phase, Is.EqualTo(ShowPhase.Run));
        }

        [Test]
        public void Run_GraceCanBeDisabled()
        {
            var r = new ShowRunLogic();
            r.Configure(false, IntroMin, true, 3, 300f, endGraceSec: 0f, endHoldMaxSec: 60f);
            r.BeginRun();
            r.NotifyLap(4);
            Assert.That(r.Tick(0.02f, true, false), Is.EqualTo(ShowRunEvent.RunFinished),
                "猶予 0 なら従来どおり即終了する");
        }

        [Test]
        public void Run_HardLimitDoesNotWaitForGraceOrTake()
        {
            // 時間切れで待つと hardLimitSec の意味が壊れる（動かない体験者の保険なので）。
            var r = ToRun(hardLimit: 30f);
            Assert.That(r.Tick(31f, true, takeRunning: true), Is.EqualTo(ShowRunEvent.RunFinished));
        }

        [Test]
        public void Run_NotifyLap_DoesNotFinishSynchronously()
        {
            // 周回の確定と離脱時演出の発火は同じ同期連鎖で起きる。NotifyLap のその場で終了させると
            // 3 周目最後の区間に置いた離脱時演出が始まる前に体験が終わる。
            var r = ToRun();
            r.NotifyLap(4);
            Assert.That(r.Phase, Is.EqualTo(ShowPhase.Run), "NotifyLap では相を動かさない");
        }

        [Test]
        public void Run_HoldsFinishWhileTakeIsRunning()
        {
            var r = ToRun();
            r.NotifyLap(4);
            Assert.That(r.Tick(1f, true, takeRunning: true), Is.EqualTo(ShowRunEvent.None));
            Assert.That(r.EndHolding, Is.True);
            Assert.That(r.Phase, Is.EqualTo(ShowPhase.Run));

            // 演出が終われば次の Tick で終了する（猶予を過ぎていること）
            Assert.That(r.Tick(ShowRunDefaults.EndGraceSec, true, takeRunning: true),
                Is.EqualTo(ShowRunEvent.None));
            Assert.That(r.Tick(0.02f, true, takeRunning: false), Is.EqualTo(ShowRunEvent.RunFinished));
        }

        [Test]
        public void Run_HoldHasUpperBound()
        {
            var r = ToRun();
            r.NotifyLap(4);
            // 演出が終わらなくても必ず終わる（上限は endHoldMaxSec）
            int steps = (int)(ShowRunDefaults.EndHoldMaxSec / 0.5f) + 10;
            for (int i = 0; i < steps; i++)
            {
                if (r.Tick(0.5f, true, takeRunning: true) == ShowRunEvent.RunFinished) break;
            }
            Assert.That(r.Phase, Is.EqualTo(ShowPhase.Finished));
        }

        [Test]
        public void Run_HardLimitFinishes_ForVisitorWhoStopsMoving()
        {
            var r = ToRun(hardLimit: 30f);
            Assert.That(r.Tick(29f, true, false), Is.EqualTo(ShowRunEvent.None));
            Assert.That(r.Tick(2f, true, false), Is.EqualTo(ShowRunEvent.RunFinished));
        }

        [Test]
        public void Run_HardLimitCanBeDisabled()
        {
            var r = ToRun(hardLimit: 0f);
            for (int i = 0; i < 100; i++) r.Tick(10f, true, false);
            Assert.That(r.Phase, Is.EqualTo(ShowPhase.Run));
        }

        [Test]
        public void Run_ExplicitFinishDoesNotWaitForTake()
        {
            var r = ToRun();
            r.RequestFinish();
            Assert.That(r.Tick(0.02f, true, takeRunning: true), Is.EqualTo(ShowRunEvent.RunFinished),
                "スタッフの明示操作は演出を待たない");
        }

        [Test]
        public void Finished_StaysFinishedUntilBeginRun()
        {
            var r = ToRun();
            r.RequestFinish();
            r.Tick(0.02f, true, false);
            for (int i = 0; i < 10; i++)
                Assert.That(r.Tick(1f, true, false), Is.EqualTo(ShowRunEvent.None));
            Assert.That(r.Phase, Is.EqualTo(ShowPhase.Finished));

            r.BeginRun();
            Assert.That(r.Phase, Is.EqualTo(ShowPhase.Intro), "次の体験者は導入から");
        }

        [Test]
        public void Intro_FinishRequestEndsWithoutEnteringRun()
        {
            var r = Make();
            r.RequestFinish();
            Assert.That(r.Tick(0.02f, false, false), Is.EqualTo(ShowRunEvent.RunFinished));
            Assert.That(r.Phase, Is.EqualTo(ShowPhase.Finished));
        }

        [Test]
        public void LapElapsed_ResetsOnEachLap()
        {
            var r = ToRun();
            r.Tick(5f, true, false);
            Assert.That(r.LapElapsedSec, Is.EqualTo(5f).Within(1e-3f));
            r.NotifyLap(2);
            Assert.That(r.LapElapsedSec, Is.EqualTo(0f));
        }

        [Test]
        public void TotalLaps_FallsBackToDefault_WhenNotAuthored()
        {
            var r = new ShowRunLogic();
            r.Configure(true, IntroMin, true, 0, 300f);
            Assert.That(r.TotalLaps, Is.EqualTo(ShowRunDefaults.TotalLaps));
        }

        // --- 導入演出を最後まで見せる（2026-07-30 実機テストで見つけた穴）---
        // introElapsed は起動から数え始めるので、設営や待機で introMinSec はとうに過ぎている。
        // そこへ体験者が開始位置に立つと、演出が始まったその瞬間に本編へ飛んでいた。

        [Test]
        public void Intro_DoesNotAutoAdvance_WhileIntroPlaying()
        {
            var r = Make();
            // 起動から時間が経ち、条件（時間・場所）は揃っている状態を作る。
            Assert.That(r.Tick(IntroMin + 5f, atStartZone: false, takeRunning: false),
                        Is.EqualTo(ShowRunEvent.None));
            // ここで体験者が開始位置に立ち、演出が動き出す。
            Assert.That(r.Tick(0.1f, atStartZone: true, takeRunning: false, introPlaying: true),
                        Is.EqualTo(ShowRunEvent.None), "演出の最中に本編へ飛ばさない");
            Assert.That(r.Tick(10f, atStartZone: true, takeRunning: false, introPlaying: true),
                        Is.EqualTo(ShowRunEvent.None));
            Assert.That(r.Phase, Is.EqualTo(ShowPhase.Intro));
        }

        [Test]
        public void Intro_Advances_AfterIntroFinishesAndWalkAroundElapsed()
        {
            var r = Make();
            r.Tick(IntroMin + 5f, atStartZone: false, takeRunning: false);
            r.Tick(13f, atStartZone: true, takeRunning: false, introPlaying: true);

            // 演出が終わった合図。ここから慣らし歩行の計時が始まる。
            r.RestartIntroClock();
            Assert.That(r.Tick(IntroMin - 1f, atStartZone: true, takeRunning: false),
                        Is.EqualTo(ShowRunEvent.None), "慣らし歩行がまだ足りない");
            Assert.That(r.Tick(2f, atStartZone: true, takeRunning: false),
                        Is.EqualTo(ShowRunEvent.RunBegan));
        }

        [Test]
        public void StaffAdvance_Works_EvenWhileIntroPlaying()
        {
            var r = Make();
            r.RequestAdvance();
            Assert.That(r.Tick(0.1f, atStartZone: false, takeRunning: false, introPlaying: true),
                        Is.EqualTo(ShowRunEvent.RunBegan), "人の明示操作は演出より優先する");
        }

        [Test]
        public void Intro_StillHolds_WhenPlayerNotAtStartZone_EvenAfterIntroPlayed()
        {
            var r = Make();
            r.Tick(IntroMin + 5f, atStartZone: false, takeRunning: false);
            r.RestartIntroClock();
            Assert.That(r.Tick(IntroMin + 1f, atStartZone: false, takeRunning: false),
                        Is.EqualTo(ShowRunEvent.None), "スタート区間に居ないなら進まない（従来どおり）");
        }

        private static ShowRunLogic ToRun(float hardLimit = 300f)
        {
            var r = Make(hardLimit: hardLimit);
            r.Tick(IntroMin + 1f, atStartZone: true, takeRunning: false);
            Assert.That(r.Phase, Is.EqualTo(ShowPhase.Run));
            return r;
        }
    }
}
