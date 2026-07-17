#nullable enable
using FixedCamVr.Diagnostics;
using FixedCamVr.Tracking;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FixedCamVr.Streaming.EditorTools
{
    /// <summary>
    /// Main.unity に Phase 2.7 用 [Zones] / [Tracker] と Phase 3 まで通しデモ用 DebugHud を
    /// 自動配置するメニュー。再実行可能（既存配置は削除して再生成）。
    ///
    /// Unity MCP やシーン編集が落ちている時の保険、および現場での再現性確保のために用意。
    /// 実行手順は <c>docs/onsite-checklist.md</c> 参照。
    /// </summary>
    public static class MainDemoSceneSetup
    {
        private const string MainScenePath = "Assets/Scenes/Main.unity";
        private const string CenterEyePath = "OVRCameraRig/TrackingSpace/CenterEyeAnchor";
        private const string RightHandPath = "OVRCameraRig/TrackingSpace/RightHandAnchor";
        private const string LogicGroupName = "=== Logic ===";
        private const string StreamingName = "[Streaming]";
        private const string ZonesName = "[Zones]";
        private const string GeneratedZonesName = "[GeneratedZones]";
        private const string TrackerName = "[Tracker]";
        private const string DebugHudName = "DebugHud";
        private const string StartupFaderName = "StartupFader";
        // Tracker と HmdTrajectoryRecorder で同値を使う（片方だけ変えると
        // 解析 CSV と実挙動の判定がズレるため 1 本化）。
        private const float HysteresisShrink = 0.15f;

        [MenuItem("Tools/FixedCamVr/Setup/Setup Main Demo Scene", priority = 50)]
        public static void Setup()
        {
            var scene = SceneManager.GetActiveScene();
            if (scene.path != MainScenePath)
            {
                EditorUtility.DisplayDialog(
                    "Main シーンを開いてから実行してください",
                    $"アクティブシーン: {scene.path}\n期待: {MainScenePath}",
                    "OK");
                return;
            }

            if (scene.isDirty)
            {
                bool cont = EditorUtility.DisplayDialog(
                    "未保存変更あり",
                    "現在のシーンに未保存変更があります。続行すると配置が上書きされます。",
                    "続行", "キャンセル");
                if (!cont) return;
            }

            // 既存配置を削除（再実行性のため）
            DeleteIfExists($"{LogicGroupName}/{ZonesName}");
            DeleteIfExists($"{LogicGroupName}/{GeneratedZonesName}");
            DeleteIfExists($"{LogicGroupName}/{TrackerName}");
            DeleteIfExists($"{CenterEyePath}/{DebugHudName}");
            DeleteIfExists($"{CenterEyePath}/{StartupFaderName}");

            var logic = GameObject.Find(LogicGroupName);
            if (logic == null)
            {
                Debug.LogError($"[MainDemoSceneSetup] '{LogicGroupName}' が見つかりません。Main.unity の Hierarchy 構造を確認してください。");
                return;
            }

            var streaming = GameObject.Find($"{LogicGroupName}/{StreamingName}");
            if (streaming == null)
            {
                Debug.LogError($"[MainDemoSceneSetup] '{StreamingName}' が見つかりません。Streaming Prefab Instance を Main に配置してください。");
                return;
            }

            var registry = streaming.GetComponent<CameraStreamRegistry>();
            if (registry == null)
            {
                Debug.LogError($"[MainDemoSceneSetup] '{StreamingName}' に CameraStreamRegistry がありません。");
                return;
            }

            var ovrBridge = streaming.GetComponent("OvrControllerBridge") as MonoBehaviour;
            if (ovrBridge == null)
            {
                Debug.LogWarning($"[MainDemoSceneSetup] '{StreamingName}' に OvrControllerBridge がありません。HUD トグル連携はスキップ。");
            }

            var centerEye = GameObject.Find(CenterEyePath);
            if (centerEye == null)
            {
                Debug.LogError($"[MainDemoSceneSetup] '{CenterEyePath}' が見つかりません。OVRCameraRig が === Rig === 配下にあるか確認してください。");
                return;
            }

            // 1. [Zones] — 廻リ視の周回経路（企画書 図4）を ±1.3m プレイレンジに当てはめた推測配置。
            // ★パーテーションで L 字壁を組んだら [HmdTrace] 実測で必ず校正すること（unity-vr.md 原則）。
            //
            // 想定: L 字壁が中央（西の腕 + 北の腕）、体験者は時計回りに 南→東→北→西→南 と周回。
            //   区間1=南辺(カメラA) / 区間2=東辺(カメラB) / 区間3=北辺+西辺(カメラC、AABB は矩形のみ
            //   なので 2 ゾーンに分割して同じ cameraIndex=2 を割る)。
            // 隣接ゾーンは角で 0.1m 以上オーバーラップ。重なりは配列順で先勝ち
            // （周回順 A→B→C で並べ、戻り側の角は A が勝つ = 周回の終わりで自然に A に戻る）。
            var zonesGo = new GameObject(ZonesName);
            zonesGo.transform.SetParent(logic.transform, worldPositionStays: false);
            var zoneA = CreateZone(zonesGo, "Zone_A_South", new Vector3(0f, 1f, -0.8f),
                halfExtents: new Vector3(1.4f, 2f, 0.55f),    // x∈[-1.4,1.4] z∈[-1.35,-0.25]
                cameraIndex: 0, label: "A:South", color: new Color(0f, 1f, 0.5f, 0.25f));
            var zoneB = CreateZone(zonesGo, "Zone_B_East", new Vector3(0.8f, 1f, 0.2f),
                halfExtents: new Vector3(0.55f, 2f, 1.2f),    // x∈[0.25,1.35] z∈[-1.0,1.4]
                cameraIndex: 1, label: "B:East", color: new Color(1f, 0.4f, 0.4f, 0.25f));
            var zoneC = CreateZone(zonesGo, "Zone_C_North", new Vector3(-0.2f, 1f, 0.8f),
                halfExtents: new Vector3(1.2f, 2f, 0.55f),    // x∈[-1.4,1.0] z∈[0.25,1.35]
                cameraIndex: 2, label: "C:North", color: new Color(0.4f, 0.6f, 1f, 0.25f));
            var zoneC2 = CreateZone(zonesGo, "Zone_C_West", new Vector3(-0.8f, 1f, 0f),
                halfExtents: new Vector3(0.55f, 2f, 1.0f),    // x∈[-1.35,-0.25] z∈[-1.0,1.0]
                cameraIndex: 2, label: "C:West", color: new Color(0.6f, 0.4f, 1f, 0.25f));

            // 2. [Tracker]
            var trackerGo = new GameObject(TrackerName);
            trackerGo.transform.SetParent(logic.transform, worldPositionStays: false);
            var tracker = trackerGo.AddComponent<PlayerZoneTracker>();
            var trackerSo = new SerializedObject(tracker);
            TrySetObjectRef(trackerSo, "registry", registry);
            TrySetObjectRef(trackerSo, "headTransform", centerEye.transform);
            SetPlayerZoneArray(trackerSo, "zones", new[] { zoneA, zoneB, zoneC, zoneC2 });
            TrySetFloat(trackerSo, "hysteresisShrink", HysteresisShrink);
            TrySetFloat(trackerSo, "updateInterval", 0.05f);
            TrySetBool(trackerSo, "keepLastWhenOutside", true);
            TrySetBool(trackerSo, "logChanges", true);
            trackerSo.ApplyModifiedPropertiesWithoutUndo();

            var rightHand = GameObject.Find(RightHandPath);
            if (rightHand == null)
                Debug.LogWarning($"[MainDemoSceneSetup] '{RightHandPath}' が見つかりません。登録の先端位置が headTransform にフォールバックします。");

            // 2.7. ShowControlClient.zoneTrackerToDisable を新 Tracker へ再配線。
            //      旧 Tracker は DeleteIfExists で消えるため、放置すると参照が missing になり
            //      Web オペレータ卓のカメラ override 時のゾーン無効化連動が黙って死ぬ
            //      （従来は手動再アサイン運用だった — unity-vr.md の注意書きを自動化）。
            var showControl = Object.FindObjectOfType<ShowControlClient>(includeInactive: true);
            if (showControl != null)
            {
                var scSo = new SerializedObject(showControl);
                TrySetObjectRef(scSo, "zoneTrackerToDisable", tracker);
                scSo.ApplyModifiedPropertiesWithoutUndo();
            }
            else
            {
                Debug.LogWarning("[MainDemoSceneSetup] ShowControlClient が見つかりません。zoneTrackerToDisable の再配線はスキップ。");
            }

            // 2.8. ゾーン再設計 Phase 1: CourseFrame + ZoneLayoutApplier を配線。
            //      show.json layout（cuts→OBB 展開）が届くと ZoneLayoutApplier が [GeneratedZones] 配下へ
            //      ゾーンを生成し tracker.zones を差し替える（CourseFrame の登録変換を通して配置）。
            //      rebuildFromDefaultOnStart=false のため、layout 不在時は上の静的ゾーン（フォールバック）を
            //      触らず、既存挙動を維持する。CourseFrame は identity デフォルト（registration.json があれば適用）。
            var courseFrame = trackerGo.AddComponent<CourseFrame>();

            var generatedZones = new GameObject(GeneratedZonesName);
            generatedZones.transform.SetParent(logic.transform, worldPositionStays: false);

            var applier = trackerGo.AddComponent<ZoneLayoutApplier>();
            var applierSo = new SerializedObject(applier);
            if (showControl != null) TrySetObjectRef(applierSo, "showControl", showControl);
            TrySetObjectRef(applierSo, "courseFrame", courseFrame);
            TrySetObjectRef(applierSo, "tracker", tracker);
            TrySetObjectRef(applierSo, "zonesContainer", generatedZones.transform);
            TrySetObjectRef(applierSo, "headTransform", centerEye.transform);
            TrySetFloat(applierSo, "zoneCenterY", 1f);
            TrySetFloat(applierSo, "zoneHalfHeight", 2f);
            TrySetBool(applierSo, "rebuildFromDefaultOnStart", false);
            applierSo.ApplyModifiedPropertiesWithoutUndo();

            // 2.9. CourseRegistrationController（HMD 2 点登録。両グリップ 3 秒長押しで開始、
            //      壁の凸角と北腕東端をコントローラ先端でタッチ → CourseFrame へ剛体変換を解いて渡す。
            //      Verify でワイヤーフレーム表示 → B 確定 / A やり直し / スティック微調整。
            //      OS recenter は OvrControllerBridge が検知して CourseFrame.MarkNeedsReRegistration を呼ぶ）。
            var registration = trackerGo.AddComponent<CourseRegistrationController>();
            var regSo = new SerializedObject(registration);
            TrySetObjectRef(regSo, "courseFrame", courseFrame);
            TrySetObjectRef(regSo, "headTransform", centerEye.transform);
            if (rightHand != null) TrySetObjectRef(regSo, "rightHandTransform", rightHand.transform);
            if (showControl != null) TrySetObjectRef(regSo, "showControl", showControl);
            regSo.ApplyModifiedPropertiesWithoutUndo();

            // 2.95. 事前オーサリング済み cue スケジュール（周回×ゾーン発火）。
            //       CueScheduler と LapCounter を [Tracker] に載せる（毎回作り直しなので冪等）。
            //       LapCounter が ActiveChanged を周回へ写像し進入を CueScheduler へ橋渡し、
            //       CueScheduler が (lap,camera,delay,once) 評価で ScreenOverlayController.PlayCue を直接呼ぶ。
            //       schedule / cue 解決 / activeCue 抑止は ShowControlClient から供給される。
            var overlay = Object.FindObjectOfType<ScreenOverlayController>(includeInactive: true);
            if (overlay == null)
                Debug.LogWarning("[MainDemoSceneSetup] ScreenOverlayController が見つかりません。CueScheduler の発火先未配線。");

            var cueScheduler = trackerGo.AddComponent<CueScheduler>();
            var schSo = new SerializedObject(cueScheduler);
            if (overlay != null) TrySetObjectRef(schSo, "overlay", overlay);
            schSo.ApplyModifiedPropertiesWithoutUndo();

            var lapCounter = trackerGo.AddComponent<LapCounter>();
            var lapSo = new SerializedObject(lapCounter);
            TrySetObjectRef(lapSo, "registry", registry);
            if (showControl != null) TrySetObjectRef(lapSo, "showControl", showControl);
            TrySetObjectRef(lapSo, "cueScheduler", cueScheduler);
            TrySetBool(lapSo, "seedInitialZone", true);
            TrySetBool(lapSo, "logChanges", true);
            lapSo.ApplyModifiedPropertiesWithoutUndo();

            // ShowControlClient.cueScheduler を新 CueScheduler へ配線（毎回 Tracker を作り直すため必須）。
            if (showControl != null)
            {
                var scSchedSo = new SerializedObject(showControl);
                TrySetObjectRef(scSchedSo, "cueScheduler", cueScheduler);
                scSchedSo.ApplyModifiedPropertiesWithoutUndo();
            }

            // 3. StartupFader（OVR 初期化 / 砂時計 / MJPEG 接続待ちを黒で覆い隠す）
            CreateStartupFader(centerEye.transform, registry);

            // 4. DebugHud
            var hud = CreateDebugHud(centerEye.transform, registry, tracker, centerEye.transform);

            // 5. OvrControllerBridge.hud に HUD 連携 + CourseRegistrationController / CourseFrame 接続
            if (ovrBridge != null)
            {
                var bridgeSo = new SerializedObject(ovrBridge);
                if (hud != null) TrySetObjectRef(bridgeSo, "hud", hud);
                TrySetObjectRef(bridgeSo, "courseRegistration", registration);
                TrySetObjectRef(bridgeSo, "courseFrame", courseFrame);
                TrySetFloat(bridgeSo, "calibToggleHoldSec", 3.0f); // 両グリップ 3 秒長押しで登録トグル
                bridgeSo.ApplyModifiedPropertiesWithoutUndo();
            }

            // 6. シーン保存
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);

            Selection.activeGameObject = trackerGo;
            Debug.Log("[MainDemoSceneSetup] 完了。Zones=4（静的フォールバック・推測配置） / Tracker / CourseFrame + ZoneLayoutApplier（show.json layout で生成） / CourseRegistrationController（両グリップ 3 秒長押しで 2 点登録、A=マーク/B=確定/スティック微調整） / LapCounter + CueScheduler（周回×ゾーンで cue 自動発火・ライブ優先） / StartupFader / DebugHud / OvrBridge 連携。シーン保存済み。" +
                      "次は URP-Balanced-Renderer.asset に FullScreenPassRendererFeature を追加（手動）。" +
                      "詳細: docs/onsite-checklist.md");
        }

        // ----- helpers -----

        private static void DeleteIfExists(string path)
        {
            // GameObject.Find は非アクティブを返さないため、ユーザーが Hierarchy で
            // 無効化した既存配置を拾えず重複生成される（冪等性の破れ）。
            // シーンルートから transform.Find（非アクティブも辿れる）で解決する。
            var go = FindByPath(path);
            if (go != null)
            {
                Object.DestroyImmediate(go);
            }
        }

        // 非アクティブを含むパス解決（"A/B/C" 形式、アクティブシーンのルートから）。
        private static GameObject? FindByPath(string path)
        {
            var segs = path.Split('/');
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (root.name != segs[0]) continue;
                Transform? t = root.transform;
                for (int i = 1; i < segs.Length && t != null; i++)
                {
                    t = t.Find(segs[i]);
                }
                if (t != null) return t.gameObject;
            }
            return null;
        }

        private static PlayerZone CreateZone(GameObject parent, string name, Vector3 position,
            Vector3 halfExtents, int cameraIndex, string label, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, worldPositionStays: false);
            go.transform.localPosition = position;

            var zone = go.AddComponent<PlayerZone>();
            var so = new SerializedObject(zone);
            TrySetVector3(so, "halfExtents", halfExtents);
            TrySetVector3(so, "centerOffset", Vector3.zero);
            TrySetInt(so, "cameraIndex", cameraIndex);
            TrySetInt(so, "priority", 0);
            TrySetString(so, "label", label);
            TrySetColor(so, "gizmoColor", color);
            so.ApplyModifiedPropertiesWithoutUndo();
            return zone;
        }

        private static void CreateStartupFader(Transform parent, CameraStreamRegistry registry)
        {
            // CenterEyeAnchor 直下に名前付き空オブジェクトを置き、StartupFader が Awake で
            // 子に Canvas を生やす。再実行時は DeleteIfExists で消されるので冪等。
            var go = new GameObject(StartupFaderName);
            go.transform.SetParent(parent, worldPositionStays: false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;

            var fader = go.AddComponent<StartupFader>();
            var so = new SerializedObject(fader);
            TrySetObjectRef(so, "registry", registry);
            TrySetFloat(so, "distance", 0.3f);
            TrySetVector2(so, "worldSize", new Vector2(2f, 2f));
            TrySetFloat(so, "minHoldSec", 0.5f);
            TrySetFloat(so, "maxWaitSec", 4f);
            TrySetFloat(so, "fadeDuration", 0.5f);
            TrySetColor(so, "fadeColor", Color.black);
            TrySetInt(so, "sortingOrder", 10000);
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static RuntimeDebugHud? CreateDebugHud(Transform parent, CameraStreamRegistry registry,
            PlayerZoneTracker tracker, Transform hmd)
        {
            var canvasGo = new GameObject(DebugHudName);
            canvasGo.transform.SetParent(parent, worldPositionStays: false);
            canvasGo.transform.localPosition = new Vector3(0f, 0f, 0.7f);

            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvasGo.AddComponent<UnityEngine.UI.CanvasScaler>();

            var rt = (RectTransform)canvasGo.transform;
            rt.sizeDelta = new Vector2(600f, 400f);
            rt.localScale = Vector3.one * 0.001f;

            var textGo = new GameObject("HudText");
            textGo.transform.SetParent(canvasGo.transform, worldPositionStays: false);
            var textRt = textGo.AddComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.anchoredPosition = Vector2.zero;
            textRt.sizeDelta = Vector2.zero;
            textRt.localScale = Vector3.one;
            textRt.localPosition = Vector3.zero;

            var tmp = textGo.AddComponent<TextMeshProUGUI>();
            tmp.text = "(HUD initializing)";
            tmp.fontSize = 28f;
            tmp.color = new Color(0.9f, 1f, 0.9f, 1f);
            tmp.alignment = TextAlignmentOptions.TopLeft;
            tmp.enableWordWrapping = false;
            tmp.richText = true;

            var hud = canvasGo.AddComponent<RuntimeDebugHud>();
            var hudSo = new SerializedObject(hud);
            TrySetObjectRef(hudSo, "text", tmp);
            TrySetObjectRef(hudSo, "registry", registry);
            TrySetObjectRef(hudSo, "tracker", tracker);
            TrySetObjectRef(hudSo, "hmd", hmd);
            TrySetFloat(hudSo, "updateInterval", 0.25f);
            TrySetBool(hudSo, "startVisible", true);
            hudSo.ApplyModifiedPropertiesWithoutUndo();

            var toggle = canvasGo.AddComponent<HudToggleInput>();
            var toggleSo = new SerializedObject(toggle);
            TrySetObjectRef(toggleSo, "hud", hud);
            toggleSo.ApplyModifiedPropertiesWithoutUndo();

            // HudLogDumper: HUD と同じ値を [HudDump] プレフィックスで Console に吐く
            // （MCP read_console で時系列取得するため）
            var dumper = canvasGo.AddComponent<HudLogDumper>();
            var dumperSo = new SerializedObject(dumper);
            TrySetObjectRef(dumperSo, "registry", registry);
            TrySetObjectRef(dumperSo, "tracker", tracker);
            TrySetObjectRef(dumperSo, "hmd", hmd);
            // 診断中は 1s 周期で出す。本番は再 Setup 時に 30 等に戻すこと。
            TrySetFloat(dumperSo, "periodicIntervalSec", 1f);
            dumperSo.ApplyModifiedPropertiesWithoutUndo();

            // HmdTrajectoryRecorder: HMD 位置 / 各ゾーン含有判定を CSV で persistentDataPath に書き出す。
            // 実機 Quest で歩いた後 adb pull で取り出し、ゾーン配置の妥当性をシュビーが解析する用途。
            // tracker.zones を直接参照できないので、Tracker と同じ並びを SerializedObject 経由で複製する。
            var rec = canvasGo.AddComponent<HmdTrajectoryRecorder>();
            var recSo = new SerializedObject(rec);
            TrySetObjectRef(recSo, "hmd", hmd);
            TrySetObjectRef(recSo, "tracker", tracker);
            TrySetObjectRef(recSo, "registry", registry);
            var trackerZonesProp = new SerializedObject(tracker).FindProperty("zones");
            if (trackerZonesProp != null && trackerZonesProp.isArray)
            {
                var zonesProp = recSo.FindProperty("zones");
                if (zonesProp != null && zonesProp.isArray)
                {
                    zonesProp.arraySize = trackerZonesProp.arraySize;
                    for (int i = 0; i < trackerZonesProp.arraySize; i++)
                    {
                        zonesProp.GetArrayElementAtIndex(i).objectReferenceValue =
                            trackerZonesProp.GetArrayElementAtIndex(i).objectReferenceValue;
                    }
                }
            }
            TrySetFloat(recSo, "sampleInterval", 1.0f);
            TrySetFloat(recSo, "hysteresisShrink", HysteresisShrink);
            recSo.ApplyModifiedPropertiesWithoutUndo();

            return hud;
        }

        private static void TrySetObjectRef(SerializedObject so, string propName, Object? value)
        {
            var p = so.FindProperty(propName);
            if (p == null)
            {
                Debug.LogWarning($"[MainDemoSceneSetup] FindProperty 失敗: {so.targetObject.GetType().Name}.{propName}");
                return;
            }
            p.objectReferenceValue = value;
        }

        private static void SetPlayerZoneArray(SerializedObject so, string propName, PlayerZone[] zones)
        {
            var p = so.FindProperty(propName);
            if (p == null || !p.isArray)
            {
                Debug.LogWarning($"[MainDemoSceneSetup] 配列プロパティが見つかりません: {propName}");
                return;
            }
            p.arraySize = zones.Length;
            for (int i = 0; i < zones.Length; i++)
            {
                p.GetArrayElementAtIndex(i).objectReferenceValue = zones[i];
            }
        }

        private static void TrySetFloat(SerializedObject so, string propName, float value)
        {
            var p = so.FindProperty(propName);
            if (p != null) p.floatValue = value;
        }

        private static void TrySetInt(SerializedObject so, string propName, int value)
        {
            var p = so.FindProperty(propName);
            if (p != null) p.intValue = value;
        }

        private static void TrySetBool(SerializedObject so, string propName, bool value)
        {
            var p = so.FindProperty(propName);
            if (p != null) p.boolValue = value;
        }

        private static void TrySetString(SerializedObject so, string propName, string value)
        {
            var p = so.FindProperty(propName);
            if (p != null) p.stringValue = value;
        }

        private static void TrySetVector2(SerializedObject so, string propName, Vector2 value)
        {
            var p = so.FindProperty(propName);
            if (p != null) p.vector2Value = value;
        }

        private static void TrySetVector3(SerializedObject so, string propName, Vector3 value)
        {
            var p = so.FindProperty(propName);
            if (p != null) p.vector3Value = value;
        }

        private static void TrySetColor(SerializedObject so, string propName, Color value)
        {
            var p = so.FindProperty(propName);
            if (p != null) p.colorValue = value;
        }
    }
}
