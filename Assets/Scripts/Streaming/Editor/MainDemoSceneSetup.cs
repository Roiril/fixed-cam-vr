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
        private const string StatusHudName = "StatusHud";
        private const string ControllerGuideName = "ControllerGuide";
        private const string DiagnosticsName = "Diagnostics";
        private const string DebugHudName = "DebugHud"; // 旧構成の掃除用（削除対象）
        private const string StartupFaderName = "StartupFader";
        private const string BgmName = "[Bgm]";
        private const string BgmClipPath = "Assets/Art/Audio/HorrBGM.mp3";
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
            DeleteIfExists($"{LogicGroupName}/{StatusHudName}");
            DeleteIfExists($"{LogicGroupName}/{ControllerGuideName}");
            DeleteIfExists($"{LogicGroupName}/{DiagnosticsName}");
            DeleteIfExists($"{CenterEyePath}/{DebugHudName}");   // 旧 HUD Canvas（統合前）
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

            // ControllerHaptics（右コントローラ振動）を [Streaming] に冪等 get-or-add。Assembly-CSharp 型のため
            // Editor asmdef から直接参照できず reflection で解決する（OvrControllerBridge を string 取得するのと同型）。
            MonoBehaviour? haptics = null;
            var hapticsType = System.Type.GetType("FixedCamVr.OvrBridge.ControllerHaptics, Assembly-CSharp");
            if (hapticsType != null)
            {
                haptics = streaming.GetComponent(hapticsType) as MonoBehaviour
                          ?? streaming.AddComponent(hapticsType) as MonoBehaviour;
            }
            else
            {
                Debug.LogWarning("[MainDemoSceneSetup] ControllerHaptics 型が解決できません（Assembly-CSharp 未コンパイル?）。触覚フィードバックの配線をスキップ。");
            }

            var centerEye = GameObject.Find(CenterEyePath);
            if (centerEye == null)
            {
                Debug.LogError($"[MainDemoSceneSetup] '{CenterEyePath}' が見つかりません。OVRCameraRig が === Rig === 配下にあるか確認してください。");
                return;
            }

            // 0.5. Phase B/C: Screen 上に CameraSwitchDirector / SwitchAudioCue / SignalLostFx を冪等配置。
            //      Screen（= ScreenOverlayController の GameObject）は MjpegScreen と material を共有するため、
            //      dip-to-black（_SwitchDim）/ 砂嵐（_SignalLost）を同じマテリアルへ書ける。
            //      Screen は prefab instance で削除再生成しないので GetComponent 優先（無ければ AddComponent）。
            var overlay = Object.FindObjectOfType<ScreenOverlayController>(includeInactive: true);
            if (overlay == null)
                Debug.LogWarning("[MainDemoSceneSetup] ScreenOverlayController が見つかりません。Director/SignalLostFx/CueScheduler の配線をスキップ。");

            CameraSwitchDirector? director = null;
            SignalLostFx? signalFx = null;
            var screenGo = overlay != null ? overlay.gameObject : null;
            if (screenGo != null)
            {
                // 切替音マスク（AudioSource + 空クリップ）。
                var audioSource = screenGo.GetComponent<AudioSource>();
                if (audioSource == null) audioSource = screenGo.AddComponent<AudioSource>();
                audioSource.playOnAwake = false;
                audioSource.spatialBlend = 0f;
                var audioCue = screenGo.GetComponent<SwitchAudioCue>();
                if (audioCue == null) audioCue = screenGo.AddComponent<SwitchAudioCue>();
                var audioSo = new SerializedObject(audioCue);
                TrySetObjectRef(audioSo, "source", audioSource);
                audioSo.ApplyModifiedPropertiesWithoutUndo();

                // カメラ切替 Director（時間ガード + dip-to-black）。
                director = screenGo.GetComponent<CameraSwitchDirector>();
                if (director == null) director = screenGo.AddComponent<CameraSwitchDirector>();
                var dirSo = new SerializedObject(director);
                TrySetObjectRef(dirSo, "registry", registry);
                TrySetObjectRef(dirSo, "overlay", overlay);
                TrySetObjectRef(dirSo, "audioCue", audioCue);
                dirSo.ApplyModifiedPropertiesWithoutUndo();

                // フェイルソフト（信号断 → 砂嵐 / トラッキングロスト → 追従凍結 + 弱ノイズ）。
                signalFx = screenGo.GetComponent<SignalLostFx>();
                if (signalFx == null) signalFx = screenGo.AddComponent<SignalLostFx>();
                var sigSo = new SerializedObject(signalFx);
                TrySetObjectRef(sigSo, "registry", registry);
                var screenAnchor = screenGo.GetComponent<ScreenAnchor>();
                if (screenAnchor != null) TrySetObjectRef(sigSo, "screenAnchor", screenAnchor);
                sigSo.ApplyModifiedPropertiesWithoutUndo();
            }

            // 0.6. CameraSwitchInput（[Streaming] 上・キーボード切替）を Director 経由へ配線。
            var switchInput = streaming.GetComponent<CameraSwitchInput>();
            if (switchInput != null && director != null)
            {
                var siSo = new SerializedObject(switchInput);
                TrySetObjectRef(siSo, "director", director);
                siSo.ApplyModifiedPropertiesWithoutUndo();
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
            if (director != null) TrySetObjectRef(trackerSo, "director", director);
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

            // 2.75. DiscoveryClient（cameraId → 現在 IP を実時間解決し、断が続いたカメラを /info 照合の上で自動張替）。
            //       [Streaming] は prefab instance で削除再生成しないため、既存を再利用して冪等にする
            //       （GetComponent 優先。無ければ AddComponent）。registry / showControl を配線。
            var discovery = streaming.GetComponent<DiscoveryClient>();
            if (discovery == null) discovery = streaming.AddComponent<DiscoveryClient>();
            var discSo = new SerializedObject(discovery);
            TrySetObjectRef(discSo, "registry", registry);
            if (showControl != null) TrySetObjectRef(discSo, "showControl", showControl);
            discSo.ApplyModifiedPropertiesWithoutUndo();

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

            // 2.9. CourseRegistrationController（HMD N 点登録。右トリガー 2 秒長押しで開始、
            //      床の×印マーカーをコントローラ先端でタッチ → CourseFrame へ剛体変換を解いて渡す。
            //      Verify でワイヤーフレーム表示 → B 確定 / A やり直し（プレビューはトランザクション。
            //      キャンセル退場は確定前 state へロールバック）。
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
            //       overlay は 0.5 で取得済み（Director/SignalLostFx と同じ Screen 上）。
            var cueScheduler = trackerGo.AddComponent<CueScheduler>();
            var schSo = new SerializedObject(cueScheduler);
            if (overlay != null) TrySetObjectRef(schSo, "overlay", overlay);
            schSo.ApplyModifiedPropertiesWithoutUndo();

            var lapCounter = trackerGo.AddComponent<LapCounter>();
            var lapSo = new SerializedObject(lapCounter);
            TrySetObjectRef(lapSo, "registry", registry);
            // director 経由で「出どころ Zone」だけ周回に数える（手動 / Web 固定 / 外部 / インサートは数えない）。
            if (director != null) TrySetObjectRef(lapSo, "director", director);
            if (showControl != null) TrySetObjectRef(lapSo, "showControl", showControl);
            TrySetObjectRef(lapSo, "cueScheduler", cueScheduler);
            TrySetBool(lapSo, "seedInitialZone", true);
            TrySetBool(lapSo, "logChanges", true);
            lapSo.ApplyModifiedPropertiesWithoutUndo();

            // 2.97. タイムライン（show.json timeline スキーマ v2）: InsertController + TimelineDirector を
            //       [Tracker] に載せる（毎回作り直しなので冪等）。TimelineDirector は CueScheduler.CameraEntered を
            //       購読して区間 cues[] を CueScheduler へ・insert を InsertController へ・区間 post を
            //       ShowControlClient.SetPostOverride へ分配する。InsertController は Screen の Director / overlay と
            //       showControl を叩く（dip-to-black 差し替え・insert 中 post 層）。
            var insertController = trackerGo.AddComponent<InsertController>();
            var insSo = new SerializedObject(insertController);
            if (director != null) TrySetObjectRef(insSo, "director", director);
            if (overlay != null) TrySetObjectRef(insSo, "overlay", overlay);
            if (showControl != null) TrySetObjectRef(insSo, "showControl", showControl);
            insSo.ApplyModifiedPropertiesWithoutUndo();

            var timelineDirector = trackerGo.AddComponent<TimelineDirector>();
            var tlSo = new SerializedObject(timelineDirector);
            TrySetObjectRef(tlSo, "cueScheduler", cueScheduler);
            TrySetObjectRef(tlSo, "insertController", insertController);
            if (showControl != null) TrySetObjectRef(tlSo, "showControl", showControl);
            tlSo.ApplyModifiedPropertiesWithoutUndo();

            // ShowControlClient.cueScheduler を新 CueScheduler へ配線（毎回 Tracker を作り直すため必須）。
            // switchDirector（cameraOverride を出どころ Override として通す）・timelineDirector も配線。
            if (showControl != null)
            {
                var scSchedSo = new SerializedObject(showControl);
                TrySetObjectRef(scSchedSo, "cueScheduler", cueScheduler);
                if (director != null) TrySetObjectRef(scSchedSo, "switchDirector", director);
                TrySetObjectRef(scSchedSo, "timelineDirector", timelineDirector);
                scSchedSo.ApplyModifiedPropertiesWithoutUndo();
            }

            // 3. StartupFader（OVR 初期化 / 砂時計 / MJPEG 接続待ちを黒で覆い隠す）
            CreateStartupFader(centerEye.transform, registry);

            // 3.5. BGM（アプリ起動中ループ再生・2D）。get-or-create で冪等 —
            //      delete+recreate にしない（現場で Inspector 調整した volume を Setup 再実行で潰さないため）。
            CreateOrUpdateBgm(logic.transform);

            // 4. StatusHud（単一サーフェス・緩追従・TMP）。本番は startVisible=false・視界保護。右 B でトグル。
            //    lap / ゾーン / 次の cue / 信号 / 要再登録 を 1 枚に統合し、登録中は登録ガイダンスを強制表示。
            //    world-space（Logic 直下・head 非親）で StatusHud が自前に緩追従する。
            var statusHud = CreateStatusHud(logic.transform, centerEye.transform, registry, tracker,
                                            director, signalFx, lapCounter, cueScheduler, courseFrame, registration);

            // 4.2. ControllerGuidePanel（スタッフ専用・右コントローラに追従する操作早見表）。
            //      右コントローラアンカー（RightHandAnchor）+ CenterEyeAnchor へ配線。rightHand が
            //      無い（OVR リグ未配置等）なら生成をスキップ（追従先が無いと常時非表示になるため）。
            ControllerGuidePanel? guidePanel = null;
            if (rightHand != null)
                guidePanel = CreateControllerGuidePanel(logic.transform, rightHand.transform, centerEye.transform);
            else
                Debug.LogWarning($"[MainDemoSceneSetup] '{RightHandPath}' が無いため ControllerGuidePanel の生成をスキップ。");

            // 4.5. Diagnostics（HUD には出さない診断: [HudDump] ログ + HMD 軌跡 CSV + Editor H トグル）
            CreateDiagnostics(logic.transform, registry, tracker, centerEye.transform, statusHud);

            // 5. OvrControllerBridge に StatusHud / CourseRegistration / CourseFrame / Director / SignalFx / ランリセット対象を接続
            if (ovrBridge != null)
            {
                var bridgeSo = new SerializedObject(ovrBridge);
                if (statusHud != null) TrySetObjectRef(bridgeSo, "statusHud", statusHud);
                if (director != null) TrySetObjectRef(bridgeSo, "switchDirector", director);
                if (signalFx != null) TrySetObjectRef(bridgeSo, "signalFx", signalFx);
                if (showControl != null) TrySetObjectRef(bridgeSo, "showControl", showControl);
                TrySetObjectRef(bridgeSo, "courseRegistration", registration);
                TrySetObjectRef(bridgeSo, "courseFrame", courseFrame);
                // 右グリップ 2 秒長押し＝ランリセットの対象。
                TrySetObjectRef(bridgeSo, "lapCounter", lapCounter);
                TrySetObjectRef(bridgeSo, "cueScheduler", cueScheduler);
                // 触覚フィードバック（押下受理 / 長押し進行 / 発火 / 失敗の振動）。
                if (haptics != null) TrySetObjectRef(bridgeSo, "haptics", haptics);
                // スタッフ用コントローラ操作ガイド（モード遷移で本文を切替・接続状態を push）。
                if (guidePanel != null) TrySetObjectRef(bridgeSo, "guidePanel", guidePanel);
                bridgeSo.ApplyModifiedPropertiesWithoutUndo();
            }

            // 6. シーン保存
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);

            Selection.activeGameObject = trackerGo;
            Debug.Log("[MainDemoSceneSetup] 完了。Zones=4（静的フォールバック・推測配置） / Tracker（Director 経由切替） / CourseFrame + ZoneLayoutApplier（show.json layout で生成） / CourseRegistrationController（トリガー 2 秒長押し→N 点登録、A=マーク/B=確定。スティックナッジ廃止） / LapCounter + CueScheduler（周回×ゾーンで cue 自動発火・ライブ優先。周回は director の Zone 切替のみ数え、手動/Web固定/外部/インサートは不算入。runEpoch 変化 or 右グリップ 2 秒長押しでランリセット） / TimelineDirector + InsertController（show.json timeline v2: 区間 cue override / インサートショット / 区間 post 上書き。timeline 不在時は従来 schedule で動く） / CameraSwitchDirector + SwitchAudioCue + SignalLostFx（切替作法・フェイルソフト・Screen 上） / [Bgm]（起動中ループ BGM・get-or-create） / StartupFader / StatusHud（単一サーフェス・緩追従・startVisible=false・右 B トグル） / ControllerGuidePanel（スタッフ専用・右コントローラ追従・モード別操作早見表） / Diagnostics（[HudDump] ログ + HMD 軌跡 CSV + Editor H） / OvrBridge（右手 4 入力: A=Next / B=ステータス / グリップ長押し=ランリセット / トリガー長押し=登録）。シーン保存済み。" +
                      "次は URP-Balanced-Renderer.asset に FullScreenPassRendererFeature を追加（手動）。" +
                      "詳細: docs/onsite-checklist.md");
        }

        // ----- helpers -----

        private static void DeleteIfExists(string path)
        {
            // GameObject.Find は非アクティブを返さないため、ユーザーが Hierarchy で
            // 無効化した既存配置を拾えず重複生成される（冪等性の破れ）。
            // さらに「最初の 1 個だけ削除」だと過去の Setup 実行で蓄積した同名重複
            // （実害: CenterEyeAnchor 配下に DebugHud ×7 / StartupFader ×8 が残存）を
            // 掃除しきれないため、親を解決して直下の同名の子を全削除する。
            int cut = path.LastIndexOf('/');
            if (cut < 0)
            {
                foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
                {
                    if (root.name == path) Object.DestroyImmediate(root);
                }
                return;
            }

            var parent = FindByPath(path[..cut]);
            if (parent == null) return;
            string leaf = path[(cut + 1)..];
            for (int i = parent.transform.childCount - 1; i >= 0; i--)
            {
                var child = parent.transform.GetChild(i);
                if (child.name == leaf) Object.DestroyImmediate(child.gameObject);
            }
        }

        // 非アクティブを含むパス解決。2 形式に対応:
        //   (a) ルート名から始まる絶対形式（"=== Logic ===/[Zones]"）
        //   (b) ルート名を含まない相対形式（"OVRCameraRig/TrackingSpace/CenterEyeAnchor"。
        //       GameObject.Find と同様に任意ルート配下を探す。CenterEyePath がこの形式で、
        //       旧実装は (a) しか解決できず CenterEye 配下の掃除が常に no-op だった）
        private static GameObject? FindByPath(string path)
        {
            var segs = path.Split('/');
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (root.name == segs[0])
                {
                    Transform? t = root.transform;
                    for (int i = 1; i < segs.Length && t != null; i++)
                    {
                        t = t.Find(segs[i]);
                    }
                    if (t != null) return t.gameObject;
                }

                // transform.Find は "A/B/C" の相対パスを非アクティブ込みで辿れる。
                var rel = root.transform.Find(path);
                if (rel != null) return rel.gameObject;
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

        // BGM（起動中ループ再生）。[Bgm] を === Logic === 直下に get-or-create し、AudioSource へ
        // クリップ・ループ・2D 化を書く。volume は新規作成時のみ既定値を入れる（現場調整の保持）。
        private static void CreateOrUpdateBgm(Transform parent)
        {
            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(BgmClipPath);
            if (clip == null)
            {
                Debug.LogWarning($"[MainDemoSceneSetup] BGM クリップが見つかりません: {BgmClipPath}。[Bgm] の配置をスキップ。");
                return;
            }

            var existing = parent.Find(BgmName);
            GameObject go;
            bool created = existing == null;
            if (created)
            {
                go = new GameObject(BgmName);
                go.transform.SetParent(parent, worldPositionStays: false);
            }
            else
            {
                go = existing!.gameObject;
            }

            var source = go.GetComponent<AudioSource>();
            if (source == null) source = go.AddComponent<AudioSource>();
            source.clip = clip;
            source.loop = true;
            source.playOnAwake = true;
            source.spatialBlend = 0f; // 2D（HMD の向き・位置に依存しない環境 BGM）
            if (created) source.volume = 0.5f;
        }

        // 単一サーフェス StatusHud を world-space（parent 直下・head 非親）に作る。StatusHud が自前で緩追従する。
        private static StatusHud CreateStatusHud(Transform parent, Transform head, CameraStreamRegistry registry,
            PlayerZoneTracker tracker, CameraSwitchDirector? director, SignalLostFx? signalFx,
            LapCounter lapCounter, CueScheduler cueScheduler, CourseFrame courseFrame,
            CourseRegistrationController registration)
        {
            // 見た目（Canvas / TMP / 配置 SerializeField / head・registration・courseFrame 参照）は
            // Editor プレビュー（RegistrationVizPreview の HUD 検証）と共有するため CreateStatusHudVisual に集約。
            // ここではステータス内容ソース（registry / tracker / lap / cue / signal / switch）だけ足す。
            var hud = CreateStatusHudVisual(parent, head, registration, courseFrame);
            var hudSo = new SerializedObject(hud);
            TrySetObjectRef(hudSo, "registry", registry);
            TrySetObjectRef(hudSo, "tracker", tracker);
            TrySetObjectRef(hudSo, "lapCounter", lapCounter);
            TrySetObjectRef(hudSo, "cueScheduler", cueScheduler);
            if (signalFx != null) TrySetObjectRef(hudSo, "signalFx", signalFx);
            if (director != null) TrySetObjectRef(hudSo, "switchDirector", director);
            hudSo.ApplyModifiedPropertiesWithoutUndo();

            return hud;
        }

        /// <summary>
        /// StatusHud の**見た目部分**（WorldSpace Canvas + CanvasScaler + TextMeshProUGUI + StatusHud
        /// コンポーネント + 配置系 SerializeField + head / registration / courseFrame 参照）を組んで返す。
        /// 本番シーン生成（<see cref="CreateStatusHud"/>）と Editor プレビュー（登録ガイダンス HUD の
        /// 位置・サイズ感の机上検証）で**同一の見た目を再現するための共有シーム**。ステータス内容ソース
        /// （registry / tracker / lap / cue / signal / switch）は含めない — 呼び出し側が足す。
        /// パネル寸法・fontSize・配置の数値定義はこの 1 箇所だけに置く（二重定義を作らない）。
        /// </summary>
        public static StatusHud CreateStatusHudVisual(Transform parent, Transform head,
            CourseRegistrationController? registration, CourseFrame? courseFrame)
        {
            var canvasGo = new GameObject(StatusHudName);
            canvasGo.transform.SetParent(parent, worldPositionStays: false);

            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvasGo.AddComponent<UnityEngine.UI.CanvasScaler>();

            var rt = (RectTransform)canvasGo.transform;
            rt.sizeDelta = new Vector2(720f, 320f);
            rt.localScale = Vector3.one * 0.001f;

            var textGo = new GameObject("StatusText");
            textGo.transform.SetParent(canvasGo.transform, worldPositionStays: false);
            var textRt = textGo.AddComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.anchoredPosition = Vector2.zero;
            textRt.sizeDelta = Vector2.zero;
            textRt.localScale = Vector3.one;
            textRt.localPosition = Vector3.zero;

            var tmp = textGo.AddComponent<TextMeshProUGUI>();
            tmp.text = "";
            tmp.fontSize = 34f;
            tmp.color = new Color(0.9f, 1f, 0.95f, 1f);
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.enableWordWrapping = false;
            tmp.richText = true;

            var hud = canvasGo.AddComponent<StatusHud>();
            var hudSo = new SerializedObject(hud);
            TrySetObjectRef(hudSo, "text", tmp);
            if (courseFrame != null) TrySetObjectRef(hudSo, "courseFrame", courseFrame);
            if (registration != null) TrySetObjectRef(hudSo, "registration", registration);
            TrySetObjectRef(hudSo, "head", head);
            // 緩追従・配置の既定（prefab-YAML 未反映罠を避けるため setup が明示的に書く）。
            TrySetFloat(hudSo, "distance", 1.6f);
            TrySetFloat(hudSo, "heightOffset", -0.43f);
            TrySetFloat(hudSo, "pitchDeg", -15f);
            TrySetFloat(hudSo, "yawDeadzoneDeg", 10f);
            TrySetFloat(hudSo, "smoothTime", 0.30f);
            TrySetFloat(hudSo, "updateInterval", 0.25f);
            TrySetBool(hudSo, "startVisible", false); // 本番の視界保護（右 B でトグル）
            TrySetFloat(hudSo, "autoHideSec", 0f);    // 既定無効（現場で必要なら設定）
            TrySetFloat(hudSo, "recenterAutoShowSec", 5f);
            hudSo.ApplyModifiedPropertiesWithoutUndo();

            return hud;
        }

        // スタッフ用の操作ガイドパネル（右コントローラに追従する小さな早見表）を作る。
        // 見た目は StatusHud（CreateStatusHudVisual）を踏襲するが、パネル幅はコントローラ幅の
        // 2〜3 倍程度に収める規模感（小さめ・左寄せの操作リスト）。配置追従は ControllerGuidePanel が
        // controller / head を見て自前で行う（world-space・parent 直下・controller 非親）。
        private static ControllerGuidePanel CreateControllerGuidePanel(Transform parent,
            Transform controller, Transform head)
        {
            var canvasGo = new GameObject(ControllerGuideName);
            canvasGo.transform.SetParent(parent, worldPositionStays: false);

            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvasGo.AddComponent<UnityEngine.UI.CanvasScaler>();

            var rt = (RectTransform)canvasGo.transform;
            // sizeDelta 560 × 0.0005 = 0.28m 幅（コントローラ幅 ≈0.1m の 2〜3 倍）。
            rt.sizeDelta = new Vector2(560f, 300f);
            rt.localScale = Vector3.one * 0.0005f;

            var textGo = new GameObject("GuideText");
            textGo.transform.SetParent(canvasGo.transform, worldPositionStays: false);
            var textRt = textGo.AddComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.anchoredPosition = Vector2.zero;
            textRt.sizeDelta = Vector2.zero;
            textRt.localScale = Vector3.one;
            textRt.localPosition = Vector3.zero;

            var tmp = textGo.AddComponent<TextMeshProUGUI>();
            tmp.text = "";
            tmp.fontSize = 26f;                 // StatusHud(34) より小さめ
            tmp.color = new Color(0.9f, 1f, 0.95f, 1f);
            tmp.alignment = TextAlignmentOptions.Left;
            tmp.enableWordWrapping = false;
            tmp.richText = false;

            var panel = canvasGo.AddComponent<ControllerGuidePanel>();
            var so = new SerializedObject(panel);
            TrySetObjectRef(so, "text", tmp);
            TrySetObjectRef(so, "controller", controller);
            TrySetObjectRef(so, "head", head);
            // 配置の既定（prefab-YAML 未反映罠を避けるため setup が明示的に書く）。
            TrySetFloat(so, "heightOffset", 0.12f);
            TrySetFloat(so, "awayOffset", 0.06f);
            TrySetFloat(so, "smoothTime", 0.15f);
            so.ApplyModifiedPropertiesWithoutUndo();

            return panel;
        }

        // HUD に出さない診断（[HudDump] ログ + HMD 軌跡 CSV + Editor H トグル）を 1 個の GameObject に載せる。
        private static void CreateDiagnostics(Transform parent, CameraStreamRegistry registry,
            PlayerZoneTracker tracker, Transform hmd, StatusHud statusHud)
        {
            var go = new GameObject(DiagnosticsName);
            go.transform.SetParent(parent, worldPositionStays: false);

            // HudLogDumper: 接続 / カメラ / ゾーン / HMD を [HudDump] プレフィックスで Console に吐く
            // （MCP read_console で時系列取得するため。診断中は 1s 周期・本番は 30 等へ）。
            var dumper = go.AddComponent<HudLogDumper>();
            var dumperSo = new SerializedObject(dumper);
            TrySetObjectRef(dumperSo, "registry", registry);
            TrySetObjectRef(dumperSo, "tracker", tracker);
            TrySetObjectRef(dumperSo, "hmd", hmd);
            TrySetFloat(dumperSo, "periodicIntervalSec", 1f);
            dumperSo.ApplyModifiedPropertiesWithoutUndo();

            // HudToggleInput: Editor（Flat シーン）用の H キーで StatusHud をトグル（実機は右 B）。
            var toggle = go.AddComponent<HudToggleInput>();
            var toggleSo = new SerializedObject(toggle);
            TrySetObjectRef(toggleSo, "hud", statusHud);
            toggleSo.ApplyModifiedPropertiesWithoutUndo();

            // HmdTrajectoryRecorder: HMD 位置 / 各ゾーン含有判定を CSV で persistentDataPath に書き出す。
            // 実機 Quest で歩いた後 adb pull で取り出し、ゾーン配置の妥当性をシュビーが解析する用途。
            // tracker.zones を直接参照できないので、Tracker と同じ並びを SerializedObject 経由で複製する。
            var rec = go.AddComponent<HmdTrajectoryRecorder>();
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
