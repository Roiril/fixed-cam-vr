#nullable enable
using System.IO;
using TableDuoVr.Hands;
using TableDuoVr.Hands.Playback;
using TableDuoVr.Net;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEditor.SceneManagement;
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

        [MenuItem("Tools/FixedCamVr/Setup/Setup TableDuo Scene", priority = 60)]
        public static void Setup()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

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

            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "Floor";
            floor.transform.SetParent(root.transform, false);

            var table = GameObject.CreatePrimitive(PrimitiveType.Cube);
            table.name = "Table";
            table.transform.SetParent(root.transform, false);
            table.transform.localPosition = new Vector3(0f, 0.35f, 0f);
            table.transform.localScale = new Vector3(1.2f, 0.7f, 0.8f); // 天板高 0.7m

            var seats = new GameObject("Seats");
            seats.transform.SetParent(root.transform, false);
            CreateSeat(seats.transform, 0, new Vector3(0f, 0f, -0.85f), 0f);    // フルアバター席
            CreateSeat(seats.transform, 1, new Vector3(0f, 0f, 0.85f), 180f);   // 手だけアバター席

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
            }

            // --- L0 用デバッグカメラ（リグを無効化して使う。既定 OFF）---
            var debugCamGo = new GameObject("DebugCamera");
            var debugCam = debugCamGo.AddComponent<Camera>();
            debugCam.nearClipPlane = 0.05f;
            debugCamGo.transform.SetPositionAndRotation(new Vector3(0f, 1.4f, -1.6f), Quaternion.Euler(20f, 0f, 0f));
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

            systems.AddComponent<HandPoseRecorder>();

            var fake = systems.AddComponent<FakeHandDriver>();
            fake.enabled = false; // L0 検証時に手動で ON

            systems.AddComponent<ConnectionManager>();

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

        private static void CreateSeat(Transform parent, int index, Vector3 pos, float yaw)
        {
            var seat = new GameObject($"Seat{index}");
            seat.transform.SetParent(parent, false);
            seat.transform.localPosition = pos;
            seat.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
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
            var go = new GameObject(isLeft ? "OVRHandLeft" : "OVRHandRight");
            go.transform.SetParent(anchor, false);

            hand = go.AddComponent<OVRHand>();
            var handSo = new SerializedObject(hand);
            SetEnum(handSo, "HandType", isLeft ? 0 : 1); // OVRPlugin.Hand: HandLeft=0, HandRight=1
            handSo.ApplyModifiedPropertiesWithoutUndo();

            skeleton = go.AddComponent<OVRSkeleton>();
            var skelSo = new SerializedObject(skeleton);
            SetEnum(skelSo, "_skeletonType", isLeft ? 0 : 1); // SkeletonType: HandLeft=0, HandRight=1
            skelSo.ApplyModifiedPropertiesWithoutUndo();
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
            var go = GameObject.Find(name);
            if (go != null) Object.DestroyImmediate(go);
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
