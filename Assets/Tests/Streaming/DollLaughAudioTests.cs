#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 人形の笑い声 1 回（カットの <c>dollLaugh</c>・2 周目 A の作り直し）。
    /// 旗の立ったカットが画面を取った頭で <see cref="SoundCue.DollLaughOnce"/> を 1 回鳴らす。2D・劇伴は退かせない。
    /// あわせて、報告でカットを進める（<c>markAdvance</c>）が TakeRunner を通っても演出を畳まないことを見る。
    ///
    /// ⚠ <b>この音の失敗は画にも動画にも出ない。</b> 取り込み忘れ・cue の名前の食い違い・撃つ行の配線漏れのどれが起きても
    /// 「笑わずに進む」だけ。機械で捕まえられるのはここと、走行ログの <c>ev=sfx id=DollLaughOnce</c>（<c>tools/analyze-xp-log.py</c>）だけ。
    /// </summary>
    public sealed class DollLaughAudioTests
    {
        private const string Res = "Sound/sfx_doll_laugh_once";
        private const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

        /// <summary>音源の尺（秒）。<c>bed_doll_one.wav</c> の頭 2.4 秒（<c>tools/ingest-doll-laugh.py</c> の CUT_SEC）。</summary>
        private const float ExpectedSec = 2.4f;

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

        // ------------------------------------------------------------------ 音源

        /// <summary>登録簿と焼いたファイルの名前が一致している（<c>tools/ingest-doll-laugh.py</c> の出力名と対）。</summary>
        [Test]
        public void ResourceName_MatchesTheBakedFile()
        {
            Assert.That(SoundCueLogic.ResourceName(SoundCue.DollLaughOnce), Is.EqualTo("sfx_doll_laugh_once"));
            Assert.That(SoundCueLogic.VariantCount(SoundCue.DollLaughOnce), Is.EqualTo(1));
            Assert.That(SoundCueLogic.DuckFor(SoundCue.DollLaughOnce), Is.EqualTo(0f), "劇伴は退かせない");
        }

        /// <summary>⚠ 焼いた後に <c>tools/unity.ps1 menu sound-import</c> を通していないと Resources から引けない。</summary>
        [Test]
        public void Clip_IsInResources()
        {
            var clip = Resources.Load<AudioClip>(Res);
            Assert.IsNotNull(clip,
                             $"{Res} が無い — `py -3.11 tools/ingest-doll-laugh.py` の後に "
                             + "`tools/unity.ps1 menu sound-import` を走らせること");
        }

        /// <summary>頭 2.4 秒の切り出しで、2D（3D の名簿に入れない）。</summary>
        [Test]
        public void Clip_IsTheCutOfTheSource_AndIs2D()
        {
            var clip = Resources.Load<AudioClip>(Res);
            if (clip == null) Assert.Ignore("音源が取り込まれていない（Clip_IsInResources を先に見る）");
            Assert.That(clip!.length, Is.EqualTo(ExpectedSec).Within(0.02f), "尺が変わっている — 切り出しの手順どおりでない");
            Assert.That(clip.frequency, Is.EqualTo(48000));
            Assert.That(Array.IndexOf(SpatialAudio.MonoRequired, "sfx_doll_laugh_once"), Is.EqualTo(-1),
                        "3D の名簿に入れない（出どころを作らない）");
        }

        /// <summary>カットの旗は卓 ⇄ 実機で往復し、キーが無ければ鳴らさない。</summary>
        [Test]
        public void Flag_SurvivesTheWire()
        {
            var step = JsonUtility.FromJson<ShowStepDef>("{\"source\":\"plate\",\"cueId\":\"x\",\"dollLaugh\":true}");
            Assert.That(step.dollLaugh, Is.True);
            Assert.That(JsonUtility.FromJson<ShowStepDef>("{\"source\":\"plate\"}").dollLaugh, Is.False);
        }

        // ------------------------------------------------------------------ TakeRunner が撃つ縁

        /// <summary>旗の立ったカットの頭で 1 回だけ鳴り、他のカットでは鳴らない。</summary>
        [Test]
        public void LaughCut_FiresOnceAtItsHead()
        {
            Rig rig = MakeRig();
            rig.Timeline.SetTimeline(new[]
            {
                Segment(EnterTake("laugh",
                    LiveStep(1f),
                    LiveStep(1f, dollLaugh: true),
                    LiveStep(1f))),
            });

            EnterZone(rig, 0);
            Frame(rig);
            Assert.That(rig.Runner.DollLaughCount, Is.Zero, "前のカットでは鳴らさない");

            _now = 1f;
            int spots = rig.Sound.SpotCount;
            Frame(rig);
            Assert.That(rig.Runner.DollLaughCount, Is.EqualTo(1), "旗のカットの頭で鳴る");
            if (Resources.Load<AudioClip>(Res) != null)
            {
                Assert.That(rig.Sound.SpotCount, Is.EqualTo(spots + 1), "一撃の累計が動いていない");
                Assert.That(rig.Sound.LastCue, Is.EqualTo(SoundCue.DollLaughOnce), "走行ログの `ev=sfx id=` がこの値を出す");
            }

            _now = 1.5f;
            Frame(rig);
            _now = 2f;
            Frame(rig);
            Assert.That(rig.Runner.DollLaughCount, Is.EqualTo(1), "同じカットの中でも次のカットでも重ねない");

            rig.Timeline.ResetRun();
            Assert.That(rig.Runner.DollLaughCount, Is.Zero, "ラン開始で数え直す");
        }

        /// <summary>素材が解決できず飛ばしたカットでは鳴らさない（画に出ていない笑い声を出さない）。</summary>
        [Test]
        public void SkippedLaughCut_DoesNotFire()
        {
            Rig rig = MakeRig();
            rig.Timeline.SetTimeline(new[]
            {
                Segment(EnterTake("skip", LiveStep(1f, dollLaugh: true, camera: 99))),
            });
            LogAssert.Expect(LogType.Warning,
                "[TakeRunner] live のカメラ 99 が範囲 [0,4) 外 → このカットを飛ばす（take=skip step=0）");
            EnterZone(rig, 0);
            Frame(rig);
            Frame(rig);
            Assert.That(rig.Runner.DollLaughCount, Is.Zero, "画面を取らなかったカットは鳴らさない");
        }

        /// <summary>
        /// 報告でカットを進める（<c>markAdvance</c>）は TakeRunner を通っても演出を畳まず、次のカットが出る。
        /// 数えるのは <see cref="TakeRunner.MarkAdvanceCount"/>（<c>ev=markadv</c>）。
        /// </summary>
        [Test]
        public void MarkAdvance_ThroughTheRunner_MovesToTheNextCut()
        {
            Rig rig = MakeRig();
            ShowStepDef first = LiveStep(30f);
            first.markAdvance = true;
            rig.Timeline.SetTimeline(new[]
            {
                Segment(EnterTake("advance", first, LiveStep(1f, dollLaugh: true), LiveStep(30f))),
            });
            EnterZone(rig, 0);
            Frame(rig);

            _now = 2f;
            Assert.That(rig.Runner.NotifyVisitorMark(), Is.EqualTo(TakeRunnerLogic.MarkResult.Advanced));
            Assert.That(rig.Runner.MarkAdvanceCount, Is.EqualTo(1));
            Assert.That(rig.Runner.LastMarkAdvanceTakeId, Is.EqualTo("advance"));
            Assert.That(rig.Runner.LastMarkAdvanceStep, Is.EqualTo(0));

            Frame(rig);
            Assert.That(rig.Runner.IsActive, Is.True, "演出は畳まない");
            Assert.That(rig.Runner.DollLaughCount, Is.EqualTo(1), "進めた先のカットの頭で笑う");

            rig.Timeline.ResetRun();
            Assert.That(rig.Runner.MarkAdvanceCount, Is.Zero, "ラン開始で数え直す");
            Assert.That(rig.Runner.LastMarkAdvanceStep, Is.EqualTo(-1));
        }

        // ------------------------------------------------------------------ rig（KidsRoomAudioTests と同じ組み方）

        private sealed class Rig
        {
            public CameraSwitchDirector Director = null!;
            public TakeRunner Runner = null!;
            public TimelineDirector Timeline = null!;
            public CueScheduler Scheduler = null!;
            public ShowSoundDirector Sound = null!;
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

            var soundGo = new GameObject("[Sound]");
            _spawned.Add(soundGo);
            var sfx = soundGo.AddComponent<SfxPlayer>();
            Invoke(sfx, "Awake");
            var sound = soundGo.AddComponent<ShowSoundDirector>();
            Invoke(sound, "Awake");

            var scheduler = host.AddComponent<CueScheduler>();
            var runner = host.AddComponent<TakeRunner>();
            var timeline = host.AddComponent<TimelineDirector>();
            SetField(runner, "director", director);
            SetField(runner, "overlay", null);
            SetField(runner, "showControl", null);
            SetField(runner, "_sound", sound);
            runner.SetTimeSource(() => _now);
            runner.SetCueResolver(id => new OverlayCueData { id = id });
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
                Sound = sound,
            };
        }

        private static ShowStepDef LiveStep(float durSec, bool dollLaugh = false, int camera = 1) => new()
        {
            source = TakeSchema.SourceLive,
            camera = camera,
            cueId = "",
            durKind = TakeSchema.DurSec,
            durSec = durSec,
            transition = TakeSchema.TransCut,
            dollLaugh = dollLaugh,
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
            => rig.Scheduler.NotifyCameraEntered(new SegmentEntry(camera, segmentLap: 1, progressLap: 1, forward: false));

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
