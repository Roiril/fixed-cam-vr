#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using FixedCamVr.Streaming;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// ShowControlClient の設定優先順位（焼き込み &lt; キャッシュ &lt; ライブ後勝ち）・rev 変化検出・
    /// CachedConfig 往復・runEpoch リセット意味論を MonoBehaviour 直接駆動で固定する。PC 不在起動での
    /// ショー継続性を守る核。SwitchWiringTests の reflection 駆動 + 一時 configCacheFileName +
    /// キャッシュファイル掃除の先例を踏襲する（ApplyBaked / LoadAndApplyCache / Apply / SaveCache を reflection で叩く）。
    ///
    /// 並行変更との整合: present-flag 正規化（別担当）には踏み込まず、CachedConfig 往復では「保存済み bool が
    /// 往復後も保存される」ことだけをアサートする。ConnectionKey 等の実装詳細名には依存しない。
    /// </summary>
    public sealed class ShowConfigPrecedenceTests
    {
        private const BindingFlags InstBF = BindingFlags.NonPublic | BindingFlags.Instance;
        private const BindingFlags StaticBF = BindingFlags.NonPublic | BindingFlags.Static;

        private static readonly Type ShowT = typeof(ShowControlClient);
        private static readonly Type StateT = ShowT.GetNestedType("ShowState", BindingFlags.NonPublic)!;
        private static readonly Type CamT = ShowT.GetNestedType("CameraDef", BindingFlags.NonPublic)!;
        private static readonly Type CtrlT = ShowT.GetNestedType("ControlState", BindingFlags.NonPublic)!;
        private static readonly Type CachedT = ShowT.GetNestedType("CachedConfig", BindingFlags.NonPublic)!;
        private static readonly Type CueT = ShowT.GetNestedType("CueDef", BindingFlags.NonPublic)!;

        private readonly List<UnityEngine.Object> _spawned = new();
        private readonly List<string> _cacheFiles = new();

        [TearDown]
        public void Cleanup()
        {
            foreach (var o in _spawned) if (o != null) UnityEngine.Object.DestroyImmediate(o);
            _spawned.Clear();
            foreach (var p in _cacheFiles)
            {
                try { if (File.Exists(p)) File.Delete(p); } catch { }
            }
            _cacheFiles.Clear();
        }

        // ---- reflection ヘルパ ----

        private static object GetF(object o, string name) => o.GetType().GetField(name, InstBF)!.GetValue(o)!;
        private static void SetF(object o, string name, object? v) => o.GetType().GetField(name, InstBF)!.SetValue(o, v);
        private static object? InvokeM(object o, string m, params object?[] args)
            => o.GetType().GetMethod(m, InstBF)!.Invoke(o, args);
        private static string CachePath(ShowControlClient show)
            => (string)ShowT.GetProperty("ConfigCachePath", InstBF)!.GetValue(show)!;

        private ShowControlClient MakeShow()
        {
            var go = new GameObject("Screen");
            _spawned.Add(go);
            var show = go.AddComponent<ShowControlClient>();
            SetF(show, "server", null);
            string fn = "test_show_cfg_" + Guid.NewGuid().ToString("N") + ".json";
            SetF(show, "configCacheFileName", fn);
            _cacheFiles.Add(Path.Combine(Application.persistentDataPath, fn));
            return show;
        }

        // ---- 私設ネスト型のビルダ（フィールドは public なので既定 GetField で足りる）----

        private static object MakeCam(string id, string host = "", int port = 0, string auth = "",
            PostParams? post = null, bool pinned = false, bool hasPost = false)
        {
            object c = Activator.CreateInstance(CamT)!;
            CamT.GetField("id")!.SetValue(c, id);
            CamT.GetField("host")!.SetValue(c, host);
            CamT.GetField("port")!.SetValue(c, port);
            CamT.GetField("auth")!.SetValue(c, auth);
            CamT.GetField("post")!.SetValue(c, post);
            CamT.GetField("pinned")!.SetValue(c, pinned);
            CamT.GetField("hasPost")!.SetValue(c, hasPost);
            return c;
        }

        private static Array MakeCams(params object[] cams)
        {
            Array a = Array.CreateInstance(CamT, cams.Length);
            for (int i = 0; i < cams.Length; i++) a.SetValue(cams[i], i);
            return a;
        }

        private static Array MakeCues(params string[] ids)
        {
            Array a = Array.CreateInstance(CueT, ids.Length);
            for (int i = 0; i < ids.Length; i++)
            {
                object c = Activator.CreateInstance(CueT)!;
                CueT.GetField("id")!.SetValue(c, ids[i]);
                a.SetValue(c, i);
            }
            return a;
        }

        private static object MakeControl(int runEpoch = 0, float minDwell = 0f, float cooldown = 0f,
            string cameraOverride = "", string activeCue = "", bool discoveryEnabled = true)
        {
            object c = Activator.CreateInstance(CtrlT)!;
            CtrlT.GetField("runEpoch")!.SetValue(c, runEpoch);
            CtrlT.GetField("minDwellSec")!.SetValue(c, minDwell);
            CtrlT.GetField("switchCooldownSec")!.SetValue(c, cooldown);
            CtrlT.GetField("cameraOverride")!.SetValue(c, cameraOverride);
            CtrlT.GetField("activeCue")!.SetValue(c, activeCue);
            CtrlT.GetField("discoveryEnabled")!.SetValue(c, discoveryEnabled);
            return c;
        }

        private static object MakeState(int rev, Array? cameras = null, object? control = null, Array? cues = null,
            ShowLayoutDef? layout = null, ShowScheduleDef? schedule = null, ShowTimelineDef? timeline = null,
            PostParams? post = null)
        {
            object s = Activator.CreateInstance(StateT)!;
            StateT.GetField("rev")!.SetValue(s, rev);
            if (cameras != null) StateT.GetField("cameras")!.SetValue(s, cameras);
            if (cues != null) StateT.GetField("cues")!.SetValue(s, cues);
            if (control != null) StateT.GetField("control")!.SetValue(s, control);
            if (layout != null) StateT.GetField("layout")!.SetValue(s, layout);
            if (schedule != null) StateT.GetField("schedule")!.SetValue(s, schedule);
            if (timeline != null) StateT.GetField("timeline")!.SetValue(s, timeline);
            if (post != null) StateT.GetField("post")!.SetValue(s, post);
            return s;
        }

        private static ShowLayoutDef MakeLayout(int rev) => new ShowLayoutDef
        {
            rev = rev,
            grid = new ShowGridDef { tileM = 0.15f, cols = 2, rows = 2, cells = new[] { "0.", "1." } },
        };

        private static ShowScheduleDef MakeSchedule(int rev) => new ShowScheduleDef
        {
            rev = rev,
            entries = new[] { new ShowScheduleEntryDef { lap = 1, camera = 0, cueId = "cue_A_1" } },
        };

        private static ShowTimelineDef MakeTimeline(int rev, int segCount = 1)
        {
            var segs = new ShowTimelineSegmentDef[segCount];
            for (int i = 0; i < segCount; i++)
                segs[i] = new ShowTimelineSegmentDef
                {
                    lap = 1, camera = 0, cues = Array.Empty<ShowSegmentCueDef>(),
                };
            return new ShowTimelineDef { rev = rev, segments = segs };
        }

        private static ShowTimelineDef MakeEmptyTimeline(int rev)
            => new ShowTimelineDef { rev = rev, segments = Array.Empty<ShowTimelineSegmentDef>() };

        private CameraStreamRegistry MakeRegistry(int count, int active)
        {
            var go = new GameObject("Registry");
            _spawned.Add(go);
            var reg = go.AddComponent<CameraStreamRegistry>();
            FieldInfo streamsF = typeof(CameraStreamRegistry).GetField("_streams", InstBF)!;
            Array arr = Array.CreateInstance(streamsF.FieldType.GetElementType()!, count);
            streamsF.SetValue(reg, arr);
            typeof(CameraStreamRegistry).GetField("_activeIndex", InstBF)!.SetValue(reg, active);
            return reg;
        }

        // ---- 1) ApplyBaked：焼き込みがフィールドへ載る・_rev は負のまま ----

        [Test]
        public void ApplyBaked_LoadsFieldsAndKeepsRevNegative()
        {
            var show = MakeShow();
            InvokeM(show, "ApplyBaked", MakeState(
                rev: 5,
                cameras: MakeCams(MakeCam("A")),
                cues: MakeCues("cue_A_1"),
                schedule: MakeSchedule(1),
                timeline: MakeTimeline(1),
                layout: MakeLayout(1)));

            Assert.That((int)GetF(show, "_rev"), Is.EqualTo(-1), "ApplyBaked は _rev を進めない（ライブ判定は別）");
            Assert.That(((Array)GetF(show, "_cameras")).Length, Is.EqualTo(1));
            Assert.That(((Array)GetF(show, "_cues")).Length, Is.EqualTo(1));
            Assert.That(GetF(show, "_schedule"), Is.Not.Null);
            Assert.That(GetF(show, "_timeline"), Is.Not.Null);
            Assert.That((int)GetF(show, "_appliedScheduleRev"), Is.EqualTo(1));
            Assert.That((int)GetF(show, "_appliedTimelineRev"), Is.EqualTo(1));
        }

        // ---- 2) ライブ適用済み（_rev>=0）なら ApplyBaked は no-op ----

        [Test]
        public void ApplyBaked_NoOpWhenLiveAlreadyApplied()
        {
            var show = MakeShow();
            SetF(show, "_rev", 0); // ライブ適用済みを偽装
            SetF(show, "_cameras", MakeCams(MakeCam("LIVE")));

            InvokeM(show, "ApplyBaked", MakeState(rev: 9, cameras: MakeCams(MakeCam("BAKED1"), MakeCam("BAKED2"))));

            var cams = (Array)GetF(show, "_cameras");
            Assert.That(cams.Length, Is.EqualTo(1), "ライブ後勝ち = 焼き込みで上書きしない");
            Assert.That(CamT.GetField("id")!.GetValue(cams.GetValue(0)), Is.EqualTo("LIVE"));
        }

        // ---- 3) LoadAndApplyCache：空データのキャッシュは焼き込み値を潰さない（runEpoch は復元）----

        [Test]
        public void LoadAndApplyCache_EmptyDataDoesNotClobberBaked()
        {
            var show = MakeShow();
            SetF(show, "_cameras", MakeCams(MakeCam("A")));
            var bakedLayout = MakeLayout(2);
            SetF(show, "_layout", bakedLayout);
            SetF(show, "_appliedLayoutRev", 2);
            SetF(show, "_knownRunEpoch", 0);

            // 空 cameras / null layout（＝空データ）だが runEpoch=7 を持つキャッシュを書く。
            object cfg = Activator.CreateInstance(CachedT)!;
            CachedT.GetField("runEpoch")!.SetValue(cfg, 7);
            File.WriteAllText(CachePath(show), JsonUtility.ToJson(cfg));

            InvokeM(show, "LoadAndApplyCache");

            Assert.That(((Array)GetF(show, "_cameras")).Length, Is.EqualTo(1), "空 cameras キャッシュは焼き込みを潰さない");
            Assert.That(GetF(show, "_layout"), Is.SameAs(bakedLayout), "空 layout キャッシュは焼き込みを潰さない");
            Assert.That((int)GetF(show, "_knownRunEpoch"), Is.EqualTo(7), "runEpoch はキャッシュから復元される");
        }

        // ---- 4) CachedConfig 往復：pinned / hasPost / runEpoch / switchTiming が保存前と一致 ----

        [Test]
        public void CachedConfig_Roundtrip_PreservesFlagsAndTiming()
        {
            var show = MakeShow();
            SetF(show, "_cameras", MakeCams(MakeCam("A", hasPost: true, pinned: true)));
            SetF(show, "_knownRunEpoch", 5);
            SetF(show, "_switchDwellSec", 0.7f);
            SetF(show, "_switchCooldownSec", 0.9f);

            InvokeM(show, "SaveCache");

            // フィールドをクリアしてからキャッシュを読み戻す。
            SetF(show, "_cameras", Array.CreateInstance(CamT, 0));
            SetF(show, "_knownRunEpoch", 0);
            SetF(show, "_switchDwellSec", 0f);
            SetF(show, "_switchCooldownSec", 0f);

            InvokeM(show, "LoadAndApplyCache");

            var cams = (Array)GetF(show, "_cameras");
            Assert.That(cams.Length, Is.EqualTo(1));
            object c0 = cams.GetValue(0)!;
            Assert.That((bool)CamT.GetField("hasPost")!.GetValue(c0)!, Is.True, "保存済み hasPost が往復後も生きる");
            Assert.That((bool)CamT.GetField("pinned")!.GetValue(c0)!, Is.True, "保存済み pinned が往復後も生きる");
            Assert.That((int)GetF(show, "_knownRunEpoch"), Is.EqualTo(5));
            Assert.That((float)GetF(show, "_switchDwellSec"), Is.EqualTo(0.7f).Within(1e-4f));
            Assert.That((float)GetF(show, "_switchCooldownSec"), Is.EqualTo(0.9f).Within(1e-4f));
        }

        // ---- 5) rev ゲート layout：同一 layout.rev の 2 回目は _appliedLayoutRev 不変・LayoutChanged 再発火なし ----

        [Test]
        public void RevGate_Layout_SameRevDoesNotRefire()
        {
            var show = MakeShow();
            int layoutEvents = 0;
            show.LayoutChanged += () => layoutEvents++;

            InvokeM(show, "Apply", MakeState(rev: 1, layout: MakeLayout(1)));
            Assert.That((int)GetF(show, "_appliedLayoutRev"), Is.EqualTo(1));
            Assert.That(layoutEvents, Is.EqualTo(1));

            InvokeM(show, "Apply", MakeState(rev: 2, layout: MakeLayout(1))); // 同一 layout.rev
            Assert.That((int)GetF(show, "_appliedLayoutRev"), Is.EqualTo(1));
            Assert.That(layoutEvents, Is.EqualTo(1), "同一 layout.rev では LayoutChanged 再発火なし");
        }

        // ---- 6a) rev ゲート schedule：rev>0 かつ前回と異なる rev のみ適用 ----

        [Test]
        public void RevGate_Schedule_OnlyNewRevApplies()
        {
            var show = MakeShow();
            InvokeM(show, "Apply", MakeState(rev: 1, schedule: MakeSchedule(1)));
            Assert.That((int)GetF(show, "_appliedScheduleRev"), Is.EqualTo(1));

            InvokeM(show, "Apply", MakeState(rev: 2, schedule: MakeSchedule(1))); // 同一 schedule.rev
            Assert.That((int)GetF(show, "_appliedScheduleRev"), Is.EqualTo(1));

            InvokeM(show, "Apply", MakeState(rev: 3, schedule: MakeSchedule(2))); // 新 schedule.rev
            Assert.That((int)GetF(show, "_appliedScheduleRev"), Is.EqualTo(2));
        }

        // ---- 6b) rev ゲート timeline：同上 ----

        [Test]
        public void RevGate_Timeline_OnlyNewRevApplies()
        {
            var show = MakeShow();
            InvokeM(show, "Apply", MakeState(rev: 1, timeline: MakeTimeline(1)));
            Assert.That((int)GetF(show, "_appliedTimelineRev"), Is.EqualTo(1));

            InvokeM(show, "Apply", MakeState(rev: 2, timeline: MakeTimeline(1))); // 同一 timeline.rev
            Assert.That((int)GetF(show, "_appliedTimelineRev"), Is.EqualTo(1));

            InvokeM(show, "Apply", MakeState(rev: 3, timeline: MakeTimeline(2))); // 新 timeline.rev
            Assert.That((int)GetF(show, "_appliedTimelineRev"), Is.EqualTo(2));
        }

        // ---- 7) timeline supersede：rev>0 && segments 非空で TimelineActive=true ----

        [Test]
        public void TimelineActive_TrueWhenRevPositiveAndSegmentsPresent()
        {
            var show = MakeShow();
            InvokeM(show, "Apply", MakeState(rev: 1, timeline: MakeTimeline(1, segCount: 1)));
            Assert.That(TimelineActive(show), Is.True);
        }

        [Test]
        public void TimelineActive_FalseWhenSegmentsEmpty()
        {
            var show = MakeShow();
            InvokeM(show, "Apply", MakeState(rev: 1, timeline: MakeEmptyTimeline(1)));
            Assert.That(TimelineActive(show), Is.False, "rev>0 で適用はされるが segments 空なら supersede しない");
        }

        private static bool TimelineActive(ShowControlClient show)
            => (bool)ShowT.GetProperty("TimelineActive", InstBF)!.GetValue(show)!;

        // ---- 8) runEpoch 既知値初期化：ApplyBaked / LoadAndApplyCache は RunReset を発火しない ----

        [Test]
        public void RunEpoch_KnownValueInit_DoesNotFireReset()
        {
            var show = MakeShow();
            int resets = 0;
            show.RunReset += () => resets++;

            InvokeM(show, "ApplyBaked", MakeState(rev: 1, control: MakeControl(runEpoch: 3)));
            Assert.That((int)GetF(show, "_knownRunEpoch"), Is.EqualTo(3));
            Assert.That(resets, Is.EqualTo(0), "ApplyBaked の runEpoch 取り込みは RunReset を発火しない");

            object cfg = Activator.CreateInstance(CachedT)!;
            CachedT.GetField("runEpoch")!.SetValue(cfg, 4);
            File.WriteAllText(CachePath(show), JsonUtility.ToJson(cfg));
            InvokeM(show, "LoadAndApplyCache");
            Assert.That((int)GetF(show, "_knownRunEpoch"), Is.EqualTo(4));
            Assert.That(resets, Is.EqualTo(0), "キャッシュの runEpoch 復元も RunReset を発火しない");
        }

        // ---- 9) runEpoch 変化：Apply で既知値と異なると RunReset を 1 回発火 ----

        [Test]
        public void RunEpoch_ChangeFiresResetOnce()
        {
            var show = MakeShow();
            int resets = 0;
            show.RunReset += () => resets++;
            SetF(show, "_knownRunEpoch", 1);

            InvokeM(show, "Apply", MakeState(rev: 1, control: MakeControl(runEpoch: 2)));
            Assert.That((int)GetF(show, "_knownRunEpoch"), Is.EqualTo(2));
            Assert.That(resets, Is.EqualTo(1));

            InvokeM(show, "Apply", MakeState(rev: 2, control: MakeControl(runEpoch: 2)));
            Assert.That(resets, Is.EqualTo(1), "同一 epoch では再発火しない");
        }

        // ---- 10) SplitAuth（reflection static）----

        private static (string user, string pass) SplitAuth(string auth)
        {
            MethodInfo m = ShowT.GetMethod("SplitAuth", StaticBF)!;
            object[] args = { auth, null!, null! };
            m.Invoke(null, args);
            return ((string)args[1], (string)args[2]);
        }

        [Test]
        public void SplitAuth_Cases()
        {
            Assert.That(SplitAuth("user:pass"), Is.EqualTo(("user", "pass")));
            Assert.That(SplitAuth("user"), Is.EqualTo(("user", "")));
            Assert.That(SplitAuth(""), Is.EqualTo(("", "")));
            Assert.That(SplitAuth("u:p:q"), Is.EqualTo(("u", "p:q")), "最初のコロンで分割（パスワードにコロン可）");
        }

        // ---- 11) cameraOverride id→index：一致 index を解決 / 未知 id は現状維持 ----

        [Test]
        public void CameraOverride_ResolvesIndex_UnknownKeepsCurrent()
        {
            var show = MakeShow();
            var reg = MakeRegistry(3, active: 0);
            SetF(show, "registry", reg);

            InvokeM(show, "Apply", MakeState(rev: 1,
                cameras: MakeCams(MakeCam("A"), MakeCam("B"), MakeCam("C")),
                control: MakeControl(cameraOverride: "B")));
            Assert.That(reg.ActiveIndex, Is.EqualTo(1), "id 一致で index=1 を解決");

            InvokeM(show, "Apply", MakeState(rev: 2,
                cameras: MakeCams(MakeCam("A"), MakeCam("B"), MakeCam("C")),
                control: MakeControl(cameraOverride: "Z")));
            Assert.That(reg.ActiveIndex, Is.EqualTo(1), "未知 id は現状維持（切り替えない）");
        }
    }
}
