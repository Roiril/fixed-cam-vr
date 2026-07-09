#nullable enable
using System.Collections.Generic;
using System.IO;
using TableDuoVr.Hands;
using TableDuoVr.Hands.Playback;
using TableDuoVr.Net;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using UnityEngine;

namespace TableDuoVr.EditorTools
{
    /// <summary>
    /// TableDuoMain.unity を一から構築する冪等メニュー。
    /// テーブル / 席 / OVRCameraRig(+OVRHand/OVRSkeleton) / NetworkManager / Systems を配置し、
    /// プレイヤープレハブも生成して NetworkConfig に登録する。
    /// 既存シーンがあれば開いて既知ルートを消してから再構築する。
    /// </summary>
    public static class TableDuoSceneSetup
    {
        private const string ScenePath = "Assets/TableDuo/Scenes/TableDuoMain.unity";
        private const string PlayerPrefabPath = "Assets/TableDuo/Prefabs/TableDuoPlayer.prefab";
        private const string RigPrefabPath = "Packages/com.meta.xr.sdk.core/Prefabs/OVRCameraRig.prefab";
        private const string HandPrefabPath = "Packages/com.meta.xr.sdk.core/Prefabs/OVRHandPrefab.prefab";
        private const string MaterialDir = "Assets/TableDuo/Materials";
        private const string ResourcesDir = "Assets/TableDuo/Resources";

        // 手の見た目バリアント（VR Hands Starter Pack）。プレハブ = 別リグ命名の skinned 手（HandVariantTable 参照）。
        private const string PackDir = "Assets/TableDuo/ThirdParty/VRHandsStarterPack";
        private const string RealisticLeftPrefab = PackDir + "/Male Hand/Prefab/Low Left Male Hand.prefab";
        private const string RealisticRightPrefab = PackDir + "/Male Hand/Prefab/Low Right Male Hand.prefab";
        private const string RobotLeftPrefab = PackDir + "/Robot Hand/Prefabs/Black Left Robot Hand.prefab";
        private const string RobotRightPrefab = PackDir + "/Robot Hand/Prefabs/Black Right Robot Hand.prefab";
        private const string MaleAlbedo = PackDir + "/Male Hand/Texture/MaleHand_Albedo.png";
        private const string MaleNormal = PackDir + "/Male Hand/Texture/MaleHand_Normal.png";
        private const string MaleAo = PackDir + "/Male Hand/Texture/MaleHand_AO.png";
        private const string RobotAlbedo = PackDir + "/Robot Hand/Textures/Black/RobotHand_Black001_Albedo.png";
        private const string RobotNormal = PackDir + "/Robot Hand/Textures/Black/RobotHand_Black001_Normal.png";
        private const string RobotMetallic = PackDir + "/Robot Hand/Textures/Black/RobotHand_Black001_Metallic.png";

        [MenuItem("Tools/FixedCamVr/Setup/Setup TableDuo Scene", priority = 60)]
        public static void Setup()
        {
            // 確認ダイアログを出さない（MCP/batchmode から呼ぶとモーダルで Editor ごと
            // ブロックする実害 2 回）。dirty なら黙って保存してから進む
            var current = SceneManager.GetActiveScene();
            if (current.isDirty)
            {
                EditorSceneManager.SaveScene(current);
            }

            var scene = File.Exists(ScenePath)
                ? EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single)
                : EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // 冪等性: 既知ルートを全削除して作り直す
            DeleteRoot("[TableDuo]");
            DeleteRoot("OVRCameraRig");
            DeleteRoot("NetworkManager");
            DeleteRoot("Directional Light");
            DeleteRoot("Ceiling Fill Light");
            DeleteRoot("DebugCamera");

            // --- 環境 ---
            var lightGo = new GameObject("Directional Light");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.0f;
            // 室内ボックス（天井あり）なので影を落とすと部屋全体が天井の影に沈む。
            // 調査用の均一な明るさを優先して影は切る（flat ambient と合わせて陰影は最小限）。
            light.shadows = LightShadows.None;
            lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            // 背景 = 室内。既定の Procedural Skybox（青空）は屋内タスクとして不自然なため外し、
            // フラット環境光に置き換える（研究アプリなので無個性・非誘目のニュートラルトーン）。
            RenderSettings.skybox = null;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            // 直接光が当たらない面（光源と逆向きの壁・天井の裏面）はこの ambient のみで
            // 照らされる。低いと部屋が陰気になるので室内照明相当まで持ち上げる
            RenderSettings.ambientLight = new Color(0.62f, 0.62f, 0.64f);
            RenderSettings.fog = false;

            // 天井の裏面は下向き directional では一切照らされないため、弱い上向き補助光で
            // 「照明の照り返し」を再現する（影なし・強度控えめで陰影は潰さない）
            var fillGo = new GameObject("Ceiling Fill Light");
            var fill = fillGo.AddComponent<Light>();
            fill.type = LightType.Directional;
            fill.intensity = 0.35f;
            fill.shadows = LightShadows.None;
            fillGo.transform.rotation = Quaternion.Euler(-140f, 20f, 0f); // 下から上へ

            var root = new GameObject("[TableDuo]");

            // URP マテリアル（ランタイム生成プリミティブが内蔵 Standard を引いて
            // 実機でマゼンタ化した実害があるため、全プリミティブに明示割当する）
            var floorMat = EnsureMaterial($"{MaterialDir}/TableDuoFloor.mat",
                "Universal Render Pipeline/Lit", new Color(0.25f, 0.27f, 0.30f));
            var tableMat = EnsureMaterial($"{MaterialDir}/TableDuoTable.mat",
                "Universal Render Pipeline/Lit", new Color(0.45f, 0.32f, 0.22f));
            var propMat = EnsureMaterial($"{MaterialDir}/TableDuoProp.mat",
                "Universal Render Pipeline/Lit", new Color(0.95f, 0.55f, 0.15f));
            var handMat = EnsureMaterial($"{MaterialDir}/TableDuoLocalHand.mat",
                "Universal Render Pipeline/Lit", new Color(0.85f, 0.75f, 0.65f));
            // リモートアバター用はランタイム生成側が Resources.Load で引く
            EnsureMaterial($"{ResourcesDir}/TableDuoAvatar.mat",
                "Universal Render Pipeline/Lit", new Color(0.55f, 0.75f, 0.95f));
            EnsureMaterial($"{ResourcesDir}/TableDuoHeadMarker.mat",
                "Universal Render Pipeline/Unlit", new Color(1f, 0.9f, 0.3f));
            // 簡易人型（人側フルアバター）の配色。研究中立な無個性トーン。
            // 無ければ RemoteAvatarView がランタイムで同色を生成するので Setup 未実行でも出る
            EnsureMaterial($"{ResourcesDir}/TableDuoSkin.mat",
                "Universal Render Pipeline/Lit", new Color(0.86f, 0.69f, 0.56f));
            EnsureMaterial($"{ResourcesDir}/TableDuoShirt.mat",
                "Universal Render Pipeline/Lit", new Color(0.32f, 0.40f, 0.52f));
            EnsureMaterial($"{ResourcesDir}/TableDuoEye.mat",
                "Universal Render Pipeline/Lit", new Color(0.12f, 0.12f, 0.14f));

            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "Floor";
            floor.transform.SetParent(root.transform, false);
            // 部屋サイズ（6m 四方）に合わせる。Plane 素体は 10m なので 0.6 倍
            floor.transform.localScale = new Vector3(RoomSize / 10f, 1f, RoomSize / 10f);
            floor.GetComponent<Renderer>().sharedMaterial = floorMat;

            // 室内ボックス（壁 4 面 + 天井）。青空 skybox の代わりに視界を室内で閉じる。
            // 調査で視線・注意を引かないよう装飾なしの無地（study-design の中立トーン方針）
            CreateRoomShell(root.transform);

