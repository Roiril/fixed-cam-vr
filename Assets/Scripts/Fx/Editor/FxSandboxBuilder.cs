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

            Debug.Log($"[FxSandboxBuilder] Built FxSandbox at {ScenePath}");
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
            var so = new SerializedObject(src);
            so.FindProperty("patternShader").objectReferenceValue = shader;
            so.FindProperty("binderToFeed").objectReferenceValue = binder;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static GameObject CreateScreenQuad(FxSourceBinder binder)
        {
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "[Fx]_Screen";
            quad.transform.position = new Vector3(0, 1.6f, 0);
            quad.transform.localScale = new Vector3(1.78f, 1.0f, 1.0f);

            var renderer = quad.GetComponent<Renderer>();
            var mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            mat.name = "FxScreenMat";
            renderer.sharedMaterial = mat;

            var feed = quad.AddComponent<FxScreenTextureFeeder>();
            var so = new SerializedObject(feed);
            so.FindProperty("binder").objectReferenceValue = binder;
            so.FindProperty("targetRenderer").objectReferenceValue = renderer;
            so.ApplyModifiedPropertiesWithoutUndo();
            return quad;
        }

        private static void CreateBlitChromatic(FxSourceBinder binder, GameObject screen)
        {
            var go = new GameObject("[Fx]_BlitChromatic (disabled)");
            var blit = go.AddComponent<FxBlitChromaticAberration>();
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(
                "Assets/Art/Shaders/Fx/FxChromaticAberration.shader");
            var so = new SerializedObject(blit);
            so.FindProperty("blitShader").objectReferenceValue = shader;
            so.FindProperty("binder").objectReferenceValue = binder;
            so.FindProperty("targetRenderer").objectReferenceValue = screen.GetComponent<Renderer>();
            so.ApplyModifiedPropertiesWithoutUndo();
            go.SetActive(false);
        }

        private static void CreateSobelCompute(FxSourceBinder binder)
        {
            var go = new GameObject("[Fx]_SobelCompute (disabled)");
            var sobel = go.AddComponent<SobelEdgeRunner>();
            var compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Assets/Art/Shaders/Fx/FxSobel.compute");
            var so = new SerializedObject(sobel);
            so.FindProperty("compute").objectReferenceValue = compute;
            so.FindProperty("binder").objectReferenceValue = binder;
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
