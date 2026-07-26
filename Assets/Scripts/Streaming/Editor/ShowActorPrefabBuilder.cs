#nullable enable
using System.IO;
using FixedCamVr.Streaming.Cg;
using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    /// <summary>
    /// **任意の humanoid モデル（Mixamo 等）から CG 人形のプレハブを作る**メニュー。
    ///
    /// 演出（show.json の <c>actors[]</c>）が指すのは Resources 配下のプレハブ名なので、
    /// ここで作った <c>Assets/Resources/ShowActors/&lt;名前&gt;.prefab</c> をそのまま指せばよい。
    /// やることは 4 つだけ:
    ///   1. モデルを配置して <see cref="ShowActorRig"/>（腕をハンドトラッキングで動かす）を付ける
    ///   2. マテリアルを <c>FixedCamVr/ShowActor</c>（シーンライトに依存しない）へ差し替える
    ///   3. レイヤを ShowCg に統一する（仮想カメラだけが描く）
    ///   4. 実寸の身長をログに出す（show.json の heightM を書くときの目安）
    ///
    /// 人形を差し替えたくなったら、別の FBX を選んで同じメニューを実行するだけでよい（コード変更なし）。
    /// 設計の正本: <c>.claude/plans/2026-07-27_cg-actor-hand-tracking.md</c>。
    /// </summary>
    public static class ShowActorPrefabBuilder
    {
        private const string DefaultModelPath = "Assets/ThirdParty/Mixamo/Remy.fbx";
        private const string OutputDir = "Assets/Resources/ShowActors";
        private const string MaterialDir = "Assets/Art/Materials/Cg";
        private const string MaterialPath = MaterialDir + "/ShowActor.mat";
        private const string ShaderName = "FixedCamVr/ShowActor";
        private const string CgLayerName = "ShowCg";

        [MenuItem("Tools/FixedCamVr/Setup/Build Show Actor Prefab", priority = 52)]
        public static void Build()
        {
            string modelPath = ResolveModelPath();
            if (string.IsNullOrEmpty(modelPath))
            {
                Debug.LogError("[ShowActor] humanoid の FBX を Project ウィンドウで選んでから実行するか、" +
                               $"既定の {DefaultModelPath} を用意してください。");
                return;
            }

            var importer = AssetImporter.GetAtPath(modelPath) as ModelImporter;
            if (importer == null)
            {
                Debug.LogError($"[ShowActor] {modelPath} はモデルアセットではありません。");
                return;
            }
            // Rig は Humanoid でも Generic でもよい（ShowActorRig は Generic なら手のボーン名から
            // 親を 2 つ遡って肘・肩を取る）。**共有 ThirdParty アセットの取り込み設定は変えない**
            // — Remy は TableDuo のフルボディアバターと同じ実体で、Humanoid へ変えると向こうが壊れる。
            if (importer.animationType == ModelImporterAnimationType.None)
            {
                Debug.LogError($"[ShowActor] {modelPath} は Rig 無しです（Animation Type = None）。" +
                               "腕を動かせないので、リグつきのモデルを選んでください。");
                return;
            }

            var model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
            if (model == null)
            {
                Debug.LogError($"[ShowActor] {modelPath} を読み込めません。");
                return;
            }

            Material mat = LoadOrCreateMaterial();
            int layer = LayerMask.NameToLayer(CgLayerName);
            if (layer < 0)
                Debug.LogWarning($"[ShowActor] レイヤ '{CgLayerName}' が未定義。プレハブのレイヤ設定はスキップします" +
                                 "（Project Settings > Tags and Layers に追加してから作り直すと確実）。");

            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(model);
            PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            instance.transform.position = Vector3.zero;
            instance.transform.rotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one;

            foreach (Renderer r in instance.GetComponentsInChildren<Renderer>(true))
            {
                var mats = new Material[Mathf.Max(1, r.sharedMaterials.Length)];
                for (int i = 0; i < mats.Length; i++) mats[i] = mat;
                r.sharedMaterials = mats;
                // 人形は監視カメラの絵に溶かすのが目的。影の投げ合いは実シーンに存在しないので切る。
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                // 腕を IK で動かすと bind ポーズの bounds から外れる。仮想カメラの画角ぎりぎりで
                // 人形が丸ごと消える（カリング）事故を防ぐため、常に bounds を再計算させる。
                if (r is SkinnedMeshRenderer smr) smr.updateWhenOffscreen = true;
            }

            if (layer >= 0) SetLayerRecursive(instance.transform, layer);

            var rig = instance.GetComponent<ShowActorRig>();
            if (rig == null) rig = instance.AddComponent<ShowActorRig>();
            rig.Prepare();
            if (!rig.HasRig)
                Debug.LogWarning($"[ShowActor] {modelPath} から腕のボーン（肩→肘→手首）を解決できませんでした。" +
                                 "人形は立ちますが腕はハンドトラッキングで動きません" +
                                 "（手のボーン名に left/right と hand が入っているリグなら自動で見つかります）。");

            // 身長はリグと同じ計測（ボーン最高点）を使う。ここと実行時で違う値を出さない。
            float height = rig.MeasuredHeightM;

            Directory.CreateDirectory(OutputDir);
            string name = Path.GetFileNameWithoutExtension(modelPath);
            // 同じモデルで作り直したら**上書き**する（Remy 1.prefab のような重複を作らない）。
            string outPath = $"{OutputDir}/{name}.prefab";
            GameObject saved = PrefabUtility.SaveAsPrefabAsset(instance, outPath);
            Object.DestroyImmediate(instance);
            AssetDatabase.SaveAssets();

            string resourcePath = $"ShowActors/{Path.GetFileNameWithoutExtension(outPath)}";
            Selection.activeObject = saved;
            EditorGUIUtility.PingObject(saved);
            Debug.Log($"[ShowActor] 作成: {outPath}（実寸 {height:F2}m）\n" +
                      $"show.json の actors[] にはこう書く:\n" +
                      "{ \"id\": \"doll\", \"name\": \"人形\", \"prefab\": \"" + resourcePath + "\", " +
                      $"\"heightM\": {height:F2}, \"fixedX\": 0, \"fixedZ\": 0, \"fixedYawDeg\": 0 }}\n" +
                      "（heightM は演出上の見せたい身長でよい。実寸との比でプレハブが自動縮尺される）");
        }

        // 選択中のモデル → 既定モデル、の順に解決する。
        private static string ResolveModelPath()
        {
            foreach (Object obj in Selection.objects)
            {
                string path = AssetDatabase.GetAssetPath(obj);
                if (string.IsNullOrEmpty(path)) continue;
                if (AssetImporter.GetAtPath(path) is ModelImporter) return path;
            }
            return File.Exists(DefaultModelPath) ? DefaultModelPath : "";
        }

        private static Material LoadOrCreateMaterial()
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (existing != null) return existing;

            Shader? shader = Shader.Find(ShaderName);
            if (shader == null)
            {
                Debug.LogWarning($"[ShowActor] シェーダ '{ShaderName}' が見つかりません。URP/Unlit で代用します。");
                shader = Shader.Find("Universal Render Pipeline/Unlit");
            }
            Directory.CreateDirectory(MaterialDir);
            var mat = new Material(shader) { name = "ShowActor" };
            AssetDatabase.CreateAsset(mat, MaterialPath);
            return mat;
        }

        private static void SetLayerRecursive(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            for (int i = 0; i < t.childCount; i++) SetLayerRecursive(t.GetChild(i), layer);
        }
    }
}
