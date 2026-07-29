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
            // 3 周目の最後の区間からスタート領域へ戻ると lap=4 になる = 走り切った
            r.NotifyLap(4);
            Assert.That(r.Tick(0.02f, true, false), Is.EqualTo(ShowRunEvent.RunFinished));
            Assert.That(r.Phase, Is.EqualTo(ShowPhase.Finished));
            Assert.That(r.GateOpen, Is.False);
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

            // 演出が終われば次の Tick で終了する
            Assert.That(r.Tick(0.02f, true, takeRunning: false), Is.EqualTo(ShowRunEvent.RunFinished));
        }

        [Test]
        public void Run_HoldHasUpperBound()
        {
            var r = ToRun();
            r.NotifyLap(4);
            // 演出が終わらなくても必ず終わる
            for (int i = 0; i < 100; i++)
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

        private static ShowRunLogic ToRun(float hardLimit = 300f)
        {
            var r = Make(hardLimit: hardLimit);
            r.Tick(IntroMin + 1f, atStartZone: true, takeRunning: false);
            Assert.That(r.Phase, Is.EqualTo(ShowPhase.Run));
            return r;
        }
    }
}
