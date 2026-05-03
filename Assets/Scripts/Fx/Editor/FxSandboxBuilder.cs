#nullable enable
using System.IO;
using FixedCamVr.Fx.Blit;
using FixedCamVr.Fx.Compute;
using FixedCamVr.Fx.Particles;
using FixedCamVr.Fx.Source;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FixedCamVr.Fx.Editor
{
    /// <summary>
    /// FxSandbox.unity をワンクリックで構築するエディタメニュー。
    ///
    /// シーン YAML 手書きを避けるための代替手段。merge 後にユーザーが Editor で
    /// メニュー実行 → 保存することで FxSandbox を生成する。
    /// </summary>
    public static class FxSandboxBuilder
    {
        private const string ScenePath = "Assets/Scenes/FxSandbox.unity";
        private const string PatternShaderPath = "Assets/Art/Shaders/Fx/FxTestPattern.shader";

        [MenuItem("FixedCamVr/Fx/Setup FxSandbox Scene", priority = 200)]
        public static void Setup()
        {
            // 未保存変更があるシーンを破棄する前にユーザーに確認させる
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                Debug.Log("[FxSandboxBuilder] キャンセルされました。");
                return;
            }

            // 既存ファイルがあれば overwrite を確認
            if (File.Exists(ScenePath))
            {
                bool overwrite = EditorUtility.DisplayDialog(
                    "FxSandbox を上書き",
                    $"{ScenePath} は既に存在します。再生成して上書きしますか?",
                    "上書き", "キャンセル");
                if (!overwrite)
                {
                    Debug.Log("[FxSandboxBuilder] キャンセルされました。");
                    return;
                }
            }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            CreateCamera();
            CreateLight();

            var binder = CreateSourceBinder();
            CreatePatternSource(binder);

            var screen = CreateScreenQuad(binder);
            CreateBlitChromatic(binder, screen);
            CreateSobelCompute(binder);
            CreateParticleOverlay(screen);

            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath)!);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();

            Debug.Log($"[FxSandboxBuilder] Built FxSandbox at {ScenePath}");
        }

        // SerializedProperty に値をセットする際の null 安全ヘルパー。
        // SerializeField がリネームされた場合に NRE で落ちるのを防ぐ。
        private static bool TrySetObjectRef(SerializedObject so, string propName, Object value, string ownerForLog)
        {
            var prop = so.FindProperty(propName);
            if (prop == null)
            {
                Debug.LogError($"[FxSandboxBuilder] {ownerForLog}: SerializedProperty '{propName}' が見つかりません。フィールド名が変わっていないか確認してください。");
                return false;
            }
            prop.objectReferenceValue = value;
            return true;
        }

        private static void CreateCamera()
        {
            var go = new GameObject("Main Camera");
            var cam = go.AddComponent<Camera>();
            cam.tag = "MainCamera";
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.transform.position = new Vector3(0, 1.6f, -2.5f);
            go.AddComponent<AudioListener>();
        }

        private static void CreateLight()
        {
            var go = new GameObject("Directional Light");
            var l = go.AddComponent<Light>();
            l.type = LightType.Directional;
            l.intensity = 1f;
            go.transform.rotation = Quaternion.Euler(50, -30, 0);
        }

        private static FxSourceBinder CreateSourceBinder()
        {
            var go = new GameObject("[Fx]_SourceBinder");
            return go.AddComponent<FxSourceBinder>();
        }

        private static void CreatePatternSource(FxSourceBinder binder)
        {
            var go = new GameObject("[Fx]_TestPatternSource");
            var src = go.AddComponent<FxTestPatternSource>();
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(PatternShaderPath);
            if (shader == null)
            {
                Debug.LogWarning($"[FxSandboxBuilder] Pattern shader が見つかりません: {PatternShaderPath}");
            }
            var so = new SerializedObject(src);
            TrySetObjectRef(so, "patternShader", shader!, "TestPatternSource");
            TrySetObjectRef(so, "binderToFeed", binder, "TestPatternSource");
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static GameObject CreateScreenQuad(FxSourceBinder binder)
        {
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "[Fx]_Screen";
            quad.transform.position = new Vector3(0, 1.6f, 0);
            quad.transform.localScale = new Vector3(1.78f, 1.0f, 1.0f);

            var renderer = quad.GetComponent<Renderer>();
            // URP が未導入 / 名前変更時のフォールバック。Sprites/Default は常時存在し、Unlit に近い見た目になる。
            var unlit = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default");
            if (unlit == null)
            {
                Debug.LogError("[FxSandboxBuilder] URP Unlit shader が見つかりません。URP package を確認してください。");
            }
            var mat = new Material(unlit != null ? unlit : Shader.Find("Hidden/InternalErrorShader"));
            mat.name = "FxScreenMat";
            renderer.sharedMaterial = mat;

            var feed = quad.AddComponent<FxScreenTextureFeeder>();
            var so = new SerializedObject(feed);
            TrySetObjectRef(so, "binder", binder, "ScreenTextureFeeder");
            TrySetObjectRef(so, "targetRenderer", renderer, "ScreenTextureFeeder");
            so.ApplyModifiedPropertiesWithoutUndo();
            return quad;
        }

        private static void CreateBlitChromatic(FxSourceBinder binder, GameObject screen)
        {
            const string shaderPath = "Assets/Art/Shaders/Fx/FxChromaticAberration.shader";
            var go = new GameObject("[Fx]_BlitChromatic (disabled)");
            var blit = go.AddComponent<FxBlitChromaticAberration>();
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(shaderPath);
            if (shader == null)
            {
                Debug.LogWarning($"[FxSandboxBuilder] Shader が見つかりません: {shaderPath}");
            }
            var so = new SerializedObject(blit);
            TrySetObjectRef(so, "blitShader", shader!, "BlitChromatic");
            TrySetObjectRef(so, "binder", binder, "BlitChromatic");
            TrySetObjectRef(so, "targetRenderer", screen.GetComponent<Renderer>(), "BlitChromatic");
            so.ApplyModifiedPropertiesWithoutUndo();
            go.SetActive(false);
        }

        private static void CreateSobelCompute(FxSourceBinder binder)
        {
            const string computePath = "Assets/Art/Shaders/Fx/FxSobel.compute";
            var go = new GameObject("[Fx]_SobelCompute (disabled)");
            var sobel = go.AddComponent<SobelEdgeRunner>();
            var compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(computePath);
            if (compute == null)
            {
                Debug.LogWarning($"[FxSandboxBuilder] ComputeShader が見つかりません: {computePath}");
            }
            var so = new SerializedObject(sobel);
            TrySetObjectRef(so, "compute", compute!, "SobelCompute");
            TrySetObjectRef(so, "binder", binder, "SobelCompute");
            so.ApplyModifiedPropertiesWithoutUndo();
            go.SetActive(false);
        }

        private static void CreateParticleOverlay(GameObject screen)
        {
            var go = new GameObject("[Fx]_DustParticles (disabled)");
            go.transform.SetParent(screen.transform, worldPositionStays: false);
            go.transform.localPosition = new Vector3(0, 0, -0.05f);
            go.AddComponent<DustOverlayBuilder>();
            go.SetActive(false);
        }
    }
}
