#nullable enable
using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FixedCamVr.Streaming.EditorTools
{
    /// <summary>Standalone cue assets and real Unity render evidence. Never edits Main or show.json.</summary>
    public static class UnauthorizedAccessPreview
    {
        public const string AssetDir = "Assets/Art/Textures/UnauthorizedAccess";
        public const string PrefabPath = "Assets/Prefabs/Effects/UnauthorizedAccess.prefab";
        private const int Width = 1280, Height = 720, Fps = 30;

        [MenuItem("Tools/FixedCamVr/Setup/Unauthorized Access Effect")]
        public static void Build()
        {
            Directory.CreateDirectory(AssetDir);
            Directory.CreateDirectory(Path.GetDirectoryName(PrefabPath)!);
            AssetDatabase.Refresh();
            var texture = ImportTexture("wordmark-v3.png");
            var subtitle = ImportTexture("subtitle-v3.png");
            var symbol = ImportTexture("triangle-v3.png");
            var interference = ImportTexture("signal-tear-v3.png");
            var shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Art/Shaders/UnauthorizedAccessFx.shader");
            if (shader == null || ShaderUtil.ShaderHasError(shader))
                throw new InvalidOperationException("UnauthorizedAccessFx shader missing or invalid");
            string materialPath = AssetDir + "/ErrorFx.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
            {
                material = new Material(shader) { name = "ErrorFx" };
                AssetDatabase.CreateAsset(material, materialPath);
            }
            material.shader = shader;
            material.SetColor("_Color", Color.white);
            material.SetFloat("_Mode", 0);
            material.SetFloat("_Strength", 1);
            var go = new GameObject("UnauthorizedAccess");
            try
            {
                var effect = go.AddComponent<UnauthorizedAccessEffect>();
                effect.Configure(material, texture, subtitle, symbol, interference);
                PrefabUtility.SaveAsPrefabAsset(go, PrefabPath);
            }
            finally { Object.DestroyImmediate(go); }
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssets();
            Debug.Log("[UnauthorizedAccess] Built " + PrefabPath);
        }

        private static Texture2D ImportTexture(string name)
        {
            string path = AssetDir + "/" + name;
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            if (importer == null) throw new InvalidOperationException("Missing generated texture: " + path);
            importer.textureType = TextureImporterType.Default;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = true;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.mipmapEnabled = true;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Trilinear;
            importer.maxTextureSize = 2048;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            var android = importer.GetPlatformTextureSettings("Android");
            android.name = "Android";
            android.overridden = true;
            android.maxTextureSize = 2048;
            android.format = TextureImporterFormat.ASTC_4x4;
            importer.SetPlatformTextureSettings(android);
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        [MenuItem("Tools/FixedCamVr/Preview/Unauthorized Access (frames)")]
        public static void Run()
        {
            Build();
            string dir = Path.GetFullPath(EditorCliArgs.Get("out") ??
                "Logs/unauthorized-access/" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(dir);
            bool sequence = EditorCliArgs.Get("sequence") == "1";
            var scene = EditorSceneManager.NewPreviewScene();
            var stage = new GameObject("Error preview stage");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(stage, scene);
            var oldRt = RenderTexture.active;
            var rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32);
            var pixels = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
            Material? background = null;
            try
            {
                var cameraGo = Child(stage.transform, "Camera");
                var cam = cameraGo.AddComponent<Camera>();
                cam.enabled = false;
                cam.scene = scene;
                cam.transform.position = new Vector3(0, 1.6f, 0);
                cam.fieldOfView = 58;
                cam.aspect = (float)Width / Height;
                cam.nearClipPlane = 0.05f;
                cam.farClipPlane = 30;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.012f, 0.013f, 0.017f, 1);
                cam.allowHDR = false;
                cam.targetTexture = rt;
                var screen = Child(stage.transform, "Screen anchor");
                screen.transform.position = new Vector3(0, 1.6f, 3);
                var size = new Vector2(2.7f, 1.51875f);
                background = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                background.SetColor("_BaseColor", new Color(0.012f, 0.013f, 0.017f, 1));
                // A neutral calibration image, not a fictional camera feed.
                Quad(screen.transform, "Screen", Vector3.zero, size, background);
                var gridMat = new Material(background);
                gridMat.SetColor("_BaseColor", new Color(0.11f, 0.135f, 0.145f, 1));
                try
                {
                    var grid = Child(screen.transform, "Calibration grid");
                    for (int i = 1; i < 12; i++)
                        Quad(grid.transform, "Calibration column", new Vector3((i / 12f - .5f) * size.x, 0, -.005f), new Vector2(.002f, size.y), gridMat);
                    for (int i = 1; i < 7; i++)
                        Quad(grid.transform, "Calibration row", new Vector3(0, (i / 7f - .5f) * size.y, -.005f), new Vector2(size.x, .002f), gridMat);
                    grid.SetActive(false);
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
                    var effectGo = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                    effectGo.transform.SetParent(stage.transform);
                    var effect = effectGo.GetComponent<UnauthorizedAccessEffect>();
                    effect.Play(screen.transform, size);
                    var samples = new[] { 0f, .08f, .2f, .35f, .57f, .8f, 1.2f, 1.6f, 2.12f, 3.2f, 3.71f, 4.95f, 5.6f, 5.9f, 6.5f, 7f };
                    foreach (float sec in samples)
                    {
                        effect.Sample(sec);
                        Shoot(cam, pixels, rt, Path.Combine(dir, $"sample-{sec:0.00}.png"));
                    }
                    effect.Sample(1.2f);
                    Shoot(cam, pixels, rt, Path.Combine(dir, "hero.png"));
                    cam.transform.position += new Vector3(.22f, 0, 0);
                    cam.transform.LookAt(screen.transform);
                    Shoot(cam, pixels, rt, Path.Combine(dir, "parallax.png"));
                    cam.transform.position = new Vector3(0, 1.6f, 0);
                    cam.transform.rotation = Quaternion.identity;
                    cam.backgroundColor = new Color(.35f, .35f, .35f, 1);
                    Shoot(cam, pixels, rt, Path.Combine(dir, "bright-surround.png"));
                    cam.backgroundColor = new Color(0.012f, 0.013f, 0.017f, 1);
                    grid.SetActive(true);
                    background.SetColor("_BaseColor", new Color(.15f, .18f, .20f, 1));
                    Shoot(cam, pixels, rt, Path.Combine(dir, "overlay-check.png"));
                    grid.SetActive(false);
                    background.SetColor("_BaseColor", new Color(.012f, .013f, .017f, 1));
                    if (sequence)
                    {
                        string frames = Path.Combine(dir, "frames");
                        Directory.CreateDirectory(frames);
                        for (int i = 0; i <= Mathf.CeilToInt(effect.Duration * Fps); i++)
                        {
                            effect.Sample((float)i / Fps);
                            Shoot(cam, pixels, rt, Path.Combine(frames, $"{i:0000}.png"));
                        }
                    }
                    effect.Sample(3.2f);
                    var evidence = new StringBuilder("Actual Unity render; background grid only in overlay-check.png.\n");
                    evidence.AppendLine($"duration={effect.Duration}; spatialElements={effect.SpatialElementCount}; size={size}; fps={Fps}");
                    foreach (var root in scene.GetRootGameObjects())
                    foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
                    {
                        if (filter.sharedMesh != null)
                            evidence.AppendLine($"mesh={filter.name}; vertices={filter.sharedMesh.vertexCount}; bounds={filter.GetComponent<Renderer>().bounds}");
                    }
                    File.WriteAllText(Path.Combine(dir, "render-evidence.txt"), evidence.ToString(), new UTF8Encoding(false));
                    effect.Stop();
                    Shoot(cam, pixels, rt, Path.Combine(dir, "stopped.png"));
                }
                finally { Object.DestroyImmediate(gridMat); }
                Debug.Log("[UnauthorizedAccess] Rendered " + dir);
            }
            finally
            {
                RenderTexture.active = oldRt;
                // Release the effect while its screen anchor is still alive.
                // Destroying the whole stage first can destroy the same screen root twice.
                foreach (var effect in stage.GetComponentsInChildren<UnauthorizedAccessEffect>(true))
                    Object.DestroyImmediate(effect);
                Object.DestroyImmediate(stage);
                EditorSceneManager.ClosePreviewScene(scene);
                if (background != null) Object.DestroyImmediate(background);
                rt.Release();
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(pixels);
            }
        }

        private static GameObject Child(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go;
        }

        private static void Quad(Transform parent, string name, Vector3 position, Vector2 size, Material material)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.transform.localScale = new Vector3(size.x, size.y, 1);
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.GetComponent<Renderer>().sharedMaterial = material;
        }

        private static void Shoot(Camera cam, Texture2D pixels, RenderTexture rt, string path)
        {
            cam.Render();
            RenderTexture.active = rt;
            pixels.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
            pixels.Apply(false);
            File.WriteAllBytes(path, pixels.EncodeToPNG());
        }
    }
}
