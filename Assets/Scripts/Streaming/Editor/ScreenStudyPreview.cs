#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace FixedCamVr.Streaming.EditorTools
{
    /// <summary>参照との比較専用。本編のシーンを開かず、保存も変更もしない。</summary>
    public static class ScreenStudyPreview
    {
        private const int W = 1920, H = 1080;
        private const string RootOut = "G:/マイドライブ/shubie/reports/2026-10-30_screen-study";
        private const string StudyShaderPath = "Assets/Scripts/Streaming/Editor/ScreenStudySurface.shader";
        private static readonly string[] PlateNames = {
            "plate_A_20260924_172209_b8e1f375.jpg",
            "plate_B_20260924_172210_4abb5bae.jpg",
            "plate_C_20260924_172211_a1c7c02f.jpg"
        };

        [Serializable] private sealed class Config { public PostParams post = new PostParams(); public ShowFeelDef? feel; }
        [Serializable] private sealed class Shot {
            public string camera = "", variant = "", view = "", file = "", sha256 = "";
            public float strength, bezel, imageLinearScale;
        }
        [Serializable] private sealed class Source {
            public string camera = "", path = "", copy = "", sha256 = "", capturedAt = "";
            public int width, height;
            public float sourceLuma, exposureBias;
        }
        [Serializable] private sealed class Manifest {
            public string generatedAt = "", unity = "", graphics = "", colorSpace = "", showHash = "";
            public int width = W, height = H;
            public float frameAspect = 16f / 9f, bulge = 0.055f, cameraDistance = 2.6f, fieldOfView = 38f;
            public PostParams post = new PostParams();
            public List<Source> sources = new List<Source>();
            public List<Shot> shots = new List<Shot>();
        }

        [MenuItem("Tools/FixedCamVr/Diagnostics/Compare Main Screen Reference", priority = 258)]
        public static void Run()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string output = EditorCliArgs.Get("out") ?? RootOut;
            Directory.CreateDirectory(Path.Combine(output, "sources"));
            Directory.CreateDirectory(Path.Combine(output, "screenshots"));
            string showPath = Path.Combine(root, "tools/web-compositor/show.json");
            var cfg = JsonUtility.FromJson<Config>(File.ReadAllText(showPath));
            if (cfg == null || cfg.post == null) throw new InvalidOperationException("show/post missing");
            var shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Art/Shaders/Streaming/ScreenComposite.shader");
            var studyShader = AssetDatabase.LoadAssetAtPath<Shader>(StudyShaderPath);
            RequireShader(shader); RequireShader(studyShader);
            var manifest = new Manifest {
                generatedAt = DateTimeOffset.Now.ToString("o"), unity = Application.unityVersion,
                graphics = SystemInfo.graphicsDeviceType.ToString(), colorSpace = QualitySettings.activeColorSpace.ToString(),
                showHash = Hash(showPath), post = cfg.post
            };
            var owned = new List<UnityEngine.Object>();
            var previousRt = RenderTexture.active;
            try
            {
                var current = new Material(shader!) { hideFlags = HideFlags.HideAndDontSave }; owned.Add(current);
                var style = new Material(studyShader!) { hideFlags = HideFlags.HideAndDontSave }; owned.Add(style);
                var display = new Material(studyShader!) { hideFlags = HideFlags.HideAndDontSave }; owned.Add(display);
                display.SetFloat("_Mode", -1);
                RenderTexture rt = NewRt(owned), plateRt = NewRt(owned), styledRt = NewRt(owned);
                var cameraGo = new GameObject("ScreenStudyCamera") { hideFlags = HideFlags.HideAndDontSave }; owned.Add(cameraGo);
                var cam = cameraGo.AddComponent<Camera>();
                cam.enabled = false; cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = Color.black;
                cam.nearClipPlane = 0.1f; cam.farClipPlane = 10f; cam.cullingMask = 1 << 1;
                cam.allowHDR = false; cam.allowMSAA = false; cam.targetTexture = rt; cam.fieldOfView = 38f;
                cam.aspect = (float)W / H;
                var screen = new GameObject("ScreenStudySurface") { hideFlags = HideFlags.HideAndDontSave, layer = 1 }; owned.Add(screen);
                screen.transform.position = new Vector3(1000, 0, 0);
                screen.transform.localScale = new Vector3(2.3704f, 1.3333f, 1f);
                var filter = screen.AddComponent<MeshFilter>();
                var renderer = screen.AddComponent<MeshRenderer>();
                var curved = CrtScreenMesh.BuildMesh(48, 0.055f); owned.Add(curved);
                var flat = CrtScreenMesh.BuildMesh(2, 0f); owned.Add(flat);
                foreach (string name in PlateNames)
                {
                    string path = Path.Combine(root, "tools/web-compositor/captures", name);
                    if (!File.Exists(path)) throw new FileNotFoundException("Required plate missing", path);
                    string id = name.Substring(6, 1);
                    var tex = new Texture2D(2, 2, TextureFormat.RGB24, true, false) { filterMode = FilterMode.Trilinear };
                    owned.Add(tex);
                    if (!tex.LoadImage(File.ReadAllBytes(path))) throw new InvalidDataException(path);
                    float luma = AverageLuma(tex);
                    var feel = CameraFeelFx.Settings.Resolve(cfg.feel);
                    var logic = new CameraFeelLogic { TargetLuma = feel.TargetLuma, FollowHalfLifeSec = feel.FollowSec };
                    float bias = logic.SteadyBiasFor(luma) * feel.Agc;
                    Apply(current, tex, cfg.post, feel, bias);
                    File.Copy(path, Path.Combine(output, "sources", name), true);
                    manifest.sources.Add(new Source { camera = id, path = path, copy = "sources/" + name,
                        sha256 = Hash(path), capturedAt = "2026-09-24T17:22:" + (id == "A" ? "09" : id == "B" ? "10" : "11") + "+09:00",
                        width = tex.width, height = tex.height, sourceLuma = luma, exposureBias = bias });
                    // 共通の色加工済み画像。ガラスの形と縁だけは各案で置き換える。
                    current.SetFloat("_CrtRound", 0); current.SetFloat("_CrtEdge", 0);
                    renderer.sharedMaterial = current; filter.sharedMesh = flat;
                    cam.orthographic = true; cam.orthographicSize = 1.3333f / 2;
                    cam.transform.SetPositionAndRotation(new Vector3(1000, 0, -2.6f), Quaternion.identity);
                    cam.targetTexture = plateRt; cam.Render(); cam.targetTexture = rt; cam.orthographic = false;
                    filter.sharedMesh = curved;
                    for (int variant = 0; variant < 5; variant++)
                    {
                        float strength = variant <= 2 ? 0.32f : 1f;
                        float bezel = variant == 2 || variant == 4 ? 1f : 0f;
                        if (variant == 0)
                        {
                            current.SetFloat("_CrtRound", CameraFeelFx.CrtRound);
                            current.SetFloat("_CrtEdge", CameraFeelFx.CrtEdge);
                            renderer.sharedMaterial = current;
                        }
                        else
                        {
                            style.SetTexture("_MainTex", plateRt); style.SetFloat("_Mode", 1);
                            style.SetFloat("_Strength", strength); style.SetFloat("_Bezel", bezel);
                            Graphics.Blit(plateRt, styledRt, style);
                            display.SetTexture("_MainTex", styledRt); renderer.sharedMaterial = display;
                        }
                        int views = id == "A" ? 2 : 1;
                        for (int view = 0; view < views; view++)
                        {
                            Vector3 eye = view == 0 ? new Vector3(1000, 0, -2.6f) : new Vector3(1000.75f, 0.12f, -2.6f);
                            cam.transform.position = eye; cam.transform.LookAt(screen.transform.position);
                            cam.Render();
                            string rel = $"screenshots/{id}_{variant}_{(view == 0 ? "front" : "oblique")}.png";
                            SavePng(rt, Path.Combine(output, rel));
                            manifest.shots.Add(new Shot { camera = id, variant = variant.ToString(),
                                view = view == 0 ? "front" : "oblique", file = rel, sha256 = Hash(Path.Combine(output, rel)),
                                strength = variant == 0 ? 0 : strength, bezel = bezel, imageLinearScale = bezel > 0 ? 0.9f : 1 });
                        }
                    }
                }
                File.WriteAllText(Path.Combine(output, "manifest.json"), JsonUtility.ToJson(manifest, true), new System.Text.UTF8Encoding(false));
                Debug.Log($"[ScreenStudy] {manifest.sources.Count} sources / {manifest.shots.Count} screenshots -> {output}");
            }
            finally
            {
                RenderTexture.active = previousRt;
                for (int i = owned.Count - 1; i >= 0; i--)
                {
                    if (owned[i] is RenderTexture target) target.Release();
                    UnityEngine.Object.DestroyImmediate(owned[i]);
                }
            }
        }

        private static void RequireShader(Shader? shader)
        {
            if (shader == null || !shader.isSupported) throw new InvalidOperationException("Screen study shader missing/unsupported");
            foreach (var message in ShaderUtil.GetShaderMessages(shader))
                if (message.severity == ShaderCompilerMessageSeverity.Error) throw new InvalidOperationException(message.message);
        }

        private static RenderTexture NewRt(List<UnityEngine.Object> owned)
        {
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            rt.Create(); owned.Add(rt); return rt;
        }

        private static void Apply(Material mat, Texture2D tex, PostParams post, CameraFeelFx.Settings feel, float bias)
        {
            mat.SetTexture("_LiveTex", tex);
            float sourceAspect = (float)tex.width / tex.height;
            Vector2 contain = sourceAspect < 16f / 9f
                ? new Vector2(sourceAspect / (16f / 9f), 1f)
                : new Vector2(1f, (16f / 9f) / sourceAspect);
            mat.SetVector("_LiveScale", new Vector4(contain.x, contain.y, 0, 0));
            mat.SetFloat("_FrameAspect", 16f / 9f);
            mat.SetFloat("_Exposure", post.exposure); mat.SetFloat("_Contrast", post.contrast);
            mat.SetFloat("_Saturation", post.saturation); mat.SetFloat("_Temperature", post.temperature);
            mat.SetFloat("_Vignette", post.vignette); mat.SetFloat("_Grain", post.grain);
            mat.SetFloat("_Scanline", post.scanline); mat.SetFloat("_Lift", post.lift);
            mat.SetFloat("_Tint", post.tint); mat.SetFloat("_Aberration", post.aberration);
            mat.SetFloat("_Pixelate", post.pixelate);
            CameraFeelFx.WriteUniforms(mat, feel.NoiseDark, feel.NoiseFixed, bias, 0, 0, 0, 0);
        }

        private static float AverageLuma(Texture2D tex)
        {
            var map = new SourceLumaMap(); map.BeginFrame();
            for (int y = 0; y < tex.height; y += Mathf.Max(1, tex.height / 16))
                for (int x = 0; x < tex.width; x += Mathf.Max(1, tex.width / 16))
                {
                    Color c = tex.GetPixel(x, y);
                    map.Add((float)x / tex.width, (float)y / tex.height, c.r * 0.299f + c.g * 0.587f + c.b * 0.114f);
                }
            map.EndFrame(); return map.Mean;
        }

        private static void SavePng(RenderTexture rt, string path)
        {
            var previous = RenderTexture.active;
            var tex = new Texture2D(W, H, TextureFormat.RGB24, false, false);
            try { RenderTexture.active = rt; tex.ReadPixels(new Rect(0, 0, W, H), 0, 0); tex.Apply(); File.WriteAllBytes(path, tex.EncodeToPNG()); }
            finally { RenderTexture.active = previous; UnityEngine.Object.DestroyImmediate(tex); }
        }

        private static string Hash(string path)
        {
            using var sha = SHA256.Create(); using var stream = File.OpenRead(path);
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
    }
}
