#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    public static partial class CrtReferencePreview
    {
        [Serializable] private sealed class HumanStackManifest
        {
            public string utc = "", unity = "", shaderHash = "", filterShaderHash = "", showHash = "", runtimeProfileHash = "";
            public string meaning = "0299: normal human-state ScreenDecay is added once before shared preGlass capture. Post/feel/AGC already exist and are not reapplied. Runtime NormalTargetFor(2,camera,[0,1,2]) supplies representative normal maximum .5; BlocksFor supplies533.5 (shader floor533). This representative stage is an interpretation, not a user-selected number. Mono's existing reference/Grade/contrast/noise/convergence coupling remains active. Doll mono1 is not used. Gaussian source offsets/sigma remain unchanged; diagnostic flag adds only CoarseLod linear mip, never optical referenceLod. Physical case/reflection/OSD/power remain shared. No runtime adoption.";
            public List<StyleFile> helpers = new List<StyleFile>(), priorManifests = new List<StyleFile>();
            public List<OpticsMap> maps = new List<OpticsMap>();
            public SignalCalibration signalCalibration = new SignalCalibration();
            public HumanDecayCalibration decayCalibration = new HumanDecayCalibration();
            public List<HumanStackPhoto> photos = new List<HumanStackPhoto>();
        }
        [Serializable] private sealed class HumanStackPhoto
        {
            public OpticsCapture capture = new OpticsCapture();
            public string condition = "", mainKeywords = "", stage1Parameters = "", filterParameters = "", expectedHash = "", preGlassFile = "", preGlassHash = "", filteredFile = "", filteredHash = "";
            public string[] mainDifferences = Array.Empty<string>(), filterDifferences = Array.Empty<string>();
            public float normalProgress, coarseBlocks, gain, powerOffMax, baseLevelMaxError;
            public int preGlassRenderCount;
            public bool baselineMatches;
        }
        [Serializable] private sealed class HumanDecayCalibration
        {
            public float normalProgress, coarseBlocks, shaderBlocks, coarseLod, constantMaxError, zeroBlocksIdentityError, inputAmplitude, flag0Amplitude, flag1Amplitude, amplitudeRatio, dcError;
            public int negativeControlsRejected;
            public List<StyleFile> images = new List<StyleFile>();
        }

        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview CRT Human Stack Matrix", priority = 273)]
        public static void RunHumanStackMatrix()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, "..")), output = EditorCliArgs.Get("out") ?? Path.Combine(Output, "processing-human-stack-01");
            if (Directory.Exists(output) && Directory.GetFileSystemEntries(output).Length != 0) throw new IOException("Human-stack output must be empty: " + output);
            if (!File.ReadAllText(Path.Combine(root, ".claude/canon/ROUNDS.md")).Contains("人形化前の通常劣化を重ねる")) throw new InvalidOperationException("Missing pre-render prediction");
            string[] priorFiles = { Path.Combine(Output, "processing-signal-01/manifest.json"), Path.Combine(Output, "processing-signal-02/manifest.json") };
            SignalManifest[] prior = priorFiles.Select(p => JsonUtility.FromJson<SignalManifest>(File.ReadAllText(p))).ToArray();
            if (prior[0].photos.Count != 12 || prior[1].photos.Count != 9) throw new InvalidDataException("Missing signal baselines");
            for (int i = 0; i < prior.Length; i++) foreach (SignalPhoto p in prior[i].photos)
                if (Hash(Path.Combine(Path.GetDirectoryName(priorFiles[i])!, p.capture.file)) != p.capture.sha256) throw new InvalidDataException("Prior photo changed");
            var cfg = JsonUtility.FromJson<Config>(File.ReadAllText(Path.Combine(root, "tools/web-compositor/show.json")));
            var manifest = new HumanStackManifest { utc = DateTime.UtcNow.ToString("o"), unity = Application.unityVersion,
                shaderHash = Hash(Path.Combine(root, "Assets/Art/Shaders/Streaming/ScreenComposite.shader")), filterShaderHash = Hash(Path.Combine(root, "Assets/Art/Shaders/Streaming/CrtSignalFilter.shader")),
                showHash = Hash(Path.Combine(root, "tools/web-compositor/show.json")), runtimeProfileHash = Hash(Path.Combine(root, "Assets/Scripts/Streaming/CrtReferenceScreen.cs")) };
            if (manifest.showHash != prior[0].showHash || manifest.runtimeProfileHash != prior[0].runtimeProfileHash || manifest.filterShaderHash != prior[0].filterShaderHash) throw new InvalidDataException("Fixed source/pipeline changed");
            foreach (StyleFile helper in prior[1].helpers) if (Hash(helper.path) != helper.sha256) throw new InvalidDataException("Existing helper changed: " + helper.path);
            foreach (string path in Directory.GetFiles(Path.Combine(root, "Assets/Scripts/Streaming/Editor"), "CrtReference*.cs").OrderBy(p => p, StringComparer.Ordinal)) manifest.helpers.Add(new StyleFile { path = path, sha256 = Hash(path) });
            foreach (string path in priorFiles) manifest.priorManifests.Add(new StyleFile { path = path, sha256 = Hash(path) });
            foreach (OpticsMap map in prior[0].maps)
            {
                var importer = AssetImporter.GetAtPath(map.path) as TextureImporter;
                if (Hash(map.path) != map.sha256 || importer == null || EditorJsonUtility.ToJson(importer) != map.importer) throw new InvalidDataException("Map/importer changed"); manifest.maps.Add(map);
            }
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Art/Shaders/Streaming/ScreenComposite.shader"), filterShader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Art/Shaders/Streaming/CrtSignalFilter.shader");
            if (shader == null || filterShader == null || !shader.isSupported || !filterShader.isSupported) throw new InvalidOperationException("Unsupported shaders");
            Directory.CreateDirectory(output); Directory.CreateDirectory(Path.Combine(output, "calibration")); Directory.CreateDirectory(Path.Combine(output, "intermediate"));
            var objects = new List<UnityEngine.Object>(); RenderTexture old = RenderTexture.active; bool oldAsync = ShaderUtil.allowAsyncCompilation;
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                var profile = new Material(shader) { hideFlags = HideFlags.HideAndDontSave }; objects.Add(profile);
                if (!CrtReferenceScreen.Apply(profile)) throw new InvalidOperationException("Missing CRT maps");
                var screen = new GameObject("HumanStackScreen") { hideFlags = HideFlags.HideAndDontSave, layer = 1 }; objects.Add(screen); screen.transform.position = new Vector3(1000, 0, 0);
                var flat = CrtScreenMesh.BuildMesh(48, 0); objects.Add(flat); var curved = CrtScreenMesh.BuildMesh(48, .055f); objects.Add(curved);
                var mesh = screen.AddComponent<MeshFilter>(); mesh.sharedMesh = flat; var renderer = screen.AddComponent<MeshRenderer>();
                var go = new GameObject("HumanStackCamera") { hideFlags = HideFlags.HideAndDontSave }; objects.Add(go); var camera = go.AddComponent<Camera>(); camera.enabled = false;
                camera.transform.SetPositionAndRotation(new Vector3(1000, 0, -2), Quaternion.identity); camera.orthographic = true; camera.orthographicSize = .5f;
                camera.nearClipPlane = .1f; camera.farClipPlane = 4; camera.cullingMask = 1 << 1; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black; camera.allowMSAA = false; camera.allowHDR = true;
                var filter = new Material(filterShader) { hideFlags = HideFlags.HideAndDontSave }; objects.Add(filter);
                CalibrateSignal(shader, filter, camera, renderer, output, manifest.signalCalibration, objects, true);
                CalibrateHumanDecay(profile, camera, renderer, screen, output, manifest.decayCalibration, objects);
                var raw = LensTarget(2560, 1920, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear, objects);
                var preGlass = LensTarget(1280, 1030, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear, objects);
                var filtered = LensTarget(1280, 1030, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear, objects);
                var final = LensTarget(1280, 1030, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB, objects);
                string[] names = { "plate_A_20260924_172209_b8e1f375.jpg", "plate_B_20260924_172210_4abb5bae.jpg", "plate_C_20260924_172211_a1c7c02f.jpg" };
                for (int plateIndex = 0; plateIndex < names.Length; plateIndex++)
                {
                    string plate = ((char)('A' + plateIndex)).ToString(), source = Path.Combine(root, "tools/web-compositor/captures", names[plateIndex]);
                    SignalPhoto expected090 = prior[0].photos.Find(p => p.capture.plate == plate && p.condition == "both") ?? throw new InvalidDataException("Missing old both");
                    SignalPhoto expected085 = prior[1].photos.Find(p => p.capture.plate == plate && p.condition == "gain085") ?? throw new InvalidDataException("Missing old gain085");
                    if (Hash(source) != expected090.capture.sourceHash || Hash(source) != expected085.capture.sourceHash) throw new InvalidDataException("Photo source changed");
                    var texture = new Texture2D(2, 2, TextureFormat.RGB24, true, false) { filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp }; objects.Add(texture);
                    if (!texture.LoadImage(File.ReadAllBytes(source)) || texture.width != 640 || texture.height != 480 || texture.format != TextureFormat.RGB24) throw new InvalidDataException("Expected640x480 RGB24 photo");
                    var linear = LinearPsfTexture(texture); objects.Add(linear); profile.SetTexture("_CrtPsfLinearTex", linear);
                    mesh.sharedMesh = flat; screen.transform.localScale = new Vector3(4f / 3f, 1, 1); camera.aspect = 4f / 3f; camera.allowHDR = true; camera.targetTexture = raw;
                    Color[] original = RenderPsfRaw(profile, texture, Vector3.zero, Vector3.zero, camera, renderer, raw, objects, 0, Vector3.zero);
                    float baseError = PsfMaxDifference(original, RenderPsfRaw(profile, texture, Vector3.zero, Vector3.zero, camera, renderer, raw, objects, 1, Vector3.zero));
                    if (baseError > .003f) throw new InvalidDataException("Linear photo decode changed");
                    profile.SetTexture("_LiveTex", texture); profile.SetVector("_LiveScale", new Vector4(.75f, 1, 0, 0)); profile.SetVector("_CgScale", new Vector4(.75f, 1, 0, 0));
                    profile.SetFloat("_FrameAspect", CrtReferenceScreen.LogicalAspect); profile.SetVector("_CrtView", new Vector4(0, 0, -1, 0)); ApplyPost(profile, cfg.post);
                    var feel = CameraFeelFx.Settings.Resolve(cfg.feel); var logic = new CameraFeelLogic { TargetLuma = feel.TargetLuma, FollowHalfLifeSec = feel.FollowSec }; float mean = 0; int count = 0;
                    for (int y = 0; y < texture.height; y += texture.height / 16) for (int x = 0; x < texture.width; x += texture.width / 16)
                    { Color p = texture.GetPixel(x, y); mean += p.r * .299f + p.g * .587f + p.b * .114f; count++; }
                    float bias = logic.SteadyBiasFor(mean / count) * feel.Agc;
                    CameraFeelFx.WriteUniforms(profile, feel.NoiseDark, feel.NoiseFixed, bias, 0, 0, 0, 0);
                    var standard = LensMaterial(profile, objects); standard.EnableKeyword("CRT_PSF_DIAGNOSTIC"); standard.EnableKeyword("CRT_STYLE_DIAGNOSTIC"); standard.EnableKeyword("CRT_SIGNAL_DIAGNOSTIC");
                    standard.SetFloat("_CrtPsfMode", 2); standard.SetVector("_CrtPsfSigma", new Vector3(.85f, .65f, .65f)); standard.SetVector("_CrtOpticalConvergence", CrtReferenceScreen.OpticalConvergence * 1.8f);
                    Vector4 adjustment = CrtReferenceScreen.ImageAdjustment; adjustment.w = -.32f; standard.SetVector("_CrtImageAdjustment", adjustment); standard.SetFloat("_Glare", 0); standard.SetFloat("_CrtLensWarp", .06f);
                    standard.SetFloat("_CrtSignalStage", 2); standard.SetTexture("_CrtSignalTex", filtered);
                    if (WithoutHumanFlag(OpticsParameters(standard)) != expected090.capture.parameters || WithoutHumanFlag(OpticsParameters(standard)) != expected085.capture.parameters) throw new InvalidDataException("Base main snapshot changed");
                    string keys = HumanKeywords(standard); if (keys != expected090.mainKeywords || keys != expected085.mainKeywords) throw new InvalidDataException("Base keywords changed");
                    for (int stack = 0; stack < 2; stack++)
                    {
                        float progress = stack == 0 ? 0 : ScreenDecayLogic.NormalTargetFor(2, plateIndex, new[] { 0, 1, 2 });
                        float blocks = stack == 0 ? 0 : ScreenDecayLogic.BlocksFor(progress);
                        if (stack == 1 && (progress != .5f || blocks != 533.5f)) throw new InvalidOperationException("Unexpected runtime normal maximum");
                        var stage = LensMaterial(standard, objects);
                        if (stack == 1)
                        {
                            CameraFeelFx.WriteUniforms(stage, feel.NoiseDark, feel.NoiseFixed, bias, 0, blocks, 0, progress);
                            stage.SetFloat("_Glare", standard.GetFloat("_Glare")); stage.SetFloat("_CrtPsfRespectDecayLod", 1);
                        }
                        string[] differences = HumanDifferences(OpticsParameters(standard), OpticsParameters(stage), new[] { "_Mono", "_CoarseBlocks", "_CrtPsfRespectDecayLod" });
                        if (differences.Length != (stack == 0 ? 0 : 3) || HumanKeywords(stage) != keys) throw new InvalidOperationException("Uncontrolled human stack properties");
                        stage.SetFloat("_CrtSignalStage", 1); CompilePsf(stage); mesh.sharedMesh = curved; screen.transform.localScale = new Vector3(CrtReferenceScreen.CaseAspect, 1, 1); camera.aspect = CrtReferenceScreen.CaseAspect;
                        camera.allowHDR = false; camera.targetTexture = preGlass; renderer.sharedMaterial = stage; camera.Render();
                        string prefix = plate + (stack == 0 ? "_base" : "_stack"), preFile = "intermediate/" + prefix + "_preGlass.png"; Save(preGlass, Path.Combine(output, preFile));
                        Color[] prePixels = ReadSignalPixels(preGlass);
                        filter.SetTexture("_SignalSource", preGlass); filter.SetFloat("_Bandwidth", 0); filter.SetFloat("_Gain", 1); mesh.sharedMesh = flat; camera.allowHDR = true;
                        if (PsfMaxDifference(prePixels, RenderSignalFilter(filter, camera, renderer, filtered)) != 0) throw new InvalidOperationException("Float filter identity failed");
                        var last = LensMaterial(stage, objects); last.SetFloat("_CrtSignalStage", 2); last.SetTexture("_CrtSignalTex", filtered);
                        for (int gainIndex = 0; gainIndex < 2; gainIndex++)
                        {
                            float gain = gainIndex == 0 ? .90f : .85f; SignalPhoto expected = gainIndex == 0 ? expected090 : expected085;
                            var processing = LensMaterial(filter, objects); processing.SetFloat("_Bandwidth", 1); processing.SetFloat("_Gain", gain);
                            if (OpticsParameters(processing) != expected.filterParameters) throw new InvalidDataException("Filter properties changed");
                            string[] filterDifferences = HumanDifferences(expected090.filterParameters, OpticsParameters(processing), new[] { "_Gain" });
                            mesh.sharedMesh = flat; camera.allowHDR = true; RenderSignalFilter(processing, camera, renderer, filtered);
                            string condition = (stack == 0 ? "base" : "stack") + (gainIndex == 0 ? "090" : "085"), filteredFile = "intermediate/" + plate + "_" + condition + "_filtered.png"; Save(filtered, Path.Combine(output, filteredFile));
                            mesh.sharedMesh = curved; camera.allowHDR = false; camera.targetTexture = final; CompilePsf(last); renderer.sharedMaterial = last; camera.Render();
                            string file = plate + "_" + condition + ".png"; Save(final, Path.Combine(output, file));
                            var capture = new OpticsCapture { plate = plate, source = source, sourceHash = Hash(source), file = file, sha256 = Hash(Path.Combine(output, file)), parameters = OpticsParameters(last), invariantHash = ParameterHash(last, "_Mono", "_CoarseBlocks", "_CrtPsfRespectDecayLod"), width = 1280, height = 1030 };
                            ValidateOpticsImage(Path.Combine(output, file), capture, objects);
                            var photo = new HumanStackPhoto { capture = capture, condition = condition, normalProgress = progress, coarseBlocks = blocks, gain = gain, baseLevelMaxError = baseError, preGlassRenderCount = 1,
                                mainKeywords = HumanKeywords(last), stage1Parameters = OpticsParameters(stage), filterParameters = OpticsParameters(processing), mainDifferences = differences, filterDifferences = filterDifferences,
                                preGlassFile = preFile, preGlassHash = Hash(Path.Combine(output, preFile)), filteredFile = filteredFile, filteredHash = Hash(Path.Combine(output, filteredFile)) };
                            if (stack == 0) { photo.expectedHash = expected.capture.sha256; photo.baselineMatches = capture.sha256 == photo.expectedHash; if (!photo.baselineMatches) throw new InvalidDataException("Baseline hash differs: " + file); }
                            var power = LensMaterial(last, objects); power.SetFloat("_ScreenPower", 0); CompilePsf(power); renderer.sharedMaterial = power; camera.Render();
                            foreach (Color p in ReadSignalPixels(final)) photo.powerOffMax = Mathf.Max(photo.powerOffMax, p.r, p.g, p.b);
                            if (photo.powerOffMax != 0) throw new InvalidOperationException("Power-off is not black"); manifest.photos.Add(photo);
                        }
                    }
                }
                if (manifest.photos.Count != 12 || manifest.photos.Count(p => p.baselineMatches) != 6) throw new InvalidDataException("Missing photos/controls");
                foreach (HumanStackPhoto p in manifest.photos) if (Hash(Path.Combine(output, p.capture.file)) != p.capture.sha256 || Hash(Path.Combine(output, p.preGlassFile)) != p.preGlassHash || Hash(Path.Combine(output, p.filteredFile)) != p.filteredHash) throw new InvalidDataException("Output changed after capture");
                foreach (string path in Directory.GetFiles(Path.Combine(output, "calibration"), "*.png")) manifest.decayCalibration.images.Add(new StyleFile { path = path, sha256 = Hash(path) });
                File.WriteAllText(Path.Combine(output, "calibration.json"), JsonUtility.ToJson(manifest.decayCalibration, true));
                File.WriteAllText(Path.Combine(output, "manifest.json"), JsonUtility.ToJson(manifest, true));
                Debug.Log("[CrtHumanStack] 12 photos; prior bases6/6 exact; normal.5/533.5 from runtime; only Mono/Blocks/decayPsfFlag; GPU DC/attenuation/identity gates passed; shader errors=0 -> " + output);
            }
            finally { ShaderUtil.allowAsyncCompilation = oldAsync; RenderTexture.active = old; for (int i = objects.Count - 1; i >= 0; i--) { if (objects[i] is RenderTexture rt) rt.Release(); UnityEngine.Object.DestroyImmediate(objects[i]); } }
        }

        private static string WithoutHumanFlag(string snapshot) => string.Join("\n", snapshot.Split('\n').Where(l => !l.StartsWith("_CrtPsfRespectDecayLod=", StringComparison.Ordinal)));
        private static string HumanKeywords(Material material) { string[] keys = material.shaderKeywords; Array.Sort(keys, StringComparer.Ordinal); return string.Join(";", keys); }
        private static string[] HumanDifferences(string before, string after, string[] allowed)
        {
            string[] a = before.Split('\n'), b = after.Split('\n'); if (a.Length != b.Length) throw new InvalidOperationException("Snapshot schema differs");
            var result = new List<string>();
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i])
            { if (!allowed.Any(p => b[i].StartsWith(p + "=", StringComparison.Ordinal))) throw new InvalidOperationException("Uncontrolled property: " + a[i] + " -> " + b[i]); result.Add(a[i] + " -> " + b[i]); }
            return result.ToArray();
        }

        private static void CalibrateHumanDecay(Material profile, Camera camera, Renderer renderer, GameObject screen, string output, HumanDecayCalibration result, List<UnityEngine.Object> objects)
        {
            const int width = 640, height = 480;
            result.normalProgress = ScreenDecayLogic.NormalTargetFor(2, 0, new[] { 0, 1, 2 }); result.coarseBlocks = ScreenDecayLogic.BlocksFor(result.normalProgress); result.shaderBlocks = Mathf.Floor(result.coarseBlocks);
            result.coarseLod = Mathf.Log(width / (result.shaderBlocks * .75f), 2);
            if (result.normalProgress != .5f || result.coarseBlocks != 533.5f) throw new InvalidOperationException("Unexpected normal stage");
            var target = LensTarget(width, height, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear, objects);
            screen.transform.localScale = new Vector3(4f / 3f, 1, 1); camera.aspect = 4f / 3f; camera.allowHDR = true; camera.targetTexture = target;
            var material = LensMaterial(profile, objects); material.SetVector("_LiveScale", new Vector4(.75f, 1, 0, 0)); material.SetFloat("_CoarseBlocks", result.coarseBlocks);
            Vector3 sigma = new Vector3(.85f, .65f, .65f);
            for (int pattern = 0; pattern < 2; pattern++)
            {
                var texture = new Texture2D(width, height, TextureFormat.RGBAFloat, true, true) { filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp }; objects.Add(texture);
                var pixels = new Color[width * height]; for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) pixels[y * width + x] = pattern == 0 ? new Color(.17f, .38f, .63f, 1) : new Color(x % 4 < 2 ? .8f : .2f, x % 4 < 2 ? .8f : .2f, x % 4 < 2 ? .8f : .2f, 1);
                texture.SetPixels(pixels); texture.Apply(true); material.SetTexture("_CrtPsfLinearTex", texture); material.SetFloat("_CrtPsfRespectDecayLod", 0);
                Color[] flag0 = RenderPsfRaw(material, texture, sigma, Vector3.zero, camera, renderer, target, objects, 2); Save(target, Path.Combine(output, "calibration/decay" + pattern + "_flag0.png"));
                material.SetFloat("_CrtPsfRespectDecayLod", 1); Color[] flag1 = RenderPsfRaw(material, texture, sigma, Vector3.zero, camera, renderer, target, objects, 2); Save(target, Path.Combine(output, "calibration/decay" + pattern + "_flag1.png"));
                material.SetFloat("_CoarseBlocks", 0); Color[] zero = RenderPsfRaw(material, texture, sigma, Vector3.zero, camera, renderer, target, objects, 2); material.SetFloat("_CoarseBlocks", result.coarseBlocks);
                result.zeroBlocksIdentityError = Mathf.Max(result.zeroBlocksIdentityError, PsfMaxDifference(flag0, zero));
                if (pattern == 0) result.constantMaxError = Mathf.Max(PsfMaxDifference(pixels, flag0), PsfMaxDifference(pixels, flag1));
                else
                {
                    double energyIn = 0, energy0 = 0, energy1 = 0, dc0 = 0, dc1 = 0; int count = 0;
                    for (int y = 16; y < height - 16; y++) for (int x = 16; x < width - 16; x++)
                    { int i = y * width + x; energyIn += Math.Pow(pixels[i].r - .5, 2); energy0 += Math.Pow(flag0[i].r - .5, 2); energy1 += Math.Pow(flag1[i].r - .5, 2); dc0 += flag0[i].r; dc1 += flag1[i].r; count++; }
                    result.inputAmplitude = (float)Math.Sqrt(energyIn / count); result.flag0Amplitude = (float)Math.Sqrt(energy0 / count); result.flag1Amplitude = (float)Math.Sqrt(energy1 / count);
                    result.amplitudeRatio = result.flag1Amplitude / result.flag0Amplitude; result.dcError = (float)Math.Abs((dc0 - dc1) / count);
                }
            }
            if (result.constantMaxError > .001f || result.zeroBlocksIdentityError != 0 || result.dcError > .001f || result.flag0Amplitude < .05f || result.amplitudeRatio > .95f || result.amplitudeRatio <= 0)
                throw new InvalidOperationException("Decay PSF DC/attenuation/negative calibration failed: " + JsonUtility.ToJson(result));
            result.negativeControlsRejected = 1;
        }
    }
}
