#nullable enable
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
            DeleteRoot("DebugCamera");

            // --- 環境 ---
            var lightGo = new GameObject("Directional Light");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.0f;
            lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

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

            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "Floor";
            floor.transform.SetParent(root.transform, false);
            floor.GetComponent<Renderer>().sharedMaterial = floorMat;

            // テーブル: Kenney Furniture Kit（CC0）。バウンディングから天板高 0.7m に正規化。
            // FBX が無い環境では従来のキューブにフォールバック
            var table = InstantiateModelFitHeight(
                "Assets/ThirdParty/Kenney/Furniture/table.fbx", root.transform,
                "Table", Vector3.zero, 0f, targetHeight: 0.7f, maxWidth: 1.3f);
            if (table == null)
            {
                table = GameObject.CreatePrimitive(PrimitiveType.Cube);
                table.name = "Table";
                table.transform.SetParent(root.transform, false);
                table.transform.localPosition = new Vector3(0f, 0.35f, 0f);
                table.transform.localScale = new Vector3(1.2f, 0.7f, 0.8f);
                table.GetComponent<Renderer>().sharedMaterial = tableMat;
            }

            // テーブル天板の実測（maxWidth で縮むと天板高が 0.7 未満になるので実バウンディングを使う）。
            // 小物・カードはこの天板の上 (y=topY) かつ天板の XZ 範囲内に収める。
            Bounds tb = WorldBounds(table);
            float topY = tb.max.y;
            float cx = tb.center.x, cz = tb.center.z;
            float hx = tb.extents.x, hz = tb.extents.z;
            const float edge = 0.07f; // 縁マージン（はみ出し防止）

            // 椅子（見た目のみ・席アンカーとは独立・床に置く）
            // Kenney chair.fbx は -z が正面（180° が「テーブルへ向く」）
            InstantiateModelFitHeight("Assets/ThirdParty/Kenney/Furniture/chair.fbx",
                root.transform, "Chair0", new Vector3(0f, 0f, -0.78f), 180f, targetHeight: 0.85f, maxWidth: 0.55f);
            InstantiateModelFitHeight("Assets/ThirdParty/Kenney/Furniture/chair.fbx",
                root.transform, "Chair1", new Vector3(0f, 0f, 0.78f), 0f, targetHeight: 0.85f, maxWidth: 0.55f);
            // 卓上ランプ：天板の奥の角に乗せる
            InstantiateModelFitHeight("Assets/ThirdParty/Kenney/Furniture/lampRoundTable.fbx",
                root.transform, "TableLamp",
                new Vector3(cx - (hx - 0.14f), topY, cz - (hz - 0.14f)), 0f,
                targetHeight: 0.25f, maxWidth: 0.18f);

            // 席 = 初期目線アンカー。**ローカル原点が目の位置**（EyeLevel なので頭が席に乗る）、
            // forward(+Z) が視線方向。Y を座位の目の高さに置く。Scene ビューで席を動かして調整可能
            // （ギズモ表示 + Tools/FixedCamVr/Diagnostics/Preview Eye）。
            const float eyeHeight = 1.15f;
            var seats = new GameObject("Seats");
            seats.transform.SetParent(root.transform, false);
            CreateSeat(seats.transform, 0, new Vector3(0f, eyeHeight, -0.85f), 0f);    // フルアバター席
            CreateSeat(seats.transform, 1, new Vector3(0f, eyeHeight, 0.85f), 180f);   // 手だけアバター席

            // 掴める小物（scene-placed NetworkObject。サーバ駆動追従 + NetworkTransform 同期）
            // Kenney Food Kit（CC0）。FBX 不在時はオレンジキューブにフォールバック
            var props = new GameObject("Props");
            props.transform.SetParent(root.transform, false);
            string[] foods = { "apple", "banana", "burger", "carrot", "cake" };
            // 天板の手前寄りに横一列。天板の実 X 範囲に均等配置（はみ出し防止）
            float foodSpan = Mathf.Max(0f, hx - edge - 0.06f);
            for (int i = 0; i < foods.Length; i++)
            {
                float fx = foods.Length > 1 ? Mathf.Lerp(-foodSpan, foodSpan, i / (float)(foods.Length - 1)) : 0f;
                var pos = new Vector3(cx + fx, topY, cz + 0.12f); // y=天板（接地）/ 奥寄り
                var model = InstantiateModelFitHeight(
                    $"Assets/ThirdParty/Kenney/Food/{foods[i]}.fbx", props.transform,
                    $"Prop_{foods[i]}", pos, 0f, targetHeight: 0.09f);
                if (model == null)
                {
                    CreateProp(props.transform, $"Prop_{foods[i]}", pos, propMat);
                    continue;
                }
                model.AddComponent<NetworkObject>();
                var nt = model.AddComponent<Unity.Netcode.Components.NetworkTransform>();
                nt.Interpolate = true;
                nt.SyncScaleX = nt.SyncScaleY = nt.SyncScaleZ = false;
                model.AddComponent<Grabbable>();
            }

            // 絵カードデッキ（RQ2: カード照準分析用。片面絵柄 + 共通裏面）。天板の手前側に乗せる
            CreateCardDeck(root.transform, topY, tb);

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
                    mgrSo.ApplyModifiedPropertiesWithoutUndo();
                }
            }

            // --- L0 用デバッグカメラ（リグを無効化して使う。既定 OFF）---
            var debugCamGo = new GameObject("DebugCamera");
            var debugCam = debugCamGo.AddComponent<Camera>();
            debugCam.nearClipPlane = 0.05f;
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

            systems.AddComponent<RecenterWatcher>();

            var fake = systems.AddComponent<FakeHandDriver>();
            fake.enabled = false; // L0 検証時に手動で ON

            systems.AddComponent<ConnectionManager>();

            // リモートの手を Meta の白い手メッシュで描くための供給（ローカル手と同じ見た目）。
            // OVRCustomHandPrefab（静的メッシュ + CustomBones）を同期 bone で駆動する（RemoteAvatarView）
            var meshProvider = systems.AddComponent<RemoteHandMeshProvider>();
            var mpSo = new SerializedObject(meshProvider);
            SetRef(mpSo, "leftHandPrefab", AssetDatabase.LoadAssetAtPath<GameObject>(
                "Packages/com.meta.xr.sdk.core/Prefabs/OVRCustomHandPrefab_L.prefab"));
            SetRef(mpSo, "rightHandPrefab", AssetDatabase.LoadAssetAtPath<GameObject>(
                "Packages/com.meta.xr.sdk.core/Prefabs/OVRCustomHandPrefab_R.prefab"));
            SetRef(mpSo, "handMaterial", handMat); // ローカル手と同じ URP 白マテリアル
            mpSo.ApplyModifiedPropertiesWithoutUndo();

            // 調査ロガー（ホストのみ稼働）+ フェーズマーク受付 + リプレイ全記録
            var sessionLogger = systems.AddComponent<SessionLogger>();
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
                TickRate = 30,
            };

            EditorSceneManager.MarkSceneDirty(scene);
            Directory.CreateDirectory("Assets/TableDuo/Scenes");
            EditorSceneManager.SaveScene(scene, ScenePath);

            Debug.Log("[TableDuoSceneSetup] 完了。テーブル/席2/OVRCameraRig(+Hands)/NetworkManager/Systems 配置済み。\n" +
                      "- 実機: OVRProjectConfig の Hand Tracking Support を Controllers And Hands 以上にすること\n" +
                      "- ビルド対象にする時は Build Settings へ本シーンを手動追加（Main.unity と排他運用）\n" +
                      "- L0 検証: OVRCameraRig を無効化 / DebugCamera と FakeHandDriver を有効化");
        }

        /// <summary>
        /// FBX をインスタンス化し、Renderer バウンディングの高さが targetHeight になるよう
        /// 一様スケール + 足元 (bounds.min.y) を pos.y に揃える。FBX が無ければ null。
        /// </summary>
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
        }

        private static GameObject CreatePlayerPrefab()
        {
            Directory.CreateDirectory("Assets/TableDuo/Prefabs");
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
            if (existing != null) return existing;

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
    }
}
