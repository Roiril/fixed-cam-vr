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
    /// 左右分割（<c>splitX</c> / <c>splitFlip</c> / <c>splitFreeze</c>）と
    /// 第 2 の差し替え層（<c>overlay2CueId</c>）の配線テスト（canon/LEDGER.md 0050）。
    ///
    /// <b>固定しているのは「カットごとに書き、演出が終わったら必ず畳む」の 1 点。</b>
    /// 分割はカットの中でしか書かれないので、畳む経路が 1 つでも抜けると
    /// <b>画が割れたまま・左半分が凍ったまま次の体験者へ持ち越される</b>。
    /// 初版は畳む経路が中止（<c>CleanupActive</c>）だけにあり、**正常終了では必ず残っていた**。
    ///
    /// EditMode は Time.time が進まないので TakeRunner の時刻源を差し替える（TakeWiringTests と同じ流儀）。
    /// </summary>
    public sealed class TakeSplitLayerTests
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

        private sealed class Rig
        {
            public CameraSwitchDirector Director = null!;
            public TakeRunner Runner = null!;
            public TimelineDirector Timeline = null!;
            public CueScheduler Scheduler = null!;
            public CameraFeelFx Feel = null!;
            public ScreenOverlayController Overlay = null!;
        }

        // 分割の書き先（CameraFeelFx）と第 2 層の読み手（ScreenOverlayController）まで含めて組む。
        //
        // ⚠⚠ **スクリーン側は director と別の GameObject に置く。** EditMode の
        //    `renderer.material` は「シーンへマテリアルを漏らす」エラーを出し、テストランナーが
        //    未宣言のエラーログでテストを落とす。ScreenOverlayController は
        //    RequireComponent(Renderer) なので MeshRenderer が要るが、**同じ GameObject に
        //    CameraSwitchDirector を載せると、その Awake が material を掴みに行って踏む**。
        // ⚠ どちらも Awake は呼ばない（同じ理由）。material が無くても SplitX / Overlay2Strength は
        //    「書こうとした値」として立つので、配線の検査はこれで足りる。
        private Rig MakeRig(int cameraCount = 4, int active = 0)
        {
            var reg = MakeRegistry(cameraCount, active);
            var host = new GameObject("Show"); _spawned.Add(host);

            var screenGo = new GameObject("Screen"); _spawned.Add(screenGo);
            screenGo.AddComponent<MeshRenderer>();   // ScreenOverlayController の RequireComponent(Renderer)
            var feel = screenGo.AddComponent<CameraFeelFx>();
            var overlay = screenGo.AddComponent<ScreenOverlayController>();

            var dir = host.AddComponent<CameraSwitchDirector>();
            SetField(dir, "registry", reg);
            SetField(dir, "feelFx", feel);
            SetField(dir, "dipDownSec", 0f);
            SetField(dir, "dipUpSec", 0f);
            SetField(dir, "switchCooldownSec", 0f);
            SetField(dir, "minDwellSec", 0f);
            dir.SetDeltaSource(() => 1f);
            Invoke(dir, "Awake");
            Invoke(dir, "OnEnable");

            var scheduler = host.AddComponent<CueScheduler>();
            var runner = host.AddComponent<TakeRunner>();
            var timeline = host.AddComponent<TimelineDirector>();

            SetField(runner, "director", dir);
            SetField(runner, "overlay", overlay);
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
                Director = dir, Runner = runner, Timeline = timeline,
                Scheduler = scheduler, Feel = feel, Overlay = overlay,
            };
        }

        private static ShowStepDef LiveStep(int camera, float durSec) => new()
        {
            source = TakeSchema.SourceLive, camera = camera,
            durKind = TakeSchema.DurSec, durSec = durSec,
            transition = TakeSchema.TransCut,
        };

        private static ShowTakeDef EnterTake(string id, params ShowStepDef[] steps) => new()
        {
            id = id, at = TakeSchema.AtEnter, offsetSec = 0f,
            ifMissed = TakeSchema.MissedFireOnExit, policy = TakeSchema.PolicyHold,
            once = true, maxDurationSec = 0f, steps = steps,
        };

        private static ShowTimelineSegmentDef SegWithTake(int lap, int camera, ShowTakeDef take) => new()
        {
            lap = lap, camera = camera,
            takes = new[] { take },
            cues = Array.Empty<ShowSegmentCueDef>(),
        };

        private static void Frame(Rig rig)
        {
            Invoke(rig.Runner, "Update");
            for (int i = 0; i < 4; i++) Invoke(rig.Director, "Update");
        }

        private Texture2D MakeTex()
        {
            var t = new Texture2D(4, 4);
            _spawned.Add(t);
            return t;
        }

        // ---- 左右分割 ----

        [Test]
        public void SplitStep_WritesSplitPositionToFeelFx()
        {
            Rig rig = MakeRig();
            ShowStepDef step = LiveStep(0, 5f);
            step.splitX = 0.5f;
            step.splitFlip = true;
            rig.Timeline.SetTimeline(new[] { SegWithTake(1, 0, EnterTake("lap3A", step)) });

            rig.Scheduler.NotifyCameraEntered(new SegmentEntry(0, segmentLap: 1, progressLap: 1));
            Frame(rig);

            Assert.That(rig.Feel.SplitX, Is.EqualTo(0.5f).Within(1e-4f), "カットの splitX が画へ渡る");
        }

        /// <summary>
        /// <b>これが本題。</b> 分割はカットの中でしか書かれないので、正常終了で畳まないと
        /// 次の体験者まで画が割れたまま残る。初版はここが抜けていた（畳む経路が中止だけ）。
        /// </summary>
        [Test]
        public void Split_IsFolded_WhenTakeEndsNormally()
        {
            Rig rig = MakeRig();
            ShowStepDef step = LiveStep(0, 1f);
            step.splitX = 0.5f;
            rig.Timeline.SetTimeline(new[] { SegWithTake(1, 0, EnterTake("lap3A", step)) });

            rig.Scheduler.NotifyCameraEntered(new SegmentEntry(0, segmentLap: 1, progressLap: 1));
            Frame(rig);
            Assert.That(rig.Feel.SplitX, Is.EqualTo(0.5f).Within(1e-4f));

            _now = 1f;
            Frame(rig);
            Assert.That(rig.Runner.IsActive, Is.False, "尺を使い切って演出は終わる");
            Assert.That(rig.Feel.SplitX, Is.EqualTo(0f),
                "演出が正常に終わったら分割は畳まれる（残すと次の体験者へ持ち越される）");
        }

        [Test]
        public void Split_IsFolded_WhenTakeIsAborted()
        {
            Rig rig = MakeRig();
            ShowStepDef step = LiveStep(0, 30f);
            step.splitX = 0.5f;
            rig.Timeline.SetTimeline(new[] { SegWithTake(1, 0, EnterTake("lap3A", step)) });

            rig.Scheduler.NotifyCameraEntered(new SegmentEntry(0, segmentLap: 1, progressLap: 1));
            Frame(rig);
            Assert.That(rig.Feel.SplitX, Is.EqualTo(0.5f).Within(1e-4f));

            rig.Runner.AbortActive();
            Assert.That(rig.Feel.SplitX, Is.EqualTo(0f), "卓からの緊急停止でも分割は畳まれる");
        }

        [Test]
        public void NextStepWithoutSplit_ClearsPreviousSplit()
        {
            Rig rig = MakeRig();
            ShowStepDef a = LiveStep(0, 1f);
            a.splitX = 0.5f;
            ShowStepDef b = LiveStep(1, 5f);   // 分割を指定していないカット
            rig.Timeline.SetTimeline(new[] { SegWithTake(1, 0, EnterTake("lap3A", a, b)) });

            rig.Scheduler.NotifyCameraEntered(new SegmentEntry(0, segmentLap: 1, progressLap: 1));
            Frame(rig);
            Assert.That(rig.Feel.SplitX, Is.EqualTo(0.5f).Within(1e-4f));

            _now = 1f;
            Frame(rig);
            Assert.That(rig.Runner.IsActive, Is.True, "2 カット目が走っている");
            Assert.That(rig.Feel.SplitX, Is.EqualTo(0f),
                "分割はカットごとに毎回書く（指定していないカットへ引き継がない）");
        }

        // ---- 第 2 の差し替え層 ----

        [Test]
        public void SecondLayer_StillImage_ReachesFeelFx()
        {
            Rig rig = MakeRig();
            var cue = new OverlayCueData
            {
                id = "gen_dolls", displayName = "大量の人形",
                stillImage = MakeTex(), maskTexture = MakeTex(), strength = 1f,
            };
            rig.Runner.SetCueResolver(id => id == "gen_dolls" ? cue : null);

            ShowStepDef step = LiveStep(0, 5f);
            step.splitX = 0.5f;
            step.overlay2CueId = "gen_dolls";
            rig.Timeline.SetTimeline(new[] { SegWithTake(1, 0, EnterTake("lap4A", step)) });

            rig.Scheduler.NotifyCameraEntered(new SegmentEntry(0, segmentLap: 1, progressLap: 1));
            Frame(rig);

            Assert.That(rig.Feel.Overlay2Strength, Is.EqualTo(1f).Within(1e-4f),
                "ローカル素材はロードを挟まないので、そのカットのうちに載る");
        }

        [Test]
        public void SecondLayer_IsFolded_WhenTakeEndsNormally()
        {
            Rig rig = MakeRig();
            var cue = new OverlayCueData
            {
                id = "gen_dolls", stillImage = MakeTex(), maskTexture = MakeTex(), strength = 1f,
            };
            rig.Runner.SetCueResolver(id => cue);

            ShowStepDef step = LiveStep(0, 1f);
            step.overlay2CueId = "gen_dolls";
            rig.Timeline.SetTimeline(new[] { SegWithTake(1, 0, EnterTake("lap4A", step)) });

            rig.Scheduler.NotifyCameraEntered(new SegmentEntry(0, segmentLap: 1, progressLap: 1));
            Frame(rig);
            Assert.That(rig.Feel.Overlay2Strength, Is.EqualTo(1f).Within(1e-4f));

            _now = 1f;
            Frame(rig);
            Assert.That(rig.Feel.Overlay2Strength, Is.EqualTo(0f),
                "演出が終わったら第 2 層も畳む（残すと live の上に生成画像が貼られたままになる）");
        }

        [Test]
        public void SecondLayer_NotRequestedByStep_StaysOff()
        {
            Rig rig = MakeRig();
            rig.Timeline.SetTimeline(new[] { SegWithTake(1, 0, EnterTake("plain", LiveStep(0, 5f))) });

            rig.Scheduler.NotifyCameraEntered(new SegmentEntry(0, segmentLap: 1, progressLap: 1));
            Frame(rig);

            Assert.That(rig.Feel.Overlay2Strength, Is.EqualTo(0f));
        }

        /// <summary>
        /// 第 2 層は静止画専用（載るのは無人プレートと生成画像だけ）。動画を指したら出さない。
        /// 黙って 1 層目と同じ絵を出すより、出ない方が原因に届く。
        /// </summary>
        [Test]
        public void SecondLayer_VideoCue_IsRefused()
        {
            Rig rig = MakeRig();
            var cue = new OverlayCueData { id = "mov", sourceUrl = "http://x/a.mp4", maskTexture = MakeTex() };

            rig.Overlay.ShowSecondLayer(cue);

            Assert.That(rig.Feel.Overlay2Strength, Is.EqualTo(0f), "動画は第 2 層に載らない");
        }

        [Test]
        public void SecondLayer_Clear_ZeroesStrength()
        {
            Rig rig = MakeRig();
            var cue = new OverlayCueData
            {
                id = "still", stillImage = MakeTex(), maskTexture = MakeTex(), strength = 1f,
            };

            rig.Overlay.ShowSecondLayer(cue);
            Assert.That(rig.Feel.Overlay2Strength, Is.EqualTo(1f).Within(1e-4f));

            rig.Overlay.ClearSecondLayer();
            Assert.That(rig.Feel.Overlay2Strength, Is.EqualTo(0f));
        }

        /// <summary>
        /// 素材が 1 つも無い cue は載せない（load 経路を持たないので黙って空テクスチャを貼らない）。
        /// </summary>
        [Test]
        public void SecondLayer_CueWithoutSource_StaysOff()
        {
            Rig rig = MakeRig();
            rig.Overlay.ShowSecondLayer(new OverlayCueData { id = "empty", maskTexture = MakeTex() });
            Assert.That(rig.Feel.Overlay2Strength, Is.EqualTo(0f));
        }

        // ---- 長押しで布が膨らむ（0349）-------------------------------------------------
        //
        // markStartCueId の cue が pressMaskUrl を持つ演出では、押し始めで画を差し替えず、
        // 長押しの進み具合を膨らみ（_OverlayPress）へ渡す。成立したら膨らみを畳み、
        // markDoneCueId の画を第 2 層に出し、markDoneSec 後に演出を畳む。離したら膨らみだけ戻る。

        private const string PressMaskUrl = "test://press_mask";

        // 膨らみの素材を読み込みなしで貼れるようにする。press マスクは URL の在庫へ先に入れ、
        // 書き先のマテリアルは Awake を呼ばずに直接渡す（renderer.material を EditMode で掴まない）。
        private void PreparePressOverlay(Rig rig)
        {
            var mat = new Material(Shader.Find("Hidden/InternalErrorShader"));
            _spawned.Add(mat);
            SetField(rig.Overlay, "_material", mat);
            FieldInfo cacheF = typeof(ScreenOverlayController).GetField("_urlTextureCache", BF)!;
            var cache = (Dictionary<string, Texture2D>)cacheF.GetValue(rig.Overlay)!;
            cache[PressMaskUrl] = MakeTex();
        }

        private static void TickPress(Rig rig, float sec, float dt = 1f / 72f)
        {
            MethodInfo tick = typeof(ScreenOverlayController).GetMethod("TickPress", BF)!;
            for (float t = 0f; t < sec - 1e-5f; t += dt) tick.Invoke(rig.Overlay, new object[] { dt });
        }

        private ShowTakeDef PressTake(float markDoneSec = 0.4f)
        {
            ShowTakeDef take = EnterTake("L2C0#0", LiveStep(0, 30f));
            take.dismissible = true;
            take.markStartCueId = "press_A";
            take.markDoneCueId = "done_A";
            take.markDoneSec = markDoneSec;
            return take;
        }

        private OverlayCueData? PressCues(string id) => id switch
        {
            "press_A" => new OverlayCueData
            {
                id = "press_A", displayName = "膨らみ", stillImage = MakeTex(), pressMaskUrl = PressMaskUrl,
                pressCenter = new Vector2(0.35f, 0.74f), strength = 1f,
            },
            "done_A" => new OverlayCueData
            {
                id = "done_A", displayName = "人形", stillImage = MakeTex(), maskTexture = MakeTex(), strength = 1f,
            },
            _ => null,
        };

        [Test]
        public void PressTake_BindsTheBulge_AndDoesNotSwapTheScreen()
        {
            Rig rig = MakeRig();
            PreparePressOverlay(rig);
            rig.Runner.SetCueResolver(PressCues);
            rig.Timeline.SetTimeline(new[] { SegWithTake(2, 0, PressTake()) });

            rig.Scheduler.NotifyCameraEntered(new SegmentEntry(0, segmentLap: 2, progressLap: 2));
            Frame(rig);
            Assert.That(rig.Overlay.PressBound, Is.True, "演出のカットが画面を取った縁で膨らみを貼る");

            rig.Timeline.NotifyVisitorMarkStarted();
            Assert.That(rig.Overlay.Current?.id, Is.Not.EqualTo("press_A"), "押し始めで画を差し替えない");

            for (int i = 1; i <= 72; i++)
            {
                rig.Timeline.SetVisitorMarkProgress(i / 72f);
                TickPress(rig, 1f / 72f);
            }
            Assert.That(rig.Overlay.PressMax, Is.GreaterThanOrEqualTo(0.6f), "1 秒の長押しで膨らむ");
        }

        [Test]
        public void PressTake_ReleaseBeforeTheMark_SpringsBack_AndTheTakeStays()
        {
            Rig rig = MakeRig();
            PreparePressOverlay(rig);
            rig.Runner.SetCueResolver(PressCues);
            rig.Timeline.SetTimeline(new[] { SegWithTake(2, 0, PressTake()) });
            rig.Scheduler.NotifyCameraEntered(new SegmentEntry(0, segmentLap: 2, progressLap: 2));
            Frame(rig);

            rig.Timeline.NotifyVisitorMarkStarted();
            rig.Timeline.SetVisitorMarkProgress(0.8f);
            TickPress(rig, 0.8f);
            rig.Timeline.NotifyVisitorMarkEnded();
            Frame(rig);
            float low = 1f;
            for (int i = 0; i < 72; i++)
            {
                rig.Timeline.SetVisitorMarkProgress(0f);
                TickPress(rig, 1f / 72f);
                low = Mathf.Min(low, rig.Overlay.PressShown);
            }
            Assert.That(low, Is.LessThan(0f), "離すと膨らみはバネで戻り、少し引っ込む");
            Assert.That(rig.Runner.IsActive, Is.True, "報告が成立していないので演出（シミ）は残る");
            Assert.That(rig.Overlay.PressBound, Is.True, "次の長押しにまた膨らむ");
        }

        [Test]
        public void PressTake_Mark_CollapsesTheBulge_ShowsTheDollOnLayer2_ThenFolds()
        {
            Rig rig = MakeRig();
            PreparePressOverlay(rig);
            rig.Runner.SetCueResolver(PressCues);
            rig.Timeline.SetTimeline(new[] { SegWithTake(2, 0, PressTake(markDoneSec: 0.4f)) });
            rig.Scheduler.NotifyCameraEntered(new SegmentEntry(0, segmentLap: 2, progressLap: 2));
            Frame(rig);

            rig.Timeline.NotifyVisitorMarkStarted();
            rig.Timeline.SetVisitorMarkProgress(1f);
            TickPress(rig, 1f);
            float before = rig.Overlay.PressShown;
            Assert.That(before, Is.GreaterThan(0.6f));

            Assert.That(rig.Timeline.NotifyVisitorMark(), Is.EqualTo(TakeRunnerLogic.MarkResult.Dismissed));
            Frame(rig);
            Assert.That(rig.Feel.Overlay2Strength, Is.EqualTo(1f).Within(1e-4f), "成立の画は第 2 層に出る");
            Assert.That(rig.Runner.IsActive, Is.True, "成立の画を markDoneSec 見せてから畳む");

            rig.Timeline.SetVisitorMarkProgress(1f);   // 押し続けていても
            TickPress(rig, OverlayPressLogic.CollapseSec + 0.02f);
            Assert.That(rig.Overlay.PressShown, Is.EqualTo(0f), "膨らみは 0.12 秒で畳む");

            _now = 0.4f;
            Frame(rig);
            Assert.That(rig.Runner.IsActive, Is.False, "markDoneSec で演出を畳む");
            Assert.That(rig.Overlay.PressBound, Is.False, "演出が終わったら膨らみを外す");
            Assert.That(rig.Feel.Overlay2Strength, Is.EqualTo(0f), "成立の画も畳む");
        }

        [Test]
        public void TakeWithoutPressMask_KeepsTheOldMarkResponse()
        {
            Rig rig = MakeRig();
            PreparePressOverlay(rig);
            var still = new OverlayCueData { id = "flutter", stillImage = MakeTex(), strength = 1f };
            rig.Runner.SetCueResolver(id => id == "flutter" ? still : null);
            ShowTakeDef take = EnterTake("L2C0#0", LiveStep(0, 30f));
            take.markStartCueId = "flutter";
            rig.Timeline.SetTimeline(new[] { SegWithTake(2, 0, take) });
            rig.Scheduler.NotifyCameraEntered(new SegmentEntry(0, segmentLap: 2, progressLap: 2));
            Frame(rig);

            Assert.That(rig.Overlay.PressBound, Is.False, "pressMaskUrl の無い cue では膨らまない");
            rig.Timeline.NotifyVisitorMarkStarted();
            rig.Timeline.SetVisitorMarkProgress(1f);
            TickPress(rig, 1f);
            Assert.That(rig.Overlay.PressShown, Is.EqualTo(0f), "長押しの進み具合は膨らみへ届かない");
            Assert.That(rig.Overlay.PressMax, Is.EqualTo(0f));
        }
    }
}
