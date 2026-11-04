#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    public static partial class CrtReferencePreview
    {
        private const string Output = "G:/マイドライブ/shubie/reports/2026-10-30_crt-reference";
        [Serializable] private sealed class Config { public PostParams post = new PostParams(); public ShowFeelDef? feel; }
        [Serializable] private sealed class Capture {
            public string source = "", sourceHash = "", file = "", sha256 = "";
            public float mono, reflectionView, physicalAspect, logicalAspect;
        }
        [Serializable] private sealed class Manifest {
            public string utc = "", unity = "", shaderHash = "", frameHash = "", caseColorHash = "", reflectanceHash = "";
            public string runtimeProfileHash = "", glassHash = "", normalHash = "", gradeHash = "", calibrationHash = "";
            public Vector4 imageAdjustment;
            public string sameProfileAHash = "", sameProfileBHash = "", sameProfileImageHash = "";
            public bool sameProfileVerified;
            public PostParams post = new PostParams();
            public List<Capture> captures = new List<Capture>();
            public List<Ablation> ablations = new List<Ablation>();
        }
        [Serializable] private sealed class Ablation {
            public string file = "", sha256 = "", source = "", sourceHash = "", invariantHash = "";
            public float vignette, field;
        }

        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview Reference CRT", priority = 259)]
        public static void Run()
        {
            ConfigureTexture("Assets/Resources/CrtReference/Frame.png", true);
            ConfigureTexture("Assets/Resources/CrtReference/CaseColor.jpg", true);
            ConfigureTexture("Assets/Resources/CrtReference/Reflectance.png", false);
            ConfigureTexture("Assets/Resources/CrtReference/Glass.png", false);
            ConfigureTexture("Assets/Resources/CrtReference/Normal.png", false);
            ConfigureTexture("Assets/Resources/CrtReference/Grade.png", false);
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string output = EditorCliArgs.Get("out") ?? Output;
            Directory.CreateDirectory(output);
            var cfg = JsonUtility.FromJson<Config>(File.ReadAllText(Path.Combine(root, "tools/web-compositor/show.json")));
            if (cfg == null) throw new InvalidDataException("Missing show settings");
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Art/Shaders/Streaming/ScreenComposite.shader");
            if (shader == null || !shader.isSupported) throw new InvalidOperationException("Reference CRT shader unsupported");
            foreach (var msg in ShaderUtil.GetShaderMessages(shader))
                if (msg.severity == ShaderCompilerMessageSeverity.Error) throw new InvalidOperationException(msg.message);
            var manifest = new Manifest { utc = DateTime.UtcNow.ToString("o"), unity = Application.unityVersion,
                runtimeProfileHash = Hash(Path.Combine(root, "Assets/Scripts/Streaming/CrtReferenceScreen.cs")),
                glassHash = Hash(Path.Combine(root, "Assets/Resources/CrtReference/Glass.png")),
                normalHash = Hash(Path.Combine(root, "Assets/Resources/CrtReference/Normal.png")),
                gradeHash = Hash(Path.Combine(root, "Assets/Resources/CrtReference/Grade.png")),
                imageAdjustment = CrtReferenceScreen.ImageAdjustment, post = cfg.post,
                shaderHash = Hash(Path.Combine(root, "Assets/Art/Shaders/Streaming/ScreenComposite.shader")),
                frameHash = Hash(Path.Combine(root, "Assets/Resources/CrtReference/Frame.png")),
                caseColorHash = Hash(Path.Combine(root, "Assets/Resources/CrtReference/CaseColor.jpg")),
                reflectanceHash = Hash(Path.Combine(root, "Assets/Resources/CrtReference/Reflectance.png")) };
            var objects = new List<UnityEngine.Object>();
            var old = RenderTexture.active;
            try
            {
                var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave }; objects.Add(material);
                if (!CrtReferenceScreen.Apply(material)) throw new InvalidOperationException("Reference materials not configured");
                var screen = new GameObject("ReferenceScreen") { hideFlags = HideFlags.HideAndDontSave, layer = 1 }; objects.Add(screen);
                screen.transform.position = new Vector3(1000, 0, 0);
                screen.transform.localScale = new Vector3(CrtReferenceScreen.CaseAspect, 1, 1);
                var mesh = CrtScreenMesh.BuildMesh(48, 0.055f); objects.Add(mesh);
                screen.AddComponent<MeshFilter>().sharedMesh = mesh;
                screen.AddComponent<MeshRenderer>().sharedMaterial = material;
                var go = new GameObject("ReferenceCapture") { hideFlags = HideFlags.HideAndDontSave }; objects.Add(go);
                var camera = go.AddComponent<Camera>(); camera.enabled = false;
                camera.transform.SetPositionAndRotation(new Vector3(1000, 0, -2), Quaternion.identity);
                camera.orthographic = true; camera.orthographicSize = 0.5f;
                camera.nearClipPlane = 0.1f; camera.farClipPlane = 4; camera.cullingMask = 1 << 1;
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
                camera.allowHDR = false; camera.allowMSAA = false;
                var target = new RenderTexture(1280, 1030, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                target.Create(); objects.Add(target); camera.targetTexture = target; camera.aspect = CrtReferenceScreen.CaseAspect;
                string[] names = { "plate_A_20260924_172209_b8e1f375.jpg", "plate_B_20260924_172210_4abb5bae.jpg", "plate_C_20260924_172211_a1c7c02f.jpg" };
                float aBias = 0;
                foreach (string name in names)
                {
                    string source = Path.Combine(root, "tools/web-compositor/captures", name);
                    if (!File.Exists(source)) throw new FileNotFoundException("Empty plate missing", source);
                    var tex = new Texture2D(2, 2, TextureFormat.RGB24, true, false) { filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp };
                    objects.Add(tex); if (!tex.LoadImage(File.ReadAllBytes(source))) throw new InvalidDataException(source);
                    if (tex.width != 640 || tex.height != 480 || tex.format != TextureFormat.RGB24)
                        throw new InvalidDataException("Input-only calibration requires matching 640x480 RGB24 sources");
                    material.SetTexture("_LiveTex", tex);
                    material.SetVector("_LiveScale", new Vector4(0.75f, 1, 0, 0));
                    material.SetVector("_CgScale", new Vector4(0.75f, 1, 0, 0));
                    material.SetFloat("_FrameAspect", CrtReferenceScreen.LogicalAspect);
                    ApplyPost(material, cfg.post);
                    var feel = CameraFeelFx.Settings.Resolve(cfg.feel);
                    var logic = new CameraFeelLogic { TargetLuma = feel.TargetLuma, FollowHalfLifeSec = feel.FollowSec };
                    float mean = 0; int count = 0;
                    for (int y = 0; y < tex.height; y += Mathf.Max(1, tex.height / 16))
                        for (int x = 0; x < tex.width; x += Mathf.Max(1, tex.width / 16))
                        { Color p = tex.GetPixel(x, y); mean += p.r * 0.299f + p.g * 0.587f + p.b * 0.114f; count++; }
                    float bias = logic.SteadyBiasFor(mean / count) * feel.Agc;
                    if (name.Contains("plate_A")) aBias = bias;
                    for (int state = 0; state < (name.Contains("plate_A") ? 4 : 1); state++)
                    {
                        float mono = state == 1 ? 0.5f : state == 2 ? 1f : 0f;
                        CameraFeelFx.WriteUniforms(material, feel.NoiseDark, feel.NoiseFixed, bias, 0,
                            mono > 0 ? ScreenDecayLogic.BlocksFor(mono) : 0, 0, mono);
                        material.SetVector("_CrtView", new Vector4(state == 3 ? 0.7f : 0, 0, -1, 0));
                        string rel = $"{name.Substring(6, 1)}_{(state == 0 ? "front" : state == 3 ? "reflection" : "mono" + mono.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))}.png";
                        camera.Render(); Save(target, Path.Combine(output, rel));
                        manifest.captures.Add(new Capture { source = source, sourceHash = Hash(source), file = rel,
                            sha256 = Hash(Path.Combine(output, rel)), mono = mono, reflectionView = state == 3 ? 0.7f : 0,
                            physicalAspect = CrtReferenceScreen.CaseAspect, logicalAspect = CrtReferenceScreen.LogicalAspect });
                        if (name.Contains("plate_A") && state == 0)
                        {
                            manifest.sameProfileAHash = ParameterHash(material);
                            material.SetFloat("_CrtGradeBypass", 1);
                            camera.Render(); Save(target, Path.Combine(output, "A_ungraded.png"));
                            manifest.calibrationHash = Hash(Path.Combine(output, "A_ungraded.png"));
                            material.SetFloat("_CrtGradeBypass", 0);
                        }
                        if (name.Contains("plate_B") && state == 0)
                        {
                            material.SetFloat("_ExposureBias", aBias);
                            manifest.sameProfileBHash = ParameterHash(material);
                            manifest.sameProfileVerified = manifest.sameProfileAHash == manifest.sameProfileBHash;
                            if (!manifest.sameProfileVerified) throw new InvalidOperationException("Input-only comparison changed material parameters");
                            camera.Render(); Save(target, Path.Combine(output, "B_same_profile.png"));
                            manifest.sameProfileImageHash = Hash(Path.Combine(output, "B_same_profile.png"));
                            material.SetFloat("_ExposureBias", bias);
                        }
                        if (state == 0 && EditorCliArgs.Get("ablation") == "1")
                            CaptureAblation(material, camera, target, source, name.Substring(6, 1), output, manifest);
                    }
                }
                CrtReferenceValidation.Run(camera, screen.GetComponent<Renderer>(), target, output);
                foreach (var msg in ShaderUtil.GetShaderMessages(shader))
                    if (msg.severity == ShaderCompilerMessageSeverity.Error) throw new InvalidOperationException(msg.message);
                string manifestPath = Path.Combine(output, "manifest.json");
                string manifestTemp = manifestPath + ".tmp";
                File.WriteAllText(manifestTemp, JsonUtility.ToJson(manifest, true));
                if (File.Exists(manifestPath)) File.Replace(manifestTemp, manifestPath, null);
                else File.Move(manifestTemp, manifestPath);
                Debug.Log($"[CrtReference] {manifest.captures.Count} actual shader captures -> {output}");
            }
            finally
            {
                RenderTexture.active = old;
                for (int i = objects.Count - 1; i >= 0; i--)
                { if (objects[i] is RenderTexture rt) rt.Release(); UnityEngine.Object.DestroyImmediate(objects[i]); }
            }
        }

        private static void ApplyPost(Material mat, PostParams post)
        {
            mat.SetFloat("_Exposure", post.exposure); mat.SetFloat("_Contrast", post.contrast);
            mat.SetFloat("_Saturation", post.saturation); mat.SetFloat("_Temperature", post.temperature);
            mat.SetFloat("_Vignette", post.vignette); mat.SetFloat("_Grain", post.grain);
            mat.SetFloat("_Scanline", post.scanline); mat.SetFloat("_Lift", post.lift);
            mat.SetFloat("_Tint", post.tint); mat.SetFloat("_Aberration", post.aberration);
            mat.SetFloat("_Pixelate", post.pixelate);
        }

        private static void CaptureAblation(Material material, Camera camera, RenderTexture target,
            string source, string plate, string output, Manifest manifest)
        {
            float originalVignette = material.GetFloat("_Vignette");
            float originalField = material.GetFloat("_CrtFieldStrength");
            string invariant = ParameterHash(material, "_Vignette", "_CrtFieldStrength");
            Directory.CreateDirectory(Path.Combine(output, "ablation"));
            try
            {
                for (int v = 0; v < 2; v++) for (int f = 0; f < 2; f++)
                {
                    material.SetFloat("_Vignette", v * originalVignette);
                    material.SetFloat("_CrtFieldStrength", f);
                    string actualInvariant = ParameterHash(material, "_Vignette", "_CrtFieldStrength");
                    if (actualInvariant != invariant) throw new InvalidOperationException("Ablation changed an uncontrolled parameter");
                    string file = $"ablation/{plate}_v{v}_f{f}.png";
                    camera.Render(); Save(target, Path.Combine(output, file));
                    manifest.ablations.Add(new Ablation { file = file, sha256 = Hash(Path.Combine(output, file)),
                        source = source, sourceHash = Hash(source), invariantHash = invariant,
                        vignette = v * originalVignette, field = f });
                }
            }
            finally
            { material.SetFloat("_Vignette", originalVignette); material.SetFloat("_CrtFieldStrength", originalField); }
        }

        private static void ConfigureTexture(string path, bool color)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) throw new InvalidOperationException("Reference texture not imported: " + path);
            if (importer.sRGBTexture == color && importer.npotScale == TextureImporterNPOTScale.None
                && importer.textureCompression == TextureImporterCompression.Uncompressed && importer.mipmapEnabled) return;
            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = color;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = color;
            importer.mipmapEnabled = true;
            importer.maxTextureSize = 2048;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Trilinear;
            importer.SaveAndReimport();
        }

        private static void Save(RenderTexture target, string file)
        {
            RenderTexture.active = target;
            var tex = new Texture2D(target.width, target.height, TextureFormat.RGB24, false, false);
            try { tex.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); tex.Apply(); File.WriteAllBytes(file, tex.EncodeToPNG()); }
            finally { UnityEngine.Object.DestroyImmediate(tex); }
        }
        private static string Hash(string path)
        { using var sha = SHA256.Create(); using var input = File.OpenRead(path); return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant(); }

        // Equality is meaningful within this run. Only the input texture is excluded.
        private static string ParameterHash(Material material, params string[] excluded)
        {
            var text = new StringBuilder(material.shader.name);
            Shader shader = material.shader;
            for (int i = 0; i < ShaderUtil.GetPropertyCount(shader); i++)
            {
                string name = ShaderUtil.GetPropertyName(shader, i);
                if (name == "_LiveTex" || Array.IndexOf(excluded, name) >= 0) continue;
                text.Append('|').Append(name).Append(':');
                var type = ShaderUtil.GetPropertyType(shader, i);
                if (type == ShaderUtil.ShaderPropertyType.TexEnv)
                { Texture texture = material.GetTexture(name); text.Append(texture != null ? texture.GetInstanceID() : 0); }
                else if (type == ShaderUtil.ShaderPropertyType.Vector || type == ShaderUtil.ShaderPropertyType.Color)
                {
                    Vector4 v = material.GetVector(name);
                    for (int c = 0; c < 4; c++) text.Append(v[c].ToString("R", CultureInfo.InvariantCulture)).Append(',');
                }
                else text.Append(material.GetFloat(name).ToString("R", CultureInfo.InvariantCulture));
            }
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "").ToLowerInvariant();
        }
    }
}
