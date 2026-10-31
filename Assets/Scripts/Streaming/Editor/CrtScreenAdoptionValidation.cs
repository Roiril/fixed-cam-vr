#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    public static partial class CrtReferencePreview
    {
        [Serializable] private sealed class AdoptionReport
        {
            public string utc = "", meaning = "Adopted screen contour and center/cool adjustment. Mono remains Director-driven; .5 is used only to compare the presented adoption image. Interior alpha1, opaque rendering and original mesh/UV retained. The original case/glass RGB is retained inside the50% mask contour.";
            public float webRgbMaxError, cutInteriorMaxError, captureMaxError;
            public string captureComparison = "Synchronous Ending capture versus the common canonical Render entrance. Curved-mesh Camera rasterization has separate contour/interior proof and is not a byte-identical Blit reference.";
            public string approvedCaptureHash = "";
            public int backgroundOnlyGreen, opaqueNegativeGreen;
            public int outsideTransparent, insideOpaque, maskDisagreements, backgroundOutside, backgroundLeaksInside;
            public bool meshUnchanged;
            public List<StyleFile> sources = new List<StyleFile>();
            public List<OpticsCapture> photos = new List<OpticsCapture>();
        }

        [MenuItem("Tools/FixedCamVr/Diagnostics/Validate Adopted Screen", priority = 275)]
        public static void RunScreenAdoptionValidation()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string output = EditorCliArgs.Get("out") ?? "G:/マイドライブ/shubie/reports/2026-11-01_screen-adoption/gpu";
            if (Directory.Exists(output) && Directory.GetFileSystemEntries(output).Length != 0) throw new IOException("Adoption output must be empty");
            var cfg = JsonUtility.FromJson<Config>(File.ReadAllText(Path.Combine(root, "tools/web-compositor/show.json")));
            var legacy = JsonUtility.FromJson<Manifest>(File.ReadAllText(Path.Combine(Output, "round-28/manifest.json")));
            if (JsonUtility.ToJson(cfg.post) != JsonUtility.ToJson(legacy.post)) throw new InvalidDataException("Post differs from fixed photo baseline");
            if (Hash("tools/crt-tuner/screen-mask.png") != Hash("Assets/Resources/CrtReference/ScreenMask.png")) throw new InvalidDataException("Runtime contour differs from Web mask bytes");
            Directory.CreateDirectory(output); var report = new AdoptionReport { utc = DateTime.UtcNow.ToString("o") };
            foreach (string path in new[] { "Assets/Art/Shaders/Streaming/ScreenComposite.shader", "Assets/Art/Shaders/Streaming/CrtCompositeShared.hlsl", "Assets/Art/Shaders/Streaming/CrtSignalAdjustment.hlsl", "Assets/Art/Shaders/Streaming/CrtRuntimeSignal.shader", "Assets/Scripts/Streaming/CrtSignalProcessor.cs", "Assets/Scripts/Streaming/CrtSignalProfile.cs", "Assets/Scripts/Streaming/Editor/CrtScreenAdoptionValidation.cs", "Assets/Resources/CrtReference/ScreenMask.png", "Assets/Resources/CrtReference/ScreenMask.png.meta", "tools/web-compositor/show.json" }) report.sources.Add(new StyleFile { path = path, sha256 = Hash(path) });
            var objects = new List<UnityEngine.Object>(); bool async = ShaderUtil.allowAsyncCompilation; RenderTexture previous = RenderTexture.active;
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                var shader = CrtSignalResources.Load().Composite;
                var surface = new GameObject("AdoptionScreen") { layer = 1, hideFlags = HideFlags.HideAndDontSave }; objects.Add(surface); surface.transform.position = new Vector3(1000, 0, 0); surface.transform.localScale = new Vector3(CrtReferenceScreen.CaseAspect, 1, 1);
                var mesh = CrtScreenMesh.BuildMesh(48, .055f); objects.Add(mesh); surface.AddComponent<MeshFilter>().sharedMesh = mesh; var renderer = surface.AddComponent<MeshRenderer>(); Matrix4x4 transform = surface.transform.localToWorldMatrix;
                var cameraObject = new GameObject("AdoptionCamera") { hideFlags = HideFlags.HideAndDontSave }; objects.Add(cameraObject); var camera = cameraObject.AddComponent<Camera>(); camera.enabled = false;
                camera.transform.SetPositionAndRotation(new Vector3(1000, 0, -2), Quaternion.identity); camera.orthographic = true; camera.orthographicSize = .5f; camera.aspect = CrtReferenceScreen.CaseAspect;
                camera.nearClipPlane = .1f; camera.farClipPlane = 4; camera.cullingMask = 1 << 1; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.clear; camera.allowHDR = false; camera.allowMSAA = false;
                var target = LensTarget(1280, 1030, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB, objects); camera.targetTexture = target;
                var adjusted = LensTarget(1280, 1030, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear, objects);
                var editorAdjustment = new Material(AssetDatabase.LoadAssetAtPath<Shader>("Assets/Art/Shaders/Streaming/Editor/CrtTuningAdjustment.shader")); objects.Add(editorAdjustment);
                editorAdjustment.SetFloat("_CenterEv", CrtSignalProfile.CenterEv); editorAdjustment.SetFloat("_CenterRadius", CrtSignalProfile.CenterRadius); editorAdjustment.SetFloat("_CenterFalloff", CrtSignalProfile.CenterFalloff);
                editorAdjustment.SetFloat("_TintStrength", CrtSignalProfile.TintStrength); editorAdjustment.SetFloat("_TintCoolness", CrtSignalProfile.TintCoolness); CompilePsf(editorAdjustment);
                var mask = new Texture2D(2, 2, TextureFormat.RGBA32, false, true) { wrapMode = TextureWrapMode.Clamp }; objects.Add(mask); mask.LoadImage(File.ReadAllBytes("tools/crt-tuner/screen-mask.png"));
                string approvedPath = Path.Combine(Path.GetDirectoryName(output)!, "gpu/A_adopted.png");
                report.approvedCaptureHash = Hash(approvedPath);
                var approved = new Texture2D(2, 2, TextureFormat.RGBA32, false, false); objects.Add(approved); approved.LoadImage(File.ReadAllBytes(approvedPath)); Color[] approvedFootprint = approved.GetPixels();
                string[] names = { "plate_A_20260924_172209_b8e1f375.jpg", "plate_B_20260924_172210_4abb5bae.jpg", "plate_C_20260924_172211_a1c7c02f.jpg" };
                var textures = new Texture2D[3]; var states = new Material[3];
                using (var processor = new CrtSignalProcessor())
                {
                    for (int plateIndex = 0; plateIndex < 3; plateIndex++)
                    {
                        string plate = ((char)('A' + plateIndex)).ToString(), source = Path.Combine(root, "tools/web-compositor/captures", names[plateIndex]);
                        var texture = new Texture2D(2, 2, TextureFormat.RGB24, true, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear }; objects.Add(texture); texture.LoadImage(File.ReadAllBytes(source)); textures[plateIndex] = texture;
                        var state = new Material(shader); objects.Add(state); states[plateIndex] = state; CrtReferenceScreen.Apply(state); ApplyPost(state, cfg.post);
                        state.SetTexture("_LiveTex", texture); state.SetVector("_LiveScale", new Vector4(.75f, 1, 0, 0)); state.SetVector("_CgScale", new Vector4(.75f, 1, 0, 0)); state.SetFloat("_FrameAspect", CrtReferenceScreen.LogicalAspect);
                        var feel = CameraFeelFx.Settings.Resolve(cfg.feel); var logic = new CameraFeelLogic { TargetLuma = feel.TargetLuma, FollowHalfLifeSec = feel.FollowSec }; float mean = 0; int count = 0;
                        for (int y = 0; y < 480; y += 30) for (int x = 0; x < 640; x += 40) { Color p = texture.GetPixel(x, y); mean += p.r * .299f + p.g * .587f + p.b * .114f; count++; }
                        float bias = logic.SteadyBiasFor(mean / count) * feel.Agc;
                        CameraFeelFx.WriteUniforms(state, feel.NoiseDark, feel.NoiseFixed, bias, 0, 0, 0, 0);
                        RenderAdoption(camera, renderer, state, target); string legacyFile = plate + "_legacy28.png"; Save(target, Path.Combine(output, legacyFile));
                        Capture expected = legacy.captures.Find(p => p.file == plate + "_front.png") ?? throw new InvalidDataException("Missing old plate");
                        if (Hash(source) != expected.sourceHash || Hash(Path.Combine(output, legacyFile)) != expected.sha256) throw new InvalidDataException("Legacy28 default-off hash changed");
                        CameraFeelFx.WriteUniforms(state, feel.NoiseDark, feel.NoiseFixed, bias, 0, ScreenDecayLogic.BlocksFor(.5f), 0, .5f); CrtSignalProfile.ApplySelected(state);
                        var web = LensMaterial(state, objects); web.SetFloat("_CrtSignalProfileSelected", 0); web.SetFloat("_CrtScreenOnly", 0); web.SetFloat("_CrtCenterEv", 0); web.SetFloat("_CrtTintStrength", 0);
                        processor.PrepareDisplay(web, plateIndex); editorAdjustment.SetVector("_Aperture", web.GetVector("_CrtAperture")); Graphics.Blit(processor.Processed, adjusted, editorAdjustment); web.SetTexture("_CrtProcessedSignalTex", adjusted);
                        RenderAdoption(camera, renderer, web, target); Color[] webPixels = ReadSignalPixels(target); SaveAdoption(target, Path.Combine(output, plate + "_web_reference.png"));
                        processor.PrepareDisplay(state, plateIndex); state.SetFloat("_CrtScreenOnly", 0); RenderAdoption(camera, renderer, state, target); Color[] fullPixels = ReadSignalPixels(target);
                        report.webRgbMaxError = Mathf.Max(report.webRgbMaxError, PsfMaxDifference(webPixels, fullPixels));
                        if (report.webRgbMaxError > .003f) throw new InvalidOperationException("Runtime center/cool differs from the single Web adjustment pass");
                        state.SetFloat("_CrtScreenOnly", 1); RenderAdoption(camera, renderer, state, target); Color[] clipped = ReadSignalPixels(target); CheckAdoptionPixels(approvedFootprint, fullPixels, clipped, report);
                        string file = plate + "_adopted.png"; SaveAdoption(target, Path.Combine(output, file));
                        if (plateIndex == 0 && Hash(Path.Combine(output, file)) != report.approvedCaptureHash) throw new InvalidOperationException("Adopted A differs from the independently approved GPU control");
                        var capture = new OpticsCapture { plate = plate, source = source, sourceHash = Hash(source), file = file, sha256 = Hash(Path.Combine(output, file)), parameters = OpticsParameters(state), width = 1280, height = 1030 }; report.photos.Add(capture);
                        File.WriteAllText(Path.Combine(output, "manifest.json"), JsonUtility.ToJson(report, true));
                        if (plateIndex == 0) Debug.Log("[CrtAdoption] A adopted PNG and provisional manifest ready -> " + output);
                    }
                    Material first = states[0];
                    foreach (float mono in new[] { ScreenDecayLogic.NormalTargetFor(1, 0, new[] { 0, 1, 2 }), ScreenDecayLogic.NormalTargetFor(1, 1, new[] { 0, 1, 2 }), ScreenDecayLogic.NormalTargetFor(1, 2, new[] { 0, 1, 2 }), 1f })
                    {
                        first.SetFloat("_Mono", mono); first.SetFloat("_CoarseBlocks", mono == 0 ? 0 : ScreenDecayLogic.BlocksFor(mono)); processor.PrepareDisplay(first, 0);
                        if (first.GetFloat("_Mono") != mono) throw new InvalidOperationException("Selected profile changed Director progress");
                        RenderAdoption(camera, renderer, first, target); SaveAdoption(target, Path.Combine(output, "A_progress_" + mono.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + ".png"));
                    }
                    first.SetFloat("_Mono", .5f); first.SetFloat("_CoarseBlocks", ScreenDecayLogic.BlocksFor(.5f)); processor.PrepareDisplay(first, 0);
                    ValidateAdoptionDepth(camera, renderer, surface, first, target, mask, output, report, objects);
                    var layer = new Texture2D(640, 480, TextureFormat.RGBA32, false, true); objects.Add(layer); var pixels = new Color[640 * 480];
                    for (int y = 150; y < 330; y++) for (int x = 230; x < 410; x++) pixels[y * 640 + x] = new Color(.05f, .6f, .05f, 1); layer.SetPixels(pixels); layer.Apply();
                    first.SetTexture("_CgTex", layer); first.SetFloat("_CgStrength", 1); first.SetTexture("_OverlayTex", layer); first.SetTexture("_MaskTex", layer); first.SetFloat("_OverlayStrength", .6f); first.SetVector("_OverlayScale", new Vector4(.75f, 1, 0, 0));
                    processor.PrepareDisplay(first, 0); first.SetFloat("_CrtScreenOnly", 0); RenderAdoption(camera, renderer, first, target); Color[] layersFull = ReadSignalPixels(target);
                    first.SetFloat("_CrtScreenOnly", 1); RenderAdoption(camera, renderer, first, target); CheckAdoptionPixels(approvedFootprint, layersFull, ReadSignalPixels(target), report); SaveAdoption(target, Path.Combine(output, "A_cg_mask.png"));
                    first.SetFloat("_CgStrength", 0); first.SetFloat("_OverlayStrength", 0); first.SetFloat("_Echo", 1); first.SetTexture("_EchoTex", textures[0]); first.SetTexture("_LiveTex", textures[1]);
                    processor.PrepareDisplay(first, 1); RenderAdoption(camera, renderer, first, target); SaveAdoption(target, Path.Combine(output, "A_frozen.png"));
                    if (!EndingFrameCapture.CaptureComposite(first, target)) throw new InvalidOperationException("Ending synchronous capture failed"); Color[] ending = ReadSignalPixels(target); SaveAdoption(target, Path.Combine(output, "A_ending_photo.png"));
                    if (!processor.Render(first, target, 1)) throw new InvalidOperationException("Common synchronous Render failed");
                    report.captureMaxError = PsfMaxDifference(ending, ReadSignalPixels(target)); if (report.captureMaxError > .003f) throw new InvalidOperationException("Ending capture differs from common Render");
                    first.SetFloat("_ScreenPower", 0); if (!EndingFrameCapture.CaptureComposite(first, target)) throw new InvalidOperationException("Power-off capture failed");
                    foreach (Color p in ReadSignalPixels(target)) if (p.r != 0 || p.g != 0 || p.b != 0) throw new InvalidOperationException("Power-off RGB is not black"); SaveAdoption(target, Path.Combine(output, "A_poweroff.png"));
                    report.meshUnchanged = surface.GetComponent<MeshFilter>().sharedMesh == mesh && surface.transform.localToWorldMatrix == transform;
                    if (!report.meshUnchanged || report.cutInteriorMaxError != 0) throw new InvalidOperationException("Screen contour/RGB/geometry contract failed");
                }
                File.WriteAllText(Path.Combine(output, "manifest.json"), JsonUtility.ToJson(report, true)); Debug.Log("[CrtAdoption] ABC adopted; legacy3/3; interiorRGB/alpha/contour/depth/Web-adjustment/Director/capture gates passed -> " + output);
            }
            finally { ShaderUtil.allowAsyncCompilation = async; RenderTexture.active = previous; for (int i = objects.Count - 1; i >= 0; i--) { if (objects[i] is RenderTexture rt) rt.Release(); UnityEngine.Object.DestroyImmediate(objects[i]); } }
        }

        private static void RenderAdoption(Camera camera, Renderer renderer, Material state, RenderTexture target)
        { CompilePsf(state); renderer.sharedMaterial = state; camera.targetTexture = target; camera.Render(); }
        private static void SaveAdoption(RenderTexture target, string path)
        {
            RenderTexture old = RenderTexture.active; var texture = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false, false);
            try { RenderTexture.active = target; texture.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); texture.Apply(); File.WriteAllBytes(path, texture.EncodeToPNG()); }
            finally { RenderTexture.active = old; UnityEngine.Object.DestroyImmediate(texture); }
        }
        private static void CheckAdoptionPixels(Color[] approved, Color[] full, Color[] clipped, AdoptionReport report)
        {
            for (int y = 0; y < 1030; y++) for (int x = 0; x < 1280; x++)
            {
                int i = y * 1280 + x; bool inside = approved[i].a == 1;
                if ((clipped[i].a > .5f) != inside) { report.maskDisagreements++; throw new InvalidOperationException("Alpha footprint differs from the independently approved GPU control"); }
                if (clipped[i].a > .5f)
                { if (clipped[i].a != 1) throw new InvalidOperationException("Interior is translucent"); report.insideOpaque++; for (int c = 0; c < 3; c++) report.cutInteriorMaxError = Mathf.Max(report.cutInteriorMaxError, Mathf.Abs(full[i][c] - clipped[i][c])); }
                else { if (clipped[i] != Color.clear) throw new InvalidOperationException("Discarded pixels wrote color/alpha"); report.outsideTransparent++; }
            }
        }
        private static void ValidateAdoptionDepth(Camera camera, Renderer renderer, GameObject surface, Material state, RenderTexture target, Texture2D mask, string output, AdoptionReport report, List<UnityEngine.Object> objects)
        {
            RenderAdoption(camera, renderer, state, target); Color[] footprint = ReadSignalPixels(target);
            var background = GameObject.CreatePrimitive(PrimitiveType.Quad); objects.Add(background); background.hideFlags = HideFlags.HideAndDontSave; background.layer = 1; background.transform.position = new Vector3(1000, 0, .2f); background.transform.localScale = surface.transform.localScale;
            var material = new Material(AssetDatabase.LoadAssetAtPath<Shader>("Assets/Art/Shaders/Streaming/Editor/CrtScreenAdoptionProbe.shader")); objects.Add(material); background.GetComponent<Renderer>().sharedMaterial = material;
            CompilePsf(material); renderer.enabled = false; camera.Render();
            foreach (Color p in ReadSignalPixels(target)) if (p.g > .99f && p.r < .001f && p.b < .001f) report.backgroundOnlyGreen++;
            renderer.enabled = true; state.SetFloat("_CrtScreenOnly", 0); RenderAdoption(camera, renderer, state, target);
            foreach (Color p in ReadSignalPixels(target)) if (p.g > .99f && p.r < .001f && p.b < .001f) report.opaqueNegativeGreen++;
            state.SetFloat("_CrtScreenOnly", 1);
            RenderAdoption(camera, renderer, state, target); Color[] result = ReadSignalPixels(target); SaveAdoption(target, Path.Combine(output, "A_depth_background.png"));
            int outsideExpected = 0;
            for (int y = 0; y < 1030; y++) for (int x = 0; x < 1280; x++)
            {
                Color p = result[y * 1280 + x]; bool green = p.g > .99f && p.r < .001f && p.b < .001f;
                bool inside = footprint[y * 1280 + x].a == 1; if (!inside) outsideExpected++;
                if (green && inside) report.backgroundLeaksInside++; if (green && !inside) report.backgroundOutside++;
            }
            background.SetActive(false);
            if (report.backgroundOnlyGreen != 1280 * 1030 || report.opaqueNegativeGreen != 0 || report.backgroundOutside != outsideExpected || report.backgroundLeaksInside != 0) throw new InvalidOperationException("Screen discard failed independent background/depth test");
        }
    }
}
