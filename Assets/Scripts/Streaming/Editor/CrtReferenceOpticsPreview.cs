#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    public static partial class CrtReferencePreview
    {
        private const string OpticsBaselineHash = "079ab50afedfc7bec271c2e2cb01a2071f86908d13baa8fdf997395b0ae27f4e";
        [Serializable] private sealed class OpticsManifest
        {
            public string utc = "", unity = "", shaderHash = "", helperHash = "", previewHash = "", showHash = "", runtimeProfileHash = "";
            public string baseline = "", baselineHash = "", baselineManifestHash = "";
            public string render = "ScreenComposite single pass; CRT_ORDER_DIAGNOSTIC off; 1280x1030 ARGB32 sRGB depth24; HDR=false MSAA=false; camera/mesh identical to CrtReferencePreview.Run";
            public bool baselinePixelMatch, baselineHashMatch;
            public int baselineMaxChannelDifference, baselineDifferingPixels;
            public List<OpticsMap> maps = new List<OpticsMap>();
            public List<OpticsCapture> captures = new List<OpticsCapture>();
        }
        [Serializable] private sealed class OpticsMap
        {
            public string path = "", sha256 = "", importer = "";
        }
        [Serializable] private sealed class OpticsCapture
        {
            public string plate = "", file = "", sha256 = "", source = "", sourceHash = "", invariantHash = "", parameters = "";
            public float spreadMultiplier, convergenceMultiplier;
            public Vector4 spread, convergence;
            public int width, height, blackPixels, pinkPixels;
            public bool diagnosticKeyword, currentHashMatches;
        }

        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview CRT Optical Matrix", priority = 261)]
        public static void RunOpticsMatrix()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string output = EditorCliArgs.Get("out") ?? Path.Combine(Output, "processing-optics-01");
            if (Directory.Exists(output) && Directory.GetFileSystemEntries(output).Length > 0)
                throw new IOException("Optical matrix output must be empty: " + output);
            if (!File.ReadAllText(Path.Combine(root, ".claude/canon/ROUNDS.md")).Contains("加工感: ぼけ幅とRGB位置差"))
                throw new InvalidOperationException("Missing pre-render optical-matrix prediction");
            string baselineFile = Path.Combine(Output, "round-28/02.png");
            string baselineManifestFile = Path.Combine(Output, "round-28/manifest.json");
            if (!File.Exists(baselineFile) || Hash(baselineFile) != OpticsBaselineHash)
                throw new InvalidDataException("Round 28 baseline missing or hash changed");
            var baseline = JsonUtility.FromJson<Manifest>(File.ReadAllText(baselineManifestFile));
            var cfg = JsonUtility.FromJson<Config>(File.ReadAllText(Path.Combine(root, "tools/web-compositor/show.json")));
            if (baseline == null || cfg == null || JsonUtility.ToJson(baseline.post) != JsonUtility.ToJson(cfg.post))
                throw new InvalidDataException("Post parameters differ from round 28");
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Art/Shaders/Streaming/ScreenComposite.shader");
            if (shader == null || !shader.isSupported) throw new InvalidOperationException("Screen shader unsupported");
            var manifest = new OpticsManifest { utc = DateTime.UtcNow.ToString("o"), unity = Application.unityVersion,
                shaderHash = Hash(Path.Combine(root, "Assets/Art/Shaders/Streaming/ScreenComposite.shader")),
                helperHash = Hash(Path.Combine(root, "Assets/Scripts/Streaming/Editor/CrtReferenceOpticsPreview.cs")),
                previewHash = Hash(Path.Combine(root, "Assets/Scripts/Streaming/Editor/CrtReferencePreview.cs")),
                showHash = Hash(Path.Combine(root, "tools/web-compositor/show.json")),
                runtimeProfileHash = Hash(Path.Combine(root, "Assets/Scripts/Streaming/CrtReferenceScreen.cs")),
                baseline = baselineFile, baselineHash = Hash(baselineFile), baselineManifestHash = Hash(baselineManifestFile) };
            if (manifest.runtimeProfileHash != baseline.runtimeProfileHash)
                throw new InvalidDataException("Runtime optical profile changed since round 28");
            string[] mapNames = { "Frame.png", "CaseColor.jpg", "Reflectance.png", "Glass.png", "Normal.png", "Grade.png" };
            string[] mapHashes = { baseline.frameHash, baseline.caseColorHash, baseline.reflectanceHash, baseline.glassHash, baseline.normalHash, baseline.gradeHash };
            for (int i = 0; i < mapNames.Length; i++)
            {
                string path = "Assets/Resources/CrtReference/" + mapNames[i];
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                bool color = i < 2;
                if (Hash(path) != mapHashes[i] || importer == null || importer.sRGBTexture != color
                    || importer.npotScale != TextureImporterNPOTScale.None || !importer.mipmapEnabled
                    || importer.textureCompression != TextureImporterCompression.Uncompressed)
                    throw new InvalidDataException("Reference map/importer differs from baseline: " + path);
                manifest.maps.Add(new OpticsMap { path = path, sha256 = Hash(path), importer = EditorJsonUtility.ToJson(importer) });
            }
            Directory.CreateDirectory(output);
            var objects = new List<UnityEngine.Object>();
            RenderTexture old = RenderTexture.active;
            bool oldAsync = ShaderUtil.allowAsyncCompilation;
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                var profile = new Material(shader) { hideFlags = HideFlags.HideAndDontSave }; objects.Add(profile);
                if (!CrtReferenceScreen.Apply(profile)) throw new InvalidOperationException("Missing reference maps");
                profile.DisableKeyword("CRT_ORDER_DIAGNOSTIC");
                var screen = new GameObject("OpticalMatrixScreen") { hideFlags = HideFlags.HideAndDontSave, layer = 1 }; objects.Add(screen);
                screen.transform.position = new Vector3(1000, 0, 0);
                screen.transform.localScale = new Vector3(CrtReferenceScreen.CaseAspect, 1, 1);
                var mesh = CrtScreenMesh.BuildMesh(48, 0.055f); objects.Add(mesh);
                screen.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = screen.AddComponent<MeshRenderer>();
                var go = new GameObject("OpticalMatrixCapture") { hideFlags = HideFlags.HideAndDontSave }; objects.Add(go);
                var camera = go.AddComponent<Camera>(); camera.enabled = false;
                camera.transform.SetPositionAndRotation(new Vector3(1000, 0, -2), Quaternion.identity);
                camera.orthographic = true; camera.orthographicSize = 0.5f;
                camera.nearClipPlane = 0.1f; camera.farClipPlane = 4; camera.cullingMask = 1 << 1;
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
                camera.allowHDR = false; camera.allowMSAA = false;
                var target = new RenderTexture(1280, 1030, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                if (!target.Create()) throw new InvalidOperationException("Cannot create optical-matrix RT");
                objects.Add(target); camera.targetTexture = target; camera.aspect = CrtReferenceScreen.CaseAspect;
                string[] names = { "plate_A_20260924_172209_b8e1f375.jpg", "plate_B_20260924_172210_4abb5bae.jpg", "plate_C_20260924_172211_a1c7c02f.jpg" };
                foreach (string name in names)
                {
                    string plate = name.Substring(6, 1);
                    string source = Path.Combine(root, "tools/web-compositor/captures", name);
                    Capture expected = baseline.captures.Find(c => c.file == plate + "_front.png")
                        ?? throw new InvalidDataException("Missing plate baseline: " + plate);
                    if (!File.Exists(source) || Hash(source) != expected.sourceHash)
                        throw new InvalidDataException("Actual plate missing or changed: " + source);
                    var tex = new Texture2D(2, 2, TextureFormat.RGB24, true, false)
                    { filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp }; objects.Add(tex);
                    if (!tex.LoadImage(File.ReadAllBytes(source)) || tex.width != 640 || tex.height != 480 || tex.format != TextureFormat.RGB24)
                        throw new InvalidDataException("Expected actual 640x480 RGB24 plate");
                    profile.SetTexture("_LiveTex", tex);
                    profile.SetVector("_LiveScale", new Vector4(0.75f, 1, 0, 0));
                    profile.SetVector("_CgScale", new Vector4(0.75f, 1, 0, 0));
                    profile.SetFloat("_FrameAspect", CrtReferenceScreen.LogicalAspect);
                    ApplyPost(profile, cfg.post);
                    var feel = CameraFeelFx.Settings.Resolve(cfg.feel);
                    var logic = new CameraFeelLogic { TargetLuma = feel.TargetLuma, FollowHalfLifeSec = feel.FollowSec };
                    float mean = 0; int count = 0;
                    for (int y = 0; y < tex.height; y += Mathf.Max(1, tex.height / 16))
                        for (int x = 0; x < tex.width; x += Mathf.Max(1, tex.width / 16))
                        { Color p = tex.GetPixel(x, y); mean += p.r * 0.299f + p.g * 0.587f + p.b * 0.114f; count++; }
                    float bias = logic.SteadyBiasFor(mean / count) * feel.Agc;
                    CameraFeelFx.WriteUniforms(profile, feel.NoiseDark, feel.NoiseFixed, bias, 0, 0, 0, 0);
                    profile.SetVector("_CrtView", new Vector4(0, 0, -1, 0));
                    string invariant = ParameterHash(profile, "_CrtOpticalSpread", "_CrtOpticalConvergence");
                    for (int s = 0; s < 2; s++) for (int c = 0; c < 2; c++)
                    {
                        var material = new Material(profile) { hideFlags = HideFlags.HideAndDontSave }; objects.Add(material);
                        float spread = s == 0 ? 1 : 0.5f, convergence = c == 0 ? 1 : 2.5f;
                        material.SetVector("_CrtOpticalSpread", CrtReferenceScreen.OpticalSpread * spread);
                        material.SetVector("_CrtOpticalConvergence", CrtReferenceScreen.OpticalConvergence * convergence);
                        if (material.IsKeywordEnabled("CRT_ORDER_DIAGNOSTIC")
                            || ParameterHash(material, "_CrtOpticalSpread", "_CrtOpticalConvergence") != invariant)
                            throw new InvalidOperationException("Optical matrix changed an uncontrolled property");
                        ShaderUtil.CompilePass(material, 0, true);
                        foreach (var message in ShaderUtil.GetShaderMessages(shader))
                            if (message.severity == ShaderCompilerMessageSeverity.Error) throw new InvalidOperationException(message.message);
                        if (!ShaderUtil.IsPassCompiled(material, 0)) throw new InvalidOperationException("Single pass is not compiled");
                        renderer.sharedMaterial = material; camera.Render();
                        string file = plate + (s == 0 && c == 0 ? "_current" : "_spread" + (s == 0 ? "1" : "0p5") + "_convergence" + (c == 0 ? "1" : "2p5")) + ".png";
                        Save(target, Path.Combine(output, file));
                        var capture = new OpticsCapture { plate = plate, file = file, sha256 = Hash(Path.Combine(output, file)),
                            source = source, sourceHash = Hash(source), invariantHash = invariant, parameters = OpticsParameters(material),
                            spreadMultiplier = spread, convergenceMultiplier = convergence, spread = material.GetVector("_CrtOpticalSpread"),
                            convergence = material.GetVector("_CrtOpticalConvergence"), width = target.width, height = target.height,
                            diagnosticKeyword = material.IsKeywordEnabled("CRT_ORDER_DIAGNOSTIC") };
                        ValidateOpticsImage(Path.Combine(output, file), capture, objects);
                        if (s == 0 && c == 0)
                        {
                            capture.currentHashMatches = capture.sha256 == expected.sha256;
                            if (plate == "A") ValidateOpticsBaseline(baselineFile, Path.Combine(output, file), manifest, objects);
                            if (!capture.currentHashMatches) throw new InvalidDataException("Singlepass current hash differs from round 28: " + plate);
                        }
                        manifest.captures.Add(capture);
                    }
                }
                foreach (var capture in manifest.captures)
                    if (!File.Exists(Path.Combine(output, capture.file)) || Hash(Path.Combine(output, capture.file)) != capture.sha256)
                        throw new InvalidDataException("Capture missing or changed: " + capture.file);
                string manifestPath = Path.Combine(output, "manifest.json");
                File.WriteAllText(manifestPath + ".tmp", JsonUtility.ToJson(manifest, true)); File.Move(manifestPath + ".tmp", manifestPath);
                Debug.Log("[CrtOptics] 12 singlepass captures; A_current round28 hash/pixels identical; shader errors=0 -> " + output);
            }
            finally
            {
                ShaderUtil.allowAsyncCompilation = oldAsync; RenderTexture.active = old;
                for (int i = objects.Count - 1; i >= 0; i--)
                { if (objects[i] is RenderTexture rt) rt.Release(); UnityEngine.Object.DestroyImmediate(objects[i]); }
            }
        }

        private static Color32[] OpticsPixels(string path, List<UnityEngine.Object> objects)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGB24, false, false); objects.Add(texture);
            if (!File.Exists(path) || !texture.LoadImage(File.ReadAllBytes(path)) || texture.width != 1280 || texture.height != 1030)
                throw new InvalidDataException("Invalid optical capture: " + path);
            return texture.GetPixels32();
        }

        private static void ValidateOpticsImage(string path, OpticsCapture capture, List<UnityEngine.Object> objects)
        {
            Color32[] pixels = OpticsPixels(path, objects);
            foreach (Color32 p in pixels)
            {
                if (p.r <= 1 && p.g <= 1 && p.b <= 1) capture.blackPixels++;
                if (p.r >= 250 && p.g <= 5 && p.b >= 250) capture.pinkPixels++;
            }
            if (capture.blackPixels > pixels.Length * 0.99 || capture.pinkPixels > pixels.Length / 100)
                throw new InvalidDataException("Pink/black optical capture: " + path);
        }

        private static void ValidateOpticsBaseline(string baseline, string current, OpticsManifest manifest, List<UnityEngine.Object> objects)
        {
            Color32[] expected = OpticsPixels(baseline, objects), actual = OpticsPixels(current, objects);
            for (int i = 0; i < expected.Length; i++)
            {
                int r = Math.Abs(expected[i].r - actual[i].r), g = Math.Abs(expected[i].g - actual[i].g), b = Math.Abs(expected[i].b - actual[i].b);
                manifest.baselineMaxChannelDifference = Math.Max(manifest.baselineMaxChannelDifference, Math.Max(r, Math.Max(g, b)));
                if (r + g + b > 0) manifest.baselineDifferingPixels++;
            }
            manifest.baselinePixelMatch = manifest.baselineDifferingPixels == 0;
            manifest.baselineHashMatch = Hash(current) == OpticsBaselineHash;
            if (!manifest.baselinePixelMatch || !manifest.baselineHashMatch)
                throw new InvalidDataException("A_current is not pixel/hash identical to round 28");
        }

        private static string OpticsParameters(Material material)
        {
            var text = new StringBuilder();
            for (int i = 0; i < ShaderUtil.GetPropertyCount(material.shader); i++)
            {
                string name = ShaderUtil.GetPropertyName(material.shader, i); text.Append(name).Append('=');
                var type = ShaderUtil.GetPropertyType(material.shader, i);
                if (type == ShaderUtil.ShaderPropertyType.TexEnv)
                {
                    string path = AssetDatabase.GetAssetPath(material.GetTexture(name)); text.Append(path);
                    if (File.Exists(path)) text.Append(" sha256:").Append(Hash(path));
                }
                else if (type == ShaderUtil.ShaderPropertyType.Vector || type == ShaderUtil.ShaderPropertyType.Color)
                { Vector4 v = material.GetVector(name); for (int c = 0; c < 4; c++) text.Append(v[c].ToString("R", CultureInfo.InvariantCulture)).Append(','); }
                else text.Append(material.GetFloat(name).ToString("R", CultureInfo.InvariantCulture));
                text.Append('\n');
            }
            return text.ToString();
        }
    }
}
