#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    public static partial class CrtReferencePreview
    {
        [Serializable] private sealed class RuntimeSignalReport
        {
            public string utc = "", unity = "", meaning = "Runtime API rendering; diagnostic values are not similarity scores. Main profile gain.85 is a prototype following0298; normal decay is current state, never fixed. Canonical UV introduces an extra surface sampling stage; legacy keyword/runtime-off hashes must remain exact. Native separable PSF is measured against the dense source-space reference before visual adoption.";
            public string showHash = "", feelJson = "", resolvedFeelJson = "";
            public PostParams post = new PostParams();
            public List<StyleFile> sources = new List<StyleFile>();
            public List<OpticsMap> maps = new List<OpticsMap>();
            public List<RuntimeSignalPhoto> photos = new List<RuntimeSignalPhoto>();
            public List<RuntimePsfCheck> psf = new List<RuntimePsfCheck>();
            public long inputUpdates, psfUpdates, processedFrames, displayBindings;
            public long peakOwnedTextureBytes;
            public string gpu = "";
            public RuntimeSignalMeters meters = new RuntimeSignalMeters();
        }
        [Serializable] private sealed class RuntimeSignalPhoto
        {
            public OpticsCapture capture = new OpticsCapture();
            public string condition = "", expectedHash = "", preGlassHash = "", processedHash = "";
            public string sceneState = "", normalApi = "";
            public float mono, blocks, coarseLod, baseLevelMaxError, powerOffMax;
            public bool exactLegacy;
        }
        [Serializable] private sealed class RuntimePsfCheck
        {
            public float blocks, lod, denseMaxError, baseLevelError;
            public PsfMeasurement dense = new PsfMeasurement(), separable = new PsfMeasurement();
        }

        [MenuItem("Tools/FixedCamVr/Diagnostics/Validate Runtime CRT Signal", priority = 274)]
        public static void RunRuntimeSignalValidation()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, "..")), output = EditorCliArgs.Get("out") ?? Path.Combine(Output, "processing-runtime-01/final-render");
            if (Directory.Exists(output) && Directory.GetFileSystemEntries(output).Length != 0) throw new IOException("Runtime validation output must be empty: " + output);
            var cfg = JsonUtility.FromJson<Config>(File.ReadAllText(Path.Combine(root, "tools/web-compositor/show.json")));
            var legacy = JsonUtility.FromJson<Manifest>(File.ReadAllText(Path.Combine(Output, "round-28/manifest.json")));
            var previous = JsonUtility.FromJson<SignalManifest>(File.ReadAllText(Path.Combine(Output, "processing-signal-02/manifest.json")));
            if (JsonUtility.ToJson(cfg.post) != JsonUtility.ToJson(legacy.post)) throw new InvalidDataException("Post changed");
            var report = new RuntimeSignalReport { utc = DateTime.UtcNow.ToString("o"), unity = Application.unityVersion, post = cfg.post,
                showHash = Hash(Path.Combine(root, "tools/web-compositor/show.json")), feelJson = cfg.feel == null ? "null" : JsonUtility.ToJson(cfg.feel), resolvedFeelJson = JsonUtility.ToJson(CameraFeelFx.Settings.Resolve(cfg.feel)) };
            foreach (string path in Directory.GetFiles(Path.Combine(root, "Assets/Scripts/Streaming"), "CrtSignal*.cs").Concat(Directory.GetFiles(Path.Combine(root, "Assets/Art/Shaders/Streaming"), "Crt*.shader")).Concat(new[] { "Assets/Art/Shaders/Streaming/ScreenComposite.shader", "Assets/Art/Shaders/Streaming/CrtCompositeShared.hlsl", "Assets/Resources/CrtSignalRuntime.asset", "Assets/Scripts/Streaming/MjpegScreen.cs", "Assets/Scripts/Streaming/EndingFrameCapture.cs", "Assets/Scripts/Streaming/ExperienceShotCapture.cs", "Assets/Scripts/Streaming/Editor/CrtRuntimeSignalValidation.cs" })) report.sources.Add(new StyleFile { path = path, sha256 = Hash(path) });
            foreach (OpticsMap map in previous.maps)
            {
                var importer = AssetImporter.GetAtPath(map.path) as TextureImporter;
                if (Hash(map.path) != map.sha256 || importer == null || EditorJsonUtility.ToJson(importer) != map.importer) throw new InvalidDataException("Map changed"); report.maps.Add(map);
            }
            Directory.CreateDirectory(output); Directory.CreateDirectory(Path.Combine(output, "calibration")); Directory.CreateDirectory(Path.Combine(output, "intermediate"));
            var objects = new List<UnityEngine.Object>(); RenderTexture old = RenderTexture.active; bool oldAsync = ShaderUtil.allowAsyncCompilation;
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                var material = new Material(CrtSignalResources.Load().Composite) { hideFlags = HideFlags.HideAndDontSave }; objects.Add(material);
                if (!CrtReferenceScreen.Apply(material)) throw new InvalidOperationException("Missing reference maps");
                foreach (Shader shader in new[] { CrtSignalResources.Load().Composite, CrtSignalResources.Load().SourcePsf, CrtSignalResources.Load().SignalFilter })
                {
                    var compiling = new Material(shader); objects.Add(compiling);
                    for (int pass = 0; pass < shader.passCount; pass++) ShaderUtil.CompilePass(compiling, pass, true);
                    foreach (var message in ShaderUtil.GetShaderMessages(shader)) if (message.severity == ShaderCompilerMessageSeverity.Error) throw new InvalidOperationException(message.message);
                }
                var screen = new GameObject("RuntimeSignalValidation") { hideFlags = HideFlags.HideAndDontSave, layer = 1 }; objects.Add(screen); screen.transform.position = new Vector3(1000, 0, 0);
                var flat = CrtScreenMesh.BuildMesh(48, 0); objects.Add(flat); var curved = CrtScreenMesh.BuildMesh(48, .055f); objects.Add(curved);
                var mesh = screen.AddComponent<MeshFilter>(); mesh.sharedMesh = flat; var renderer = screen.AddComponent<MeshRenderer>();
                var go = new GameObject("RuntimeSignalValidationCamera") { hideFlags = HideFlags.HideAndDontSave }; objects.Add(go); var camera = go.AddComponent<Camera>(); camera.enabled = false;
                camera.transform.SetPositionAndRotation(new Vector3(1000, 0, -2), Quaternion.identity); camera.orthographic = true; camera.orthographicSize = .5f;
                camera.nearClipPlane = .1f; camera.farClipPlane = 4; camera.cullingMask = 1 << 1; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black; camera.allowMSAA = false;
                var raw = LensTarget(2560, 1920, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear, objects);
                var final = LensTarget(1280, 1030, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB, objects);
                using (var processor = new CrtSignalProcessor())
                {
                    CalibrateRuntimePsf(material, processor, camera, renderer, screen, raw, output, report, objects);
                    ValidateRuntimeSignalMeters(camera, renderer, screen, curved, output, report.meters, objects);
                    string[] names = { "plate_A_20260924_172209_b8e1f375.jpg", "plate_B_20260924_172210_4abb5bae.jpg", "plate_C_20260924_172211_a1c7c02f.jpg" };
                    for (int index = 0; index < names.Length; index++)
                    {
                        string plate = ((char)('A' + index)).ToString(), source = Path.Combine(root, "tools/web-compositor/captures", names[index]);
                        Capture expected = legacy.captures.Find(p => p.file == plate + "_front.png") ?? throw new InvalidDataException("Missing legacy");
                        if (Hash(source) != expected.sourceHash) throw new InvalidDataException("Photo changed");
                        var texture = new Texture2D(2, 2, TextureFormat.RGB24, true, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear }; objects.Add(texture);
                        if (!texture.LoadImage(File.ReadAllBytes(source)) || texture.width != 640 || texture.height != 480 || texture.format != TextureFormat.RGB24) throw new InvalidDataException("Invalid original photo");
                        var state = LensMaterial(material, objects); CrtReferenceScreen.Apply(state); state.SetFloat("_CrtSignalRuntime", 0); state.SetFloat("_CrtSignalProfileSelected", 0);
                        state.SetTexture("_LiveTex", texture); state.SetVector("_LiveScale", new Vector4(.75f, 1, 0, 0)); state.SetVector("_CgScale", new Vector4(.75f, 1, 0, 0)); state.SetFloat("_FrameAspect", CrtReferenceScreen.LogicalAspect); ApplyPost(state, cfg.post);
                        var feel = CameraFeelFx.Settings.Resolve(cfg.feel); var logic = new CameraFeelLogic { TargetLuma = feel.TargetLuma, FollowHalfLifeSec = feel.FollowSec }; float mean = 0; int count = 0;
                        for (int y = 0; y < texture.height; y += texture.height / 16) for (int x = 0; x < texture.width; x += texture.width / 16) { Color p = texture.GetPixel(x, y); mean += p.r * .299f + p.g * .587f + p.b * .114f; count++; }
                        float bias = logic.SteadyBiasFor(mean / count) * feel.Agc;
                        CameraFeelFx.WriteUniforms(state, feel.NoiseDark, feel.NoiseFixed, bias, 0, 0, 0, 0);
                        mesh.sharedMesh = curved; screen.transform.localScale = new Vector3(CrtReferenceScreen.CaseAspect, 1, 1); camera.aspect = CrtReferenceScreen.CaseAspect; camera.allowHDR = false; camera.targetTexture = final;
                        CompilePsf(state); renderer.sharedMaterial = state; camera.Render();
                        RuntimeSignalPhoto oldPhoto = RuntimeSave(state, final, output, plate, "legacy28", source, objects); oldPhoto.expectedHash = expected.sha256; oldPhoto.exactLegacy = oldPhoto.capture.sha256 == expected.sha256;
                        if (!oldPhoto.exactLegacy) throw new InvalidDataException("Default-off legacy28 hash differs: " + plate); report.photos.Add(oldPhoto);
                        CrtSignalProfile.ApplySelected(state);
                        float firstB = ScreenDecayLogic.NormalTargetFor(1, 1, new[] { 0, 1, 2 }), firstC = ScreenDecayLogic.NormalTargetFor(1, 2, new[] { 0, 1, 2 });
                        foreach (float mono in new[] { 0f, .5f, 1f, firstB, firstC })
                        {
                            float blocks = mono == 0 ? 0 : ScreenDecayLogic.BlocksFor(mono);
                            CameraFeelFx.WriteUniforms(state, feel.NoiseDark, feel.NoiseFixed, bias, 0, blocks, 0, mono); CrtSignalProfile.ApplySelected(state);
                            processor.PrepareDisplay(state, index);
                            CompilePsf(state);
                            Color[] decoded = ReadSignalPixels(processor.LinearSource!); Color[] originalPixels = texture.GetPixels();
                            for (int i = 0; i < originalPixels.Length; i++) originalPixels[i] = originalPixels[i].linear;
                            float decodeError = PsfMaxDifference(decoded, originalPixels); if (decodeError > .003f) throw new InvalidDataException("Hardware source decode gate failed");
                            renderer.sharedMaterial = state; camera.targetTexture = final; camera.Render();
                            string condition = mono == firstB ? "runtime_normal_Bfirst" : mono == firstC ? "runtime_normal_Cfirst" : "runtime_mono" + mono.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
                            var photo = RuntimeSave(state, final, output, plate, condition, source, objects); photo.baseLevelMaxError = decodeError;
                            photo.sceneState = mono == firstB ? "First lap B, course A->B->C; same stage crosschecked on plate " + plate
                                : mono == firstC ? "First lap C, course A->B->C; same stage crosschecked on plate " + plate
                                : mono == 0 ? "First lap A / clear normal start" : mono == .5f ? "Second and later lap normal maximum" : "Doll endpoint diagnostic, not normal human state";
                            photo.normalApi = mono == firstB ? "NormalTargetFor(1,1,[0,1,2]); BlocksFor(result)" : mono == firstC ? "NormalTargetFor(1,2,[0,1,2]); BlocksFor(result)"
                                : mono == 0 ? "NormalTargetFor(1,0,[0,1,2]); off means blocks0" : mono == .5f ? "NormalTargetFor(2,camera,[0,1,2]); BlocksFor(result)" : "BlocksFor(1), endpoint probe only";
                            string pre = Path.Combine(output, "intermediate/" + photo.capture.file); Save(processor.PreGlass!, pre); photo.preGlassHash = Hash(pre);
                            string processed = Path.Combine(output, "intermediate/processed_" + photo.capture.file); Save(processor.Processed!, processed); photo.processedHash = Hash(processed); report.photos.Add(photo);
                        }
                        if (index == 0) RuntimeScenarios(state, processor, camera, renderer, final, output, source, report, objects);
                    }
                    report.inputUpdates = processor.InputUpdates; report.psfUpdates = processor.PsfUpdates; report.processedFrames = processor.ProcessedFrames; report.displayBindings = processor.DisplayBindings;
                    report.peakOwnedTextureBytes = processor.PeakOwnedTextureBytes; report.gpu = SystemInfo.graphicsDeviceName + "/" + SystemInfo.graphicsDeviceType;
                }
                ValidateRuntimeLifecycle(output, report.meters, objects);
                foreach (RuntimeSignalPhoto p in report.photos) if (Hash(Path.Combine(output, p.capture.file)) != p.capture.sha256) throw new InvalidDataException("Capture changed after save");
                File.WriteAllText(Path.Combine(output, "manifest.json"), JsonUtility.ToJson(report, true));
                Debug.Log("[CrtRuntime] Runtime API photos=" + report.photos.Count + "; legacy28=3/3 exact; input=" + report.inputUpdates + " psf=" + report.psfUpdates + " processed=" + report.processedFrames + " display=" + report.displayBindings + "; shader errors=0 -> " + output);
            }
            finally { ShaderUtil.allowAsyncCompilation = oldAsync; RenderTexture.active = old; for (int i = objects.Count - 1; i >= 0; i--) { if (objects[i] is RenderTexture rt) rt.Release(); UnityEngine.Object.DestroyImmediate(objects[i]); } }
        }

        private static RuntimeSignalPhoto RuntimeSave(Material state, RenderTexture target, string output, string plate, string condition, string source, List<UnityEngine.Object> objects)
        {
            string file = plate + "_" + condition + ".png"; Save(target, Path.Combine(output, file));
            var capture = new OpticsCapture { plate = plate, file = file, source = source, sourceHash = Hash(source), sha256 = Hash(Path.Combine(output, file)), parameters = OpticsParameters(state), width = target.width, height = target.height };
            if (condition != "poweroff") ValidateOpticsImage(Path.Combine(output, file), capture, objects);
            return new RuntimeSignalPhoto { capture = capture, condition = condition, mono = state.GetFloat("_Mono"), blocks = state.GetFloat("_CoarseBlocks"), coarseLod = CrtSignalProcessor.CoarseLod(state, state.GetTexture("_LiveTex").width) };
        }

        private static void CalibrateRuntimePsf(Material original, CrtSignalProcessor processor, Camera camera, Renderer renderer, GameObject screen, RenderTexture raw, string output, RuntimeSignalReport report, List<UnityEngine.Object> objects)
        {
            var points = PsfPattern(false); objects.Add(points); var linear = LinearPsfTexture(points); objects.Add(linear);
            var state = LensMaterial(original, objects); state.SetTexture("_LiveTex", points); state.SetVector("_LiveScale", new Vector4(.75f, 1, 0, 0)); CrtSignalProfile.ApplySelected(state);
            screen.transform.localScale = new Vector3(4f / 3f, 1, 1); camera.aspect = 4f / 3f; camera.allowHDR = true; camera.targetTexture = raw;
            foreach (float blocks in new[] { 0f, 853f, 533.5f, 428f, 426f, 425f, 267f })
            {
                state.SetFloat("_CoarseBlocks", blocks); processor.PrepareDisplay(state, 0); camera.targetTexture = raw;
                Color[] actual = RuntimePointProbe(state, points, camera, renderer, raw, objects); Save(raw, Path.Combine(output, "calibration/points_runtime_" + blocks + ".png"));
                var reference = LensMaterial(state, objects); reference.SetFloat("_CrtSignalRuntime", 0); reference.SetFloat("_CrtPsfRespectDecayLod", 1); reference.SetTexture("_CrtPsfLinearTex", linear);
                Color[] dense = RuntimePointProbe(reference, points, camera, renderer, raw, objects); Save(raw, Path.Combine(output, "calibration/points_dense_" + blocks + ".png"));
                var check = new RuntimePsfCheck { blocks = blocks, lod = CrtSignalProcessor.CoarseLod(state, points.width), denseMaxError = PsfMaxDifference(dense, actual), dense = MeasurePsfPoints(dense, CrtSignalProfile.Sigma), separable = MeasurePsfPoints(actual, CrtSignalProfile.Sigma) };
                report.psf.Add(check);
                File.WriteAllText(Path.Combine(output, "calibration/psf-measurements.json"), JsonUtility.ToJson(report, true));
                for (int c = 0; c < 3; c++) if (Mathf.Abs(check.separable.energy[c] - 1) > .01f) throw new InvalidOperationException("Runtime PSF energy is not normalized");
                // Same .003 linear precision gate as source calibration; widths stay within2%.
                // Sixteen points cover all4x4 mip phases, and the4x readback covers subpixel positions.
                if (check.denseMaxError > .003f || Vector3.Distance(check.separable.varianceX, check.dense.varianceX) / check.dense.varianceX.magnitude > .02f
                    || Vector3.Distance(check.separable.varianceY, check.dense.varianceY) / check.dense.varianceY.magnitude > .02f)
                    throw new InvalidOperationException("Runtime PSF differs from dense reference: " + JsonUtility.ToJson(check));
            }
        }

        private static Color[] RuntimePointProbe(Material state, Texture points, Camera camera, Renderer renderer, RenderTexture raw, List<UnityEngine.Object> objects)
        {
            var probe = new Material(AssetDatabase.LoadAssetAtPath<Shader>("Assets/Art/Shaders/Streaming/CrtRuntimePsfReference.shader")); objects.Add(probe);
            probe.CopyPropertiesFromMaterial(state); probe.SetTexture("_LiveTex", points); probe.SetFloat("_CrtPsfMode", 2); probe.SetFloat("_CrtPsfStage", 1); probe.SetVector("_CrtPsfSigma", CrtSignalProfile.Sigma);
            CompilePsf(probe); renderer.sharedMaterial = probe; camera.targetTexture = raw; camera.Render(); return ReadSignalPixels(raw);
        }

        private static void RuntimeScenarios(Material baseline, CrtSignalProcessor processor, Camera camera, Renderer renderer, RenderTexture final, string output, string source, RuntimeSignalReport report, List<UnityEngine.Object> objects)
        {
            foreach (string condition in new[] { "record_frame1", "record_frame2", "freeze", "cg_mask", "noSignal", "intro", "ending", "poweroff", "blackCapture" })
            {
                var state = LensMaterial(baseline, objects); state.SetFloat("_Mono", 0); state.SetFloat("_CoarseBlocks", 0);
                if (condition.StartsWith("record", StringComparison.Ordinal)) state.SetFloat("_SrcFrame", condition.EndsWith("1", StringComparison.Ordinal) ? 1 : 2);
                if (condition == "freeze") { state.SetTexture("_EchoTex", baseline.GetTexture("_LiveTex")); state.SetFloat("_Echo", .65f); }
                if (condition == "cg_mask")
                {
                    var layer = new Texture2D(640, 480, TextureFormat.RGBA32, false, true); objects.Add(layer); var pixels = new Color[640 * 480];
                    for (int y = 160; y < 320; y++) for (int x = 240; x < 400; x++) pixels[y * 640 + x] = new Color(.02f, .5f, .02f, 1); layer.SetPixels(pixels); layer.Apply();
                    state.SetTexture("_CgTex", layer); state.SetFloat("_CgStrength", 1); state.SetTexture("_OverlayTex", layer); state.SetTexture("_MaskTex", layer); state.SetFloat("_OverlayStrength", .5f); state.SetVector("_OverlayScale", new Vector4(.75f, 1, 0, 0));
                }
                if (condition == "noSignal") { state.SetFloat("_SignalLost", 1); state.SetFloat("_SignalFloor", 1); }
                if (condition == "intro") { state.SetFloat("_IntroLive", .3f); state.SetFloat("_CrtIgnite", .6f); }
                if (condition == "ending") { state.SetFloat("_ScreenNoise", .6f); state.SetFloat("_ScreenCollapse", .3f); }
                if (condition == "poweroff") state.SetFloat("_ScreenPower", 0);
                if (condition == "blackCapture")
                {
                    if (!ExperienceShotCapture.CaptureCompositeWithoutLive(state, final)) throw new InvalidOperationException("Black capture failed");
                }
                else
                {
                    if (!processor.Render(state, final)) throw new InvalidOperationException("Synchronous runtime capture failed");
                    renderer.sharedMaterial = state; camera.targetTexture = final; camera.Render();
                }
                var photo = RuntimeSave(state, final, output, "A", condition, source, objects);
                if (condition == "poweroff") { foreach (Color p in ReadSignalPixels(final)) photo.powerOffMax = Mathf.Max(photo.powerOffMax, p.r, p.g, p.b); if (photo.powerOffMax != 0) throw new InvalidOperationException("Power-off not black"); }
                report.photos.Add(photo);
            }
        }
    }
}
