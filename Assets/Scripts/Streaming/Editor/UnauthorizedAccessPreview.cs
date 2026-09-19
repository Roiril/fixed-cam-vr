#nullable enable
using System;
using System.IO;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using Object = UnityEngine.Object;

namespace FixedCamVr.Streaming.EditorTools
{
    /// <summary>Standalone cue assets and real Unity render evidence. Never edits Main or show.json.</summary>
    public static class UnauthorizedAccessPreview
    {
        public const string AssetDir = "Assets/Art/Textures/UnauthorizedAccess";
        public const string PrefabPath = "Assets/Prefabs/Effects/UnauthorizedAccess.prefab";
        private const int Width = 1280, Height = 720, Fps = 24;

        [MenuItem("Tools/FixedCamVr/Setup/Unauthorized Access Effect")]
        public static void Build()
        {
            Directory.CreateDirectory(AssetDir);
            Directory.CreateDirectory(Path.GetDirectoryName(PrefabPath)!);
            AssetDatabase.Refresh();
            string texturePath = AssetDir + "/interference.png";
            var importer = (TextureImporter)AssetImporter.GetAtPath(texturePath);
            if (importer == null) throw new InvalidOperationException("Missing generated interference.png");
            importer.textureType = TextureImporterType.Default;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = true;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Trilinear;
            importer.maxTextureSize = 2048;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            var android = importer.GetPlatformTextureSettings("Android");
            android.name = "Android";
            android.overridden = true;
            android.maxTextureSize = 2048;
            android.format = TextureImporterFormat.ASTC_6x6;
            importer.SetPlatformTextureSettings(android);
            importer.SaveAndReimport();
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
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
            var font = MakeFont();
            font.material.renderQueue = 3460;
            EditorUtility.SetDirty(font.material);
            var go = new GameObject("UnauthorizedAccess");
            try
            {
                var effect = go.AddComponent<UnauthorizedAccessEffect>();
                effect.Configure(material, font, texture);
                PrefabUtility.SaveAsPrefabAsset(go, PrefabPath);
            }
            finally { Object.DestroyImmediate(go); }
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssets();
            Debug.Log("[UnauthorizedAccess] Built " + PrefabPath);
        }

        private static TMP_FontAsset MakeFont()
        {
            const string path = AssetDir + "/Error SDF.asset";
            var existing = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
            var charset = new StringBuilder();
            for (char c = ' '; c <= '~'; ++c) charset.Append(c);
            charset.Append("不正アクセスを検出");
            if (existing != null)
            {
                if (!existing.HasCharacters(charset.ToString()))
                    throw new InvalidOperationException("Existing Error SDF lacks required glyphs");
                return existing;
            }
            var source = AssetDatabase.LoadAssetAtPath<Font>("Assets/Art/Fonts/SourceHanSansJP-Normal.otf");
            var font = TMP_FontAsset.CreateFontAsset(source, 80, 8, GlyphRenderMode.SDFAA,
                1024, 1024, AtlasPopulationMode.Dynamic);
            if (font == null || !font.TryAddCharacters(charset.ToString(), out string missing))
                throw new InvalidOperationException("Error font bake failed");
            font.name = "Error SDF";
            font.atlasPopulationMode = AtlasPopulationMode.Static;
            font.isMultiAtlasTexturesEnabled = false;
            AssetDatabase.CreateAsset(font, path);
            foreach (var atlas in font.atlasTextures)
            {
                atlas.name = "Error SDF Atlas";
                AssetDatabase.AddObjectToAsset(atlas, font);
            }
            font.material.name = "Error SDF Material";
            AssetDatabase.AddObjectToAsset(font.material, font);
            return font;
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
                var material = AssetDatabase.LoadAssetAtPath<Material>(AssetDir + "/ErrorFx.mat");
                background = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                background.SetColor("_BaseColor", new Color(0.035f, 0.046f, 0.053f, 1));
                // A neutral calibration image, not a fictional camera feed.
                Quad(screen.transform, "Screen", Vector3.zero, size, background);
                var gridMat = new Material(background);
                gridMat.SetColor("_BaseColor", new Color(0.11f, 0.135f, 0.145f, 1));
                try
                {
                    for (int i = 1; i < 12; i++)
                        Quad(screen.transform, "Calibration column", new Vector3((i / 12f - .5f) * size.x, 0, -.005f), new Vector2(.002f, size.y), gridMat);
                    for (int i = 1; i < 7; i++)
                        Quad(screen.transform, "Calibration row", new Vector3(0, (i / 7f - .5f) * size.y, -.005f), new Vector2(size.x, .002f), gridMat);
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
                    var effectGo = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                    effectGo.transform.SetParent(stage.transform);
                    var effect = effectGo.GetComponent<UnauthorizedAccessEffect>();
                    effect.Play(screen.transform, size);
                    var samples = new[] { 0f, .35f, .8f, 1.6f, 3.2f, 5.9f, 7f };
                    foreach (float sec in samples)
                    {
                        effect.Sample(sec);
                        Shoot(cam, pixels, rt, Path.Combine(dir, $"sample-{sec:0.00}.png"));
                    }
                    effect.Sample(3.2f);
                    Shoot(cam, pixels, rt, Path.Combine(dir, "hero.png"));
                    cam.transform.position += new Vector3(.22f, 0, 0);
                    cam.transform.LookAt(screen.transform);
                    Shoot(cam, pixels, rt, Path.Combine(dir, "parallax.png"));
                    cam.transform.position = new Vector3(0, 1.6f, 0);
                    cam.transform.rotation = Quaternion.identity;
                    cam.backgroundColor = new Color(.35f, .35f, .35f, 1);
                    Shoot(cam, pixels, rt, Path.Combine(dir, "bright-surround.png"));
                    cam.backgroundColor = new Color(0.012f, 0.013f, 0.017f, 1);
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
                    var evidence = new StringBuilder("Actual Unity render; neutral calibration background.\n");
                    evidence.AppendLine($"duration={effect.Duration}; spatialElements={effect.SpatialElementCount}; size={size}; fps={Fps}");
                    foreach (var root in scene.GetRootGameObjects())
                    foreach (var text in root.GetComponentsInChildren<TMP_Text>(true))
                    {
                        text.ForceMeshUpdate(true);
                        evidence.AppendLine($"text={text.text.Replace('\n', '|')}; characters={text.textInfo.characterCount}; bounds={text.textBounds.size}");
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
            foreach (var root in cam.scene.GetRootGameObjects())
            foreach (var text in root.GetComponentsInChildren<TMP_Text>()) text.ForceMeshUpdate();
            cam.Render();
            RenderTexture.active = rt;
            pixels.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
            pixels.Apply(false);
            File.WriteAllBytes(path, pixels.EncodeToPNG());
        }
    }
}
