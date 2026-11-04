#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    public static partial class CrtReferencePreview
    {
        [Serializable] private sealed class OrderManifest
        {
            public string utc = "", unity = "", shaderHash = "", previewHash = "", runtimeProfileHash = "", showHash = "";
            public string intermediate = "640x480 ARGBHalf linear, depth24, MSAA1, clamp/trilinear, mip chain, explicit GenerateMips after each render";
            public string comparison = "control=raw RT -> PSF+Grade RT; variant=Grade RT -> PSF RT; identical final shader stage";
            public string limitations = "Both arms move Grade before lens/sensor/glare. Reference PSF uses source-space mip filtering before final baseLod rather than max(baseLod,referenceLod). Refraction occurs only in final stage. These common changes and RT resampling are measured against current singlepass; pair differences isolate Grade/PSF order. CG=0, mono=0, field=0. No runtime adoption.";
            public float calibrationCopyError, calibrationRejectedError;
            public List<OrderCapture> captures = new List<OrderCapture>();
            public List<OrderDifference> differences = new List<OrderDifference>();
        }
        [Serializable] private sealed class OrderCapture
        {
            public string plate = "", source = "", sourceHash = "", invariantHash = "", parameters = "";
            public string singlepass = "", control = "", variant = "", singlepassHash = "", controlHash = "", variantHash = "";
            public float rawCopyMaxLinearError;
        }
        [Serializable] private sealed class OrderDifference
        {
            public string plate = "", comparison = "";
            public double mae, rmse;
            public int maxChannelDifference, differingPixels, blackPixels, pinkPixels;
        }

        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview CRT Processing Order", priority = 260)]
        public static void RunProcessingOrder()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string output = EditorCliArgs.Get("out") ?? Path.Combine(Output, "processing-order-01");
            if (Directory.Exists(output) && Directory.GetFileSystemEntries(output).Length > 0)
                throw new IOException("Processing order output must be empty: " + output);
            if (!File.ReadAllText(Path.Combine(root, ".claude/canon/ROUNDS.md")).Contains("加工感: Grade/PSF順序"))
                throw new InvalidOperationException("Missing pre-render processing-order prediction");
            Directory.CreateDirectory(output);
            var cfg = JsonUtility.FromJson<Config>(File.ReadAllText(Path.Combine(root, "tools/web-compositor/show.json")));
            if (cfg == null) throw new InvalidDataException("Missing show settings");
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Art/Shaders/Streaming/ScreenComposite.shader");
            if (shader == null || !shader.isSupported) throw new InvalidOperationException("Screen shader unsupported");
            var manifest = new OrderManifest { utc = DateTime.UtcNow.ToString("o"), unity = Application.unityVersion,
                shaderHash = Hash(Path.Combine(root, "Assets/Art/Shaders/Streaming/ScreenComposite.shader")),
                previewHash = Hash(Path.Combine(root, "Assets/Scripts/Streaming/Editor/CrtReferenceProcessingOrder.cs")),
                runtimeProfileHash = Hash(Path.Combine(root, "Assets/Scripts/Streaming/CrtReferenceScreen.cs")),
                showHash = Hash(Path.Combine(root, "tools/web-compositor/show.json")) };
            var objects = new List<UnityEngine.Object>();
            RenderTexture old = RenderTexture.active;
            bool oldAsyncCompilation = ShaderUtil.allowAsyncCompilation;
            try
            {
                // A pending diagnostic variant must never fall back to the runtime shader.
                ShaderUtil.allowAsyncCompilation = false;
                var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave }; objects.Add(material);
                if (!CrtReferenceScreen.Apply(material)) throw new InvalidOperationException("Missing reference maps");
                ApplyPost(material, cfg.post);
                material.SetVector("_LiveScale", new Vector4(0.75f, 1, 0, 0));
                material.SetVector("_CgScale", new Vector4(0.75f, 1, 0, 0));
                material.SetFloat("_FrameAspect", CrtReferenceScreen.LogicalAspect);
                material.SetFloat("_CgStrength", 0);
                var screen = new GameObject("OrderScreen") { hideFlags = HideFlags.HideAndDontSave, layer = 1 }; objects.Add(screen);
                screen.transform.position = new Vector3(1000, 0, 0);
                Mesh curved = CrtScreenMesh.BuildMesh(48, 0.055f); objects.Add(curved);
                Mesh flat = CrtScreenMesh.BuildMesh(48, 0); objects.Add(flat);
                var filter = screen.AddComponent<MeshFilter>();
                var renderer = screen.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material;
                var cameraGo = new GameObject("OrderCamera") { hideFlags = HideFlags.HideAndDontSave }; objects.Add(cameraGo);
                var camera = cameraGo.AddComponent<Camera>(); camera.enabled = false;
                camera.transform.SetPositionAndRotation(new Vector3(1000, 0, -2), Quaternion.identity);
                camera.orthographic = true; camera.orthographicSize = 0.5f;
                camera.nearClipPlane = 0.1f; camera.farClipPlane = 4; camera.cullingMask = 1 << 1;
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
                camera.allowHDR = true; camera.allowMSAA = false;
                var final = new RenderTexture(1280, 1030, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                final.Create(); objects.Add(final);
                var first = OrderTarget(); objects.Add(first);
                var second = OrderTarget(); objects.Add(second);
                // Non-symmetric, known sRGB values calibrate both UV direction and linear readback.
                var calibration = new Texture2D(640, 480, TextureFormat.RGB24, true, false)
                { filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp }; objects.Add(calibration);
                var calibrationPixels = new Color[640 * 480];
                for (int y = 0; y < 480; y++) for (int x = 0; x < 640; x++)
                    calibrationPixels[y * 640 + x] = new Color(x < 237 ? 0.12f : 0.82f, y < 173 ? 0.3f : 0.65f,
                        ((x / 17 + y / 29) % 3) * 0.4f);
                calibration.SetPixels(calibrationPixels); calibration.Apply(true, false);
                filter.sharedMesh = flat; screen.transform.localScale = new Vector3(4f / 3f, 1, 1);
                camera.aspect = 4f / 3f; camera.targetTexture = first;
                renderer.sharedMaterial = OrderMaterial(material, calibration, 1, 0, objects);
                camera.Render(); first.GenerateMips();
                Save(first, Path.Combine(output, "calibration_raw.png"));
                manifest.calibrationCopyError = ValidateOrderCopy(first, calibration);
                renderer.sharedMaterial = OrderMaterial(material, calibration, 3, 0, objects);
                camera.Render();
                Save(first, Path.Combine(output, "calibration_rejected.png"));
                manifest.calibrationRejectedError = OrderCopyError(first, calibration);
                if (manifest.calibrationRejectedError <= 0.003f)
                    throw new InvalidOperationException("Raw-copy validator accepted a processed/cased image");
                string[] names = { "plate_A_20260924_172209_b8e1f375.jpg", "plate_B_20260924_172210_4abb5bae.jpg", "plate_C_20260924_172211_a1c7c02f.jpg" };
                foreach (string name in names)
                {
                    string source = Path.Combine(root, "tools/web-compositor/captures", name);
                    if (!File.Exists(source)) throw new FileNotFoundException("Missing actual empty plate", source);
                    var tex = new Texture2D(2, 2, TextureFormat.RGB24, true, false) { filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp };
                    objects.Add(tex);
                    if (!tex.LoadImage(File.ReadAllBytes(source)) || tex.width != 640 || tex.height != 480 || tex.format != TextureFormat.RGB24)
                        throw new InvalidDataException("Expected real 640x480 RGB24 plate: " + source);
                    var feel = CameraFeelFx.Settings.Resolve(cfg.feel);
                    var logic = new CameraFeelLogic { TargetLuma = feel.TargetLuma, FollowHalfLifeSec = feel.FollowSec };
                    float mean = 0; int count = 0;
                    for (int y = 0; y < tex.height; y += tex.height / 16)
                        for (int x = 0; x < tex.width; x += tex.width / 16)
                        { Color p = tex.GetPixel(x, y); mean += p.r * 0.299f + p.g * 0.587f + p.b * 0.114f; count++; }
                    CameraFeelFx.WriteUniforms(material, feel.NoiseDark, feel.NoiseFixed, logic.SteadyBiasFor(mean / count) * feel.Agc, 0, 0, 0, 0);
                    material.SetTexture("_LiveTex", tex);
                    var capture = new OrderCapture { plate = name.Substring(6, 1), source = source, sourceHash = Hash(source) };
                    capture.invariantHash = ParameterHash(material, "_CrtOrderStage", "_CrtOrderGradeFirst");
                    capture.parameters = OrderParameters(material);
                    renderer.sharedMaterial = material;
                    ShaderUtil.CompilePass(material, 0, true);
                    filter.sharedMesh = curved; screen.transform.localScale = new Vector3(CrtReferenceScreen.CaseAspect, 1, 1);
                    camera.aspect = CrtReferenceScreen.CaseAspect; camera.targetTexture = final;
                    camera.Render();
                    capture.singlepass = capture.plate + "_singlepass.png";
                    Save(final, Path.Combine(output, capture.singlepass));
                    Color32[] baseline = OrderPixels(final);
                    Color32[]? control = null;
                    for (int arm = 0; arm < 2; arm++)
                    {
                        // Dedicated materials avoid render-state reuse within this single Editor frame.
                        Material firstMaterial = OrderMaterial(material, tex, 1, arm, objects);
                        Material secondMaterial = OrderMaterial(material, first, 2, arm, objects);
                        Material finalMaterial = OrderMaterial(material, second, 3, arm, objects);
                        filter.sharedMesh = flat; screen.transform.localScale = new Vector3(4f / 3f, 1, 1);
                        camera.aspect = 4f / 3f;
                        renderer.sharedMaterial = firstMaterial; camera.targetTexture = first;
                        camera.Render(); first.GenerateMips();
                        Save(first, Path.Combine(output, capture.plate + (arm == 0 ? "_control" : "_variant") + "_stage1.png"));
                        if (arm == 0) capture.rawCopyMaxLinearError = ValidateOrderCopy(first, tex);
                        renderer.sharedMaterial = secondMaterial; camera.targetTexture = second;
                        camera.Render(); second.GenerateMips();
                        Save(second, Path.Combine(output, capture.plate + (arm == 0 ? "_control" : "_variant") + "_stage2.png"));
                        if (ParameterHash(finalMaterial, "_CrtOrderStage", "_CrtOrderGradeFirst") != capture.invariantHash)
                            throw new InvalidOperationException("Order experiment changed a fixed parameter");
                        renderer.sharedMaterial = finalMaterial;
                        filter.sharedMesh = curved; screen.transform.localScale = new Vector3(CrtReferenceScreen.CaseAspect, 1, 1);
                        camera.aspect = CrtReferenceScreen.CaseAspect; camera.targetTexture = final;
                        camera.Render();
                        string file = capture.plate + (arm == 0 ? "_control.png" : "_variant.png");
                        Save(final, Path.Combine(output, file));
                        Color32[] pixels = OrderPixels(final);
                        manifest.differences.Add(OrderDifferenceFor(capture.plate, arm == 0 ? "control-singlepass" : "variant-singlepass", baseline, pixels));
                        if (arm == 0) { capture.control = file; control = pixels; }
                        else { capture.variant = file; manifest.differences.Add(OrderDifferenceFor(capture.plate, "variant-control", control!, pixels)); }
                    }
                    capture.singlepassHash = Hash(Path.Combine(output, capture.singlepass));
                    capture.controlHash = Hash(Path.Combine(output, capture.control));
                    capture.variantHash = Hash(Path.Combine(output, capture.variant));
                    manifest.captures.Add(capture);
                }
                foreach (var message in ShaderUtil.GetShaderMessages(shader))
                    if (message.severity == ShaderCompilerMessageSeverity.Error) throw new InvalidOperationException(message.message);
                string path = Path.Combine(output, "manifest.json");
                File.WriteAllText(path + ".tmp", JsonUtility.ToJson(manifest, true));
                File.Move(path + ".tmp", path);
                Debug.Log("[CrtOrder] 3 plates, 9 final captures, 12 intermediate captures; shader errors=0 -> " + output);
            }
            finally
            {
                ShaderUtil.allowAsyncCompilation = oldAsyncCompilation;
                RenderTexture.active = old;
                for (int i = objects.Count - 1; i >= 0; i--)
                { if (objects[i] is RenderTexture rt) rt.Release(); UnityEngine.Object.DestroyImmediate(objects[i]); }
            }
        }

        private static RenderTexture OrderTarget()
        {
            var rt = new RenderTexture(640, 480, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            { useMipMap = true, autoGenerateMips = false, filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp, antiAliasing = 1 };
            if (!rt.Create()) throw new InvalidOperationException("Failed to create processing-order RT");
            return rt;
        }

        private static Material OrderMaterial(Material profile, Texture source, int stage, int arm, List<UnityEngine.Object> objects)
        {
            var material = new Material(profile) { hideFlags = HideFlags.HideAndDontSave }; objects.Add(material);
            material.EnableKeyword("CRT_ORDER_DIAGNOSTIC");
            material.SetFloat("_CrtOrderStage", stage); material.SetFloat("_CrtOrderGradeFirst", arm);
            material.SetTexture("_LiveTex", source);
            ShaderUtil.CompilePass(material, 0, true);
            if (!ShaderUtil.IsPassCompiled(material, 0)) throw new InvalidOperationException("Diagnostic pass is not compiled");
            foreach (var message in ShaderUtil.GetShaderMessages(material.shader))
                if (message.severity == ShaderCompilerMessageSeverity.Error) throw new InvalidOperationException(message.message);
            return material;
        }

        private static float ValidateOrderCopy(RenderTexture target, Texture2D source)
        {
            float error = OrderCopyError(target, source);
            if (error > 0.003f) throw new InvalidOperationException("Stage 1 is not an unprocessed source copy: " + error);
            return error;
        }

        private static float OrderCopyError(RenderTexture target, Texture2D source)
        {
            RenderTexture.active = target;
            var readback = new Texture2D(target.width, target.height, TextureFormat.RGBAFloat, false, true);
            try
            {
                readback.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); readback.Apply();
                Color[] actual = readback.GetPixels(), expected = source.GetPixels();
                float maxError = 0;
                for (int i = 0; i < actual.Length; i++)
                {
                    Color e = QualitySettings.activeColorSpace == ColorSpace.Linear ? expected[i].linear : expected[i];
                    maxError = Mathf.Max(maxError, Mathf.Abs(actual[i].r - e.r), Mathf.Abs(actual[i].g - e.g), Mathf.Abs(actual[i].b - e.b));
                }
                return maxError;
            }
            finally { UnityEngine.Object.DestroyImmediate(readback); }
        }

        private static Color32[] OrderPixels(RenderTexture rt)
        {
            RenderTexture.active = rt;
            var texture = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false, false);
            try { texture.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); texture.Apply(); return texture.GetPixels32(); }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
        }

        private static OrderDifference OrderDifferenceFor(string plate, string name, Color32[] a, Color32[] b)
        {
            if (a.Length != b.Length) throw new InvalidOperationException("Mismatched comparison dimensions");
            var metric = new OrderDifference { plate = plate, comparison = name };
            double sum = 0, squares = 0;
            for (int i = 0; i < a.Length; i++)
            {
                int dr = Math.Abs(a[i].r - b[i].r), dg = Math.Abs(a[i].g - b[i].g), db = Math.Abs(a[i].b - b[i].b);
                sum += dr + dg + db; squares += dr * dr + dg * dg + db * db;
                metric.maxChannelDifference = Math.Max(metric.maxChannelDifference, Math.Max(dr, Math.Max(dg, db)));
                if (dr + dg + db != 0) metric.differingPixels++;
                if (b[i].r <= 1 && b[i].g <= 1 && b[i].b <= 1) metric.blackPixels++;
                if (b[i].r >= 250 && b[i].g <= 5 && b[i].b >= 250) metric.pinkPixels++;
            }
            metric.mae = sum / (a.Length * 3.0); metric.rmse = Math.Sqrt(squares / (a.Length * 3.0));
            if (metric.pinkPixels > a.Length / 100 || metric.blackPixels > a.Length * 0.99)
                throw new InvalidOperationException("Pink/black render failure: " + plate + " " + name);
            return metric;
        }

        private static string OrderParameters(Material material)
        {
            var result = new System.Text.StringBuilder();
            for (int i = 0; i < ShaderUtil.GetPropertyCount(material.shader); i++)
            {
                string name = ShaderUtil.GetPropertyName(material.shader, i);
                result.Append(name).Append('=');
                var type = ShaderUtil.GetPropertyType(material.shader, i);
                if (type == ShaderUtil.ShaderPropertyType.TexEnv)
                {
                    string path = AssetDatabase.GetAssetPath(material.GetTexture(name));
                    result.Append(path);
                    if (File.Exists(path)) result.Append(" sha256:").Append(Hash(path));
                }
                else if (type == ShaderUtil.ShaderPropertyType.Vector || type == ShaderUtil.ShaderPropertyType.Color)
                { Vector4 v = material.GetVector(name); for (int c = 0; c < 4; c++) result.Append(v[c].ToString("R", CultureInfo.InvariantCulture)).Append(','); }
                else result.Append(material.GetFloat(name).ToString("R", CultureInfo.InvariantCulture));
                result.Append('\n');
            }
            return result.ToString();
        }
    }
}
