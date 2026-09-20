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
        public void YieldWatchdogAndExternalAbort_DoNotCompleteCatchUp()
        {
            Rig yielded = MakeRig();
            ShowTakeDef yieldTake = EnterTake("yield", LiveStep(30f, dollCall: true));
            yieldTake.policy = TakeSchema.PolicyYield;
            yielded.Timeline.SetTimeline(new[] { Segment(yieldTake) });
            EnterZone(yielded, 0);
            Frame(yielded);
            EnterZone(yielded, 1);
            Assert.That(yielded.Timeline.DollCatchUpCompletedCount, Is.Zero, "区間離脱は自然完了ではない");

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

        private static ShowTimelineSegmentDef Segment(ShowTakeDef take) => new()
        {
            lap = 1,
            camera = 0,
            takes = new[] { take },
            cues = Array.Empty<ShowSegmentCueDef>(),
        };

        private static void EnterZone(Rig rig, int camera)
            => rig.Scheduler.NotifyCameraEntered(camera, 1, 1);

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
