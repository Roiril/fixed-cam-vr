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
    /// 2 周目 B の別の場所（子供部屋の動画）の音（<c>canon/LEDGER.md</c> 0374）。
    /// 動画のカット（<see cref="TakeRunner.KidsRoomCueId"/>）が画面を取った頭で 1 回鳴り、
    /// 別の場所が途中で畳まれたら 0.2 秒で退く。自然に静止画のカットへ進んだときは鳴り切る。
    ///
    /// ⚠⚠ <b>この音の失敗は画にも動画にも出ない。</b> 動画は音を出さずに再生しているので、
    /// 取り込み忘れ・cue の名前の食い違い・撃つ行の配線漏れのどれが起きても「動画が無音で進む」だけ。
    /// 機械で捕まえられるのはここと、走行ログの <c>ev=sfx id=KidsRoom</c>（<c>tools/analyze-xp-log.py</c>）だけ。
    /// </summary>
    public sealed class KidsRoomAudioTests
    {
        private const string Res = "Sound/sfx_kidsroom";
        private const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

        /// <summary>動画の音の尺（秒）。AAC の 6.037 秒をそのまま PCM にしてある。</summary>
        private const float ExpectedSec = 6.037f;

        /// <summary>動画のカットの尺（<c>show.json</c> の <c>backrooms_B_in</c> の <c>durSec</c>）。</summary>
        private const float CutSec = 5.9f;

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

        /// <summary>
        /// 登録簿と焼いたファイルの名前が一致している。
        /// ⚠ <c>SoundCueLogic.ResourceName</c> と <c>tools/ingest-kidsroom-audio.py</c> の <c>TARGET</c> は対。
        /// </summary>
        [Test]
        public void ResourceName_MatchesTheBakedFile()
        {
            Assert.That(SoundCueLogic.ResourceName(SoundCue.KidsRoom), Is.EqualTo("sfx_kidsroom"));
            Assert.That(SoundCueLogic.VariantCount(SoundCue.KidsRoom), Is.EqualTo(1));
            Assert.That(SoundCueLogic.DuckFor(SoundCue.KidsRoom), Is.EqualTo(0f),
                        "劇伴は別の場所のあいだ風へ入れ替わっている（0131）。一撃の退きは掛けない");
        }

        /// <summary>
        /// ⚠ 焼いた後に <c>tools/unity.ps1 menu sound-import</c> を通していないと Resources から引けない。
        /// </summary>
        [Test]
        public void Clip_IsInResources()
        {
            var clip = Resources.Load<AudioClip>(Res);
            Assert.IsNotNull(clip,
                             $"{Res} が無い — `py -3.11 tools/ingest-kidsroom-audio.py` の後に "
                             + "`tools/unity.ps1 menu sound-import` を走らせること");
        }

        /// <summary>
        /// <b>動画の音そのまま</b>。尺が縮んでいない（無音落としや切り出しが入っていない）・48kHz・ステレオ。
        /// ユーザーが Flow で聴いた 6 秒の音を採用している（0374）ので、ここが縮むと別の音になる。
        /// </summary>
        [Test]
        public void Clip_IsTheWholeVideoSound()
        {
            var clip = Resources.Load<AudioClip>(Res);
            if (clip == null) Assert.Ignore("音源が取り込まれていない（Clip_IsInResources を先に見る）");
            Assert.That(clip!.length, Is.EqualTo(ExpectedSec).Within(0.02f),
                        "尺が変わっている — 動画の音を無加工で抜いていない");
            Assert.That(clip.length, Is.GreaterThan(CutSec), "動画のカットより短い音は動画の尻で黙る");
            Assert.That(clip.frequency, Is.EqualTo(48000), "48kHz で焼いてある（PreserveSampleRate）");
            Assert.That(clip.channels, Is.EqualTo(2), "2D の音なのでステレオのまま");
            Assert.That(Array.IndexOf(SpatialAudio.MonoRequired, "sfx_kidsroom"), Is.EqualTo(-1),
                        "3D の名簿に入れない（体験者の周りに出どころを作らない）");
        }

        /// <summary>
        /// cue id は別の場所の頭を持つ（風と時計の ? もこのカットで立つ）。名前の判定は完全一致。
        /// </summary>
        [Test]
        public void CueId_IsAnOtherworldCue_AndMatchedExactly()
        {
            Assert.That(TakeRunner.IsOtherworldCue(TakeRunner.KidsRoomCueId), Is.True);
            Assert.That(TakeRunner.IsKidsRoomCue("backrooms_B_in"), Is.True);
            Assert.That(TakeRunner.IsKidsRoomCue("backrooms_B"), Is.False, "2 カット目の静止画では鳴らさない");
            Assert.That(TakeRunner.IsKidsRoomCue(""), Is.False);
            Assert.That(TakeRunner.IsKidsRoomCue(null), Is.False);
            Assert.That(TakeRunner.KidsRoomStopFadeSec, Is.EqualTo(0.2f).Within(1e-6f));
        }

        /// <summary>
        /// 鳴らす目印は cue id で、カットの欄を足していない。卓 ⇄ 実機で cue id が往復することだけ見る
        /// （<c>DollCallAudioTests.DollCall_SurvivesTheWire</c> の対）。
        /// </summary>
        [Test]
        public void CueId_SurvivesTheWire()
        {
            const string json = "{\"source\":\"plate\",\"cueId\":\"backrooms_B_in\",\"durSec\":5.9,"
                                + "\"transition\":\"glitch\",\"switchSfx\":true}";
            var step = JsonUtility.FromJson<ShowStepDef>(json);
            Assert.IsNotNull(step);
            Assert.That(TakeRunner.IsKidsRoomCue(step!.cueId), Is.True);
        }

        // ------------------------------------------------------------------ 鳴らす口と止める口

        /// <summary><c>PlaySpot</c> で鳴り、<c>StopSpot</c> で子供部屋の声だけが退く（他の一撃は残る）。</summary>
        [Test]
        public void PlaySpot_FiresAndStopSpot_FadesOnlyThisSound()
        {
            var go = new GameObject("kidsroom-sound-test");
            _spawned.Add(go);
            var sfx = go.AddComponent<SfxPlayer>();
            Invoke(sfx, "Awake");
            var dir = go.AddComponent<ShowSoundDirector>();
            Invoke(dir, "Awake");
            if (Resources.Load<AudioClip>(Res) == null)
                Assert.Ignore("音源が取り込まれていない（Clip_IsInResources を先に見る）");

            var other = AudioClip.Create("other", 48000 * 3, 1, 48000, false);
            _spawned.Add(other);
            Assert.IsTrue(sfx.Play(other, pitchSpread: 0f, gainSpreadDb: 0f));

            int before = dir.SpotCount;
            Assert.IsTrue(dir.PlaySpot(SoundCue.KidsRoom), "子供部屋の音を鳴らせなかった");
            Assert.That(dir.SpotCount, Is.EqualTo(before + 1), "一撃の累計が動いていない");
            Assert.That(dir.LastCue, Is.EqualTo(SoundCue.KidsRoom), "走行ログの `ev=sfx id=` がこの値を出す");

            Assert.That(dir.StopSpot(SoundCue.KidsRoom, TakeRunner.KidsRoomStopFadeSec), Is.EqualTo(1));
            Assert.That(sfx.FadingVoices, Is.EqualTo(1), "子供部屋の声だけを退かせる");
            Assert.That(sfx.BusyVoices, Is.EqualTo(2), "退かせ終わるまで声はふさがっている・他の音は残る");
        }

        /// <summary>鳴っていない音を止めても何も起きない。尺 0 は即座に空ける。</summary>
        [Test]
        public void SfxPlayer_FadeOut_IgnoresIdleClips_AndZeroSecStopsAtOnce()
        {
            var go = new GameObject("kidsroom-fade-test");
            _spawned.Add(go);
            var sfx = go.AddComponent<SfxPlayer>();
            Invoke(sfx, "Awake");
            var a = AudioClip.Create("a", 48000 * 3, 1, 48000, false);
            var b = AudioClip.Create("b", 48000 * 3, 1, 48000, false);
            _spawned.Add(a);
            _spawned.Add(b);

            Assert.That(sfx.FadeOut(a, 0.2f), Is.Zero, "鳴っていない");
            Assert.That(sfx.FadeOut(null, 0.2f), Is.Zero);
            sfx.Play(a, pitchSpread: 0f, gainSpreadDb: 0f);
            sfx.Play(b, pitchSpread: 0f, gainSpreadDb: 0f);
            Assert.That(sfx.FadeOut(a, 0f), Is.EqualTo(1));
            Assert.That(sfx.FadingVoices, Is.Zero, "尺 0 は退かせずに止める");
            Assert.That(sfx.BusyVoices, Is.EqualTo(1), "b は残る");

            sfx.Play(a, pitchSpread: 0f, gainSpreadDb: 0f);
            Assert.That(sfx.FadeOut(a, 0.2f), Is.EqualTo(1));
            Assert.That(sfx.FadeOut(a, 0.2f), Is.EqualTo(1), "退かせている途中でも数える（尺は延ばさない）");
            sfx.StopAll();
            Assert.That(sfx.FadingVoices, Is.Zero, "ラン開始で退かせの状態も落とす");
        }

        // ------------------------------------------------------------------ TakeRunner が撃つ縁

        /// <summary>
        /// 動画のカットの頭で 1 回鳴り、自然に静止画のカットへ進み、演出が自然に終わっても止めない。
        /// </summary>
        [Test]
        public void KidsRoomCut_FiresAtItsHead_AndNaturalProgressionLetsItRingOut()
        {
            Rig rig = MakeRig();
            rig.Timeline.SetTimeline(new[]
            {
                Segment(EnterTake("other-place",
                    LiveStep(1f),
                    LiveStep(1f, cueId: TakeRunner.KidsRoomCueId),
                    LiveStep(1f, cueId: "backrooms_B"))),
            });

            EnterZone(rig, 0);
            Frame(rig);
            Assert.That(rig.Runner.KidsRoomSoundCount, Is.Zero, "前のカットでは鳴らさない");

            _now = 1f;
            Frame(rig);
            Assert.That(rig.Runner.KidsRoomSoundCount, Is.EqualTo(1), "動画のカットの頭で鳴る");
            AssertKidsRoomVoices(rig, busy: 1, fading: 0);

            _now = 2f;
            Frame(rig);
            Assert.That(rig.Runner.KidsRoomSoundCount, Is.EqualTo(1), "静止画のカットでは重ねない");
            Assert.That(rig.Runner.KidsRoomStopCount, Is.Zero, "自然に次のカットへ進んだら止めない");

            _now = 3f;
            Frame(rig);
            Assert.That(rig.Runner.IsActive, Is.False, "演出は自然に終わった");
            Assert.That(rig.Runner.KidsRoomStopCount, Is.Zero, "自然に終わった演出の音は鳴り切る");
            AssertKidsRoomVoices(rig, busy: 1, fading: 0);
        }

        /// <summary>体験者が報告して別の場所が畳まれたら、子供部屋の音を退かせる。</summary>
        [Test]
        public void ReportDismissal_FadesTheKidsRoomSound()
        {
            Rig rig = MakeRig();
            ShowTakeDef take = EnterTake("dismiss", LiveStep(5.9f, cueId: TakeRunner.KidsRoomCueId),
                                         LiveStep(1.625f, cueId: "backrooms_B"));
            take.dismissible = true;
            rig.Timeline.SetTimeline(new[] { Segment(take) });

            EnterZone(rig, 0);
            Frame(rig);
            Assert.That(rig.Runner.KidsRoomSoundCount, Is.EqualTo(1));

            _now = 2f;
            Assert.That(rig.Runner.NotifyVisitorMark(), Is.EqualTo(TakeRunnerLogic.MarkResult.Dismissed));
            Frame(rig);
            Assert.That(rig.Timeline.LastEndReason, Is.EqualTo(TakeRunnerLogic.EndReason.VisitorDismissed));
            Assert.That(rig.Runner.KidsRoomStopCount, Is.EqualTo(1), "報告で畳まれたら退かせる");
            AssertKidsRoomVoices(rig, busy: 1, fading: 1);
        }

        /// <summary>前へ抜けて yield で畳まれても、卓の中止でも退かせる。</summary>
        [Test]
        public void YieldAndAbort_FadeTheKidsRoomSound()
        {
            Rig rig = MakeRig();
            rig.Timeline.SetTimeline(new[]
            {
                Segment(YieldTake("yield", LiveStep(5.9f, cueId: TakeRunner.KidsRoomCueId))),
            });
            EnterZone(rig, 0);
            Frame(rig);
            _now = 1f;
            EnterZone(rig, 1, forward: true);
            Assert.That(rig.Timeline.LastEndReason, Is.EqualTo(TakeRunnerLogic.EndReason.Yielded));
            Assert.That(rig.Runner.KidsRoomStopCount, Is.EqualTo(1), "区間を移って畳まれたら退かせる");
            AssertKidsRoomVoices(rig, busy: 1, fading: 1);

            _now = 0f;
            Rig aborted = MakeRig();
            aborted.Timeline.SetTimeline(new[]
            {
                Segment(EnterTake("abort", LiveStep(5.9f, cueId: TakeRunner.KidsRoomCueId))),
            });
            EnterZone(aborted, 0);
            Frame(aborted);
            aborted.Timeline.AbortActive();
            Assert.That(aborted.Runner.KidsRoomStopCount, Is.EqualTo(1), "卓の中止でも退かせる");
            AssertKidsRoomVoices(aborted, busy: 1, fading: 1);

            aborted.Timeline.ResetRun();
            Assert.That(aborted.Runner.KidsRoomSoundCount, Is.Zero, "ラン開始で数え直す");
            Assert.That(aborted.Runner.KidsRoomStopCount, Is.Zero);
        }

        /// <summary>素材が解決できず飛ばしたカットでは鳴らさない（画に出ていない別の場所の音を出さない）。</summary>
        [Test]
        public void SkippedKidsRoomCut_DoesNotFire()
        {
            Rig rig = MakeRig();
            rig.Timeline.SetTimeline(new[]
            {
                Segment(EnterTake("skip", LiveStep(5.9f, cueId: TakeRunner.KidsRoomCueId, camera: 99))),
            });
            LogAssert.Expect(LogType.Warning,
                "[TakeRunner] live のカメラ 99 が範囲 [0,4) 外 → このカットを飛ばす（take=skip step=0）");
            EnterZone(rig, 0);
            Frame(rig);
            Frame(rig);
            Assert.That(rig.Runner.KidsRoomSoundCount, Is.Zero, "画面を取らなかったカットは鳴らさない");
            AssertKidsRoomVoices(rig, busy: 0, fading: 0);
        }

        // ------------------------------------------------------------------ rig（CommsCatchUpCompletionTests と同じ組み方）

        private sealed class Rig
        {
            public CameraSwitchDirector Director = null!;
            public TakeRunner Runner = null!;
            public TimelineDirector Timeline = null!;
            public CueScheduler Scheduler = null!;
            public SfxPlayer Sfx = null!;
        }

        private void AssertKidsRoomVoices(Rig rig, int busy, int fading)
        {
            // 音源が無い構成では声の数は見られない（撃った回数・止めた回数は上で見ている）。
            if (Resources.Load<AudioClip>(Res) == null) return;
            Assert.That(rig.Sfx.BusyVoices, Is.EqualTo(busy), "鳴っている声の数");
            Assert.That(rig.Sfx.FadingVoices, Is.EqualTo(fading), "退かせている声の数");
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
            // 動画のカットの cue id を引けるようにする（引けないと「cue 未解決」の警告が出るだけで画は live のまま）。
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
                Sfx = sfx,
            };
        }

        private static ShowStepDef LiveStep(float durSec, string cueId = "", int camera = 1) => new()
        {
            source = TakeSchema.SourceLive,
            camera = camera,
            cueId = cueId,
            durKind = TakeSchema.DurSec,
            durSec = durSec,
            transition = TakeSchema.TransCut,
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