            // テーブル: Kenney Furniture Kit（CC0）。天板高はここ 1 定数で決める（派生値は topY から自動追従）。
            // FitHeight は幅（maxWidth）で拡大が頭打ちになり targetHeight が効かないため、幅フィット後に
            // Y スケールだけ伸ばして目標天板高へ合わせる（＝横幅そのまま脚だけ伸びる）。
            const float TableTopHeight = 0.75f; // 天板高（座位の目線 1.15m に対しての机の高さ）
            var table = InstantiateModelFitHeight(
                "Assets/ThirdParty/Kenney/Furniture/table.fbx", root.transform,
                "Table", Vector3.zero, 0f, targetHeight: TableTopHeight, maxWidth: 1.3f);
            if (table == null)
            {
                table = GameObject.CreatePrimitive(PrimitiveType.Cube);
                table.name = "Table";
                table.transform.SetParent(root.transform, false);
                table.transform.localPosition = new Vector3(0f, TableTopHeight * 0.5f, 0f);
                table.transform.localScale = new Vector3(1.2f, TableTopHeight, 0.8f);
                table.GetComponent<Renderer>().sharedMaterial = tableMat;
            }
            else
            {
                StretchToTopHeight(table, TableTopHeight); // 横幅を保ち縦だけ伸ばして天板高を合わせる
            }

            // テーブル天板の実測（maxWidth で縮むと天板高が 0.7 未満になるので実バウンディングを使う）。
            // 小物・カードはこの天板の上 (y=topY) かつ天板の XZ 範囲内に収める。
            Bounds tb = WorldBounds(table);
            float topY = tb.max.y;
            float cx = tb.center.x, cz = tb.center.z;
            float hx = tb.extents.x, hz = tb.extents.z;
            const float edge = 0.07f; // 縁マージン（はみ出し防止）

            // 卓上ピース物理: TableProps レイヤー確保 + 天板の物理コライダー（不可視・静的）。
            // ピースはこのレイヤー同士のみ衝突（PiecePhysicsConfig がランタイムで設定）。
            // 卓外へ落ちたピースは床と衝突せず落下 → Grabbable が spawn 位置へリスポーンする
            int propsLayer = EnsureTablePropsLayer();
            var tableTop = new GameObject("TableTopCollider");
            tableTop.transform.SetParent(root.transform, false);
            tableTop.transform.position = new Vector3(cx, topY - 0.025f, cz);
            var topBox = tableTop.AddComponent<BoxCollider>();
            topBox.size = new Vector3(tb.size.x, 0.05f, tb.size.z);
            topBox.sharedMaterial = EnsurePhysicMaterial(
                $"{MaterialDir}/TableDuoTableTop.physicMaterial", friction: 0.6f, bounciness: 0.1f);
            if (propsLayer >= 0) tableTop.layer = propsLayer;

            // 椅子（見た目のみ・席アンカーとは独立・床に置く）
            // Kenney chair.fbx は -z が正面（180° が「テーブルへ向く」）
            InstantiateModelFitHeight("Assets/ThirdParty/Kenney/Furniture/chair.fbx",
                root.transform, "Chair0", new Vector3(0f, 0f, -0.78f), 180f, targetHeight: 0.85f, maxWidth: 0.55f);
            InstantiateModelFitHeight("Assets/ThirdParty/Kenney/Furniture/chair.fbx",
                root.transform, "Chair1", new Vector3(0f, 0f, 0.78f), 0f, targetHeight: 0.85f, maxWidth: 0.55f);
            // 卓上ランプは廃止（2026-07-09 ユーザー指示: 盤面の邪魔）

            // 席 = 初期目線アンカー。**ローカル原点が目の位置**（EyeLevel なので頭が席に乗る）、
            // forward(+Z) が視線方向。Y を座位の目の高さに置く。Scene ビューで席を動かして調整可能
            // （ギズモ表示 + Tools/FixedCamVr/Diagnostics/Preview Eye）。
            const float eyeHeight = 1.15f;
            var seats = new GameObject("Seats");
            seats.transform.SetParent(root.transform, false);
            CreateSeat(seats.transform, 0, new Vector3(0f, eyeHeight, -0.85f), 0f);    // フルアバター席
            CreateSeat(seats.transform, 1, new Vector3(0f, eyeHeight, 0.85f), 180f);   // 手だけアバター席

            // 卓上 = ボードゲーム「海底探検（Deep Sea Adventure）」一式（旧: Kenney 食べ物プロップ＋絵カードを置換）。
            // 潜水艦ボードを中央奥に静置、宝物チップ/裏トークン/空気マーカーを手前にグリッド配置、駒2+サイコロは掴める。
            // GLB は glTFast 取込（Assets/TableDuo/ThirdParty/DeepSeaAdventure/glb、テクスチャ埋込・実スケール=メートル）。
            var props = new GameObject("Props");
            props.transform.SetParent(root.transform, false);
            PlaceDeepSeaAdventure(props.transform, topY, cx, cz, hx, hz, edge);

            // 協調配置課題の目標パネル（手役ローカルのみ表示）
            CreatePatternPanel(root.transform);

            // --- OVRCameraRig + ハンドトラッキング ---
            var rig = InstantiateRig();
            Transform? trackingSpace = null, centerEye = null;
            OVRHand? leftHand = null, rightHand = null;
            OVRSkeleton? leftSkel = null, rightSkel = null;
            if (rig != null)
            {
                trackingSpace = rig.transform.Find("TrackingSpace");
                centerEye = rig.transform.Find("TrackingSpace/CenterEyeAnchor");
                AddHand(rig, "TrackingSpace/LeftHandAnchor", isLeft: true, out leftHand, out leftSkel);
                AddHand(rig, "TrackingSpace/RightHandAnchor", isLeft: false, out rightHand, out rightSkel);
                rig.transform.SetPositionAndRotation(new Vector3(0f, 0f, -0.85f), Quaternion.identity);

                // トラッキング原点は EyeLevel（=0）。FloorLevel だと実身長で目線高が変わり
                // 参加者間で体験差が出る。EyeLevel + 席を目の高さに置くことで、全員が
                // 席（=目線アンカー）の高さ・向きで揃う。リグを席にアライン → trackingSpace が
                // 席フレームと一致するので、リモートアバターの頭高も席基準で自動的に正しくなる
                var ovrManager = rig.GetComponent("OVRManager") as MonoBehaviour;
                if (ovrManager != null)
                {
                    var mgrSo = new SerializedObject(ovrManager);
                    SetEnum(mgrSo, "_trackingOriginType", 0); // OVRManager.TrackingOrigin.EyeLevel
                    // コントローラを握ってもハンドトラッキングが切れて手が消えないようにする（Quest 3 マルチモーダル）。
                    // - launchSimultaneousHandsControllersOnStartup: 手とコントローラを同時トラッキング（起動時に有効化）
                    // - controllerDrivenHandPosesType=Natural(2): 握った手の骨格をコントローラ入力から自然な手形で駆動
                    //   → OVRHand.IsTracked が保たれ、握っていても手メッシュ（＝送信 pose）が出続ける。
                    // 素手（コントローラ非把持）のときは通常のハンドトラッキングがそのまま働く。
                    SetBool(mgrSo, "launchSimultaneousHandsControllersOnStartup", true);
                    SetEnum(mgrSo, "controllerDrivenHandPosesType", 2); // Natural
                    mgrSo.ApplyModifiedPropertiesWithoutUndo();
                }

                // 背景保険: skybox は外してあるが、clear flags が Skybox のままだと
                // 環境によって既定空が出る。室内トーンの単色 clear に固定する
                if (centerEye != null && centerEye.TryGetComponent<Camera>(out var eyeCam))
                {
                    eyeCam.clearFlags = CameraClearFlags.SolidColor;
                    eyeCam.backgroundColor = new Color(0.30f, 0.30f, 0.32f);
                }
            }

            // --- L0 用デバッグカメラ（リグを無効化して使う。既定 OFF）---
            var debugCamGo = new GameObject("DebugCamera");
            var debugCam = debugCamGo.AddComponent<Camera>();
            debugCam.nearClipPlane = 0.05f;
            debugCam.clearFlags = CameraClearFlags.SolidColor;
            debugCam.backgroundColor = new Color(0.30f, 0.30f, 0.32f);
            // 向かいの席の頭（y≈1.6m）まで画角に入る高さ・引き
            debugCamGo.transform.SetPositionAndRotation(new Vector3(0f, 1.7f, -2.1f), Quaternion.Euler(14f, 0f, 0f));
            debugCamGo.SetActive(false);

