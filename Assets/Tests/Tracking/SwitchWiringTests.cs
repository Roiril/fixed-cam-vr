#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using FixedCamVr.Streaming;
using FixedCamVr.Tracking;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Tracking.Tests
{
    /// <summary>
    /// L0.5 統合テスト（XR / OVR 非依存・EditMode + 手動 Tick）。MonoBehaviour 配線層
    /// （CameraSwitchDirector ↔ InsertController ↔ TimelineDirector ↔ CueScheduler ↔ LapCounter ↔
    ///  PlayerZoneTracker ↔ ShowControlClient）を実行時に組み立て、切替系バグの再現→修正を固定する。
    ///
    /// 監査バックログ（fixed_cam_review_backlog.md 2026-07-23 節）の 1/3/4 を対象:
    ///   T1: insert 表示中に ResetRun → ゾーン凍結が解除され自動切替が再開する
    ///   T2: insert 表示中に SetInserts（タイムライン差し替え）→ 同上
    ///   T4: insert 中に別ゾーンへ移動 → 復帰後 LapCounter が実ゾーンに追従・lap 二重加算なし
    ///   T5: Web override 解除 → 同一ゾーン滞在のまま表示がゾーンカメラへ復帰
    /// （T3 グリップ緊急停止は Streaming 側 GripStopLocalTests。）
    ///
    /// batchmode で PlayMode に入ると本プロジェクトの Oculus XR 自動初期化が headless で落ちるため、
    /// PlayMode ランナーは使わず EditMode で駆動する。EditMode は Awake/OnEnable/Update が自動起動しないので
    /// reflection で明示駆動し、dip / dwell / cooldown を 0 に落として時間非依存の 1 ステップ確定にする。
    /// 純ロジックの時間計時は別途 EditMode 単体テスト（SwitchDirectorLogicTests / InsertLogicTests 等）が固定済み。
    /// </summary>
    public sealed class SwitchWiringTests
    {
        private const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

        private readonly List<UnityEngine.Object> _spawned = new();

        [TearDown]
        public void Cleanup()
        {
            foreach (var o in _spawned) if (o != null) UnityEngine.Object.DestroyImmediate(o);
            _spawned.Clear();
        }

        // ---- reflection ヘルパ ----

        private static void SetField(object target, string name, object? value)
        {
            FieldInfo? f = target.GetType().GetField(name, BF);
            Assert.That(f, Is.Not.Null, $"field '{name}' not found on {target.GetType().Name}");
            f!.SetValue(target, value);
        }

        private static object GetField(object target, string name)
        {
            FieldInfo? f = target.GetType().GetField(name, BF);
            Assert.That(f, Is.Not.Null, $"field '{name}' not found on {target.GetType().Name}");
            return f!.GetValue(target);
        }

        // 引数なし private メソッド（Awake/OnEnable/Update 等）を reflection で駆動する。無ければ no-op。
        private static void Invoke(object target, string method)
            => target.GetType().GetMethod(method, BF)?.Invoke(target, null);

        // registry の Awake（CameraStream 生成＝ネットワーク）を回避し _streams / _activeIndex を直接注入する。
        private CameraStreamRegistry MakeRegistry(int count, int active)
        {
            var go = new GameObject("Registry");
            _spawned.Add(go);
            var reg = go.AddComponent<CameraStreamRegistry>();
            FieldInfo streamsF = typeof(CameraStreamRegistry).GetField("_streams", BF)!;
            Array arr = Array.CreateInstance(streamsF.FieldType.GetElementType()!, count); // CameraStream[count]（null 要素）
            streamsF.SetValue(reg, arr);
            typeof(CameraStreamRegistry).GetField("_activeIndex", BF)!.SetValue(reg, active);
            return reg;
        }

        // dip / dwell / cooldown を 0 に落として時間非依存にした Director を Awake+OnEnable 済みで返す。
        private CameraSwitchDirector MakeDirector(CameraStreamRegistry reg, GameObject host)
        {
            var dir = host.AddComponent<CameraSwitchDirector>();
            SetField(dir, "registry", reg);
            SetField(dir, "dipDownSec", 0f);
            SetField(dir, "dipUpSec", 0f);
            SetField(dir, "switchCooldownSec", 0f);
            SetField(dir, "minDwellSec", 0f);
            // カット側が持つ遷移秒（既定 170ms）は Director の dipDownSec=0 を上書きするので、
            // dip の dt も固定して実エディタ fps から切り離す（1 フレーム = 1 秒扱い）。
            dir.SetDeltaSource(() => 1f);
            Invoke(dir, "Awake");     // _logic.Configure(0,0,0)・registry 解決
            Invoke(dir, "OnEnable");  // registry.ActiveChanged 購読・_logic.Reset
            return dir;
        }

        private PlayerZone MakeZone(string name, Vector3 center, Vector3 half, int cameraIndex, int priority)
        {
            var go = new GameObject(name);
            _spawned.Add(go);
            go.transform.position = center;
            var z = go.AddComponent<PlayerZone>();
            SetField(z, "halfExtents", half);
            SetField(z, "centerOffset", Vector3.zero);
            SetField(z, "cameraIndex", cameraIndex);
            SetField(z, "priority", priority);
            return z;
        }

        /// <summary>「進入と同時に別カメラのライブを N 秒映す」区間（v2 の enter インサート相当）。</summary>
        private static ShowTimelineSegmentDef SegmentWithLiveTake(int lap, int camera, int takeCam, float durSec)
            => new()
            {
                lap = lap,
                camera = camera,
                post = null,
                hasPost = false,
                takes = new[]
                {
                    new ShowTakeDef
                    {
                        id = "t_live", name = "", at = TakeSchema.AtEnter, offsetSec = 0f,
                        ifMissed = TakeSchema.MissedFireOnExit, policy = TakeSchema.PolicyHold,
                        once = true, maxDurationSec = 0f,
                        steps = new[]
                        {
                            new ShowStepDef
                            {
                                source = TakeSchema.SourceLive, camera = takeCam, assetUrl = "", cueId = "",
                                strength = -1f, fadeInSec = -1f, fadeOutSec = -1f,
                                trimStartSec = -1f, trimEndSec = -1f,
                                durKind = TakeSchema.DurSec, durSec = durSec,
                                // 遷移は cut（瞬時）。dip にすると **カット側の遷移秒**（既定 170ms）が
                                // Director の dipDownSec=0 を上書きするため、PumpDirector の 4 フレームで
                                // 実 unscaledDeltaTime が 68ms 貯まるかどうかで結果が変わる
                                // （Editor がフォーカス中＝高 fps だと貯まらず落ちる flaky）。
                                // このクラスは配線・凍結の検査で、dip の計時は SwitchDirectorLogicTests の担当。
                                transition = TakeSchema.TransCut, transitionMs = 0f, hasPost = false,
                            },
                        },
                    },
                },
            };

        // dip（Down→Up）を確実に畳むため Director.Update を数回叩く。
        private static void PumpDirector(CameraSwitchDirector dir, int frames = 4)
        {
            for (int i = 0; i < frames; i++) Invoke(dir, "Update");
        }

        // dwell / cooldown を 0 へ戻す。ShowControlClient.Apply が ApplyTimingOverride(0,0) 経由で
        // コード既定 0.5s を再適用するため、EditMode（時間凍結）で commit ゲートが通らなくなるのを防ぐ。
        // dwell は時計（_progress）側、cooldown は画面（_logic）側に分かれている（段 B）。
        private static void ForceZeroTiming(CameraSwitchDirector dir)
        {
            SetField(dir, "minDwellSec", 0f);
            SetField(dir, "switchCooldownSec", 0f);
            object logic = GetField(dir, "_logic");
            logic.GetType().GetMethod("Configure")!.Invoke(logic, new object[] { 0f, 0f });
            object progress = GetField(dir, "_progress");
            progress.GetType().GetMethod("Configure")!.Invoke(progress, new object[] { 0f });
        }

        // ---- T4: 演出で画面が占有されている間の実ゾーン移動が、復帰後に周回追跡へ反映される（二重加算なし）----
        //   旧 InsertController 廃止（2026-07-25 の v3 一本化）に伴い、実行体を TakeRunner に置き換えた。

        [Test]
        public void T4_ZoneMoveDuringTake_ReflectedInLapCounterOnReturn()
        {
            var reg = MakeRegistry(3, active: 0);
            var host = new GameObject("Screen"); _spawned.Add(host);
            var dir = MakeDirector(reg, host);
            var cueScheduler = host.AddComponent<CueScheduler>();
            var timeline = host.AddComponent<TimelineDirector>();
            var runner = host.AddComponent<TakeRunner>();
            var lap = host.AddComponent<LapCounter>();

            float now = 0f;
            SetField(runner, "director", dir);
            SetField(runner, "overlay", null);
            SetField(runner, "showControl", null);
            runner.SetTimeSource(() => now);

            SetField(timeline, "cueScheduler", cueScheduler);
            SetField(timeline, "takeRunner", runner);
            SetField(lap, "registry", reg);
            SetField(lap, "director", dir);
            SetField(lap, "cueScheduler", cueScheduler);
            SetField(lap, "seedInitialZone", false);
            SetField(lap, "logChanges", false);

            Invoke(cueScheduler, "Awake");
            Invoke(timeline, "Awake");
            Invoke(timeline, "OnEnable");   // cueScheduler.CameraEntered 購読
            Invoke(lap, "OnEnable");        // director.ZoneCommitted（ショーの時計）購読

            // 周回順 [0,1,2]（A→B→C）を LapCounter へ直接注入（ShowControlClient を使わない）。
            object lapLogic = GetField(lap, "_logic");
            lapLogic.GetType().GetMethod("SetOrder")!.Invoke(lapLogic, new object[] { new[] { 0, 1, 2 } });

            // (1,0) に「cam2 のライブを 30 秒映す」演出（v2 の enter インサート相当）。
            timeline.SetTimeline(new[] { SegmentWithLiveTake(1, 0, takeCam: 2, durSec: 30f) });

            // ゾーン0進入（seed 相当）。演出を武装。pos は order[0]=cam0。
            cueScheduler.NotifyCameraEntered(0, 1, 1);

            Invoke(runner, "Update");   // 進入 +0s → カット開始 → director.InsertBegin(2)
            PumpDirector(dir);          // begin dip → active=2

            Assert.That(dir.InsertActive, Is.True, "演出が画面を占有している間は凍結しているはず");
            Assert.That(reg.ActiveIndex, Is.EqualTo(2), "演出のカメラ(2)表示中");
            Assert.That(lap.Position, Is.EqualTo(0), "演出開始では周回ポインタは進まない（cam0 のまま）");

            // insert 表示中に体験者がゾーン1（B）へ移動。画面は凍結されたままだが、**時計は進む**（段 B）。
            dir.RequestZone(1);
            PumpDirector(dir, 1);       // _progress.Tick が確定 → ZoneCommitted(1) → Feed(1)（画面は凍結のまま）

            Assert.That(lap.Position, Is.EqualTo(1),
                "演出で画面が凍結していても、体験者が歩いたら周回ポインタは即前進する（不変条件 4）");
            Assert.That(reg.ActiveIndex, Is.EqualTo(2), "画面はまだ insert カメラのまま");

            now += 31f;                 // 尺（30s）を越えさせる
            Invoke(runner, "Update");   // 演出終了 → 復帰先は「いま体験者がいるゾーン」= cam1
            PumpDirector(dir);          // 復帰 dip → active=1

            Assert.That(reg.ActiveIndex, Is.EqualTo(1), "演出終了後は実ゾーン(cam1)へ復帰しているはず");
            Assert.That(lap.Position, Is.EqualTo(1), "復帰の画面切替では二重に進まない");
            Assert.That(lap.CurrentLap, Is.EqualTo(1), "lap は二重加算されない");

            // 二重カウントが無いことの追試: 次に cam2 へゾーン移動すれば pos は 2 へ 1 段だけ進む。
            dir.RequestZone(2);
            PumpDirector(dir);
            Assert.That(lap.Position, Is.EqualTo(2), "ポインタは 1→2 へ 1 段進む（スキップ・二重進行なし）");
            Assert.That(lap.CurrentLap, Is.EqualTo(1));
        }

        // ---- T5: override 解除後、同一ゾーン滞在のまま表示がゾーンカメラへ復帰 ----

        [Test]
        public void T5_OverrideRelease_ReturnsToZoneCameraWhileStayingInZone()
        {
            var reg = MakeRegistry(3, active: 0);
            var screen = new GameObject("Screen"); _spawned.Add(screen);
            var dir = MakeDirector(reg, screen);
            var show = screen.AddComponent<ShowControlClient>();
            SetField(show, "registry", reg);
            SetField(show, "switchDirector", dir);
            SetField(show, "server", null);
            SetField(show, "configCacheFileName", "test_show_config_" + Guid.NewGuid().ToString("N") + ".json");

            // ゾーン0（cam0）が原点を含む。head は原点固定。
            var zone0 = MakeZone("Zone0", Vector3.zero, new Vector3(2, 2, 2), cameraIndex: 0, priority: 0);
            var head = new GameObject("Head"); _spawned.Add(head);
            head.transform.position = Vector3.zero;

            var trackerGo = new GameObject("Tracker"); _spawned.Add(trackerGo);
            var tracker = trackerGo.AddComponent<PlayerZoneTracker>();
            SetField(tracker, "registry", reg);
            SetField(tracker, "director", dir);
            SetField(tracker, "headTransform", head.transform);
            SetField(tracker, "zones", new[] { zone0 });
            SetField(tracker, "updateInterval", 0f);
            SetField(tracker, "keepLastWhenOutside", true);
            SetField(tracker, "logChanges", false);
            SetField(tracker, "hysteresisShrink", 0.1f);
            SetField(show, "zoneTrackerToDisable", tracker);

            // 初期: tracker が現ゾーン(0)を掴む（active は 0 のまま）。
            Invoke(tracker, "Update");
            PumpDirector(dir);
            Assert.That(reg.ActiveIndex, Is.EqualTo(0), "初期はゾーンカメラ(0)");

            // Web override → cam1（"B"）。tracker 無効化、表示は override カメラに固定。
            InvokeApply(show, rev: 1, cameraOverride: "B");
            PumpDirector(dir);
            Assert.That(reg.ActiveIndex, Is.EqualTo(1), "override 中は cam1 を表示");
            Assert.That(tracker.enabled, Is.False, "override 中は tracker 無効");

            // override 解除。head は動かさない（同一ゾーン滞在）。修正が無いと _current 不変で固着する。
            InvokeApply(show, rev: 2, cameraOverride: "");
            Assert.That(tracker.enabled, Is.True, "解除で tracker 再有効");
            ForceZeroTiming(dir); // Apply が既定 0.5s を再適用するため 0 へ戻す（EditMode 時間凍結対策）

            // 再有効化で PlayMode なら OnEnable が発火し _current を無効化する。EditMode は自動発火しないため
            // 明示駆動して production の再有効化を再現する（enabled=true → OnEnable → InvalidateCurrent）。
            Invoke(tracker, "OnEnable");

            // 再 Pick → ゾーンカメラ(0)へ戻る（修正が無いと _current 不変で cam1 に固着し失敗する）。
            Invoke(tracker, "Update");
            PumpDirector(dir);
            Assert.That(reg.ActiveIndex, Is.EqualTo(0),
                "override 解除後、同一ゾーン滞在のままでもゾーンカメラ(0)へ復帰するはず");

            CleanupCacheFile(show);
        }

        // ---- T6: Web override が stale ゾーン保留を第一級凍結し、固定破れ・LapCounter 誤進行を防ぐ ----

        [Test]
        public void T6_OverrideFreezesStalePending_NoZoneCommit_ThenReleaseReturnsToZone()
        {
            var reg = MakeRegistry(3, active: 0);
            var screen = new GameObject("Screen"); _spawned.Add(screen);
            var dir = MakeDirector(reg, screen);
            var show = screen.AddComponent<ShowControlClient>();
            SetField(show, "registry", reg);
            SetField(show, "switchDirector", dir);
            SetField(show, "server", null);
            SetField(show, "configCacheFileName", "test_show_config_" + Guid.NewGuid().ToString("N") + ".json");

            var lap = screen.AddComponent<LapCounter>();
            SetField(lap, "registry", reg);
            SetField(lap, "director", dir);
            SetField(lap, "seedInitialZone", false);
            SetField(lap, "logChanges", false);
            Invoke(lap, "OnEnable");   // director.ZoneCommitted（ショーの時計）購読
            object lapLogic = GetField(lap, "_logic");
            lapLogic.GetType().GetMethod("SetOrder")!.Invoke(lapLogic, new object[] { new[] { 0, 1, 2 } });

            // tracker + zone0（override 解除後の復帰検証用）。head は原点（zone0=cam0 内）。
            var zone0 = MakeZone("Zone0", Vector3.zero, new Vector3(2, 2, 2), cameraIndex: 0, priority: 0);
            var head = new GameObject("Head"); _spawned.Add(head);
            head.transform.position = Vector3.zero;
            var trackerGo = new GameObject("Tracker"); _spawned.Add(trackerGo);
            var tracker = trackerGo.AddComponent<PlayerZoneTracker>();
            SetField(tracker, "registry", reg);
            SetField(tracker, "director", dir);
            SetField(tracker, "headTransform", head.transform);
            SetField(tracker, "zones", new[] { zone0 });
            SetField(tracker, "updateInterval", 0f);
            SetField(tracker, "keepLastWhenOutside", true);
            SetField(tracker, "logChanges", false);
            SetField(tracker, "hysteresisShrink", 0.1f);
            SetField(show, "zoneTrackerToDisable", tracker);

            // tracker が override 前にゾーン2切替を積んだ状態を作る（Update せず保留維持）。
            dir.RequestZone(2);
            Assert.That(dir.SwitchSuppressed, Is.True, "override 前に stale 保留(2)が積まれている");
            Assert.That(lap.Position, Is.EqualTo(0));
            Assert.That(lap.CurrentLap, Is.EqualTo(1));

            // Web override → cam1（"B"）。第一級凍結 + stale 保留の無条件クリア。
            InvokeApply(show, rev: 1, cameraOverride: "B");
            ForceZeroTiming(dir);   // Apply が既定 0.5s を再適用するため 0 へ戻す（EditMode 時間凍結対策）
            PumpDirector(dir);

            Assert.That(reg.ActiveIndex, Is.EqualTo(1),
                "override 固定: 修正なしなら stale 保留(2)が cooldown 後に Zone commit して 2 に化ける");
            Assert.That(lap.Position, Is.EqualTo(0), "override 中は周回ポインタが進まない（Override は数えず stale commit も起きない）");
            Assert.That(lap.CurrentLap, Is.EqualTo(1));

            // override 解除 → 同一ゾーン滞在のままゾーンカメラ(0)へ復帰（既存 T5 の自己回復と両立）。
            InvokeApply(show, rev: 2, cameraOverride: "");
            Assert.That(tracker.enabled, Is.True, "解除で tracker 再有効");
            ForceZeroTiming(dir);
            Invoke(tracker, "OnEnable");   // 再有効化 → InvalidateCurrent
            Invoke(tracker, "Update");     // 現在位置から再 Pick → RequestZone(0)
            PumpDirector(dir);
            Assert.That(reg.ActiveIndex, Is.EqualTo(0),
                "override 解除後、同一ゾーン滞在のままでもゾーンカメラ(0)へ復帰するはず");

            CleanupCacheFile(show);
        }

        // ShowControlClient.Apply を最小の ShowState（cameras A/B/C + control.cameraOverride）で駆動する。
        private static void InvokeApply(ShowControlClient show, int rev, string cameraOverride)
        {
            Type showT = typeof(ShowControlClient);
            Type stateT = showT.GetNestedType("ShowState", BindingFlags.NonPublic)!;
            Type camT = showT.GetNestedType("CameraDef", BindingFlags.NonPublic)!;
            Type ctrlT = showT.GetNestedType("ControlState", BindingFlags.NonPublic)!;

            Array cams = Array.CreateInstance(camT, 3);
            string[] ids = { "A", "B", "C" };
            for (int i = 0; i < 3; i++)
            {
                object c = Activator.CreateInstance(camT)!;
                camT.GetField("id")!.SetValue(c, ids[i]);
                cams.SetValue(c, i);
            }

            object ctrl = Activator.CreateInstance(ctrlT)!;
            ctrlT.GetField("cameraOverride")!.SetValue(ctrl, cameraOverride);

            object state = Activator.CreateInstance(stateT)!;
            stateT.GetField("rev")!.SetValue(state, rev);
            stateT.GetField("cameras")!.SetValue(state, cams);
            stateT.GetField("control")!.SetValue(state, ctrl);

            showT.GetMethod("Apply", BindingFlags.NonPublic | BindingFlags.Instance)!
                 .Invoke(show, new[] { state });
        }

        // Apply の SaveCache が書いた一時キャッシュを掃除する。
        private static void CleanupCacheFile(ShowControlClient show)
        {
            try
            {
                var pathProp = typeof(ShowControlClient).GetProperty("ConfigCachePath", BF);
                if (pathProp?.GetValue(show) is string p && System.IO.File.Exists(p))
                    System.IO.File.Delete(p);
            }
            catch { /* テスト後始末はベストエフォート */ }
        }
    }
}
