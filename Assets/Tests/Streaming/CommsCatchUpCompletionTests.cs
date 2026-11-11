#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using FixedCamVr.Streaming;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 通信乗っ取りの開始事実を「表示が下りた」ではなく、追いつきカットの自然完了で数える契約。
    /// TakeRunner の実カット進行と CommsCueLogic の優先順位を同じ入力値で固定する。
    /// </summary>
    public sealed class CommsCatchUpCompletionTests
    {
        private const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

        private readonly List<UnityEngine.Object> _spawned = new();
        private float _now;

        [SetUp]
        public void SetUp() => _now = 0f;

        [TearDown]
        public void TearDown()
        {
            foreach (UnityEngine.Object item in _spawned)
                if (item != null) UnityEngine.Object.DestroyImmediate(item);
            _spawned.Clear();
        }

        [Test]
        public void PlayableDollCall_NaturalStepTransitionAndNaturalEnd_IncrementCounter()
        {
            Rig rig = MakeRig();
            rig.Timeline.SetTimeline(new[]
            {
                Segment(EnterTake("catch-up",
                    LiveStep(1f, dollCall: true),
                    LiveStep(1f),
                    LiveStep(1f, dollCall: true))),
            });

            EnterZone(rig, 0);
            Frame(rig);
            Assert.That(rig.Timeline.DollCatchUpCompletedCount, Is.Zero,
                "表示を開始しただけでは完了にしない");

            _now = 1f;
            Frame(rig);
            Assert.That(rig.Timeline.DollCatchUpCompletedCount, Is.EqualTo(1),
                "同じ演出の次カットへ自然に進んだ時点で数える");

            _now = 2f;
            Frame(rig);
            Assert.That(rig.Timeline.DollCatchUpCompletedCount, Is.EqualTo(1));

            _now = 3f;
            Frame(rig);
            Assert.That(rig.Timeline.DollCatchUpCompletedCount, Is.EqualTo(2),
                "最後のカットなら演出の Completed 終了で数える");
        }

        [Test]
        public void SkippedDollCall_DoesNotIncrementCounter()
        {
            Rig rig = MakeRig();
            rig.Timeline.SetTimeline(new[]
            {
                Segment(EnterTake("skip", LiveStep(1f, dollCall: true, camera: 99))),
            });
            LogAssert.Expect(LogType.Warning,
                "[TakeRunner] live のカメラ 99 が範囲 [0,4) 外 → このカットを飛ばす（take=skip step=0）");

            EnterZone(rig, 0);
            Frame(rig);
            Frame(rig);

            Assert.That(rig.Timeline.DollCatchUpCompletedCount, Is.Zero,
                "playable 判定を通らず画面へ出なかったカットは数えない");
        }

        [Test]
        public void ForwardYieldAfterDollCallShown_CompletesCatchUpOnTheSameEdge()
        {
            Rig rig = MakeRig();
            rig.Timeline.SetTimeline(new[]
            {
                Segment(YieldTake("forward",
                    LiveStep(1f),
                    LiveStep(1.4f, dollCall: true),
                    LiveStep(30f))),
            });
            EnterZone(rig, 0);
            Frame(rig);
            _now = 1f;
            Frame(rig);
            Assert.That(rig.Timeline.DollCatchUpCompletedCount, Is.Zero, "呼びかけのカットが出ただけでは数えない");

            _now = 1.86f;
            EnterZone(rig, 1, forward: true);
            Assert.That(rig.Timeline.DollCatchUpCompletedCount, Is.EqualTo(1),
                "声の後に前へ抜けて yield で畳まれたら、その縁で追いつきが成立する");
            Assert.That(rig.Timeline.LastEndReason, Is.EqualTo(TakeRunnerLogic.EndReason.Yielded));
            Assert.That(rig.Runner.DollCallVoiceCount, Is.EqualTo(1),
                "声は呼びかけのカットの頭で鳴っているので、抜けた縁で重ねて撃たない");

            _now = 3f;
            Frame(rig);
            Frame(rig);
            Assert.That(rig.Timeline.DollCatchUpCompletedCount, Is.EqualTo(1), "畳んだ後に重ねて数えない");
        }

        [Test]
        public void ForwardYieldBeforeDollCallStarts_CompletesCatchUpAndVoicesOnce()
        {
            // 人形視点（pov_1〜3）の途中で前へ抜けた人にも乗っ取りを起こす（canon/LEDGER.md 0323 の委任）。
            Rig rig = MakeRig();
            rig.Timeline.SetTimeline(new[]
            {
                Segment(YieldTake("early",
                    LiveStep(3f),
                    LiveStep(1.4f, dollCall: true))),
            });
            EnterZone(rig, 0);
            Frame(rig);
            Assert.That(rig.Runner.DollCallVoiceCount, Is.Zero, "呼びかけのカットはまだ始まっていない");

            _now = 1f;
            EnterZone(rig, 1, forward: true);
            Assert.That(rig.Timeline.DollCatchUpCompletedCount, Is.EqualTo(1),
                "呼びかけのカットが始まる前でも、演出のカットが画面に出ていれば前へ抜けた縁で成立する");
            Assert.That(rig.Runner.DollCallVoiceCount, Is.EqualTo(1), "まだ鳴っていない声をその縁で 1 回撃つ");
            Assert.That(rig.Timeline.LastEndReason, Is.EqualTo(TakeRunnerLogic.EndReason.Yielded));

            _now = 3f;
            Frame(rig);
            Frame(rig);
            Assert.That(rig.Timeline.DollCatchUpCompletedCount, Is.EqualTo(1), "畳んだ後に重ねて数えない");
            Assert.That(rig.Runner.DollCallVoiceCount, Is.EqualTo(1), "声も重ねない");
        }

        [Test]
        public void ForwardYieldBeforeTheTakeStarts_DoesNotCompleteCatchUp()
        {
            // 線を越える前・待たされている間に前へ抜けた人は、人形視点を 1 コマも見ていない。
            Rig rig = MakeRig();
            ShowTakeDef take = YieldTake("not-yet",
                LiveStep(1f),
                LiveStep(1.4f, dollCall: true));
            take.offsetSec = 5f;
            take.ifMissed = TakeSchema.MissedSkip;
            rig.Timeline.SetTimeline(new[] { Segment(take) });

            EnterZone(rig, 0);
            Frame(rig);
            _now = 1f;
            Frame(rig);
            Assert.That(rig.Runner.IsActive, Is.False, "演出はまだ始まっていない");

            EnterZone(rig, 1, forward: true);
            Assert.That(rig.Timeline.DollCatchUpCompletedCount, Is.Zero, "始まる前の前進は追いつきではない");
            Assert.That(rig.Runner.DollCallVoiceCount, Is.Zero, "声も撃たない");
        }

        [Test]
        public void ForwardYieldWhileDollCallWasSkipped_CountsWhenAnEarlierCutWasShown()
        {
            Rig rig = MakeRig();
            rig.Timeline.SetTimeline(new[]
            {
                Segment(YieldTake("skip-yield",
                    LiveStep(1f),
                    LiveStep(1.4f, dollCall: true, camera: 99),
                    LiveStep(30f))),
            });
            LogAssert.Expect(LogType.Warning,
                "[TakeRunner] live のカメラ 99 が範囲 [0,4) 外 → このカットを飛ばす（take=skip-yield step=1）");
            EnterZone(rig, 0);
            Frame(rig);
            _now = 1f;
            Frame(rig);
            EnterZone(rig, 1, forward: true);
            Assert.That(rig.Timeline.DollCatchUpCompletedCount, Is.EqualTo(1),
                "人形視点のカットが 1 つでも画面に出ていれば、呼びかけのカットが飛んでいても成立する");
            Assert.That(rig.Runner.DollCallVoiceCount, Is.EqualTo(1),
                "飛ばしたカットでは声が鳴っていないので、抜けた縁で 1 回撃つ");
        }

        [Test]
        public void ForwardYieldWithNoCutShown_DoesNotCompleteCatchUp()
        {
            Rig rig = MakeRig();
            rig.Timeline.SetTimeline(new[]
            {
                Segment(YieldTake("nothing-shown",
                    LiveStep(1f, camera: 99),
                    LiveStep(1.4f, dollCall: true))),
            });
            LogAssert.Expect(LogType.Warning,
                "[TakeRunner] live のカメラ 99 が範囲 [0,4) 外 → このカットを飛ばす（take=nothing-shown step=0）");
            EnterZone(rig, 0);
            Frame(rig);
            Assert.That(rig.Runner.IsActive, Is.True, "演出は始まったが、1 カット目は飛んで画面に出ていない");

            EnterZone(rig, 1, forward: true);
            Assert.That(rig.Timeline.DollCatchUpCompletedCount, Is.Zero,
                "1 カットも画面に出ていない演出は、前へ抜けても数えない");
            Assert.That(rig.Runner.DollCallVoiceCount, Is.Zero);
        }

        [Test]
        public void ForwardYieldCatchUp_AppliesTheBgmAuthoredAfterTheDollCallCut()
        {
            Rig rig = MakeRig();
            BgmDirector bgm = AttachBgm(rig);
            bgm.ApplySegment(new ShowBgmDef { action = BgmPlanLogic.ActionPlay, trackId = "amb" }, present: true);

            ShowStepDef after = LiveStep(30f);
            after.bgm = new ShowBgmDef { action = BgmPlanLogic.ActionPlay, trackId = "bgm_after" };
            after.hasBgm = true;
            rig.Timeline.SetTimeline(new[]
            {
                Segment(YieldTake("with-bgm", LiveStep(3f), LiveStep(1.4f, dollCall: true), after)),
            });
            EnterZone(rig, 0);
            Frame(rig);
            Assert.That(Field(bgm, "_laneTrackId"), Is.EqualTo("amb"));

            _now = 1f;
            EnterZone(rig, 1, forward: true);
            Assert.That(rig.Timeline.DollCatchUpCompletedCount, Is.EqualTo(1));
            Assert.That(Field(bgm, "_laneTrackId"), Is.EqualTo("bgm_after"),
                "呼びかけのカットの次に著作された曲を、自然に進んだときと同じ口で掛ける");
        }

        [Test]
        public void ForwardYieldCatchUp_WithoutAuthoredBgm_LeavesTheLaneAlone()
        {
            Rig rig = MakeRig();
            BgmDirector bgm = AttachBgm(rig);
            bgm.ApplySegment(new ShowBgmDef { action = BgmPlanLogic.ActionPlay, trackId = "amb" }, present: true);
            rig.Timeline.SetTimeline(new[]
            {
                Segment(YieldTake("no-bgm", LiveStep(3f), LiveStep(1.4f, dollCall: true), LiveStep(30f))),
            });
            EnterZone(rig, 0);
            Frame(rig);
            _now = 1f;
            EnterZone(rig, 1, forward: true);
            Assert.That(rig.Timeline.DollCatchUpCompletedCount, Is.EqualTo(1));
            Assert.That(Field(bgm, "_laneTrackId"), Is.EqualTo("amb"), "曲の指示が無ければ何も替えない");
        }

        [Test]
        public void SecondCatchUpInTheSameRun_DoesNotRestartTheTakeoverStatic()
        {
            Rig rig = MakeRig();
            Material material = AttachFeelFx(rig, out CameraFeelFx feel);
            ShowTakeDef take = EnterTake("again", LiveStep(1f, dollCall: true));
            take.once = false;
            take.ifMissed = TakeSchema.MissedSkip;
            rig.Timeline.SetTimeline(new[] { Segment(take) });

            EnterZone(rig, 0);
            Frame(rig);
            _now = 1f;
            Frame(rig);
            Assert.That(rig.Timeline.DollCatchUpCompletedCount, Is.EqualTo(1));
            Assert.That(material.GetFloat("_NarrativeStatic"), Is.EqualTo(1f), "ランで最初の成立で砂嵐を始める");

            // 乗っ取りの面が消えた（同時消去で砂嵐が解ける）後に、引き返して同じ演出が再び成立した。
            feel.CancelTakeoverStatic();
            Assert.That(material.GetFloat("_NarrativeStatic"), Is.EqualTo(0f));
            EnterZone(rig, 1);
            EnterZone(rig, 0);
            Frame(rig);
            _now = 2f;
            Frame(rig);
            Assert.That(rig.Timeline.DollCatchUpCompletedCount, Is.EqualTo(2));
            Assert.That(material.GetFloat("_NarrativeStatic"), Is.EqualTo(0f),
                "2 回目の成立では砂嵐を始めない（乗っ取りは 1 回で、解く側が二度と来ない）");

            // 体験者が替われば、また最初の成立から数える。
            rig.Timeline.ResetRun();
            EnterZone(rig, 1);
            EnterZone(rig, 0);
            Frame(rig);
            _now = 3f;
            Frame(rig);
            Assert.That(rig.Timeline.DollCatchUpCompletedCount, Is.EqualTo(1));
            Assert.That(material.GetFloat("_NarrativeStatic"), Is.EqualTo(1f), "ラン開始で 0→1 の縁が戻る");
        }

        [Test]
        public void ForwardYieldPolicy_IsAPureFunctionOfShownAndCounted()
        {
            Assert.That(TakeRunner.ShouldCountForwardYieldCatchUp(true, true, false), Is.True);
            Assert.That(TakeRunner.ShouldCountForwardYieldCatchUp(false, true, false), Is.False, "呼びかけの無い演出");
            Assert.That(TakeRunner.ShouldCountForwardYieldCatchUp(true, false, false), Is.False, "1 カットも出ていない");
            Assert.That(TakeRunner.ShouldCountForwardYieldCatchUp(true, true, true), Is.False, "この演出で数え済み");
        }

        [Test]
        public void BacktrackYieldAfterDollCallShown_DoesNotCompleteCatchUp()
        {
            Rig rig = MakeRig();
            rig.Timeline.SetTimeline(new[]
            {
                Segment(YieldTake("backtrack", LiveStep(30f, dollCall: true))),
            });
            EnterZone(rig, 0);
            Frame(rig);
            EnterZone(rig, 1, forward: false);
            Assert.That(rig.Timeline.DollCatchUpCompletedCount, Is.Zero, "引き返しの yield は追いつきではない");
            Assert.That(rig.Timeline.LastEndReason, Is.EqualTo(TakeRunnerLogic.EndReason.Yielded));
        }

        [Test]
        public void NaturalCompletionThenForwardYield_DoesNotCountTwice()
        {
            Rig rig = MakeRig();
            rig.Timeline.SetTimeline(new[]
            {
                Segment(YieldTake("natural-then-exit",
                    LiveStep(1f, dollCall: true),
                    LiveStep(30f))),
            });
            EnterZone(rig, 0);
            Frame(rig);
            _now = 1f;
            Frame(rig);
            Assert.That(rig.Timeline.DollCatchUpCompletedCount, Is.EqualTo(1));

            EnterZone(rig, 1, forward: true);
            Assert.That(rig.Timeline.DollCatchUpCompletedCount, Is.EqualTo(1),
                "自然完了で数えた後に前へ抜けても 2 回目を数えない");
        }

        [Test]
        public void WatchdogAndExternalAbort_DoNotCompleteCatchUp()
        {
            Rig watchdog = MakeRig();
            ShowTakeDef watched = EnterTake("watchdog", LiveStep(30f, dollCall: true));
            watched.maxDurationSec = 1f;
            watchdog.Timeline.SetTimeline(new[] { Segment(watched) });
            EnterZone(watchdog, 0);
            Frame(watchdog);
            _now = 1f;
            Frame(watchdog);
            Assert.That(watchdog.Timeline.DollCatchUpCompletedCount, Is.Zero, "watchdog は自然完了ではない");

            _now = 0f;
            Rig aborted = MakeRig();
            aborted.Timeline.SetTimeline(new[]
            {
                Segment(EnterTake("abort", LiveStep(30f, dollCall: true))),
            });
            EnterZone(aborted, 0);
            Frame(aborted);
            aborted.Timeline.AbortActive();
            Assert.That(aborted.Timeline.DollCatchUpCompletedCount, Is.Zero, "卓の緊急停止は自然完了ではない");
            Assert.That(aborted.Timeline.PresentationAbortCount, Is.EqualTo(1),
                "外部中止は通信面が即時消灯できる別カウンタへ出す");

            aborted.Timeline.AbortActive();
            Assert.That(aborted.Timeline.PresentationAbortCount, Is.EqualTo(2),
                "演出がすでに終わっていても明示的な緊急停止は通信面へ伝える");
        }

        [Test]
        public void ResetRun_ClearsCompletionAndAbortCounters()
        {
            Rig rig = MakeRig();
            rig.Timeline.SetTimeline(new[]
            {
                Segment(EnterTake("completed", LiveStep(1f, dollCall: true))),
            });
            EnterZone(rig, 0);
            Frame(rig);
            _now = 1f;
            Frame(rig);
            Assert.That(rig.Timeline.DollCatchUpCompletedCount, Is.EqualTo(1));

            rig.Timeline.SetTimeline(new[]
            {
                Segment(EnterTake("aborted", LiveStep(30f, dollCall: true))),
            });
            EnterZone(rig, 0);
            Frame(rig);
            rig.Timeline.AbortActive();
            Assert.That(rig.Timeline.PresentationAbortCount, Is.EqualTo(1));

            rig.Timeline.ResetRun();
            Assert.That(rig.Timeline.DollCatchUpCompletedCount, Is.Zero);
            Assert.That(rig.Timeline.PresentationAbortCount, Is.Zero);
        }

        [Test]
        public void CompletionInSameFrameAsReport_TakeoverHasPriority()
        {
            var logic = new CommsCueLogic();
            CommsNotice notice = logic.Tick(Input(completed: 1, mark: true));
            Assert.That(notice, Is.EqualTo(CommsNotice.Takeover),
                "自然完了は同じフレームの報告より先に配送する");
        }

        [Test]
        public void CompletionObservedAfterEnteringTheNextSegment_ReturnsTakeoverOnTheNextTick()
        {
            var logic = new CommsCueLogic();
            CommsCueInput inPov = Input(completed: 0);
            inPov.invasionProgress = 0.75f;
            inPov.dollCatchUpShowing = true;
            Assert.That(logic.Tick(inPov), Is.EqualTo(CommsNotice.None), "声の最中はまだ乗っ取らない");

            // 前へ抜けた縁で完了が増え、次の Tick は 3-A の頭（人形の表示で侵食度 1・締めの時計は未到達）。
            CommsCueInput inThreeA = Input(completed: 1);
            inThreeA.invasionProgress = 1f;
            inThreeA.closingSec = -1f;
            Assert.That(logic.Tick(inThreeA), Is.EqualTo(CommsNotice.Takeover),
                "区間が替わった後でも、完了が増えた次の Tick で乗っ取りを返す");
        }

        [Test]
        public void CompletionWhileSuppressed_IsConsumedWithoutDelayedTakeover()
        {
            var logic = new CommsCueLogic();
            Assert.That(logic.Tick(Input(completed: 1, suppressed: true)), Is.EqualTo(CommsNotice.None));
            Assert.That(logic.Tick(Input(completed: 1)), Is.EqualTo(CommsNotice.None),
                "卓の介入中に観測した完了を解除後へ持ち越さない");

            var pending = new CommsCueLogic();
            Assert.That(pending.Tick(Input(completed: 1)), Is.EqualTo(CommsNotice.Takeover));
            Assert.That(pending.Tick(Input(completed: 1, suppressed: true)), Is.EqualTo(CommsNotice.None));
            Assert.That(pending.Tick(Input(completed: 1)), Is.EqualTo(CommsNotice.None),
                "まだ配送されていない候補も卓の介入で破棄する");
        }

        [Test]
        public void LeavingRun_ClearsObservedCompletionForNextRun()
        {
            var logic = new CommsCueLogic();
            Assert.That(logic.Tick(Input(completed: 1)), Is.EqualTo(CommsNotice.Takeover));
            logic.NotifyDelivered(CommsNotice.Takeover);

            Assert.That(logic.Tick(new CommsCueInput { inRun = false, dt = 0.1f }),
                Is.EqualTo(CommsNotice.None));
            Assert.That(logic.Tick(Input(completed: 0)), Is.EqualTo(CommsNotice.None));
            Assert.That(logic.Tick(Input(completed: 1)), Is.EqualTo(CommsNotice.Takeover),
                "次のランの 0→1 は新しい自然完了として扱う");
        }

        private static CommsCueInput Input(int completed, bool mark = false, bool suppressed = false) => new()
        {
            inRun = true,
            panelDoneReading = false,
            takeoverAllowed = true,
            takeoverSuppressed = suppressed,
            dollCatchUpCompletedCount = completed,
            markPressed = mark,
            markDetected = mark,
            invasionProgress = 1f,
            dt = 0.1f,
        };

        private sealed class Rig
        {
            public CameraSwitchDirector Director = null!;
            public TakeRunner Runner = null!;
            public TimelineDirector Timeline = null!;
            public CueScheduler Scheduler = null!;
        }

        private Rig MakeRig()
        {
            var registryGo = new GameObject("Registry");
            _spawned.Add(registryGo);
            var registry = registryGo.AddComponent<CameraStreamRegistry>();
            FieldInfo streams = typeof(CameraStreamRegistry).GetField("_streams", BF)!;
            streams.SetValue(registry, Array.CreateInstance(streams.FieldType.GetElementType()!, 4));
            SetField(registry, "_activeIndex", 0);

            var host = new GameObject("Screen");
            _spawned.Add(host);
            var director = host.AddComponent<CameraSwitchDirector>();
            SetField(director, "registry", registry);
            SetField(director, "dipDownSec", 0f);
            SetField(director, "dipUpSec", 0f);
            SetField(director, "switchCooldownSec", 0f);
            SetField(director, "minDwellSec", 0f);
            director.SetDeltaSource(() => 1f);
            Invoke(director, "Awake");
            Invoke(director, "OnEnable");

            var scheduler = host.AddComponent<CueScheduler>();
            var runner = host.AddComponent<TakeRunner>();
            var timeline = host.AddComponent<TimelineDirector>();
            SetField(runner, "director", director);
            SetField(runner, "overlay", null);
            SetField(runner, "showControl", null);
            runner.SetTimeSource(() => _now);
            SetField(timeline, "cueScheduler", scheduler);
            SetField(timeline, "takeRunner", runner);
            SetField(timeline, "showControl", null);
            Invoke(scheduler, "Awake");
            Invoke(timeline, "Awake");
            Invoke(timeline, "OnEnable");

            return new Rig
            {
                Director = director,
                Runner = runner,
                Timeline = timeline,
                Scheduler = scheduler,
            };
        }

        // 主画面の砂嵐が実際に始まったかを、材質の値で読む（TakeoverStaticTests と同じ組み方）。
        private Material AttachFeelFx(Rig rig, out CameraFeelFx feel)
        {
            Shader shader = Shader.Find("FixedCamVr/ScreenComposite");
            Assert.That(shader, Is.Not.Null);
            var material = new Material(shader);
            _spawned.Add(material);
            var screen = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _spawned.Add(screen);
            screen.GetComponent<Renderer>().sharedMaterial = material;
            var signal = screen.AddComponent<SignalLostFx>();
            feel = screen.AddComponent<CameraFeelFx>();
            SetField(signal, "_material", material);
            SetField(rig.Runner, "_feelFx", feel);
            Assert.That(material.GetFloat("_NarrativeStatic"), Is.EqualTo(0f));
            return material;
        }

        private BgmDirector AttachBgm(Rig rig)
        {
            var go = new GameObject("Bgm");
            _spawned.Add(go);
            var bgm = go.AddComponent<BgmDirector>();
            Invoke(bgm, "Awake");
            SetField(rig.Runner, "bgmDirector", bgm);
            return bgm;
        }

        private static object? Field(object target, string name)
            => target.GetType().GetField(name, BF)!.GetValue(target);

        private static ShowStepDef LiveStep(float durSec, bool dollCall = false, int camera = 1) => new()
        {
            source = TakeSchema.SourceLive,
            camera = camera,
            durKind = TakeSchema.DurSec,
            durSec = durSec,
            transition = TakeSchema.TransCut,
            dollCall = dollCall,
        };

        private static ShowTakeDef EnterTake(string id, params ShowStepDef[] steps) => new()
        {
            id = id,
            at = TakeSchema.AtEnter,
            offsetSec = 0f,
            ifMissed = TakeSchema.MissedFireOnExit,
            policy = TakeSchema.PolicyHold,
            once = true,
            maxDurationSec = 0f,
            steps = steps,
        };

        private static ShowTakeDef YieldTake(string id, params ShowStepDef[] steps)
        {
            ShowTakeDef take = EnterTake(id, steps);
            take.policy = TakeSchema.PolicyYield;
            return take;
        }

        private static ShowTimelineSegmentDef Segment(ShowTakeDef take) => new()
        {
            lap = 1,
            camera = 0,
            takes = new[] { take },
            cues = Array.Empty<ShowSegmentCueDef>(),
        };

        private static void EnterZone(Rig rig, int camera, bool forward = false)
            => rig.Scheduler.NotifyCameraEntered(new SegmentEntry(camera, segmentLap: 1, progressLap: 1, forward: forward));

        private static void Frame(Rig rig)
        {
            Invoke(rig.Runner, "Update");
            for (int i = 0; i < 4; i++) Invoke(rig.Director, "Update");
        }

        private static void SetField(object target, string name, object? value)
        {
            FieldInfo? field = target.GetType().GetField(name, BF);
            Assert.That(field, Is.Not.Null, $"field '{name}' not found on {target.GetType().Name}");
            field!.SetValue(target, value);
        }

        private static void Invoke(object target, string method)
        {
            MethodInfo? member = target.GetType().GetMethod(method, BF);
            Assert.That(member, Is.Not.Null, $"method '{method}' not found on {target.GetType().Name}");
            member!.Invoke(target, null);
        }
    }
}