            // --- Systems ---
            var systems = new GameObject("Systems");
            systems.transform.SetParent(root.transform, false);

            var sampler = systems.AddComponent<HandPoseSampler>();
            var so = new SerializedObject(sampler);
            SetRef(so, "rigRoot", rig != null ? rig.transform : null);
            SetRef(so, "trackingSpace", trackingSpace);
            SetRef(so, "centerEye", centerEye);
            SetRef(so, "leftHand", leftHand);
            SetRef(so, "rightHand", rightHand);
            SetRef(so, "leftSkeleton", leftSkel);
            SetRef(so, "rightSkeleton", rightSkel);
            so.ApplyModifiedPropertiesWithoutUndo();

            // 実機セッションは常に自動録画（装着が検知されてから 120s、pause で保存）。
            // 実手データが L0 再生資産になるため既定 ON
            var recorder = systems.AddComponent<HandPoseRecorder>();
            var recSo = new SerializedObject(recorder);
            var p = recSo.FindProperty("autoStartOnPlay");
            if (p != null) p.boolValue = true;
            var pm = recSo.FindProperty("maxSeconds");
            if (pm != null) pm.floatValue = 120f;
            recSo.ApplyModifiedPropertiesWithoutUndo();

            // 調査セッションのローカル lossless 手 pose 録画（study-design §4 の完全忠実度バックアップ。
            // tdv_* フラグ起動時のみ自動稼働・逐次書き込みで長時間可・pause ごとに確定保存）
            systems.AddComponent<StreamingPoseRecorder>();

            systems.AddComponent<RecenterWatcher>();
            // 手動リセット: コントローラ両手グリップ長押しで頭を席へ戻す（人側/手側両方・ジェスチャー不可）
            systems.AddComponent<ControllerRecenterWatcher>();

            var fake = systems.AddComponent<FakeHandDriver>();
            fake.enabled = false; // L0 検証時に手動で ON

            systems.AddComponent<ConnectionManager>();
            // 卓上ピース物理の衝突マトリクス（TableProps 同士のみ衝突。起動時 1 回設定）
            systems.AddComponent<PiecePhysicsConfig>();
            // 左コントローラ Y で手の見た目を巡回切替（お試し用。調査本番は tdv_hand フラグで固定）
            systems.AddComponent<HandVariantWatcher>();
            // 右コントローラ B でワイヤタップ記録トグル（ソロ実機検証: 送出 pose + 受信 pose を CSV 化）
            systems.AddComponent<WireTapRecorder>();

            // リモートの手を描くプレハブ/材質の供給（3 バリアント）。
            // Default = Meta 白手（OVRCustomHandPrefab、同期 bone を直接駆動）。
            // Realistic/Robot = 購入パックの別リグ手（バインド差分リターゲット、RemoteAvatarView/LocalVariantHand）。
            var meshProvider = systems.AddComponent<RemoteHandMeshProvider>();
            var mpSo = new SerializedObject(meshProvider);
            SetRef(mpSo, "leftHandPrefab", AssetDatabase.LoadAssetAtPath<GameObject>(
                "Packages/com.meta.xr.sdk.core/Prefabs/OVRCustomHandPrefab_L.prefab"));
            SetRef(mpSo, "rightHandPrefab", AssetDatabase.LoadAssetAtPath<GameObject>(
                "Packages/com.meta.xr.sdk.core/Prefabs/OVRCustomHandPrefab_R.prefab"));
            SetRef(mpSo, "handMaterial", handMat); // ローカル手と同じ URP 白マテリアル
            // Realistic（Male Hand・肌 URP/Lit）
            SetRef(mpSo, "realisticLeftPrefab", AssetDatabase.LoadAssetAtPath<GameObject>(RealisticLeftPrefab));
            SetRef(mpSo, "realisticRightPrefab", AssetDatabase.LoadAssetAtPath<GameObject>(RealisticRightPrefab));
            SetRef(mpSo, "realisticMaterial", EnsureHandVariantMaterial(
                $"{MaterialDir}/TableDuoRealisticHand.mat", MaleAlbedo, MaleNormal, MaleAo, metal: false));
            // Robot（Robot Hand・金属 URP/Lit）
            SetRef(mpSo, "robotLeftPrefab", AssetDatabase.LoadAssetAtPath<GameObject>(RobotLeftPrefab));
            SetRef(mpSo, "robotRightPrefab", AssetDatabase.LoadAssetAtPath<GameObject>(RobotRightPrefab));
            SetRef(mpSo, "robotMaterial", EnsureHandVariantMaterial(
                $"{MaterialDir}/TableDuoRobotHand.mat", RobotAlbedo, RobotNormal, RobotMetallic, metal: true));
            mpSo.ApplyModifiedPropertiesWithoutUndo();

            // 調査ロガー（ホストのみ稼働）+ フェーズマーク受付 + リプレイ全記録
            var sessionLogger = systems.AddComponent<SessionLogger>();
            // 盤面リセット（curl mark?label=reset_board で卓上プロップを初期配置へ）
            systems.AddComponent<BoardReset>();
            var markServer = systems.AddComponent<FacilitatorMarkServer>();
            var markSo = new SerializedObject(markServer);
            SetRef(markSo, "logger", sessionLogger);
            markSo.ApplyModifiedPropertiesWithoutUndo();
            systems.AddComponent<SessionReplayRecorder>();

            // リプレイビューア（stimulated recall 用・既定無効。有効化して Play で再生）
            var replayGo = new GameObject("ReplayViewer");
            replayGo.transform.SetParent(root.transform, false);
            replayGo.AddComponent<ReplayViewer>();
            replayGo.SetActive(false);

            // --- NetworkManager + プレイヤープレハブ ---
            var playerPrefab = CreatePlayerPrefab();
            var nmGo = new GameObject("NetworkManager");
            var nm = nmGo.AddComponent<NetworkManager>();
            var utp = nmGo.AddComponent<UnityTransport>();
            nm.NetworkConfig = new NetworkConfig
            {
                NetworkTransport = utp,
                PlayerPrefab = playerPrefab,
                // 60Hz tick: named message（pose）の flush 間隔を半減（33→16ms）。
                // TableDuoPlayer.sendRate=60 と必ず揃える（送信が速くても tick で律速されるため）。
                TickRate = 60,
            };

            EditorSceneManager.MarkSceneDirty(scene);
            Directory.CreateDirectory("Assets/TableDuo/Scenes");
            EditorSceneManager.SaveScene(scene, ScenePath);

            Debug.Log("[TableDuoSceneSetup] 完了。テーブル/席2/OVRCameraRig(+Hands)/NetworkManager/Systems 配置済み。\n" +
                      "- 実機: OVRProjectConfig の Hand Tracking Support を Controllers And Hands 以上にすること\n" +
                      "- ビルド対象にする時は Build Settings へ本シーンを手動追加（Main.unity と排他運用）\n" +
                      "- L0 検証: OVRCameraRig を無効化 / DebugCamera と FakeHandDriver を有効化");
        }

        // 部屋の内寸（正方形の一辺）と天井高。プレイ空間（テーブル±1m 程度）を
        // 圧迫せず、かつ「屋外に見えない」最小限の箱
        private const float RoomSize = 6f;
        private const float RoomHeight = 2.6f;

