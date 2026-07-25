#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using FixedCamVr.Streaming;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// v3 演出経路の配線テスト（EditMode + 手動 Tick）。
    /// TimelineDirector ↔ TakeRunner ↔ CameraSwitchDirector ↔ CameraStreamRegistry を実行時に組み立て、
    /// 設計 §5 の不変条件が**配線レベルで**成立することを固定する。
    ///
    /// EditMode は Time.time が進まないので、TakeRunner の時刻源を差し替えて時間を手で進める
    /// （dip / dwell / cooldown は 0 に落として 1 ステップ確定）。
    /// </summary>
    public sealed class TakeWiringTests
    {
        private const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

        private readonly List<UnityEngine.Object> _spawned = new();
        private float _now;

        [SetUp]
        public void Reset() => _now = 0f;

        [TearDown]
        public void Cleanup()
        {
            foreach (var o in _spawned) if (o != null) UnityEngine.Object.DestroyImmediate(o);
            _spawned.Clear();
        }

        private static void SetField(object target, string name, object? value)
        {
            FieldInfo? f = target.GetType().GetField(name, BF);
            Assert.That(f, Is.Not.Null, $"field '{name}' not found on {target.GetType().Name}");
            f!.SetValue(target, value);
        }

        private static void Invoke(object target, string method)
            => target.GetType().GetMethod(method, BF)?.Invoke(target, null);

        private CameraStreamRegistry MakeRegistry(int count, int active)
        {
            var go = new GameObject("Registry");
            _spawned.Add(go);
            var reg = go.AddComponent<CameraStreamRegistry>();
            FieldInfo streamsF = typeof(CameraStreamRegistry).GetField("_streams", BF)!;
            Array arr = Array.CreateInstance(streamsF.FieldType.GetElementType()!, count);
            streamsF.SetValue(reg, arr);
            typeof(CameraStreamRegistry).GetField("_activeIndex", BF)!.SetValue(reg, active);
            return reg;
        }

        private CameraSwitchDirector MakeDirector(CameraStreamRegistry reg, GameObject host)
        {
            var dir = host.AddComponent<CameraSwitchDirector>();
            SetField(dir, "registry", reg);
            SetField(dir, "dipDownSec", 0f);
            SetField(dir, "dipUpSec", 0f);
            SetField(dir, "switchCooldownSec", 0f);
            SetField(dir, "minDwellSec", 0f);
            Invoke(dir, "Awake");
            Invoke(dir, "OnEnable");
            return dir;
        }

        private static void PumpDirector(CameraSwitchDirector dir, int frames = 4)
        {
            for (int i = 0; i < frames; i++) Invoke(dir, "Update");
        }

        // ---- show.json v3 のミニ定義 ----

        private static ShowStepDef LiveStep(int camera, float durSec) => new()
        {
            source = TakeSchema.SourceLive, camera = camera,
            durKind = TakeSchema.DurSec, durSec = durSec,
        };

        private static ShowStepDef ClipStep(string url, float durSec) => new()
        {
            source = TakeSchema.SourceClip, camera = -1, assetUrl = url,
            durKind = TakeSchema.DurSec, durSec = durSec,
        };

        private static ShowTimelineSegmentDef SegWithTake(int lap, int camera, ShowTakeDef take) => new()
        {
            lap = lap, camera = camera,
            takes = new[] { take },
            cues = Array.Empty<ShowSegmentCueDef>(),
        };

        private static ShowTakeDef EnterTake(string id, params ShowStepDef[] steps) => new()
        {
            id = id, at = TakeSchema.AtEnter, offsetSec = 0f,
            ifMissed = TakeSchema.MissedFireOnExit, policy = TakeSchema.PolicyHold,
            once = true, maxDurationSec = 0f, steps = steps,
        };

        private sealed class Rig
        {
            public CameraStreamRegistry Registry = null!;
            public CameraSwitchDirector Director = null!;
            public TakeRunner Runner = null!;
            public TimelineDirector Timeline = null!;
            public CueScheduler Scheduler = null!;
        }

        private Rig MakeRig(int cameraCount = 4, int active = 0)
        {
            var reg = MakeRegistry(cameraCount, active);
            var host = new GameObject("Screen"); _spawned.Add(host);
            var dir = MakeDirector(reg, host);
            var scheduler = host.AddComponent<CueScheduler>();
            var runner = host.AddComponent<TakeRunner>();
            var timeline = host.AddComponent<TimelineDirector>();

            SetField(runner, "director", dir);
            SetField(runner, "overlay", null);
            SetField(runner, "showControl", null);
            runner.SetTimeSource(() => _now);

            SetField(timeline, "cueScheduler", scheduler);
            SetField(timeline, "insertController", null);
            SetField(timeline, "takeRunner", runner);
            SetField(timeline, "showControl", null);

            Invoke(scheduler, "Awake");
            Invoke(timeline, "Awake");
            Invoke(timeline, "OnEnable");   // cueScheduler.CameraEntered 購読

            return new Rig
            {
                Registry = reg, Director = dir, Runner = runner, Timeline = timeline, Scheduler = scheduler,
            };
        }

        // ゾーン確定を（LapCounter 相当の経路で）流す。
        private static void EnterZone(Rig rig, int camera, int lap) => rig.Scheduler.NotifyCameraEntered(camera, lap);

        // TakeRunner.Update + Director.Update を 1 フレーム分回す。
        private static void Frame(Rig rig)
        {
            Invoke(rig.Runner, "Update");
            PumpDirector(rig.Director);
        }

        // ---- 多段カット（要求の演出形） ----

        [Test]
        public void MultiStepTake_SwitchesCamerasInOrder_ThenReturnsToCurrentZone()
        {
            Rig rig = MakeRig();
            rig.Timeline.SetTimelineV3(new[]
            {
                SegWithTake(1, 0, EnterTake("final", LiveStep(3, 2f), LiveStep(1, 3f))),
            });

            EnterZone(rig, 0, 1);
            Frame(rig);
            Assert.That(rig.Registry.ActiveIndex, Is.EqualTo(3), "1 カット目のカメラへ切り替わる");
            Assert.That(rig.Director.InsertActive, Is.True, "演出が画面を占有している（自動切替は凍結）");

            _now = 2f;
            Frame(rig);
            Assert.That(rig.Registry.ActiveIndex, Is.EqualTo(1), "2 カット目のカメラへ進む");
            Assert.That(rig.Runner.IsActive, Is.True);

            // 演出中に体験者は cam2 のゾーンへ移動していた（時計は進む）。
            rig.Director.RequestZone(2);
            PumpDirector(rig.Director, 1);

            _now = 5f;
            Frame(rig);
            Assert.That(rig.Runner.IsActive, Is.False, "尺を使い切って演出は終わる");
            Assert.That(rig.Director.InsertActive, Is.False, "占有が解除される");
            Assert.That(rig.Registry.ActiveIndex, Is.EqualTo(2),
                "復帰先は開始時ゾーン(0)ではなく、いま体験者が居るゾーン(2)（不変条件 3）");
        }

        [Test]
        public void AfterTake_ZoneAutoSwitchResumes()
        {
            Rig rig = MakeRig();
            rig.Timeline.SetTimelineV3(new[] { SegWithTake(1, 0, EnterTake("t", LiveStep(3, 1f))) });

            EnterZone(rig, 0, 1);
            Frame(rig);
            Assert.That(rig.Registry.ActiveIndex, Is.EqualTo(3));

            _now = 1f;
            Frame(rig);
            Assert.That(rig.Director.InsertActive, Is.False);

            rig.Director.RequestZone(1);
            PumpDirector(rig.Director);
            Assert.That(rig.Registry.ActiveIndex, Is.EqualTo(1), "演出後はゾーン自動切替が再開する（不変条件 1）");
        }

        // ---- 不変条件 2: 必ず終わる ----

        [Test]
        public void Watchdog_EndsTake_AndReleasesHold()
        {
            Rig rig = MakeRig();
            // untilClipEnd だが overlay 未配線 → 素材が出ない。既定尺フォールバックの後、最終的に watchdog が保証する。
            var step = new ShowStepDef
            {
                source = TakeSchema.SourceLive, camera = 3,
                durKind = TakeSchema.DurUntilClipEnd, durSec = 0f,
            };
            var take = EnterTake("stuck", step);
            take.maxDurationSec = 5f;
            rig.Timeline.SetTimelineV3(new[] { SegWithTake(1, 0, take) });

            EnterZone(rig, 0, 1);
            Frame(rig);
            Assert.That(rig.Director.InsertActive, Is.True);

            _now = 5f;
            Frame(rig);
            Assert.That(rig.Runner.IsActive, Is.False, "maxDurationSec で強制終了する（不変条件 2）");
            Assert.That(rig.Director.InsertActive, Is.False, "凍結も必ず解除される");
        }

        // ---- 不変条件 8: 走行中の差し替え / ラン開始で必ず畳む ----

        [Test]
        public void SetTakesDuringTake_FoldsAndUnfreezes()
        {
            Rig rig = MakeRig();
            rig.Timeline.SetTimelineV3(new[] { SegWithTake(1, 0, EnterTake("t", LiveStep(3, 30f))) });
            EnterZone(rig, 0, 1);
            Frame(rig);
            Assert.That(rig.Director.InsertActive, Is.True);

            rig.Timeline.SetTimelineV3(Array.Empty<ShowTimelineSegmentDef>()); // ショー中のタイムライン編集
            PumpDirector(rig.Director);
            Assert.That(rig.Runner.IsActive, Is.False);
            Assert.That(rig.Director.InsertActive, Is.False, "差し替えで占有が残らない");

            rig.Director.RequestZone(1);
            PumpDirector(rig.Director);
            Assert.That(rig.Registry.ActiveIndex, Is.EqualTo(1));
        }

        [Test]
        public void ResetRunDuringTake_FoldsAndUnfreezes()
        {
            Rig rig = MakeRig();
            rig.Timeline.SetTimelineV3(new[] { SegWithTake(1, 0, EnterTake("t", LiveStep(3, 30f))) });
            EnterZone(rig, 0, 1);
            Frame(rig);
            Assert.That(rig.Director.InsertActive, Is.True);

            rig.Timeline.ResetRun();   // 体験者交代
            PumpDirector(rig.Director);
            Assert.That(rig.Runner.IsActive, Is.False);
            Assert.That(rig.Director.InsertActive, Is.False,
                "ラン開始は走行中の演出を必ず割り込んで畳む（不変条件 8・スタッフの唯一の出口）");
        }

        // ---- 不変条件 1: 経路が二重にならない ----

        [Test]
        public void V3Path_LeavesLegacyCueSchedulerEmpty()
        {
            Rig rig = MakeRig();
            // v2 の cue を持つ区間を v3 として渡す（takes が正・cues は無視されるべき）。
            var seg = SegWithTake(1, 0, EnterTake("t", LiveStep(3, 1f)));
            seg.cues = new[] { new ShowSegmentCueDef { cueId = "should_not_fire", delaySec = 0f } };
            rig.Timeline.SetTimelineV3(new[] { seg });

            object logic = typeof(CueScheduler).GetField("_logic", BF)!.GetValue(rig.Scheduler);
            var entries = (Array)logic.GetType().GetField("_entries", BF)!.GetValue(logic);
            Assert.That(entries.Length, Is.EqualTo(0),
                "v3 のときは旧 cue 経路を空にして、画面の所有者を 1 つに保つ（不変条件 1）");
        }

        [Test]
        public void SwitchingBackToV2_RestoresLegacyPath_AndStopsTakes()
        {
            Rig rig = MakeRig();
            rig.Timeline.SetTimelineV3(new[] { SegWithTake(1, 0, EnterTake("t", LiveStep(3, 30f))) });
            EnterZone(rig, 0, 1);
            Frame(rig);
            Assert.That(rig.Runner.IsActive, Is.True);

            // show.json を v2 に戻す（退避路）。
            var v2Seg = new ShowTimelineSegmentDef
            {
                lap = 1, camera = 0,
                cues = new[] { new ShowSegmentCueDef { cueId = "c", delaySec = 0f } },
                takes = Array.Empty<ShowTakeDef>(),
            };
            rig.Timeline.SetTimeline(new[] { v2Seg });
            PumpDirector(rig.Director);

            Assert.That(rig.Runner.IsActive, Is.False, "v2 へ戻したら演出は畳まれる");
            Assert.That(rig.Director.InsertActive, Is.False);

            object logic = typeof(CueScheduler).GetField("_logic", BF)!.GetValue(rig.Scheduler);
            var entries = (Array)logic.GetType().GetField("_entries", BF)!.GetValue(logic);
            Assert.That(entries.Length, Is.EqualTo(1), "旧 cue 経路が復活する");
        }

        // ---- clip カット（カメラを変えず画面を占有） ----

        [Test]
        public void ClipStep_HoldsScreenWithoutSwitchingCamera()
        {
            Rig rig = MakeRig();
            rig.Timeline.SetTimelineV3(new[]
            {
                SegWithTake(1, 0, EnterTake("v", ClipStep("sa://assets/pre_01.mp4", 3f))),
            });

            EnterZone(rig, 0, 1);
            Frame(rig);
            Assert.That(rig.Registry.ActiveIndex, Is.EqualTo(0), "全面差し替えのカットではカメラを動かさない");
            Assert.That(rig.Director.InsertActive, Is.True, "それでも画面は演出が占有する");

            _now = 3f;
            Frame(rig);
            Assert.That(rig.Director.InsertActive, Is.False);
        }

        // ---- exit アンカー ----

        [Test]
        public void ExitTake_FiresOnLeaving_AndReturnsToNewZone()
        {
            Rig rig = MakeRig();
            var take = EnterTake("bye", LiveStep(3, 2f));
            take.at = TakeSchema.AtExit;
            rig.Timeline.SetTimelineV3(new[] { SegWithTake(1, 0, take) });

            EnterZone(rig, 0, 1);
            Frame(rig);
            Assert.That(rig.Registry.ActiveIndex, Is.EqualTo(0), "滞在中は出ない");

            // 体験者がゾーン 1 へ移動（時計が確定 → 区間進入通知）。
            rig.Director.RequestZone(1);
            PumpDirector(rig.Director, 1);
            EnterZone(rig, 1, 1);
            PumpDirector(rig.Director);
            Assert.That(rig.Registry.ActiveIndex, Is.EqualTo(3), "離脱の瞬間に差し込みカメラへ");

            _now = 2f;
            Frame(rig);
            Assert.That(rig.Registry.ActiveIndex, Is.EqualTo(1), "終わったら新しいゾーンのカメラへ");
        }
    }
}
