#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    ///   2. マテリアルを <c>FixedCamVr/ShowActor</c>（シーンライトに依存しない）へ差し替える。
    ///      **元モデルがアルベドを持っていればそれを引き継ぐ**（実物をスキャンした人形の色）
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
        // テクスチャを持たない人形（Mixamo のマネキン等）が共有する 1 枚。従来からあるもの。
        private const string SharedMaterialPath = MaterialDir + "/ShowActor.mat";
        private const string ShaderName = "FixedCamVr/ShowActor";
        private const string CgLayerName = "ShowCg";

        private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        private static readonly int BumpMapId = Shader.PropertyToID("_BumpMap");

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

            int layer = LayerMask.NameToLayer(CgLayerName);
            if (layer < 0)
                Debug.LogWarning($"[ShowActor] レイヤ '{CgLayerName}' が未定義。プレハブのレイヤ設定はスキップします" +
                                 "（Project Settings > Tags and Layers に追加してから作り直すと確実）。");

            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(model);
            PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            instance.transform.position = Vector3.zero;
            instance.transform.rotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one;

            // ⚠ アルベドの収集は**マテリアルを差し替える前**に行う（差し替えた後では元の色が失われる）。
            string modelName = Path.GetFileNameWithoutExtension(modelPath);
            Material mat = ResolveMaterial(instance, modelName);

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
            // 同じモデルで作り直したら**上書き**する（Remy 1.prefab のような重複を作らない）。
            string outPath = $"{OutputDir}/{modelName}.prefab";
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

        /// <summary>
        /// 人形へ差すマテリアルを決める。**元モデルがアルベドを持っていれば、その 1 枚を焼いた
        /// 専用マテリアル**（<c>ShowActor_&lt;モデル名&gt;.mat</c>）を作る。実物をスキャンした人形は
        /// 色が命なので、ここで拾わないと灰色のマネキンとして映る。
        ///
        /// テクスチャを持たないモデルは従来どおり共有の <c>ShowActor.mat</c> を使う（見た目は不変）。
        ///
        /// ⚠ 呼ぶのは**マテリアルを差し替える前**。差し替えた後では元の色が消えている。
        /// </summary>
        private static Material ResolveMaterial(GameObject instance, string modelName)
        {
            var textures = new List<Texture>();
            foreach (Renderer r in instance.GetComponentsInChildren<Renderer>(true))
            {
                foreach (Material m in r.sharedMaterials)
                {
                    if (m == null) continue;
                    Texture? tex = m.HasProperty(BaseMapId) ? m.GetTexture(BaseMapId) : null;
                    if (tex == null && m.HasProperty(MainTexId)) tex = m.GetTexture(MainTexId);
                    if (tex != null && !textures.Contains(tex)) textures.Add(tex);
                }
            }

            if (textures.Count == 0) return LoadOrCreateMaterial(SharedMaterialPath, "ShowActor", null);

            if (textures.Count > 1)
            {
                // 人形は 1 マテリアルへ潰す。これは影の付け方（ShowCgLayer.AttachShadowMaterial が
                // sharedMaterials の末尾へ 1 枚足す）と対で、サブメッシュが複数あると
                // **影が最後のサブメッシュにしか出ない**。書き出し前に 1 枚へまとめるのが正しい。
                Debug.LogWarning($"[ShowActor] アルベドが {textures.Count} 枚あります" +
                                 $"（{string.Join(", ", textures.Select(t => t.name))}）。" +
                                 "先頭の 1 枚だけを使います。全身に色と影を出すには、書き出す前に" +
                                 "テクスチャを 1 枚へまとめて 1 メッシュ 1 マテリアルにしてください。");
            }

            return LoadOrCreateMaterial($"{MaterialDir}/ShowActor_{modelName}.mat",
                                        $"ShowActor_{modelName}", textures[0]);
        }

        // 既存があれば拾って albedo だけ更新し、無ければ作る（作り直しで .mat が増殖しない）。
        private static Material LoadOrCreateMaterial(string path, string name, Texture? albedo)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                Shader? shader = Shader.Find(ShaderName);
                if (shader == null)
                {
                    Debug.LogWarning($"[ShowActor] シェーダ '{ShaderName}' が見つかりません。URP/Unlit で代用します。");
                    shader = Shader.Find("Universal Render Pipeline/Unlit");
                }
                Directory.CreateDirectory(MaterialDir);
                mat = new Material(shader) { name = name };
                AssetDatabase.CreateAsset(mat, path);
            }

            if (albedo != null && mat.HasProperty(BaseMapId))
            {
                mat.SetTexture(BaseMapId, albedo);
                // テクスチャの色をそのまま出す。既定の _BaseColor（マネキンの肌色）が乗ると濁る。
                mat.SetColor("_BaseColor", Color.white);

                // 同じ場所の `<...>_normal` を法線として拾う（布の襞をライトに反応させる）。
                // 無ければ既定のフラットのまま＝従来と同じ絵。
                Texture? bump = FindSibling(albedo, "_albedo", "_normal");
                if (bump != null && mat.HasProperty(BumpMapId))
                {
                    mat.SetTexture(BumpMapId, bump);
                    // ⚠ 外側の `path`（マテリアルの保存先）と名前が衝突するので別名にする。
                    //    ここを `path` にすると CS0136 でエディタアセンブリ全体がコンパイルできず、
                    //    Unity が**古い DLL のまま動き続ける**（メニューを叩いても前の版が走る）。
                    var bumpPath = AssetDatabase.GetAssetPath(bump);
                    if (AssetImporter.GetAtPath(bumpPath) is TextureImporter ti
                        && ti.textureType != TextureImporterType.NormalMap)
                    {
                        ti.textureType = TextureImporterType.NormalMap;
                        ti.SaveAndReimport();
                    }
                }
                EditorUtility.SetDirty(mat);
            }
            return mat;
        }

        /// <summary>同じフォルダにある「名前の一部を差し替えた」テクスチャを探す。</summary>
        private static Texture? FindSibling(Texture albedo, string from, string to)
        {
            string path = AssetDatabase.GetAssetPath(albedo);
            if (string.IsNullOrEmpty(path)) return null;
            string dir = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "";
            string name = Path.GetFileNameWithoutExtension(path);
            string want = name.Contains(from) ? name.Replace(from, to) : name + to;
            foreach (string guid in AssetDatabase.FindAssets($"{want} t:Texture", new[] { dir }))
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(p) == want)
                    return AssetDatabase.LoadAssetAtPath<Texture>(p);
            }
            return null;
        }

        private static void SetLayerRecursive(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            for (int i = 0; i < t.childCount; i++) SetLayerRecursive(t.GetChild(i), layer);
        }
    }
}