        /// <summary>
        /// 壁 4 面 + 天井の無地室内ボックスを生成する。青空 skybox を隠して屋内タスクの
        /// 文脈に合わせるのが目的で、装飾は置かない（調査の注意誘引を避ける）。
        /// Collider は不要（プレイヤーは物理移動しない）ので外す。
        /// </summary>
        private static void CreateRoomShell(Transform root)
        {
            var wallMat = EnsureMaterial($"{MaterialDir}/TableDuoWall.mat",
                "Universal Render Pipeline/Lit", new Color(0.63f, 0.61f, 0.58f)); // 暖色寄りグレー（漆喰調）
            var ceilMat = EnsureMaterial($"{MaterialDir}/TableDuoCeiling.mat",
                "Universal Render Pipeline/Lit", new Color(0.72f, 0.72f, 0.73f)); // 天井は少し明るく

            var shell = new GameObject("RoomShell");
            shell.transform.SetParent(root, false);

            float half = RoomSize * 0.5f;
            const float t = 0.1f; // 壁厚
            // (name, position, scale)
            (string name, Vector3 pos, Vector3 scale)[] parts =
            {
                ("Wall_N", new Vector3(0f, RoomHeight * 0.5f, half + t * 0.5f), new Vector3(RoomSize + t * 2f, RoomHeight, t)),
                ("Wall_S", new Vector3(0f, RoomHeight * 0.5f, -half - t * 0.5f), new Vector3(RoomSize + t * 2f, RoomHeight, t)),
                ("Wall_E", new Vector3(half + t * 0.5f, RoomHeight * 0.5f, 0f), new Vector3(t, RoomHeight, RoomSize)),
                ("Wall_W", new Vector3(-half - t * 0.5f, RoomHeight * 0.5f, 0f), new Vector3(t, RoomHeight, RoomSize)),
                ("Ceiling", new Vector3(0f, RoomHeight + t * 0.5f, 0f), new Vector3(RoomSize + t * 2f, t, RoomSize + t * 2f)),
            };
            foreach (var (name, pos, scale) in parts)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = name;
                go.transform.SetParent(shell.transform, false);
                go.transform.localPosition = pos;
                go.transform.localScale = scale;
                go.GetComponent<Renderer>().sharedMaterial = name == "Ceiling" ? ceilMat : wallMat;
                var col = go.GetComponent<Collider>();
                if (col != null) Object.DestroyImmediate(col);
            }

