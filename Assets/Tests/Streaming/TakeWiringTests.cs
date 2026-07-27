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
            // dip の dt を固定する（EditMode の Time.unscaledDeltaTime = エディタの実フレーム間隔だと
            // カットの遷移秒（既定 170ms）が run ごとの fps で満たされたり満たされなかったりする）。
            // 1 フレーム = 1 秒とみなすので、Director.Update 1 回で dip の各相が必ず終わる。
            dir.SetDeltaSource(() => 1f);
            Invoke(dir, "Awake");
            Invoke(dir, "OnEnable");
            return dir;
        }

        private static void PumpDirector(CameraSwitchDirector dir, int frames = 4)
        {
            for (int i = 0; i < frames; i++) Invoke(dir, "Update");
        }

        // ---- show.json v3 のミニ定義 ----

        // 遷移は **cut（瞬時）** を既定にする。既定の dip にすると 1 カットあたり 170ms の実時間が要り、
        // EditMode の Time.unscaledDeltaTime（= エディタの実フレーム間隔。フォーカス中は 7〜16ms、
        // 非フォーカス時は数百 ms）に結果が左右されて **run ごとに落ちる / 通る** が変わる
        // （2026-07-27 に実際に 7 件同時に落ちた）。遷移そのものの検査は
        // CutTransition_* / DipTransition_* が明示的に指定して行う。
        private static ShowStepDef LiveStep(int camera, float durSec) => new()
        {
            source = TakeSchema.SourceLive, camera = camera,
            durKind = TakeSchema.DurSec, durSec = durSec,
            transition = TakeSchema.TransCut,
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
            rig.Timeline.SetTimeline(new[]
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
            rig.Timeline.SetTimeline(new[] { SegWithTake(1, 0, EnterTake("t", LiveStep(3, 1f))) });

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
                transition = TakeSchema.TransCut,   // 実フレーム時間に依存させない（LiveStep と同じ理由）
            };
            var take = EnterTake("stuck", step);
            take.maxDurationSec = 5f;
            rig.Timeline.SetTimeline(new[] { SegWithTake(1, 0, take) });

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
            rig.Timeline.SetTimeline(new[] { SegWithTake(1, 0, EnterTake("t", LiveStep(3, 30f))) });
            EnterZone(rig, 0, 1);
            Frame(rig);
            Assert.That(rig.Director.InsertActive, Is.True);

            rig.Timeline.SetTimeline(Array.Empty<ShowTimelineSegmentDef>()); // ショー中のタイムライン編集
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
            rig.Timeline.SetTimeline(new[] { SegWithTake(1, 0, EnterTake("t", LiveStep(3, 30f))) });
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
            rig.Timeline.SetTimeline(new[] { seg });

            object logic = typeof(CueScheduler).GetField("_logic", BF)!.GetValue(rig.Scheduler);
            var entries = (Array)logic.GetType().GetField("_entries", BF)!.GetValue(logic);
            Assert.That(entries.Length, Is.EqualTo(0),
                "v3 のときは旧 cue 経路を空にして、画面の所有者を 1 つに保つ（不変条件 1）");
        }

        [Test]
        public void ClearingTimeline_FoldsRunningTake_AndUnfreezes()
        {
            Rig rig = MakeRig();
            rig.Timeline.SetTimeline(new[] { SegWithTake(1, 0, EnterTake("t", LiveStep(3, 30f))) });
            EnterZone(rig, 0, 1);
            Frame(rig);
            Assert.That(rig.Runner.IsActive, Is.True);

            // timeline が消えた（show.json から外れた / 焼き込みが空）→ 走行中の演出は畳む。
            rig.Timeline.Clear();
            PumpDirector(rig.Director);

            Assert.That(rig.Runner.IsActive, Is.False, "timeline を外したら演出は畳まれる");
            Assert.That(rig.Director.InsertActive, Is.False, "画面の占有も返る");
        }

        [Test]
        public void V2Segments_AreMigratedAndRunAsTakes()
        {
            // v2（cues[] / insert）の show.json も、ShowControlClient が EnsureTakes で変換してから渡す。
            // ここでは変換 → 実行までを 1 本の経路で確かめる（旧 InsertController は廃止済み）。
            var v2Seg = new ShowTimelineSegmentDef
            {
                lap = 1, camera = 0,
                insert = new ShowInsertDef
                {
                    anchor = TakeSchema.AtEnter, camera = 3, delaySec = 0f, durationSec = 30f, once = true,
                },
                hasInsert = true,
                takes = Array.Empty<ShowTakeDef>(),
            };
            var segments = new[] { v2Seg };
            TimelineMigration.EnsureTakes(new ShowTimelineDef { rev = 1, segments = segments });
            // 変換結果の遷移は dip（v2 insert の意味として正しい）。ここは「変換 → 実行」の経路検査なので、
            // 実フレーム時間に依存しない cut へ落としてから走らせる（LiveStep と同じ理由）。
            // dip 自体の計時は DipTransition_TakesTimeBeforeSwitching が見ている。
            v2Seg.takes[0].steps[0].transition = TakeSchema.TransCut;

            Rig rig = MakeRig();
            rig.Timeline.SetTimeline(segments);
            EnterZone(rig, 0, 1);
            Frame(rig);

            Assert.That(rig.Runner.IsActive, Is.True, "v2 の insert が演出として走る");
            Assert.That(rig.Registry.ActiveIndex, Is.EqualTo(3), "insert のカメラが映る");
        }

        // ---- clip カット（カメラを変えず画面を占有） ----

        [Test]
        public void ClipStep_HoldsScreenWithoutSwitchingCamera()
        {
            Rig rig = MakeRig();
            rig.Timeline.SetTimeline(new[]
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

        // ---- カット遷移（cut / dip / fade）----

        [Test]
        public void CutTransition_SwitchesWithoutVisibleBlack()
        {
            Rig rig = MakeRig();
            var step = LiveStep(3, 2f);
            step.transition = TakeSchema.TransCut;
            rig.Timeline.SetTimeline(new[] { SegWithTake(1, 0, EnterTake("t", step)) });

            EnterZone(rig, 0, 1);
            Invoke(rig.Runner, "Update");
            Invoke(rig.Director, "Update");   // dip 1 フレーム目で黒 → 差し替えまで到達する
            Assert.That(rig.Registry.ActiveIndex, Is.EqualTo(3), "cut は 1 フレームで差し替わる");
        }

        [Test]
        public void DipTransition_TakesTimeBeforeSwitching()
        {
            Rig rig = MakeRig();
            // Director の dip を実尺に戻す（MakeRig は 0 に落としている）。
            SetField(rig.Director, "dipDownSec", 1f);
            SetField(rig.Director, "dipUpSec", 1f);
            var step = LiveStep(3, 5f);
            step.transition = TakeSchema.TransDip;
            step.transitionMs = 1000f;        // 全体 1s → 落とし 0.4s / 立ち上げ 0.6s
            rig.Timeline.SetTimeline(new[] { SegWithTake(1, 0, EnterTake("t", step)) });

            EnterZone(rig, 0, 1);
            Invoke(rig.Runner, "Update");   // ここで演出が始まり dip が起動する
            // Director.Update（dip を進める側）を 1 回も回していない時点で見る。
            // 以前は 1 フレーム回してから ActiveIndex を見ていたが、EditMode の Time.unscaledDeltaTime は
            // エディタの実フレーム間隔（非フォーカス時は数百 ms）なので、1 フレームで黒に届いて落ちることがあった
            // （2026-07-26 に実際に落ちた）。時間に依存しない観測点へ移す。
            Assert.That(rig.Director.Dipping, Is.True, "dip 指定のカットは dip 状態に入る");
            Assert.That(rig.Registry.ActiveIndex, Is.EqualTo(0), "黒へ落ちきるまで差し替えない");
        }

        // ---- policy: yield ----

        [Test]
        public void YieldTake_AbortsWhenViewerLeaves_AndScreenFollowsZone()
        {
            Rig rig = MakeRig();
            var take = EnterTake("amb", LiveStep(3, 30f));
            take.policy = TakeSchema.PolicyYield;
            rig.Timeline.SetTimeline(new[] { SegWithTake(1, 0, take) });

            EnterZone(rig, 0, 1);
            Frame(rig);
            Assert.That(rig.Registry.ActiveIndex, Is.EqualTo(3));
            Assert.That(rig.Director.InsertActive, Is.True);

            // 体験者がゾーン 1 へ（時計が確定 → 区間進入通知）。
            rig.Director.RequestZone(1);
            PumpDirector(rig.Director, 1);
            EnterZone(rig, 1, 1);
            PumpDirector(rig.Director);

            Assert.That(rig.Runner.IsActive, Is.False, "yield は境界を跨いだら打ち切る");
            Assert.That(rig.Director.InsertActive, Is.False);
            Assert.That(rig.Registry.ActiveIndex, Is.EqualTo(1), "画面は体験者のゾーンに追従する");
        }

        // ---- exit アンカー ----

        [Test]
        public void ExitTake_FiresOnLeaving_AndReturnsToNewZone()
        {
            Rig rig = MakeRig();
            var take = EnterTake("bye", LiveStep(3, 2f));
            take.at = TakeSchema.AtExit;
            rig.Timeline.SetTimeline(new[] { SegWithTake(1, 0, take) });

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

        // ---- §6.4 不正値のカットは飛ばす（2026-07-26 監査で未実装が判明した契約）----

        [Test]
        public void OutOfRangeLiveCamera_SkipsStep_InsteadOfClampingToAnotherCamera()
        {
            Rig rig = MakeRig(cameraCount: 4, active: 0);
            // 1 カット目のカメラ 9 は registry に無い。旧実装は registry.SetActive の clamp で
            // 無言に最終カメラ(3)へ飛んでいた。契約は「その step を飛ばす」。
            rig.Timeline.SetTimeline(new[]
            {
                SegWithTake(1, 0, EnterTake("bad", LiveStep(9, 5f), LiveStep(1, 3f))),
            });

            EnterZone(rig, 0, 1);
            Frame(rig);
            Assert.That(rig.Registry.ActiveIndex, Is.Not.EqualTo(3), "範囲外を末尾カメラへ clamp しない");

            Frame(rig);   // 飛ばした step は即終了 → 次のカットへ
            Assert.That(rig.Registry.ActiveIndex, Is.EqualTo(1), "2 カット目は通常どおり実行される");
        }

        [Test]
        public void AllStepsInvalid_TakeDoesNotDisturbTheScreen()
        {
            Rig rig = MakeRig(cameraCount: 4, active: 2);
            var clipWithoutAsset = new ShowStepDef
            {
                source = TakeSchema.SourceClip, camera = -1, assetUrl = "",
                durKind = TakeSchema.DurSec, durSec = 5f,
            };
            rig.Timeline.SetTimeline(new[]
            {
                SegWithTake(1, 0, EnterTake("empty", clipWithoutAsset)),
            });

            EnterZone(rig, 0, 1);
            for (int i = 0; i < 4; i++) Frame(rig);

            Assert.That(rig.Runner.IsActive, Is.False, "全 step が飛べば演出は残らない");
            Assert.That(rig.Director.InsertActive, Is.False, "画面を掴んだままにしない");
            Assert.That(rig.Registry.ActiveIndex, Is.EqualTo(2), "画面に触っていないので暗転も切替も起きない");
        }

        // ---- §6.3-6 ライブ卓が最優先（走行中の演出も畳む）----

        [Test]
        public void LiveDeskIntervention_FoldsRunningTake()
        {
            Rig rig = MakeRig(cameraCount: 4, active: 0);
            rig.Timeline.SetTimeline(new[] { SegWithTake(1, 0, EnterTake("t", LiveStep(3, 30f))) });

            EnterZone(rig, 0, 1);
            Frame(rig);
            Assert.That(rig.Runner.IsActive, Is.True);
            Assert.That(rig.Director.InsertActive, Is.True);

            // 卓が cue を出した（cameraOverride は無し）→ 演出は即畳み、カメラは体験者のゾーンへ返す。
            rig.Timeline.SetSuppressed(true);
            PumpDirector(rig.Director);

            Assert.That(rig.Runner.IsActive, Is.False, "抑止フラグだけでなく走行中の演出を畳む");
            Assert.That(rig.Director.InsertActive, Is.False, "凍結も解ける");
            Assert.That(rig.Registry.ActiveIndex, Is.EqualTo(0), "カメラは体験者のゾーンへ戻る");
        }

        [Test]
        public void LiveDeskCameraOverride_FoldsTake_WithoutStealingTheCamera()
        {
            Rig rig = MakeRig(cameraCount: 4, active: 0);
            rig.Timeline.SetTimeline(new[] { SegWithTake(1, 0, EnterTake("t", LiveStep(3, 30f))) });

            EnterZone(rig, 0, 1);
            Frame(rig);
            Assert.That(rig.Director.InsertActive, Is.True);

            // 卓がカメラを握った（override）→ 演出は畳むが、カメラは卓のものなので動かさない。
            rig.Director.SetOverrideActive(true);
            rig.Director.SetActiveExternal(2, CameraSwitchDirector.SwitchSource.Override);
            rig.Timeline.SetSuppressed(true);
            PumpDirector(rig.Director);

            Assert.That(rig.Runner.IsActive, Is.False);
            Assert.That(rig.Director.InsertActive, Is.False);
            Assert.That(rig.Registry.ActiveIndex, Is.EqualTo(2), "卓の固定カメラを演出の復帰 dip で外さない");
        }

        // ---- 「瞬時（cut）」は黒を挟まない ----

        [Test]
        public void CutTransition_SwitchesWithoutABlackFrame()
        {
            Rig rig = MakeRig(cameraCount: 4, active: 0);
            var step = LiveStep(3, 5f);
            step.transition = TakeSchema.TransCut;
            rig.Timeline.SetTimeline(new[] { SegWithTake(1, 0, EnterTake("cut", step)) });

            EnterZone(rig, 0, 1);
            Invoke(rig.Runner, "Update");   // Director.Update を回す前に見る

            Assert.That(rig.Registry.ActiveIndex, Is.EqualTo(3), "その場で切り替わる");
            Assert.That(rig.Director.Dipping, Is.False, "dip 状態に入らない（1 フレーム真っ黒を出さない）");
        }

        // ---- 演出の BGM（区間レーンの一時占有）--------------------------------------

        private BgmDirector AttachBgm(Rig rig)
        {
            var go = new GameObject("Bgm"); _spawned.Add(go);
            var bgm = go.AddComponent<BgmDirector>();
            Invoke(bgm, "Awake");
            SetField(rig.Runner, "bgmDirector", bgm);
            return bgm;
        }

        private static ShowBgmDef Bgm(string action, string trackId = "")
            => new() { action = action, trackId = trackId };

        private static object? Field(object target, string name)
            => target.GetType().GetField(name, BF)!.GetValue(target);

        [Test]
        public void TakeWithoutBgm_LeavesTheSegmentTrackPlaying()
        {
            Rig rig = MakeRig();
            BgmDirector bgm = AttachBgm(rig);
            bgm.ApplySegment(Bgm(BgmPlanLogic.ActionPlay, "amb"), present: true);
            rig.Timeline.SetTimeline(new[] { SegWithTake(1, 0, EnterTake("t", LiveStep(3, 1f))) });

            EnterZone(rig, 0, 1);
            Frame(rig);
            Assert.That(Field(bgm, "_takeOverrideActive"), Is.False, "指示の無い演出は音を占有しない");
            Assert.That(Field(bgm, "_laneTrackId"), Is.EqualTo("amb"), "レーン（区間の曲）はそのまま");
        }

        [Test]
        public void TakeBgm_OccupiesDuringTake_AndReleasesAtEnd()
        {
            Rig rig = MakeRig();
            BgmDirector bgm = AttachBgm(rig);
            bgm.ApplySegment(Bgm(BgmPlanLogic.ActionPlay, "amb"), present: true);

            ShowTakeDef take = EnterTake("scare", LiveStep(3, 1f));
            take.bgm = Bgm(BgmPlanLogic.ActionPlay, "scare");
            take.hasBgm = true;
            rig.Timeline.SetTimeline(new[] { SegWithTake(1, 0, take) });

            EnterZone(rig, 0, 1);
            Frame(rig);
            Assert.That(Field(bgm, "_takeOverrideActive"), Is.True, "演出が音を占有する");
            Assert.That(Field(rig.Runner, "_bgmOverrideActive"), Is.True);

            _now = 2f;
            Frame(rig);
            Assert.That(rig.Runner.IsActive, Is.False);
            Assert.That(Field(bgm, "_takeOverrideActive"), Is.False, "演出が終われば占有は必ず解ける");
            Assert.That(Field(bgm, "_laneTrackId"), Is.EqualTo("amb"), "戻り先はレーン（区間の曲）");
        }

        [Test]
        public void SegmentBgmDuringTake_BecomesTheRestoreTarget()
        {
            // 演出中に体験者が次のゾーンへ入った → 音は演出のまま、戻り先だけ新しい区間の曲になる
            //（画面の「戻り先は再計算」と対称）。
            Rig rig = MakeRig();
            BgmDirector bgm = AttachBgm(rig);
            bgm.ApplySegment(Bgm(BgmPlanLogic.ActionPlay, "amb"), present: true);

            ShowTakeDef take = EnterTake("scare", LiveStep(3, 4f));
            take.bgm = Bgm(BgmPlanLogic.ActionPlay, "scare");
            take.hasBgm = true;
            rig.Timeline.SetTimeline(new[] { SegWithTake(1, 0, take) });

            EnterZone(rig, 0, 1);
            Frame(rig);
            bgm.ApplySegment(Bgm(BgmPlanLogic.ActionPlay, "next"), present: true);   // 次の区間の指示
            Assert.That(Field(bgm, "_takeOverrideActive"), Is.True, "占有は続く（音は演出のまま）");
            Assert.That(Field(bgm, "_laneTrackId"), Is.EqualTo("next"), "戻り先だけ更新される");

            _now = 5f;
            Frame(rig);
            Assert.That(Field(bgm, "_takeOverrideActive"), Is.False);
            Assert.That(Field(bgm, "_laneTrackId"), Is.EqualTo("next"));
        }

        [Test]
        public void LiveDeskIntervention_ReleasesTakeBgm()
        {
            // ライブ卓の介入で演出が畳まれる時、音の占有も一緒に解ける（凍結ストランドを残さない）。
            Rig rig = MakeRig();
            BgmDirector bgm = AttachBgm(rig);
            bgm.ApplySegment(Bgm(BgmPlanLogic.ActionPlay, "amb"), present: true);

            ShowTakeDef take = EnterTake("scare", LiveStep(3, 10f));
            take.bgm = Bgm(BgmPlanLogic.ActionStop);
            take.hasBgm = true;
            rig.Timeline.SetTimeline(new[] { SegWithTake(1, 0, take) });

            EnterZone(rig, 0, 1);
            Frame(rig);
            Assert.That(Field(bgm, "_takeOverrideActive"), Is.True);

            rig.Runner.SetSuppressed(true);
            Assert.That(Field(bgm, "_takeOverrideActive"), Is.False, "介入で音も返る");
            Assert.That(Field(rig.Runner, "_bgmOverrideActive"), Is.False);
        }
    }
}
