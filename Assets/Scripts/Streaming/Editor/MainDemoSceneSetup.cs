#nullable enable
using FixedCamVr.Diagnostics;
using FixedCamVr.Streaming.Cg;
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
        private const string EndingFaderName = "ShowEndingFader";
        private const string IntroPromptName = "IntroPrompt";
        private const string IntroVeilName = "IntroVeil";
        private const string IntroDirectorName = "IntroDirector";
        private const string TitleName = "Title";
        private const string BgmName = "[Bgm]";
        private const string SoundName = "[Sound]";

        /// <summary>音源が焼かれているかの抜き取り検査（全部並べても意味が無いので代表を数本）。</summary>
        private static readonly string[] SoundProbeResources =
        {
            "bed_room", "bed_device", "bed_seal", "sfx_switch_1", "sfx_seal_close", "sfx_shatter",
        };
        private const string BgmClipPath = "Assets/Art/Audio/HorrBGM.mp3";
        /// <summary>CG 人形だけを置くレイヤ。仮想カメラだけが描き、HMD カメラからは外す。</summary>
        private const string CgLayerName = "ShowCg";
        // Tracker と HmdTrajectoryRecorder で同値を使う（片方だけ変えると
        // 解析 CSV と実挙動の判定がズレるため 1 本化）。
        private const float HysteresisShrink = 0.15f;

        [MenuItem("Tools/FixedCamVr/Setup/Setup Main Demo Scene", priority = 50)]
        public static void Setup()
        {
            var scene = SceneManager.GetActiveScene();
            if (scene.path != MainScenePath)
            {
                // 別シーン（TableDuoMain 等）を開いたまま実行されがち。**未保存でなければ自分で Main を開く**。
                // 旧実装はモーダルで止めていたが、MCP 経由の自動実行だとダイアログが Editor ごと固まらせ、
                // 人がクリックするまで全部の応答が止まる（2026-07-27 実害）。
                // CLI（batchmode）は人が居ないうえ守るべき手作業も無い（起動直後の空シーン）ので素通りする。
                if (scene.isDirty && !EditorCliArgs.IsBatch)
                {
                    EditorUtility.DisplayDialog(
                        "Main シーンを開いてから実行してください",
                        $"アクティブシーン: {scene.path}（未保存変更あり）\n期待: {MainScenePath}\n\n" +
                        "未保存の変更を失わないため自動では開きません。保存または破棄してから再実行してください。",
                        "OK");
                    return;
                }
                Debug.Log($"[MainDemoSceneSetup] アクティブシーンが {scene.path} だったので {MainScenePath} を開きます。");
                EditorSceneManager.OpenScene(MainScenePath, OpenSceneMode.Single);
                scene = SceneManager.GetActiveScene();
                if (scene.path != MainScenePath)
                {
                    Debug.LogError($"[MainDemoSceneSetup] {MainScenePath} を開けませんでした。");
                    return;
                }
            }

            // ⚠ batchmode で DisplayDialog は表示されず false（＝キャンセル）を返す。
            //    分岐を入れないと CLI からの実行が**黙って何もせずに終わる**。
            if (scene.isDirty && !EditorCliArgs.IsBatch)
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
            DeleteIfExists($"{CenterEyePath}/{EndingFaderName}");
            DeleteIfExists($"{CenterEyePath}/{IntroPromptName}");
            DeleteIfExists($"{CenterEyePath}/{IntroVeilName}");
            DeleteIfExists($"{CenterEyePath}/{TitleName}");
            DeleteIfExists($"{LogicGroupName}/{IntroDirectorName}");

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

                // 演出としての「映像の乱れ」（_Glitch の唯一の writer）。障害表示の砂嵐とは別系統。
                var glitchFx = screenGo.GetComponent<GlitchFx>();
                if (glitchFx == null) glitchFx = screenGo.AddComponent<GlitchFx>();

                // 撮像の質（暗部ノイズ・固定パターン・自動露出の追従遅れ）と、凍らせた 1 枚
                // （ホールド / 焼き付き）。show.json の feel が無くてもコード既定で効く。
                var feelFx = screenGo.GetComponent<CameraFeelFx>();
                if (feelFx == null) feelFx = screenGo.AddComponent<CameraFeelFx>();

                // カメラ切替 Director（時間ガード + dip-to-black / 乱れ遷移）。
                director = screenGo.GetComponent<CameraSwitchDirector>();
                if (director == null) director = screenGo.AddComponent<CameraSwitchDirector>();
                var dirSo = new SerializedObject(director);
                TrySetObjectRef(dirSo, "registry", registry);
                TrySetObjectRef(dirSo, "overlay", overlay);
                TrySetObjectRef(dirSo, "audioCue", audioCue);
                TrySetObjectRef(dirSo, "glitchFx", glitchFx);
                TrySetObjectRef(dirSo, "feelFx", feelFx);
                dirSo.ApplyModifiedPropertiesWithoutUndo();

                // フェイルソフト（信号断 → 砂嵐 / トラッキングロスト → 追従凍結 + 弱ノイズ）。
                signalFx = screenGo.GetComponent<SignalLostFx>();
                if (signalFx == null) signalFx = screenGo.AddComponent<SignalLostFx>();
                var sigSo = new SerializedObject(signalFx);
                TrySetObjectRef(sigSo, "registry", registry);
                var screenAnchor = screenGo.GetComponent<ScreenAnchor>();
                if (screenAnchor != null) TrySetObjectRef(sigSo, "screenAnchor", screenAnchor);
                sigSo.ApplyModifiedPropertiesWithoutUndo();

                // ブラウン管の曲面（中央が体験者側へ膨らむ）。**シーンに焼く**必要がある
                // — Awake でメッシュを組むので、付いていなければ平らな Quad のまま出る。
                // ⚠ Build は呼ばない。生成した Mesh はアセットではないので、Editor で差し替えると
                //   シーンに壊れた参照が焼かれる。形は実行時（Awake）に組み直す。
                if (screenGo.GetComponent<CrtScreenMesh>() == null)
                    screenGo.AddComponent<CrtScreenMesh>();
            }

            // 0.6. CameraSwitchInput（[Streaming] 上・キーボード切替）を Director 経由へ配線。
            var switchInput = streaming.GetComponent<CameraSwitchInput>();
            if (switchInput != null && director != null)
            {
                var siSo = new SerializedObject(switchInput);
                TrySetObjectRef(siSo, "director", director);
                siSo.ApplyModifiedPropertiesWithoutUndo();
            }

            // 0.7. CG レイヤ（映像の上に立つ人形）。Screen の material（ScreenComposite）の 3 層目 _CgTex へ
            //      仮想カメラの絵を書く。**HMD カメラからは ShowCg レイヤを外す**（外さないと人形が
            //      VR 空間にそのまま浮いて見え、「映像の中に居る」が壊れる）。
            //      体験者のハンドトラッキングは Assembly-CSharp の OvrHandTrackingBridge から push する。
            ShowCgLayer? cgLayer = null;
            if (screenGo != null)
            {
                cgLayer = screenGo.GetComponent<ShowCgLayer>();
                if (cgLayer == null) cgLayer = screenGo.AddComponent<ShowCgLayer>();
                var cgSo = new SerializedObject(cgLayer);
                TrySetObjectRef(cgSo, "screenRenderer", screenGo.GetComponent<Renderer>());
                TrySetObjectRef(cgSo, "showControl", Object.FindObjectOfType<ShowControlClient>(includeInactive: true));
                cgSo.ApplyModifiedPropertiesWithoutUndo();
            }

            int cgLayerIndex = LayerMask.NameToLayer(CgLayerName);
            if (cgLayerIndex < 0)
            {
                Debug.LogWarning($"[MainDemoSceneSetup] レイヤ '{CgLayerName}' が未定義。CG 人形は出ません" +
                                 "（Project Settings > Tags and Layers に追加）。");
            }
            else
            {
                int mask = ~(1 << cgLayerIndex);
                foreach (var cam in Object.FindObjectsOfType<Camera>(includeInactive: true))
                {
                    if ((cam.cullingMask & (1 << cgLayerIndex)) == 0) continue;
                    cam.cullingMask &= mask;
                    EditorUtility.SetDirty(cam);
                }
            }

            // 体験者の素手 → 人形の腕。Editor asmdef から OVR / Assembly-CSharp を直接参照できないので
            // ControllerHaptics と同型の reflection で解決する。
            var handBridgeType = System.Type.GetType("FixedCamVr.OvrBridge.OvrHandTrackingBridge, Assembly-CSharp");
            if (handBridgeType != null)
            {
                var handBridge = streaming.GetComponent(handBridgeType) as MonoBehaviour
                                 ?? streaming.AddComponent(handBridgeType) as MonoBehaviour;
                if (handBridge != null)
                {
                    var hbSo = new SerializedObject(handBridge);
                    TrySetObjectRef(hbSo, "cgLayer", cgLayer);
                    TrySetObjectRef(hbSo, "trackingSpace", centerEye.transform.parent);
                    TrySetObjectRef(hbSo, "centerEye", centerEye.transform);
                    hbSo.ApplyModifiedPropertiesWithoutUndo();
                }
            }
            else
            {
                Debug.LogWarning("[MainDemoSceneSetup] OvrHandTrackingBridge 型が解決できません（Assembly-CSharp 未コンパイル?）。人形の腕は動きません。");
            }

            EnableSimultaneousHandsAndControllers();

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
            // ⚠ **起動と同時に位置合わせへ入る**（2026-08-13・canon/LEDGER.md 0023）。
            //   アプリを起動するのはスタッフなので、最初に必ず通る作業から始める。
            //   シーンに焼かれた値が SerializeField の初期値より優先されるので、ここで明示的に書く
            //   （書かないと既存シーンの 0 が残って「既定を true にしたのに入らない」になる）。
            TrySetBool(regSo, "startInRegistration", true);
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

            // 2.97. タイムライン（show.json timeline）: TimelineDirector + TakeRunner を [Tracker] に載せる
            //       （毎回作り直しなので冪等）。TimelineDirector は CueScheduler.CameraEntered を購読して
            //       区間 takes[] を TakeRunner へ・区間 post を ShowControlClient.SetPostOverride へ・
            //       区間 bgm を BgmDirector へ分配する。
            //       **演出の実行体は TakeRunner 1 つに一本化**（2026-07-25。旧 InsertController は廃止し、
            //       v2 の cues[] / insert は TimelineMigration が takes[] へ変換してから流れてくる）。
            var takeRunner = trackerGo.AddComponent<TakeRunner>();
            var takeSo = new SerializedObject(takeRunner);
            if (director != null) TrySetObjectRef(takeSo, "director", director);
            if (overlay != null) TrySetObjectRef(takeSo, "overlay", overlay);
            if (showControl != null) TrySetObjectRef(takeSo, "showControl", showControl);
            takeSo.ApplyModifiedPropertiesWithoutUndo();

            var timelineDirector = trackerGo.AddComponent<TimelineDirector>();
            var tlSo = new SerializedObject(timelineDirector);
            TrySetObjectRef(tlSo, "cueScheduler", cueScheduler);
            TrySetObjectRef(tlSo, "takeRunner", takeRunner);
            if (showControl != null) TrySetObjectRef(tlSo, "showControl", showControl);
            tlSo.ApplyModifiedPropertiesWithoutUndo();

            // 2.98. 体験の骨格（導入 → 本編 3 周 → 終了）。CueScheduler のゲートを開け閉めして
            //       演出・録画・区間 post / BGM・実測滞在をまとめて止める単一の首。
            var runDirector = trackerGo.AddComponent<ShowRunDirector>();
            var runSo = new SerializedObject(runDirector);
            TrySetObjectRef(runSo, "cueScheduler", cueScheduler);
            TrySetObjectRef(runSo, "timelineDirector", timelineDirector);
            if (showControl != null) TrySetObjectRef(runSo, "showControl", showControl);
            if (director != null) TrySetObjectRef(runSo, "switchDirector", director);
            runSo.ApplyModifiedPropertiesWithoutUndo();

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
            // 3.1. ShowEndingFader（体験の終わりを黒で閉じる）。相の変化だけを購読する。
            CreateEndingFader(centerEye.transform);

            // 3.2. 導入演出（パススルー → 2D スクリーン）。計画 2026-07-30_intro-passthrough-to-screen.md。
            //      覆い（枠）は head-lock なので CenterEyeAnchor 直下、進行役は Logic 直下に置く。
            //      show.json の run.intro が無い / enabled=false なら何も起きない（従来の見えになる）。
            var screenTf = screenGo != null ? screenGo.transform : null;
            var introVeil = CreateIntroVeil(centerEye.transform, screenTf);
            // 隔離殻（会場を黒で落とし、実物の壁と足元の床だけを残す面）。覆いと同じ GameObject に
            // 載せる — どちらも CenterEyeAnchor 直下の全画面パスで、描画順だけが違う（覆い 4900 → 殻 4910）。
            var containment = CreateContainmentShell(introVeil.gameObject, showControl);
            // 封印の箱（外から見た隔離）。world 空間の箱なので親の transform に依存しないが、
            // 隔離殻と同じ GameObject に載せて「境界を持つのはここ」を 1 箇所に見せる。
            var sealedBox = CreateSealedBox(introVeil.gameObject, showControl);
            var introDirector = CreateIntroDirector(logic.transform, runDirector, introVeil,
                screenGo != null ? screenGo.GetComponent<GlitchFx>() : null,
                registry, showControl, centerEye.transform, screenTf);
            // 導入の合図（段 5 の「右手をあげて」・開始位置の案内・歩き出し）。
            //   ⚠ **体験者には出さない**（2026-08-07〜）。読むのはスタッフだけで、門は
            //   StatusHud.StaffViewing。配線は StatusHud を作った後（下の 4 節）で行う。
            var introPrompt = CreateIntroPrompt(centerEye.transform, introDirector);
            // 3.3. タイトル画面「廻リ視」。導入の段 0（開始待ち）に被さる薄い層で、右 A で閉じる。
            //      head-lock なので CenterEyeAnchor 直下。**封印の箱（4920）より後（4950/4960）に描く**
            //      ので、黒を開けば既に立っている箱がそのまま現れる（壁が覗くフレームが構造的に無い）。
            var titleScreen = CreateTitleScreen(centerEye.transform, runDirector, introDirector,
                                                sealedBox, containment, showControl);
            // 3.35. 体験前の注意書き（周回リセット直後の真っ暗＝ TitleStage.Wait のあいだだけ）。
            //       題字と同じ 2.6m に立てる。⚠ 体験者に見せる唯一の文字なので、
            //       文言を変えたら `unity.ps1 menu hud-font` を再実行する（忘れると実機で豆腐）。
            var noticeGo = new GameObject("TitleNotice");
            noticeGo.transform.SetParent(centerEye.transform, worldPositionStays: false);
            var notice = noticeGo.AddComponent<FixedCamVr.Diagnostics.TitleNotice>();
            var noticeSo = new SerializedObject(notice);
            TrySetObjectRef(noticeSo, "titleScreen", titleScreen);
            TrySetFloat(noticeSo, "distanceM", 2.6f);
            noticeSo.ApplyModifiedPropertiesWithoutUndo();
            // 終幕（2D スクリーン → パススルー）。導入と**同じ覆い**を使う（開口の式を共有しないと
            // 閉じた形と開く形が食い違う）。IntroDirector と同じオブジェクトに載せるので、
            // 進行役が 2 つに散らず、PassthroughStyler の自己解決も 1 度で済む。
            var outroDirector = introDirector.gameObject.GetComponent<OutroDirector>();
            if (outroDirector == null) outroDirector = introDirector.gameObject.AddComponent<OutroDirector>();
            GlitchFx? screenGlitch = screenGo != null ? screenGo.GetComponent<GlitchFx>() : null;
            var outroSo = new SerializedObject(outroDirector);
            TrySetObjectRef(outroSo, "runDirector", runDirector);
            TrySetObjectRef(outroSo, "veil", introVeil);
            TrySetObjectRef(outroSo, "shell", containment);
            TrySetObjectRef(outroSo, "glitch", screenGlitch);
            outroSo.ApplyModifiedPropertiesWithoutUndo();
            // 導入側にも同じ殻を配る（自己解決に任せず明示する — 見つからないと隔離が黙って出ない）。
            var introShellSo = new SerializedObject(introDirector);
            TrySetObjectRef(introShellSo, "shell", containment);
            TrySetObjectRef(introShellSo, "sealedBox", sealedBox);
            introShellSo.ApplyModifiedPropertiesWithoutUndo();
            // 段 3 の構造の線（部屋の輪郭とカメラの印）。LineRenderer は world 空間で描くので
            // 親の transform には依存しない（IntroDirector と同じオブジェクトに載せる）。
            // パススルー自体の見た目（彩度・輪郭線）は Assembly-CSharp 側の PassthroughStyler が当てる。
            // Editor asmdef から OVR / Assembly-CSharp を直接参照できないので reflection で付ける
            // （OvrControllerBridge / ControllerHaptics と同型の作法）。
            EnsurePassthroughStyler(GameObject.Find("OVRCameraRig"));

            // 3.5. BGM（BgmDirector・2D）。get-or-create で冪等 — delete+recreate にしない
            //      （現場で Inspector 調整した音量を Setup 再実行で潰さないため）。
            //      show.json の bgmTracks / bgm / 区間 bgm 指示で切り替わる。未オーサリングなら
            //      defaultClip（HorrBGM）を従来どおりループ再生する。
            var bgmDirector = CreateOrUpdateBgm(logic.transform);
            if (bgmDirector != null)
            {
                var tlBgmSo = new SerializedObject(timelineDirector);
                TrySetObjectRef(tlBgmSo, "bgmDirector", bgmDirector);
                tlBgmSo.ApplyModifiedPropertiesWithoutUndo();
                if (showControl != null)
                {
                    var scBgmSo = new SerializedObject(showControl);
                    TrySetObjectRef(scBgmSo, "bgmDirector", bgmDirector);
                    scBgmSo.ApplyModifiedPropertiesWithoutUndo();
                }
            }

            // 3.6. 体験の音（ShowSoundDirector）。**既存の演出コードには何も足していない** —
            //      導入・終幕・タイトル・乱れ・信号断・周回の劣化を外から読んで鳴らすだけ。
            //      音源は `Resources/Sound/`（`py -3.11 tools/make-sounds.py` が焼く）。
            //      設計の正本は `.claude/rules/sound-design.md`。
            CreateOrUpdateSound(logic.transform);

            // 4. StatusHud（単一サーフェス・緩追従・TMP）。本番は startVisible=false・視界保護。右 B でトグル。
            //    lap / ゾーン / 次の cue / 信号 / 要再登録 を 1 枚に統合し、登録中は登録ガイダンスを強制表示。
            //    world-space（Logic 直下・head 非親）で StatusHud が自前に緩追従する。
            var statusHud = CreateStatusHud(logic.transform, centerEye.transform, registry, tracker,
                                            director, signalFx, lapCounter, cueScheduler, courseFrame, registration);

            // 4.1. HMD 内の文字面は全部「スタッフが被っているか」を StatusHud に問う（体験者の視界には
            //      1 文字も出さない・2026-08-07〜）。実行時も FindObjectOfType で自己解決するが、
            //      明示配線しておく（シーンに 2 つ目の StatusHud が現れたときに取り違えない）。
            var promptSo = new SerializedObject(introPrompt);
            TrySetObjectRef(promptSo, "statusHud", statusHud);
            promptSo.ApplyModifiedPropertiesWithoutUndo();

            // 4.2. ControllerGuidePanel（スタッフ専用・右コントローラに追従する操作早見表）。
            //      右コントローラアンカー（RightHandAnchor）+ CenterEyeAnchor へ配線。rightHand が
            //      無い（OVR リグ未配置等）なら生成をスキップ（追従先が無いと常時非表示になるため）。
            ControllerGuidePanel? guidePanel = null;
            if (rightHand != null)
                guidePanel = CreateControllerGuidePanel(logic.transform, rightHand.transform, centerEye.transform, statusHud);
            else
                Debug.LogWarning($"[MainDemoSceneSetup] '{RightHandPath}' が無いため ControllerGuidePanel の生成をスキップ。");

            // 4.5. Diagnostics（HUD には出さない診断: [HudDump] ログ + HMD 軌跡 CSV + Editor H トグル）
            CreateDiagnostics(logic.transform, registry, tracker, centerEye.transform, statusHud);

            // 5. OvrControllerBridge に StatusHud / CourseRegistration / CourseFrame / Director / SignalFx / ランリセット対象を接続
            if (ovrBridge != null)
            {
                var bridgeSo = new SerializedObject(ovrBridge);
                if (statusHud != null) TrySetObjectRef(bridgeSo, "statusHud", statusHud);
                // ⚠ switchDirector / registry は 2026-08-12 に撤去した（A のカメラ手動送りを外したため）。
                //    ここへ書き戻さないこと — フィールドが無いので TrySetObjectRef が無言で空振りする。
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
                // タイトル画面。**A の意味がここで分岐する**（立っていれば閉じる / 無ければカメラ送り）。
                if (titleScreen != null) TrySetObjectRef(bridgeSo, "titleScreen", titleScreen);
                bridgeSo.ApplyModifiedPropertiesWithoutUndo();
            }

            // 6. シーン保存
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);

            Selection.activeGameObject = trackerGo;
            Debug.Log("[MainDemoSceneSetup] 完了。Zones=4（静的フォールバック・推測配置） / Tracker（Director 経由切替） / CourseFrame + ZoneLayoutApplier（show.json layout で生成） / CourseRegistrationController（トリガー 2 秒長押し→N 点登録、A=マーク/B=確定。スティックナッジ廃止） / LapCounter + CueScheduler（周回×ゾーンで cue 自動発火・ライブ優先。周回は director の Zone 切替のみ数え、手動/Web固定/外部/インサートは不算入。runEpoch 変化 or 右グリップ 2 秒長押しでランリセット） / TimelineDirector + TakeRunner（show.json timeline: 区間の演出・カット / 区間 post 上書き / 区間 BGM。v2 の cue・インサートは読み込み時に演出へ変換。timeline 不在時は従来 schedule で動く） / CameraSwitchDirector + SwitchAudioCue + SignalLostFx（切替作法・フェイルソフト・Screen 上） / [Bgm]（BgmDirector: 区間 BGM 切替・ループ範囲・クロスフェード。show.json 未指定なら従来の固定ループ） / StartupFader / StatusHud（単一サーフェス・緩追従・startVisible=false・右 B トグル） / ControllerGuidePanel（スタッフ専用・右コントローラ追従・モード別操作早見表） / Diagnostics（[HudDump] ログ + HMD 軌跡 CSV + Editor H） / Title（タイトル画面「廻リ視」・導入の段 0 に被さる・右 A で閉じる） / OvrBridge（右手 4 入力: A=タイトルを閉じて体験を始める / B=ステータス / グリップ長押し=ランリセット / トリガー長押し=登録。カメラ手動送りは 2026-08-12 に撤去）。シーン保存済み。" +
                      "次は URP-Balanced-Renderer.asset に FullScreenPassRendererFeature を追加（手動）。" +
                      "詳細: docs/onsite-checklist.md");
        }

        // ----- helpers -----

        /// <summary>
        /// 体験者の素手（ハンドトラッキング）とスタッフのコントローラを**同時に**使えるようにする。
        /// OVRManager の 2 フラグはビルド時に立っている必要があるのでシーンへ焼く（実行時設定では遅い）。
        /// Editor asmdef から Meta XR を直接参照できないため reflection で触る。
        /// </summary>
        private static void EnableSimultaneousHandsAndControllers()
        {
            var type = System.Type.GetType("OVRManager, Oculus.VR");
            if (type == null)
            {
                Debug.LogWarning("[MainDemoSceneSetup] OVRManager 型が解決できません。素手 + コントローラ同時使用の設定をスキップ。");
                return;
            }
            var manager = Object.FindObjectOfType(type, includeInactive: true) as MonoBehaviour;
            if (manager == null)
            {
                Debug.LogWarning("[MainDemoSceneSetup] シーンに OVRManager が居ません。素手 + コントローラ同時使用の設定をスキップ。");
                return;
            }

            bool changed = false;
            foreach (string name in new[] { "SimultaneousHandsAndControllersEnabled",
                                            "launchSimultaneousHandsControllersOnStartup" })
            {
                var field = type.GetField(name);
                if (field == null || field.FieldType != typeof(bool)) continue;
                if (field.GetValue(manager) is bool b && b) continue;
                field.SetValue(manager, true);
                changed = true;
            }
            if (changed)
            {
                EditorUtility.SetDirty(manager);
                Debug.Log("[MainDemoSceneSetup] OVRManager: 素手 + コントローラの同時使用を有効化しました" +
                          "（CG 人形の腕を体験者の手で動かすため。スタッフの右手 4 入力は従来どおり）。");
            }
        }

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

        // 導入演出の覆い（現実を枠の中へ閉じ込める面）。head-lock なので CenterEyeAnchor 直下。
        // 開口の大きさは screenQuad から逆算するので、枠が閉じ切ると本編のスクリーンと同じ見かけ角になる。
        private static IntroVeil CreateIntroVeil(Transform parent, Transform? screenQuad)
        {
            var go = new GameObject(IntroVeilName);
            go.transform.SetParent(parent, worldPositionStays: false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;

            var veil = go.AddComponent<IntroVeil>();
            var so = new SerializedObject(veil);
            TrySetFloat(so, "distance", 0.3f);
            TrySetVector2(so, "veilSize", new Vector2(2f, 2f));
            if (screenQuad != null) TrySetObjectRef(so, "screenQuad", screenQuad);
            TrySetFloat(so, "feather", 0.08f);
            TrySetFloat(so, "scanlineCount", 240f);
            so.ApplyModifiedPropertiesWithoutUndo();
            return veil;
        }

        // 隔離殻。**現実のうち「見てよいもの」以外を黒で落とす面**（canon/LEDGER.md 0002）。
        // 覆い (IntroVeil) と同じ GameObject に載せる。判定は世界空間の視線 × 著作した箱なので、
        // 面の距離・大きさは「視界を覆い切る」ためだけの値。
        private static ContainmentShell CreateContainmentShell(GameObject veilGo, ShowControlClient? showControl)
        {
            var shell = veilGo.GetComponent<ContainmentShell>();
            if (shell == null) shell = veilGo.AddComponent<ContainmentShell>();
            var so = new SerializedObject(shell);
            if (showControl != null) TrySetObjectRef(so, "showControl", showControl);
            TrySetBool(so, "shellEnabled", true);
            TrySetFloat(so, "distance", 0.3f);
            TrySetVector2(so, "planeSize", new Vector2(2f, 2f));
            so.ApplyModifiedPropertiesWithoutUndo();
            return shell;
        }

        // 封印の箱。**外から見た隔離**（canon/LEDGER.md 0003）。段 0 で体験エリアの外に居るあいだだけ出る。
        private static SealedBox CreateSealedBox(GameObject veilGo, ShowControlClient? showControl)
        {
            var box = veilGo.GetComponent<SealedBox>();
            if (box == null) box = veilGo.AddComponent<SealedBox>();
            var so = new SerializedObject(box);
            if (showControl != null) TrySetObjectRef(so, "showControl", showControl);
            TrySetBool(so, "boxEnabled", true);
            TrySetFloat(so, "heightM", 2.4f);
            TrySetFloat(so, "hexSizeM", 0.45f);
            TrySetFloat(so, "glowGain", 1.0f);
            so.ApplyModifiedPropertiesWithoutUndo();
            return box;
        }

        // タイトル画面「廻リ視」。黒の中に立体文字だけを置き、右 A で閉じて体験へ入る。
        // head-lock なので CenterEyeAnchor 直下（IntroVeil と同じ理由）。
        // ⚠ **封印の箱・隔離殻への参照を明示する。** タイトルはこの 2 つが実際に立っているのを
        //    確かめてから黒を開ける（見つからないと確かめようが無く、上限 0.5 秒で諦めて開く）。
        private static TitleScreen CreateTitleScreen(
            Transform parent, ShowRunDirector runDirector, IntroDirector introDirector,
            SealedBox? sealedBox, ContainmentShell? shell, ShowControlClient? showControl)
        {
            var go = new GameObject(TitleName);
            go.transform.SetParent(parent, worldPositionStays: false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;

            var title = go.AddComponent<TitleScreen>();
            var so = new SerializedObject(title);
            TrySetObjectRef(so, "runDirector", runDirector);
            TrySetObjectRef(so, "introDirector", introDirector);
            if (sealedBox != null) TrySetObjectRef(so, "sealedBox", sealedBox);
            if (shell != null) TrySetObjectRef(so, "shell", shell);
            if (showControl != null) TrySetObjectRef(so, "showControl", showControl);
            TrySetObjectRef(so, "head", parent);
            TrySetBool(so, "titleEnabled", true);
            // 2026-08-13 に 2.0 → 2.6（近すぎて見づらいという判定・canon/LEDGER.md 0023）。
            TrySetFloat(so, "distanceM", 2.6f);
            TrySetFloat(so, "titleHeightM", 1.30f);
            TrySetFloat(so, "pitchOffsetDeg", 2.0f);
            so.ApplyModifiedPropertiesWithoutUndo();
            return title;
        }

        // 導入演出の進行役。ShowPhase は増やさず Intro の内側のサブ状態を持つ。
        // show.json の run.intro が無い / enabled=false なら何もしない（従来の見えになる）。
        private static IntroDirector CreateIntroDirector(
            Transform parent, ShowRunDirector runDirector, IntroVeil veil, GlitchFx? glitch,
            CameraStreamRegistry registry, ShowControlClient? showControl,
            Transform head, Transform? screenQuad)
        {
            var go = new GameObject(IntroDirectorName);
            go.transform.SetParent(parent, worldPositionStays: false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;

            var dir = go.AddComponent<IntroDirector>();
            // 段 3 の構造の線。LineRenderer は world 空間で描くので親の transform に依存しない。
            var wire = go.AddComponent<IntroStructureWire>();
            var wireSo = new SerializedObject(wire);
            if (showControl != null) TrySetObjectRef(wireSo, "showControl", showControl);
            // 既定は線を出さない（2026-08-01）。実行時は show.json の run.intro が
            // IntroStructureWire.SetSources で上書きするので、ここはシーンを見た人が
            // 「既定では出ない」と分かるための明示。
            TrySetBool(wireSo, "showRoomWire", false);
            TrySetBool(wireSo, "showCameraMarks", false);
            wireSo.ApplyModifiedPropertiesWithoutUndo();

            var so = new SerializedObject(dir);
            TrySetObjectRef(so, "runDirector", runDirector);
            TrySetObjectRef(so, "veil", veil);
            TrySetObjectRef(so, "structureWire", wire);
            if (glitch != null) TrySetObjectRef(so, "glitch", glitch);
            TrySetObjectRef(so, "registry", registry);
            if (showControl != null) TrySetObjectRef(so, "showControl", showControl);
            TrySetObjectRef(so, "head", head);
            if (screenQuad != null) TrySetObjectRef(so, "screenQuad", screenQuad);
            TrySetFloat(so, "blackClearSec", 5f);
            TrySetFloat(so, "centeredHalfAngleDeg", 25f);
            TrySetFloat(so, "freshFrameSec", 1.5f);
            so.ApplyModifiedPropertiesWithoutUndo();
            return dir;
        }

        /// <summary>
        /// OVRCameraRig に <c>OVRPassthroughLayer</c> と <c>PassthroughStyler</c> を用意する。
        /// どちらも Editor asmdef から型を直接参照できない（Oculus.VR / Assembly-CSharp）ので
        /// reflection で解決する。見つからなければ警告だけ出して続ける — 導入演出のうち
        /// パススルーの見た目（彩度・輪郭線）が効かなくなるだけで、枠と映像のすり替えは動く。
        /// </summary>
        private static void EnsurePassthroughStyler(GameObject? rig)
        {
            if (rig == null)
            {
                Debug.LogWarning("[MainDemoSceneSetup] OVRCameraRig が見つかりません。導入演出のパススルー加工はスキップ。");
                return;
            }
            var layerType = System.Type.GetType("OVRPassthroughLayer, Oculus.VR");
            if (layerType == null)
            {
                Debug.LogWarning("[MainDemoSceneSetup] OVRPassthroughLayer が見つかりません（Meta XR SDK 未導入？）。導入演出のパススルー加工はスキップ。");
                return;
            }
            if (rig.GetComponent(layerType) == null)
            {
                rig.gameObject.AddComponent(layerType);
                Debug.Log("[MainDemoSceneSetup] OVRCameraRig に OVRPassthroughLayer を追加（導入演出用・既定 Underlay）。");
            }

            // ⚠ **背景は不透明な黒 (a=1) にする。透明 (a=0) にしてはいけない。**
            //
            // Underlay パススルーは「アプリが描かない画素」(alpha 0) にしか出ないが、**穴を開けるのは
            // 覆い (IntroVeil) の仕事**で、カメラの背景ではない。覆いは `Blend Zero SrcAlpha`
            // （出力 = dst × srcAlpha）で全画面に掛かり、段 1〜3 では srcAlpha=0 を書くので
            // 背景が不透明でも全面パススルーになる。
            //
            // 逆に背景を透明にすると、乗算ブレンドは **alpha を減らすことしかできない**ため
            // 0 を 1 へ戻せない ＝ **段 4 で「周縁から黒が寄せて正面に長方形が残る」演出が
            // 原理的に起こらない**（2026-07-31 実測: 枠の外に現実が残り続け、次の段で
            // パススルーが切れて一気に黒くなっていた）。
            //
            // 2026-07-31 に一度 a=0 にしたのは「パススルーが一切出ない」の対処だったが、
            // 真因は `OculusProjectConfig._insightPassthroughSupport` が 0 だったことと
            // IntroVeil シェーダがビルドから剥がれていたことの 2 つで、この行は不要だった。
            foreach (var cam in rig.GetComponentsInChildren<Camera>(includeInactive: true))
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0f, 0f, 0f, 1f);
                EditorUtility.SetDirty(cam);
            }

            var stylerType = System.Type.GetType("FixedCamVr.OvrBridge.PassthroughStyler, Assembly-CSharp");
            if (stylerType == null)
            {
                Debug.LogWarning("[MainDemoSceneSetup] PassthroughStyler が見つかりません。導入演出のパススルー加工はスキップ。");
                return;
            }
            if (rig.GetComponent(stylerType) == null) rig.gameObject.AddComponent(stylerType);
        }

        // 導入の合図（head-lock の 1 行）。覆いより後に描かないと潰されるので、
        // renderQueue は IntroPrompt が自分で設定する（IntroVeil の 5000 と対）。
        // ⚠ 読み手はスタッフだけ（StatusHud.StaffViewing で門を閉じる）。配線は呼び出し側が後から行う
        //    — StatusHud はこれより後に作られるので、ここでは渡せない。
        private static FixedCamVr.Diagnostics.IntroPrompt CreateIntroPrompt(Transform parent, IntroDirector director)
        {
            var existing = parent.Find(IntroPromptName);
            if (existing != null) Object.DestroyImmediate(existing.gameObject);

            var go = new GameObject(IntroPromptName);
            go.transform.SetParent(parent, worldPositionStays: false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;

            var prompt = go.AddComponent<FixedCamVr.Diagnostics.IntroPrompt>();
            var so = new SerializedObject(prompt);
            TrySetObjectRef(so, "director", director);
            so.ApplyModifiedPropertiesWithoutUndo();
            return prompt;
        }

        // 体験の終わりを閉じる黒。StartupFader は解除後に自分を Destroy するので再利用できない。
        // 起動フェードと同じ CenterEyeAnchor 直下に置き、相の変化だけを購読する。
        private static void CreateEndingFader(Transform parent)
        {
            var go = new GameObject(EndingFaderName);
            go.transform.SetParent(parent, worldPositionStays: false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;

            var fader = go.AddComponent<ShowEndingFader>();
            var so = new SerializedObject(fader);
            TrySetFloat(so, "distance", 0.3f);
            TrySetVector2(so, "worldSize", new Vector2(2f, 2f));
            TrySetColor(so, "fadeColor", Color.black);
            TrySetInt(so, "sortingOrder", 10000);
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // BGM。[Bgm] を === Logic === 直下に get-or-create し、BgmDirector（区間で切り替わる再生器）を付ける。
        // get-or-create で冪等 — delete+recreate にしない（現場で Inspector 調整した音量を潰さないため）。
        //
        // 旧構成（AudioSource 1 本・playOnAwake の固定ループ）から移行する:
        //   BgmDirector が自前の voice（AudioSource ×2）を Awake で作るので、[Bgm] 直下の
        //   旧 AudioSource は playOnAwake を落として黙らせる（残しておくと二重再生になる）。
        //   show.json に bgm 指定が無ければ BgmDirector が defaultClip（= 従来の HorrBGM）を
        //   ループ再生するので、未オーサリングのショーの聴こえ方は変わらない。
        /// <summary>
        /// 体験の音（<see cref="ShowSoundDirector"/> ＋ <see cref="SfxPlayer"/>）を [Sound] へ冪等に置く。
        ///
        /// ⚠ **これはシーンに焼かれた GameObject** なので、コードを実装しただけでは APK に入らない
        /// （導入演出・隔離殻・タイトルと同じ罠 — `rules/show-design.md`）。
        /// 音を足したら <c>.\tools\unity.ps1 menu scene</c> を再実行してからビルドすること。
        /// 確認は <c>grep "m_Name: \[Sound\]" Assets/Scenes/Main.unity</c>。
        /// </summary>
        private static void CreateOrUpdateSound(Transform parent)
        {
            var existing = parent.Find(SoundName);
            GameObject go;
            if (existing != null) go = existing.gameObject;
            else
            {
                go = new GameObject(SoundName);
                go.transform.SetParent(parent, worldPositionStays: false);
            }
            if (go.GetComponent<SfxPlayer>() == null) go.AddComponent<SfxPlayer>();
            if (go.GetComponent<ShowSoundDirector>() == null) go.AddComponent<ShowSoundDirector>();

            // 音源が焼かれているかをここで 1 度だけ見る。無いまま実機へ持っていくと
            // **完全な無音でも何のエラーも出ない**（音は録画にも映らないので気づけない）。
            int found = 0;
            foreach (var res in SoundProbeResources)
            {
                if (Resources.Load<AudioClip>("Sound/" + res) != null) found++;
            }
            if (found < SoundProbeResources.Length)
            {
                Debug.LogWarning($"[MainDemoSceneSetup] 音源が足りません（Resources/Sound/ に "
                                 + $"{found}/{SoundProbeResources.Length} 本）。"
                                 + "`py -3.11 tools/make-sounds.py` を走らせてから焼き直すこと。");
            }
        }

        private static BgmDirector? CreateOrUpdateBgm(Transform parent)
        {
            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(BgmClipPath);
            if (clip == null)
                Debug.LogWarning($"[MainDemoSceneSetup] BGM クリップが見つかりません: {BgmClipPath}。既定クリップなしで [Bgm] を作ります。");

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

            // 旧 AudioSource（固定ループ）が残っていたら黙らせる。BgmDirector の voice と二重に鳴らない。
            var legacy = go.GetComponent<AudioSource>();
            if (legacy != null)
            {
                legacy.playOnAwake = false;
                legacy.loop = false;
                legacy.clip = null;
            }

            var director = go.GetComponent<BgmDirector>();
            if (director == null) director = go.AddComponent<BgmDirector>();
            var so = new SerializedObject(director);
            var clipProp = so.FindProperty("defaultClip");
            if (clipProp != null) clipProp.objectReferenceValue = clip;
            // 音量は新規作成時のみ既定を書く（現場調整の保持）。
            if (created)
            {
                var volProp = so.FindProperty("defaultVolume");
                if (volProp != null) volProp.floatValue = 0.5f;
            }
            so.ApplyModifiedPropertiesWithoutUndo();
            return director;
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
            // ⚠ 旧 `recenterAutoShowSec`（要再登録で 5 秒だけ自動表示）は 2026-08-07 に廃止。
            //    体験者の視界へ業務連絡が湧く唯一の経路だった（StatusHud のコメント参照）。
            hudSo.ApplyModifiedPropertiesWithoutUndo();

            return hud;
        }

        // スタッフ用の操作ガイドパネル（右コントローラに追従する小さな早見表）を作る。
        // 見た目は StatusHud（CreateStatusHudVisual）を踏襲するが、パネル幅はコントローラ幅の
        // 2〜3 倍程度に収める規模感（小さめ・左寄せの操作リスト）。配置追従は ControllerGuidePanel が
        // controller / head を見て自前で行う（world-space・parent 直下・controller 非親）。
        private static ControllerGuidePanel CreateControllerGuidePanel(Transform parent,
            Transform controller, Transform head, StatusHud? statusHud)
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
            // 体験者の視界に手元の早見表が浮かないようにする門（右 B or 位置合わせ中だけ出す）。
            if (statusHud != null) TrySetObjectRef(so, "statusHud", statusHud);
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