            // 幅木（壁と床の境界を締める細いライン）。無地の箱が「テクスチャ抜け」に
            // 見えないよう、最低限のスケール手がかりだけ足す
            var trimMat = EnsureMaterial($"{MaterialDir}/TableDuoTrim.mat",
                "Universal Render Pipeline/Lit", new Color(0.38f, 0.36f, 0.34f));
            (Vector3 pos, Vector3 scale)[] trims =
            {
                (new Vector3(0f, 0.05f, half - 0.02f), new Vector3(RoomSize, 0.1f, 0.04f)),
                (new Vector3(0f, 0.05f, -half + 0.02f), new Vector3(RoomSize, 0.1f, 0.04f)),
                (new Vector3(half - 0.02f, 0.05f, 0f), new Vector3(0.04f, 0.1f, RoomSize)),
                (new Vector3(-half + 0.02f, 0.05f, 0f), new Vector3(0.04f, 0.1f, RoomSize)),
            };
            for (int i = 0; i < trims.Length; i++)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = $"Baseboard_{i}";
                go.transform.SetParent(shell.transform, false);
                go.transform.localPosition = trims[i].pos;
                go.transform.localScale = trims[i].scale;
                go.GetComponent<Renderer>().sharedMaterial = trimMat;
                var col = go.GetComponent<Collider>();
                if (col != null) Object.DestroyImmediate(col);
            }
        }

        /// <summary>
        /// FBX をインスタンス化し、Renderer バウンディングの高さが targetHeight になるよう
        /// 一様スケール + 足元 (bounds.min.y) を pos.y に揃える。FBX が無ければ null。
        /// </summary>
        /// <summary>横幅（X/Z スケール）を保ったまま Y スケールだけ伸縮して天板を targetTopY へ合わせ、
        /// 足元を床（y=0）へ再接地する。幅フィットで頭打ちになった机を「脚だけ高く」するのに使う。</summary>
        private static void StretchToTopHeight(GameObject go, float targetTopY)
        {
            var b = WorldBounds(go);
            float cur = b.max.y; // 接地済みなので現在の天板高 ≒ max.y
            if (cur < 0.0001f) return;
            var s = go.transform.localScale;
            s.y *= targetTopY / cur;
            go.transform.localScale = s;
            // 再接地（足元を y=0 へ）
            var b2 = WorldBounds(go);
            var lp = go.transform.localPosition;
            lp.y -= b2.min.y;
            go.transform.localPosition = lp;
        }

        private static GameObject? InstantiateModelFitHeight(string assetPath, Transform parent,
            string name, Vector3 pos, float yaw, float targetHeight, float maxWidth = 0f)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefab == null)
            {
                Debug.LogWarning($"[TableDuoSceneSetup] モデルが見つかりません: {assetPath}");
                return null;
            }
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);

            // Kenney FBX はピボットが中心に無いものがあるため、
            // バウンディングで「高さ正規化 + 水平センタリング + 足元接地」を行う
            var renderers = go.GetComponentsInChildren<Renderer>();
            if (renderers.Length > 0)
            {
                var b = CalcBounds(renderers);
                if (b.size.y > 0.0001f)
                {
                    float k = targetHeight / b.size.y;
                    float horizontal = Mathf.Max(b.size.x, b.size.z);
                    if (maxWidth > 0f && horizontal * k > maxWidth)
                    {
                        k = maxWidth / horizontal;
                    }
                    go.transform.localScale = go.transform.localScale * k;
                }
                b = CalcBounds(renderers);
                var pivotToBounds = b.center - go.transform.position;
                go.transform.localPosition = new Vector3(
                    pos.x - pivotToBounds.x,
                    pos.y - (b.min.y - go.transform.position.y),
                    pos.z - pivotToBounds.z);
            }
            else
            {
                go.transform.localPosition = pos;
            }
            return go;
        }

        private static Bounds CalcBounds(Renderer[] renderers)
        {
            var b = renderers[0].bounds;
            foreach (var r in renderers) b.Encapsulate(r.bounds);
            return b;
        }

        /// <summary>GameObject 配下の全 Renderer のワールドバウンディング。Renderer 無しは中心ゼロ。</summary>
        private static Bounds WorldBounds(GameObject? go)
        {
            if (go == null) return new Bounds(Vector3.zero, Vector3.zero);
            var rs = go.GetComponentsInChildren<Renderer>();
            return rs.Length > 0 ? CalcBounds(rs) : new Bounds(go.transform.position, Vector3.zero);
        }

        private const string DsaGlbDir = "Assets/TableDuo/ThirdParty/DeepSeaAdventure/glb";

        /// <summary>
        /// 卓上にボードゲーム「海底探検」一式を**プレイ可能な形**で配置（G1・2026-07-02）。
        /// 潜水艦ボードは静置、宝物チップ16/裏トークン5/空気マーカー/駒2/サイコロ2 は全部掴める。
        /// ルール裁定はコード化しない（人間が運用 = 無言交渉そのものが研究データ）。
        /// チップ類はピンチで拾いやすいよう実物の 1.6 倍。ピースは物理（Rigidbody + TableProps レイヤー）、
        /// サイコロは物理転がし + 静止面読み取り（DiceRoller）。
        /// GLB は実スケール（メートル）。盤面リセットは BoardReset（mark?label=reset_board）。
        /// </summary>
        private static void PlaceDeepSeaAdventure(Transform parent, float topY, float cx, float cz,
            float hx, float hz, float edge)
        {
            // 掴めるチップ類の拡大率（ハンドトラッキングのピンチ精度対策）
            const float chipScale = 1.6f;

            // 宝物チップ = レベル順（浅→深）の 1 本の鎖。本家 海底探検に合わせ各値 2 枚ずつ = 32 枚。
            // 三角(lv1,値0-3)→四角(lv2,4-7)→五角(lv3,8-11)→六角(lv4,12-15)。同じ glb をもう 1 枚ずつ複製。
            var chain = new List<string>();
            foreach (var lvl in new[]
                     {
                         new[] { "tri_0", "tri_1", "tri_2", "tri_3" },
                         new[] { "sq_4", "sq_5", "sq_6", "sq_7" },
                         new[] { "pen_8", "pen_9", "pen_10", "pen_11" },
                         new[] { "hex_12", "hex_13", "hex_14", "hex_15" },
                     })
            {
                chain.AddRange(lvl);
                chain.AddRange(lvl); // 各値 2 枚
            }

            // 本家の初期配置と同じ「1 本の連続した数珠つなぎの S カーブ」（kaitei-seiretu.jpg 準拠。
            // グリッド蛇行ではなくチップ同士がほぼ接して曲線を描く）。行 + ヘアピンで繋いだポリラインを
            // ユークリッド間隔でサンプリングし、チップ向きも経路接線に沿わせる。
            // 数珠の間隔: 実測の最大チップ=五角 0.067m（1.6x）に対し +3mm ＝「くっつくかどうかギリギリ」。
            // 物理があるので実接触（<0.068）にはしない（ロード時に押し合って弾ける）
            const float step = 0.070f;
            float zStart = cz + (hz - edge) - 0.18f; // 鎖の最奥行（潜水艦の下）
            float zEnd = cz - (hz - edge) + 0.05f;   // 手前縁の内側まで

            var pts = SampleSerpentine(cx, zStart, zEnd, hx - edge - 0.05f, chain.Count, step);
            for (int i = 0; i < chain.Count; i++)
            {
                // 接線向き（次点との差分）。数字面の基準 yaw=180 に接線回りを加える
                Vector3 dir = (i < pts.Count - 1 ? pts[i + 1] - pts[i] : pts[i] - pts[i - 1]);
                float yaw = 180f + Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
                // 先頭チップ（潜水艦のもぐり口に繋がる）は先端を潜水艦（+Z）へ向ける
                if (i == 0) yaw = 0f;
                var pos = new Vector3(pts[i].x, topY, pts[i].z);
                var chip = PlaceModelRealScale($"{DsaGlbDir}/{chain[i]}.glb", parent, $"DSA_{chain[i]}_{i}", pos, yaw,
                    grabbable: true, scale: chipScale, physics: true);
                SetSurfaceClamp(chip, topY, cx, cz, hx, hz);
            }

            // 潜水艦ボードを 180° 回転（もぐり口 ⊗ を手前＝チェーン側へ）し、その中央下（⊗）が
            // 先頭チップの先端に来るよう、先頭チップの真上・すぐ奥へ置く（船の中央 = チップ 1 の上）。
            // 先頭チップは先端(+Z)が最も奥へ張り出す（実測でチップ中心から +0.022m）。船の前縁が
            // その先端の 8mm 奥に来るよう置く＝被らず「船の下端からチップが続く」隙間になる
            const float boardHalfDepth = 0.043f; // 実測 size.z 0.086 の半分
            const float chip0TipReach = 0.022f;  // 先頭三角の中心→先端(+Z)
            const float boardChipGap = 0.008f;    // 船下端とチップ先端の隙間
            float board0X = pts[0].x;
            float boardZ = pts[0].z + chip0TipReach + boardChipGap + boardHalfDepth;
            PlaceModelRealScale($"{DsaGlbDir}/submarine_board.glb", parent, "DSA_Board",
                new Vector3(board0X, topY, boardZ), 180f, grabbable: false);

            // 裏トークン（丸 X）20 枚を人役側の左（-X）に 2 山で積む。掴めない静置マーカー。
            PlaceBlankStacks(parent, topY, cx - (hx - edge - 0.05f), cz - (hz - edge - 0.06f), chipScale);

            // 駒2 + サイコロ2 + 空気マーカーは潜水艦の脇（右, +X 側）に一列。掴める＋物理。
            float sideX = cx + (hx - edge - 0.06f);
            var mp = PlaceModelRealScale($"{DsaGlbDir}/meeple_purple.glb", parent, "DSA_MeeplePurple",
                new Vector3(sideX - 0.09f, topY, boardZ), 0f, grabbable: true, scale: 1.6f, physics: true);
            var mr = PlaceModelRealScale($"{DsaGlbDir}/meeple_red.glb", parent, "DSA_MeepleRed",
                new Vector3(sideX - 0.02f, topY, boardZ), 0f, grabbable: true, scale: 1.6f, physics: true);
            var die1 = PlaceModelRealScale($"{DsaGlbDir}/die.glb", parent, "DSA_Die1",
                new Vector3(sideX - 0.09f, topY, boardZ - 0.09f), 0f, grabbable: true, scale: 1.5f, physics: true, ccd: true);
            var die2 = PlaceModelRealScale($"{DsaGlbDir}/die.glb", parent, "DSA_Die2",
                new Vector3(sideX - 0.02f, topY, boardZ - 0.09f), 0f, grabbable: true, scale: 1.5f, physics: true, ccd: true);
            var air = PlaceModelRealScale($"{DsaGlbDir}/air_marker.glb", parent, "DSA_air_marker",
                new Vector3(sideX + 0.02f, topY, boardZ), 0f, grabbable: true, scale: chipScale, physics: true);
            SetSurfaceClamp(air, topY, cx, cz, hx, hz);

            if (die1 != null) die1.AddComponent<DiceRoller>();
            if (die2 != null) die2.AddComponent<DiceRoller>();
            foreach (var piece in new[] { mp, mr, die1, die2 })
            {
                SetSurfaceClamp(piece, topY, cx, cz, hx, hz);
            }
        }

        /// <summary>
        /// 写真（kaitei-seiretu.jpg）の構造 = 「直線の行 ＋ 半円ターンで繋がる 1 本のサーペンタイン」。
        /// 行（X 方向の直線）を交互方向に走らせ、行端を半円弧（半径 = 行間/2）で連結した密なポリラインを作り、
        /// その上を「直前チップからのユークリッド距離 ≥ step」で count 点サンプリングする
        /// （弧長等間隔だとターンで直線距離が縮み、物理チップが重なって弾け飛ぶ）。
        /// </summary>
        private static List<Vector3> SampleSerpentine(float cx, float zStart, float zEnd, float usableHalfX,
            int count, float step)
        {
            // 行数・行長を決める: 8 枚/行 を基本に、ターン半径ぶん（rowPitch/2）を左右に確保して収める
            int perRow = 8;
            int rows = Mathf.CeilToInt(count / (float)perRow);
            float rowPitch = rows > 1 ? Mathf.Min(0.10f, (zStart - zEnd) / (rows - 1)) : 0.10f;
            float turnR = rowPitch * 0.5f;
            float rowHalf = (perRow - 1) * step * 0.5f;
            // 行 + ターンが卓幅を超えるなら行あたり枚数を減らして作り直す
            while (rowHalf + turnR > usableHalfX && perRow > 4)
            {
                perRow--;
                rows = Mathf.CeilToInt(count / (float)perRow);
                rowPitch = rows > 1 ? Mathf.Min(0.10f, (zStart - zEnd) / (rows - 1)) : 0.10f;
                turnR = rowPitch * 0.5f;
                rowHalf = (perRow - 1) * step * 0.5f;
            }

            // 密なポリライン（行 → 半円 → 行 → …）。行 0 は左→右、以後交互
            var poly = new List<Vector2>(rows * 64);
            for (int r = 0; r < rows; r++)
            {
                float z = zStart - r * rowPitch;
                bool ltr = (r % 2 == 0);
                float xa = ltr ? -rowHalf : rowHalf;
                float xb = -xa;
                const int SegN = 32;
                for (int i = 0; i <= SegN; i++)
                {
                    poly.Add(new Vector2(Mathf.Lerp(xa, xb, i / (float)SegN), z));
                }
                if (r == rows - 1) break;
                // 行端の半円ターン（外側へ膨らみつつ次の行へ）。中心 = (xb, z - turnR)
                const int ArcN = 24;
                float dirSign = ltr ? 1f : -1f; // 右端ターンは +X 側へ膨らむ
                for (int i = 1; i < ArcN; i++)
                {
                    float a = Mathf.PI * 0.5f - Mathf.PI * (i / (float)ArcN); // +90°→-90°
                    poly.Add(new Vector2(xb + dirSign * turnR * Mathf.Cos(a), z - turnR + turnR * Mathf.Sin(a)));
                }
            }

            // ユークリッド間隔サンプリング
            var pts = new List<Vector3>(count);
            Vector2 lastPlaced = poly[0];
            pts.Add(new Vector3(cx + poly[0].x, 0f, poly[0].y));
            for (int i = 1; i < poly.Count && pts.Count < count; i++)
            {
                // ポリラインの粗さで step を飛び越さないよう線分内も補間しながら進む
                Vector2 a = poly[i - 1], b = poly[i];
                float seg = Vector2.Distance(a, b);
                const float ds = 0.004f;
                int sub = Mathf.Max(1, Mathf.CeilToInt(seg / ds));
                for (int s = 1; s <= sub && pts.Count < count; s++)
                {
                    Vector2 p = Vector2.Lerp(a, b, s / (float)sub);
                    if (Vector2.Distance(lastPlaced, p) >= step)
                    {
                        pts.Add(new Vector3(cx + p.x, 0f, p.y));
                        lastPlaced = p;
                    }
                }
            }
            while (pts.Count < count) // 保険（通常発生しない）
            {
                Vector3 last = pts[pts.Count - 1];
                Vector3 dir = pts.Count >= 2 ? (last - pts[pts.Count - 2]).normalized : Vector3.back;
                pts.Add(last + dir * step);
            }
            return pts;
        }

        /// <summary>裏トークン（back_circle・丸 X）20 枚を 2 山（各 10 枚）で積む。掴めない静置マーカー。</summary>
        private static void PlaceBlankStacks(Transform parent, float topY, float baseX, float baseZ, float scale)
        {
            const int perStack = 10;
            const float stackGap = 0.075f; // 2 山の間隔
            for (int s = 0; s < 2; s++)
            {
                float x = baseX + s * stackGap;
                float y = topY;
                float thickness = 0f;
                for (int k = 0; k < perStack; k++)
                {
                    var tok = PlaceModelRealScale($"{DsaGlbDir}/back_circle.glb", parent,
                        $"DSA_blank_{s}_{k}", new Vector3(x, y, baseZ), 0f, grabbable: false, scale: scale);
                    if (tok != null && thickness <= 0f)
                    {
                        var b = WorldBounds(tok);
                        thickness = Mathf.Max(0.004f, b.size.y); // 実測厚み（次段の積み上げ量）
                    }
                    y += thickness > 0f ? thickness : 0.006f;
                }
            }
        }

        /// <summary>掴めるピースに卓上拘束を焼き込む（テーブル貫通・卓外落下の防止。Grabbable が clamp）。</summary>
        private static void SetSurfaceClamp(GameObject? go, float topY, float cx, float cz, float hx, float hz)
        {
            if (go == null) return;
            var grab = go.GetComponent<Grabbable>();
            if (grab == null) return;
            var so = new SerializedObject(grab);
            var pY = so.FindProperty("surfaceY");
            var pC = so.FindProperty("surfaceCenter");
            var pH = so.FindProperty("surfaceHalf");
            if (pY != null) pY.floatValue = topY;
            if (pC != null) pC.vector2Value = new Vector2(cx, cz);
            // 卓縁より少し内側まで許す（縁ギリギリで浮くのを防ぎつつ卓外へは出さない）
            if (pH != null) pH.vector2Value = new Vector2(hx - 0.02f, hz - 0.02f);
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>TableProps レイヤーを TagManager に確保する（無ければ 8 以降の空きスロットへ追記）。</summary>
        private static int EnsureTablePropsLayer()
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
            if (assets.Length == 0)
            {
                Debug.LogWarning("[TableDuoSceneSetup] TagManager.asset を開けずレイヤー確保をスキップ");
                return -1;
            }
            var tagManager = new SerializedObject(assets[0]);
            var layers = tagManager.FindProperty("layers");
            if (layers == null) return -1;
            int firstEmpty = -1;
            for (int i = 8; i < layers.arraySize; i++)
            {
                string v = layers.GetArrayElementAtIndex(i).stringValue;
                if (v == PiecePhysicsConfig.LayerName) return i;
                if (firstEmpty < 0 && string.IsNullOrEmpty(v)) firstEmpty = i;
            }
            if (firstEmpty < 0)
            {
                Debug.LogWarning("[TableDuoSceneSetup] レイヤー空きスロットなし。TableProps を確保できない");
                return -1;
            }
            layers.GetArrayElementAtIndex(firstEmpty).stringValue = PiecePhysicsConfig.LayerName;
            tagManager.ApplyModifiedPropertiesWithoutUndo();
            Debug.Log($"[TableDuoSceneSetup] レイヤー {PiecePhysicsConfig.LayerName} を slot {firstEmpty} に追加");
            return firstEmpty;
        }

        /// <summary>PhysicMaterial アセットを生成（無ければ）して係数を焼き込む。</summary>
        private static PhysicMaterial EnsurePhysicMaterial(string path, float friction, float bounciness)
        {
            var mat = AssetDatabase.LoadAssetAtPath<PhysicMaterial>(path);
            if (mat == null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                mat = new PhysicMaterial();
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.dynamicFriction = friction;
            mat.staticFriction = friction;
            mat.bounciness = bounciness;
            mat.frictionCombine = PhysicMaterialCombine.Average;
            mat.bounceCombine = PhysicMaterialCombine.Maximum;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        /// <summary>
        /// 掴めるピースに物理一式を付ける: バウンディング適合 BoxCollider + Rigidbody +
        /// NetworkRigidbody（非権威側を自動 kinematic 化）+ TableProps レイヤー。
        /// ccd=true は高速投擲でのトンネリング防止（サイコロ用）。
        /// </summary>
        private static void AttachPiecePhysics(GameObject go, bool ccd)
        {
            var renderers = go.GetComponentsInChildren<Renderer>();
            var b = renderers.Length > 0 ? CalcBounds(renderers)
                : new Bounds(go.transform.position, Vector3.one * 0.03f);
            var box = go.AddComponent<BoxCollider>();
            // yaw 0/180 配置前提でワールド AABB を局所サイズへ流用（軸の大きさは一致する）
            box.center = go.transform.InverseTransformPoint(b.center);
            var ls = go.transform.lossyScale;
            box.size = new Vector3(
                Mathf.Abs(b.size.x / ls.x), Mathf.Abs(b.size.y / ls.y), Mathf.Abs(b.size.z / ls.z));
            box.sharedMaterial = EnsurePhysicMaterial(
                $"{MaterialDir}/TableDuoPiece.physicMaterial", friction: 0.5f, bounciness: 0.3f);

            var rb = go.AddComponent<Rigidbody>();
            rb.mass = 0.1f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = ccd
                ? CollisionDetectionMode.ContinuousDynamic : CollisionDetectionMode.Discrete;

            go.AddComponent<Unity.Netcode.Components.NetworkRigidbody>();

            int layer = LayerMask.NameToLayer(PiecePhysicsConfig.LayerName);
            if (layer >= 0) go.layer = layer;
        }

        /// <summary>
        /// GLB プレハブ（glTFast 取込）を実スケールのまま卓上に接地配置（足元 = topY, 水平センタリング）。
        /// grabbable=true なら旧プロップ同様 NetworkObject + NetworkTransform + Grabbable を付ける。
        /// physics=true はさらに Rigidbody + Collider + NetworkRigidbody（卓上ボードゲームのピース用）。
        /// </summary>
        private static GameObject? PlaceModelRealScale(string glbPath, Transform parent, string name,
            Vector3 pos, float yaw, bool grabbable, float scale = 1f, bool physics = false, bool ccd = false)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(glbPath);
            if (prefab == null)
            {
                Debug.LogWarning($"[TableDuoSceneSetup] GLB が見つかりません: {glbPath}（glTFast 取込未完？）");
                return null;
            }
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            if (!Mathf.Approximately(scale, 1f)) go.transform.localScale *= scale;

            var renderers = go.GetComponentsInChildren<Renderer>();
            if (renderers.Length > 0)
            {
                var b = CalcBounds(renderers);
                var pivotToBounds = b.center - go.transform.position;
                go.transform.localPosition = new Vector3(
                    pos.x - pivotToBounds.x,
                    pos.y - (b.min.y - go.transform.position.y),
                    pos.z - pivotToBounds.z);
            }
            else
            {
                go.transform.localPosition = pos;
            }

            if (grabbable)
            {
                go.AddComponent<NetworkObject>();
                var nt = go.AddComponent<Unity.Netcode.Components.NetworkTransform>();
                nt.Interpolate = true;
                nt.SyncScaleX = nt.SyncScaleY = nt.SyncScaleZ = false;
                go.AddComponent<Grabbable>();
                if (physics) AttachPiecePhysics(go, ccd);
            }
            return go;
        }

        private static readonly string[] CardIcons =
        {
            "home", "phone", "gamepad", "video", "star", "trophy",
            "wrench", "gear", "shoppingCart", "mouse", "target", "trashcan",
        };

        /// <summary>絵カード 12 枚をテーブル天板の手前側に 2 列で乗せる（天板高 topY・範囲 tb 内）。</summary>
        private static void CreateCardDeck(Transform root, float topY, Bounds tb)
        {
            var deck = new GameObject("Cards");
            deck.transform.SetParent(root, false);
            var backMat = TableDuoStudyAssets.EnsureCardBackMaterial();
            var bodyMat = AssetDatabase.LoadAssetAtPath<Material>($"{MaterialDir}/TableDuoFloor.mat");

            const int cols = 6;
            float colSpan = Mathf.Min(0.45f, tb.extents.x - 0.07f - 0.06f); // 天板 X 内・カード幅考慮
            float frontLimit = tb.center.z - (tb.extents.z - 0.07f - 0.08f); // 手前縁の内側
            const float cardHalfThick = 0.002f;

            for (int i = 0; i < CardIcons.Length; i++)
            {
                string id = CardIcons[i];
                var faceMat = TableDuoStudyAssets.EnsureCardFaceMaterial(
                    $"Assets/ThirdParty/Kenney/Icons/{id}.png", id);

                int row = i / cols;
                int col = i % cols;
                float fx = cols > 1 ? Mathf.Lerp(-colSpan, colSpan, col / (float)(cols - 1)) : 0f;
                float fz = Mathf.Max(tb.center.z - 0.04f - row * 0.16f, frontLimit);
                var pos = new Vector3(tb.center.x + fx, topY + cardHalfThick, fz);

                var card = GameObject.CreatePrimitive(PrimitiveType.Cube);
                card.name = $"Card_{id}";
                card.transform.SetParent(deck.transform, false);
                card.transform.localPosition = pos;
                card.transform.localScale = new Vector3(0.10f, 0.004f, 0.14f);
                if (bodyMat != null) card.GetComponent<Renderer>().sharedMaterial = bodyMat;

                CreateCardFace(card.transform, "Face", faceMat, up: true);
                CreateCardFace(card.transform, "Back", backMat, up: false);

                card.AddComponent<NetworkObject>();
                var nt = card.AddComponent<Unity.Netcode.Components.NetworkTransform>();
                nt.Interpolate = true;
                nt.SyncScaleX = nt.SyncScaleY = nt.SyncScaleZ = false;
                card.AddComponent<Grabbable>();
                var prop = card.AddComponent<CardProp>();
                var propSo = new SerializedObject(prop);
                var idProp = propSo.FindProperty("cardId");
                if (idProp != null) idProp.stringValue = id;
                var normalProp = propSo.FindProperty("faceNormalLocal");
                if (normalProp != null) normalProp.vector3Value = Vector3.up;
                propSo.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        private static void CreateCardFace(Transform card, string name, Material mat, bool up)
        {
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = name;
            var col = quad.GetComponent<Collider>();
            if (col != null) Object.DestroyImmediate(col);
            quad.transform.SetParent(card, false);
            // 親 cube が (0.10, 0.004, 0.14) スケールなのでローカルは正規化座標
            quad.transform.localPosition = new Vector3(0f, up ? 0.51f : -0.51f, 0f);
            quad.transform.localRotation = Quaternion.Euler(up ? 90f : -90f, 0f, 0f);
            quad.transform.localScale = new Vector3(0.95f, 0.95f, 1f);
            quad.GetComponent<Renderer>().sharedMaterial = mat;
        }

        /// <summary>手役席（席1）の斜め上方・作業空間の外に目標配置パネルを置く。</summary>
        private static void CreatePatternPanel(Transform root)
        {
            var holder = new GameObject("PatternPanel");
            holder.transform.SetParent(root, false);

            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "PanelQuad";
            var col = quad.GetComponent<Collider>();
            if (col != null) Object.DestroyImmediate(col);
            quad.transform.SetParent(holder.transform, false);
            // 席1 (0,0,0.85) の斜め上・横。テーブル上の作業視線と分離した位置
            var panelPos = new Vector3(0.8f, 1.6f, 1.4f);
            quad.transform.position = panelPos;
            var lookFrom = new Vector3(0f, 1.5f, 0.85f); // 席1 の頭の想定位置
            quad.transform.rotation = Quaternion.LookRotation(panelPos - lookFrom);
            quad.transform.localScale = new Vector3(0.35f, 0.35f, 1f);
            quad.SetActive(false); // PatternPanel が手役ローカルでのみ有効化する

            var panel = holder.AddComponent<PatternPanel>();
            var so = new SerializedObject(panel);
            SetRef(so, "panelRenderer", quad.GetComponent<Renderer>());
            var mats = TableDuoStudyAssets.EnsurePatternMaterials(4);
            var matsProp = so.FindProperty("patterns");
            if (matsProp != null && matsProp.isArray)
            {
                matsProp.arraySize = mats.Length;
                for (int i = 0; i < mats.Length; i++)
                {
                    matsProp.GetArrayElementAtIndex(i).objectReferenceValue = mats[i];
                }
            }
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static Material EnsureMaterial(string path, string shaderName, Color color)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var shader = Shader.Find(shaderName);
                if (shader == null)
                {
                    Debug.LogError($"[TableDuoSceneSetup] シェーダが見つかりません: {shaderName}");
                    shader = Shader.Find("Universal Render Pipeline/Lit");
                }
                mat = new Material(shader!);
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.color = color;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        /// <summary>
        /// パック手用の URP/Lit マテリアルを生成（無ければ）してテクスチャを割り当てる。パック同梱の
        /// Standard マテリアルは URP でマゼンタ化するため、これで全 Renderer を上書きする（BuildExternalHand）。
        /// metal=true は金属（metallic map + metallic=1）、false は肌（AO map + 非金属）。
        /// normal テクスチャは NormalMap タイプに強制インポートしてから割り当てる。
        /// </summary>
        private static Material EnsureHandVariantMaterial(string path, string albedoPath, string normalPath,
            string extraPath, bool metal)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var shader = Shader.Find("Universal Render Pipeline/Lit");
                mat = new Material(shader!);
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.color = Color.white;
            mat.SetColor("_BaseColor", Color.white);

            var albedo = AssetDatabase.LoadAssetAtPath<Texture>(albedoPath);
            if (albedo != null) mat.SetTexture("_BaseMap", albedo);

            EnsureNormalMapImport(normalPath);
            var normal = AssetDatabase.LoadAssetAtPath<Texture>(normalPath);
            if (normal != null) { mat.SetTexture("_BumpMap", normal); mat.EnableKeyword("_NORMALMAP"); }

            var extra = AssetDatabase.LoadAssetAtPath<Texture>(extraPath);
            if (metal)
            {
                if (extra != null) { mat.SetTexture("_MetallicGlossMap", extra); mat.EnableKeyword("_METALLICSPECGLOSSMAP"); }
                mat.SetFloat("_Metallic", 1f);
                mat.SetFloat("_Smoothness", 0.5f);
            }
            else
            {
                if (extra != null) { mat.SetTexture("_OcclusionMap", extra); mat.EnableKeyword("_OCCLUSIONMAP"); }
                mat.SetFloat("_Metallic", 0f);
                mat.SetFloat("_Smoothness", 0.3f);
            }
            EditorUtility.SetDirty(mat);
            return mat;
        }

        /// <summary>normal テクスチャの ImporterType が NormalMap でなければ設定して再インポートする。</summary>
        private static void EnsureNormalMapImport(string texturePath)
        {
            var importer = AssetImporter.GetAtPath(texturePath) as TextureImporter;
            if (importer == null || importer.textureType == TextureImporterType.NormalMap) return;
            importer.textureType = TextureImporterType.NormalMap;
            importer.SaveAndReimport();
        }

        private static void CreateProp(Transform parent, string name, Vector3 pos, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = Vector3.one * 0.08f;
            go.GetComponent<Renderer>().sharedMaterial = mat;
            go.AddComponent<NetworkObject>();
            var nt = go.AddComponent<Unity.Netcode.Components.NetworkTransform>();
            nt.Interpolate = true;
            nt.SyncScaleX = nt.SyncScaleY = nt.SyncScaleZ = false;
            go.AddComponent<Grabbable>();
        }

        private static void CreateSeat(Transform parent, int index, Vector3 pos, float yaw)
        {
            var seat = new GameObject($"Seat{index}");
            seat.transform.SetParent(parent, false);
            seat.transform.localPosition = pos;
            seat.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            seat.AddComponent<SeatEyeGizmo>(); // Scene ビューで目線位置・向きを可視化
        }

        private static GameObject? InstantiateRig()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(RigPrefabPath);
            if (prefab == null)
            {
                // パッケージ構成変更に備えたフォールバック検索
                foreach (var guid in AssetDatabase.FindAssets("OVRCameraRig t:Prefab"))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (Path.GetFileNameWithoutExtension(path) == "OVRCameraRig")
                    {
                        prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                        break;
                    }
                }
            }
            if (prefab == null)
            {
                Debug.LogError("[TableDuoSceneSetup] OVRCameraRig.prefab が見つかりません。Meta XR SDK を確認。");
                return null;
            }
            var rig = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            rig.name = "OVRCameraRig";
            return rig;
        }

        private static void AddHand(GameObject rig, string anchorPath, bool isLeft,
            out OVRHand? hand, out OVRSkeleton? skeleton)
        {
            hand = null;
            skeleton = null;
            var anchor = rig.transform.Find(anchorPath);
            if (anchor == null)
            {
                Debug.LogError($"[TableDuoSceneSetup] アンカーが見つかりません: {anchorPath}");
                return;
            }
            // OVRHandPrefab = OVRHand + OVRSkeleton + OVRMesh + OVRMeshRenderer + SkinnedMeshRenderer。
            // ローカル手の見た目（自分の手）をネット往復なしで描画するために必須
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(HandPrefabPath);
            GameObject go;
            if (prefab != null)
            {
                go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                go.transform.SetParent(anchor, false);
            }
            else
            {
                Debug.LogWarning("[TableDuoSceneSetup] OVRHandPrefab が見つからず素の OVRHand で代替（手の見た目なし）");
                go = new GameObject();
                go.transform.SetParent(anchor, false);
                go.AddComponent<OVRHand>();
                go.AddComponent<OVRSkeleton>();
            }
            go.name = isLeft ? "OVRHandLeft" : "OVRHandRight";

            hand = go.GetComponent<OVRHand>();
            var handSo = new SerializedObject(hand);
            SetEnum(handSo, "HandType", isLeft ? 0 : 1); // OVRPlugin.Hand: HandLeft=0, HandRight=1
            handSo.ApplyModifiedPropertiesWithoutUndo();

            skeleton = go.GetComponent<OVRSkeleton>();
            var skelSo = new SerializedObject(skeleton);
            SetEnum(skelSo, "_skeletonType", isLeft ? 0 : 1); // SkeletonType: HandLeft=0, HandRight=1
            skelSo.ApplyModifiedPropertiesWithoutUndo();

            var mesh = go.GetComponent<OVRMesh>();
            if (mesh != null)
            {
                var meshSo = new SerializedObject(mesh);
                SetEnum(meshSo, "_meshType", isLeft ? 0 : 1); // MeshType: HandLeft=0, HandRight=1
                meshSo.ApplyModifiedPropertiesWithoutUndo();
            }

            // 手マテリアルも URP に差し替え（プレハブ既定がビルドでマゼンタ化する保険）
            var smr = go.GetComponentInChildren<SkinnedMeshRenderer>();
            var urpHandMat = AssetDatabase.LoadAssetAtPath<Material>($"{MaterialDir}/TableDuoLocalHand.mat");
            if (smr != null && urpHandMat != null)
            {
                smr.sharedMaterial = urpHandMat;
            }

            // 自分のローカル手をバリアント（Realistic/Robot）で表示する駆動。Default 時は白手のまま。
            var localVariant = go.AddComponent<LocalVariantHand>();
            var lvSo = new SerializedObject(localVariant);
            SetRef(lvSo, "skeleton", skeleton);
            lvSo.FindProperty("isRight").boolValue = !isLeft;
            SetRef(lvSo, "metaMesh", smr);
            lvSo.ApplyModifiedPropertiesWithoutUndo();
        }

        private static GameObject CreatePlayerPrefab()
        {
            Directory.CreateDirectory("Assets/TableDuo/Prefabs");
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
            if (existing != null)
            {
                // 冪等: 既存 prefab に必須コンポーネントが揃っていればそのまま使う。欠けていれば
                // 非破壊で補う（以前は「あれば即 return」で、必須構成が増えても Setup 再実行で反映されなかった）。
                // 構成済みなら何もしないので sendRate 等の serialized 値は保持される。
                bool hasNetworkObject = existing.GetComponent<NetworkObject>() != null;
                bool hasPlayer = existing.GetComponent<TableDuoPlayer>() != null;
                if (hasNetworkObject && hasPlayer) return existing;

                var contents = PrefabUtility.LoadPrefabContents(PlayerPrefabPath);
                if (contents.GetComponent<NetworkObject>() == null) contents.AddComponent<NetworkObject>();
                if (contents.GetComponent<TableDuoPlayer>() == null) contents.AddComponent<TableDuoPlayer>();
                PrefabUtility.SaveAsPrefabAsset(contents, PlayerPrefabPath);
                PrefabUtility.UnloadPrefabContents(contents);
                Debug.Log("[TableDuoSceneSetup] 既存 TableDuoPlayer.prefab に欠けていた必須コンポーネントを補完");
                return AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
            }

            var go = new GameObject("TableDuoPlayer");
            go.AddComponent<NetworkObject>();
            go.AddComponent<TableDuoPlayer>();
            var prefab = PrefabUtility.SaveAsPrefabAsset(go, PlayerPrefabPath);
            Object.DestroyImmediate(go);
            return prefab;
        }

        private static void DeleteRoot(string name)
        {
            // GameObject.Find は inactive を見つけられず重複が生まれる（L0 トグルでリグを
            // 無効化したまま再 Setup した事故が実績あり）。ルートを直接走査する。
            var scene = SceneManager.GetActiveScene();
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.name == name) Object.DestroyImmediate(root);
            }
        }

        private static void SetRef(SerializedObject so, string prop, Object? value)
        {
            var p = so.FindProperty(prop);
            if (p == null)
            {
                Debug.LogWarning($"[TableDuoSceneSetup] FindProperty 失敗: {so.targetObject.GetType().Name}.{prop}");
                return;
            }
            p.objectReferenceValue = value;
        }

        private static void SetEnum(SerializedObject so, string prop, int intValue)
        {
            var p = so.FindProperty(prop);
            if (p == null)
            {
                Debug.LogWarning($"[TableDuoSceneSetup] FindProperty 失敗: {so.targetObject.GetType().Name}.{prop}");
                return;
            }
            p.intValue = intValue;
        }

        private static void SetBool(SerializedObject so, string prop, bool value)
        {
            var p = so.FindProperty(prop);
            if (p == null)
            {
                Debug.LogWarning($"[TableDuoSceneSetup] FindProperty 失敗: {so.targetObject.GetType().Name}.{prop}");
                return;
            }
            p.boolValue = value;
        }
    }
}
