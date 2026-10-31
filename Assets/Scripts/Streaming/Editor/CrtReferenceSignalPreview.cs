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
        [Serializable] private sealed class SignalManifest
        {
            public string utc = "", unity = "", shaderHash = "", filterShaderHash = "", baselineManifestHash = "", showHash = "", runtimeProfileHash = "";
            public string comparisonManifestHash = "";
            public string meaning = "0297: all conditions retain faded-lens warpOnly color, contrast, Grade, temperature, ChromaKill and sigma. Baseline is exact 2-pass identity; gainOnly multiplies all display RGB by.90 before glass; bandwidthOnly normalizes separate horizontal9tap Gaussian Y sigma.6 / C sigma2 in display sRGB with Y=.299R+.587G+.114B and C=RGB-Y; both filters then gains. DC/color differences are preserved away from transitions; no desaturation coefficient is reduced. Glass/casing/reflection/power are composed once after the shared signal. No runtime adoption.";
            public string sampling = "One preGlass render per plate at1280x1030 ARGBFloat linear depth24; filter ARGBFloat linear same size, integer SV_POSITION LOAD, no resampling/mips. Stage2 uses the same mesh/camera/pixel position. No-bandwidth/gain1 returns the loaded float/half value directly. Horizontal source endpoints clamp; the glass aperture is inset farther than4pixels. Reconstruction negative display RGB is clamped at0 before transfer decode; positive HDR is retained until final display clamp. Calibration records affected samples.";
            public SignalCalibration calibration = new SignalCalibration();
            public List<StyleFile> helpers = new List<StyleFile>();
            public List<OpticsMap> maps = new List<OpticsMap>();
            public List<SignalPlate> plates = new List<SignalPlate>();
            public List<SignalPhoto> photos = new List<SignalPhoto>();
        }
        [Serializable] private sealed class SignalPlate
        {
            public string plate = "", source = "", sourceHash = "", preGlassFile = "", preGlassHash = "", preGlassParameters = "", legacyHash = "", expectedLegacyHash = "";
            public int preGlassRenderCount;
            public bool legacyMatches;
            public float baseLevelMaxError;
            public float preGlassFloatIdentityError;
            public string preGlassFormat = "", preGlassGraphicsFormat = "", singlepassHash = "";
            public bool preGlassSrgb, preGlassCameraHdr;
        }
        [Serializable] private sealed class SignalPhoto
        {
            public OpticsCapture capture = new OpticsCapture();
            public string condition = "", mainKeywords = "", filterParameters = "", filteredFile = "", filteredHash = "", expectedHash = "";
            public float gain, bandwidth, rawDisplayMin, rawDisplayMax, powerOffMax;
            public int negativeDisplayChannels, overOneDisplayChannels;
            public bool baselineMatches;
        }
        [Serializable] private sealed class SignalCalibration
        {
            public string meaning = "GPU fixtures use the existing raw SampleBase probe to populate a linear RT. Identity is bit-exact against its actual readback. All color/gain tests are in display RGB. Wrong saturation.5, wrong Y direction and unfiltered chroma stripes are negative controls, not candidates. Meter values are not similarity scores.";
            public float uploadMaxError, identityMaxError, flippedNegativeError, constantColorMaxError, broadRegionMaxError, broadDifferenceMaxError;
            public float gainRatioMaxError, regionRatioMaxError, saturationNegativeError, inputChromaAmplitude, outputChromaAmplitude, chromaAmplitudeRatio, lumaDcError;
            public int identityDifferingChannels, negativeControlsRejected;
            public List<SignalGainCalibration> gainChecks = new List<SignalGainCalibration>();
            public List<StyleFile> images = new List<StyleFile>();
        }
        [Serializable] private sealed class SignalGainCalibration
        {
            public float gain, colorMaxError, gainRatioMaxError, regionRatioMaxError, normalizedDifferenceMaxError;
        }

        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview CRT Signal Matrix", priority = 271)]
        public static void RunSignalMatrix()
        {
            RunSignalStudy(false);
        }

        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview CRT Signal Brightness Study", priority = 272)]
        public static void RunSignalBrightnessStudy()
        {
            RunSignalStudy(true);
        }

        private static void RunSignalStudy(bool brightnessStudy)
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string output = EditorCliArgs.Get("out") ?? Path.Combine(Output, brightnessStudy ? "processing-signal-02" : "processing-signal-01");
            if (Directory.Exists(output) && Directory.GetFileSystemEntries(output).Length != 0) throw new IOException("Signal output must be empty: " + output);
            if (!File.ReadAllText(Path.Combine(root, ".claude/canon/ROUNDS.md")).Contains(brightnessStudy ? "選ばれた右下の明るさをさらに下げる" : "色の相対差を残して信号を落とす")) throw new InvalidOperationException("Missing pre-render signal prediction");
            string comparisonFile = Path.Combine(Output, "processing-signal-01/manifest.json");
            SignalManifest? comparison = brightnessStudy ? JsonUtility.FromJson<SignalManifest>(File.ReadAllText(comparisonFile)) : null;
            if (brightnessStudy && (comparison == null || comparison.photos.Count != 12)) throw new InvalidDataException("Missing signal01 comparison");
            if (comparison != null) foreach (SignalPhoto photo in comparison.photos)
                if (Hash(Path.Combine(Path.GetDirectoryName(comparisonFile)!, photo.capture.file)) != photo.capture.sha256) throw new InvalidDataException("Signal01 image changed");
            string baselineFile = Path.Combine(Output, "processing-faded-lens-01/manifest.json"), legacyFile = Path.Combine(Output, "round-28/manifest.json");
            var baseline = JsonUtility.FromJson<FadedLensManifest>(File.ReadAllText(baselineFile)); var legacy = JsonUtility.FromJson<Manifest>(File.ReadAllText(legacyFile));
            var cfg = JsonUtility.FromJson<Config>(File.ReadAllText(Path.Combine(root, "tools/web-compositor/show.json")));
            if (baseline == null || baseline.photos.Count != 15 || legacy == null || cfg == null || JsonUtility.ToJson(cfg.post) != JsonUtility.ToJson(legacy.post)) throw new InvalidDataException("Missing warpOnly/legacy/post baseline");
            foreach (FadedLensPhoto photo in baseline.photos)
                if (Hash(Path.Combine(Path.GetDirectoryName(baselineFile)!, photo.capture.file)) != photo.capture.sha256) throw new InvalidDataException("Prior photo changed");
            var manifest = new SignalManifest { utc = DateTime.UtcNow.ToString("o"), unity = Application.unityVersion,
                shaderHash = Hash(Path.Combine(root, "Assets/Art/Shaders/Streaming/ScreenComposite.shader")), filterShaderHash = Hash(Path.Combine(root, "Assets/Art/Shaders/Streaming/CrtSignalFilter.shader")),
                baselineManifestHash = Hash(baselineFile), showHash = Hash(Path.Combine(root, "tools/web-compositor/show.json")), runtimeProfileHash = Hash(Path.Combine(root, "Assets/Scripts/Streaming/CrtReferenceScreen.cs")) };
            if (manifest.showHash != baseline.showHash || manifest.runtimeProfileHash != baseline.runtimeProfileHash) throw new InvalidDataException("Fixed pipeline changed");
            if (comparison != null)
            {
                manifest.comparisonManifestHash = Hash(comparisonFile);
                manifest.meaning = "0298 brightness comparison: all arms retain signal01 both bandwidth(Y sigma.6/C sigma2), warp.06, original color/Saturation/ChromaKill/Grade/contrast/temperature, Gaussian(.85,.65,.65), Glare0, glass/casing and all post. Only HiddenFilter _Gain changes .90/.85/.80 in display RGB. .90 must reproduce old both hash. .85 is the main prototype; .80 is a stronger comparison. These numeric values are not claimed as user-adopted or scored. No runtime adoption.";
                if (manifest.shaderHash != comparison.shaderHash || manifest.filterShaderHash != comparison.filterShaderHash || manifest.showHash != comparison.showHash || manifest.runtimeProfileHash != comparison.runtimeProfileHash)
                    throw new InvalidDataException("Signal01 fixed shader/source settings changed");
                foreach (StyleFile helper in comparison.helpers)
                    if (!helper.path.EndsWith("CrtReferenceSignalPreview.cs", StringComparison.Ordinal) && Hash(helper.path) != helper.sha256) throw new InvalidDataException("Fixed signal helper changed");
            }
            foreach (StyleFile helper in baseline.helpers) if (Hash(helper.path) != helper.sha256) throw new InvalidDataException("Existing helper changed: " + helper.path);
            foreach (string path in Directory.GetFiles(Path.Combine(root, "Assets/Scripts/Streaming/Editor"), "CrtReference*.cs").OrderBy(p => p, StringComparer.Ordinal)) manifest.helpers.Add(new StyleFile { path = path, sha256 = Hash(path) });
            foreach (OpticsMap map in baseline.maps)
            {
                var importer = AssetImporter.GetAtPath(map.path) as TextureImporter;
                if (Hash(map.path) != map.sha256 || importer == null || EditorJsonUtility.ToJson(importer) != map.importer) throw new InvalidDataException("Map/importer changed: " + map.path); manifest.maps.Add(map);
            }
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Art/Shaders/Streaming/ScreenComposite.shader"), filterShader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Art/Shaders/Streaming/CrtSignalFilter.shader");
            if (shader == null || filterShader == null || !shader.isSupported || !filterShader.isSupported) throw new InvalidOperationException("Signal shaders unsupported");
            Directory.CreateDirectory(output); Directory.CreateDirectory(Path.Combine(output, "intermediate")); Directory.CreateDirectory(Path.Combine(output, "calibration"));
            var objects = new List<UnityEngine.Object>(); RenderTexture old = RenderTexture.active; bool oldAsync = ShaderUtil.allowAsyncCompilation;
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                var profile = new Material(shader) { hideFlags = HideFlags.HideAndDontSave }; objects.Add(profile);
                if (!CrtReferenceScreen.Apply(profile)) throw new InvalidOperationException("Missing CRT maps");
                var screen = new GameObject("SignalScreen") { hideFlags = HideFlags.HideAndDontSave, layer = 1 }; objects.Add(screen); screen.transform.position = new Vector3(1000, 0, 0);
                var flat = CrtScreenMesh.BuildMesh(48, 0); objects.Add(flat); var curved = CrtScreenMesh.BuildMesh(48, .055f); objects.Add(curved);
                var mesh = screen.AddComponent<MeshFilter>(); mesh.sharedMesh = flat; var renderer = screen.AddComponent<MeshRenderer>();
                var go = new GameObject("SignalCamera") { hideFlags = HideFlags.HideAndDontSave }; objects.Add(go); var camera = go.AddComponent<Camera>(); camera.enabled = false;
                camera.transform.SetPositionAndRotation(new Vector3(1000, 0, -2), Quaternion.identity); camera.orthographic = true; camera.orthographicSize = .5f;
                camera.nearClipPlane = .1f; camera.farClipPlane = 4; camera.cullingMask = 1 << 1; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black; camera.allowMSAA = false; camera.allowHDR = true;
                var filter = new Material(filterShader) { hideFlags = HideFlags.HideAndDontSave }; objects.Add(filter);
                CalibrateSignal(shader, filter, camera, renderer, output, manifest.calibration, objects, brightnessStudy);
                var raw = LensTarget(2560, 1920, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear, objects);
                var preGlass = LensTarget(1280, 1030, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear, objects);
                var filtered = LensTarget(1280, 1030, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear, objects);
                var final = LensTarget(1280, 1030, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB, objects);
                filter.SetTexture("_SignalSource", preGlass);
                string[] names = { "plate_A_20260924_172209_b8e1f375.jpg", "plate_B_20260924_172210_4abb5bae.jpg", "plate_C_20260924_172211_a1c7c02f.jpg" };
                foreach (string name in names)
                {
                    string plate = name.Substring(6, 1), source = Path.Combine(root, "tools/web-compositor/captures", name);
                    FadedLensPhoto expected = baseline.photos.Find(p => p.capture.plate == plate && p.condition == "warpOnly") ?? throw new InvalidDataException("Missing warpOnly");
                    SignalPhoto? expectedSignal = comparison?.photos.Find(p => p.capture.plate == plate && p.condition == "both");
                    if (brightnessStudy && expectedSignal == null) throw new InvalidDataException("Missing old both plate");
                    Capture oldExpected = legacy.captures.Find(p => p.file == plate + "_front.png") ?? throw new InvalidDataException("Missing legacy");
                    if (Hash(source) != expected.capture.sourceHash || Hash(source) != oldExpected.sourceHash) throw new InvalidDataException("Source photo changed");
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
                    CameraFeelFx.WriteUniforms(profile, feel.NoiseDark, feel.NoiseFixed, logic.SteadyBiasFor(mean / count) * feel.Agc, 0, 0, 0, 0);
                    mesh.sharedMesh = curved; screen.transform.localScale = new Vector3(CrtReferenceScreen.CaseAspect, 1, 1); camera.aspect = CrtReferenceScreen.CaseAspect; camera.allowHDR = false; camera.targetTexture = final;
                    var legacyMaterial = LensMaterial(profile, objects); CompilePsf(legacyMaterial); renderer.sharedMaterial = legacyMaterial; camera.Render();
                    string legacyFileName = "calibration/" + plate + "_legacy28_off.png"; Save(final, Path.Combine(output, legacyFileName));
                    var plateRecord = new SignalPlate { plate = plate, source = source, sourceHash = Hash(source), baseLevelMaxError = baseError,
                        legacyHash = Hash(Path.Combine(output, legacyFileName)), expectedLegacyHash = oldExpected.sha256 };
                    plateRecord.legacyMatches = plateRecord.legacyHash == oldExpected.sha256; if (!plateRecord.legacyMatches) throw new InvalidDataException("Signal keyword-off legacy28 differs");
                    var standard = LensMaterial(profile, objects); standard.EnableKeyword("CRT_PSF_DIAGNOSTIC"); standard.EnableKeyword("CRT_STYLE_DIAGNOSTIC");
                    standard.SetFloat("_CrtPsfMode", 2); standard.SetVector("_CrtPsfSigma", new Vector3(.85f, .65f, .65f)); standard.SetVector("_CrtOpticalConvergence", CrtReferenceScreen.OpticalConvergence * 1.8f);
                    Vector4 adjustment = CrtReferenceScreen.ImageAdjustment; adjustment.w = -.32f; standard.SetVector("_CrtImageAdjustment", adjustment); standard.SetFloat("_Glare", 0); standard.SetFloat("_CrtLensWarp", .06f);
                    string priorSnapshot = string.Join("\n", OpticsParameters(standard).Split('\n').Where(l => !l.StartsWith("_CrtSignalStage=", StringComparison.Ordinal) && !l.StartsWith("_CrtSignalTex=", StringComparison.Ordinal)));
                    if (priorSnapshot != expected.capture.parameters) throw new InvalidDataException("WarpOnly color/source properties changed");
                    string[] standardKeywords = standard.shaderKeywords; Array.Sort(standardKeywords, StringComparer.Ordinal);
                    if (string.Join(";", standardKeywords) != expected.keywords) throw new InvalidDataException("WarpOnly keywords changed");
                    CompilePsf(standard); renderer.sharedMaterial = standard; camera.Render();
                    string singleFile = "calibration/" + plate + "_warpOnly_singlepass.png"; Save(final, Path.Combine(output, singleFile));
                    plateRecord.singlepassHash = Hash(Path.Combine(output, singleFile));
                    if (plateRecord.singlepassHash != expected.capture.sha256) throw new InvalidDataException("Signal keyword-off warpOnly differs from old baseline: " + plate);
                    var first = LensMaterial(standard, objects); first.EnableKeyword("CRT_SIGNAL_DIAGNOSTIC"); first.SetFloat("_CrtSignalStage", 1);
                    CompilePsf(first); renderer.sharedMaterial = first; camera.targetTexture = preGlass; camera.allowHDR = false; camera.Render(); plateRecord.preGlassRenderCount++;
                    plateRecord.preGlassFormat = preGlass.format.ToString(); plateRecord.preGlassGraphicsFormat = preGlass.graphicsFormat.ToString(); plateRecord.preGlassSrgb = preGlass.sRGB; plateRecord.preGlassCameraHdr = camera.allowHDR;
                    if (preGlass.format != RenderTextureFormat.ARGBFloat || preGlass.sRGB) throw new InvalidOperationException("Pre-glass RT is not Float linear");
                    plateRecord.preGlassParameters = OpticsParameters(first); plateRecord.preGlassFile = "intermediate/" + plate + "_preGlass.png"; Save(preGlass, Path.Combine(output, plateRecord.preGlassFile)); plateRecord.preGlassHash = Hash(Path.Combine(output, plateRecord.preGlassFile));
                    Color[] preGlassPixels = ReadSignalPixels(preGlass);
                    if (comparison != null && plateRecord.preGlassHash != comparison.plates.Find(p => p.plate == plate)?.preGlassHash) throw new InvalidDataException("Shared pre-glass source changed since signal01");
                    var last = LensMaterial(standard, objects); last.EnableKeyword("CRT_SIGNAL_DIAGNOSTIC"); last.SetFloat("_CrtSignalStage", 2); last.SetTexture("_CrtSignalTex", filtered);
                    string mainHash = ParameterHash(last), baseFilterHash = ParameterHash(filter, "_Gain", "_Bandwidth");
                    if (expectedSignal != null && OpticsParameters(last) != expectedSignal.capture.parameters) throw new InvalidDataException("Signal01 main snapshot changed");
                    if (expectedSignal != null)
                    {
                        string[] keys = last.shaderKeywords; Array.Sort(keys, StringComparer.Ordinal);
                        if (string.Join(";", keys) != expectedSignal.mainKeywords) throw new InvalidDataException("Signal01 main keywords changed");
                    }
                    if (brightnessStudy)
                    {
                        mesh.sharedMesh = flat; camera.allowHDR = true;
                        plateRecord.preGlassFloatIdentityError = PsfMaxDifference(preGlassPixels, RenderSignalFilter(filter, camera, renderer, filtered));
                        if (plateRecord.preGlassFloatIdentityError != 0) throw new InvalidOperationException("Float identity probe failed");
                    }
                    string[] conditions = brightnessStudy ? new[] { "gain090", "gain085", "gain080" } : new[] { "baseline", "gainOnly", "bandwidthOnly", "both" };
                    Material? gainReference = null;
                    for (int arm = 0; arm < conditions.Length; arm++)
                    {
                        bool bandwidth = brightnessStudy || arm >= 2; float gain = brightnessStudy ? arm == 0 ? .90f : arm == 1 ? .85f : .80f : arm == 1 || arm == 3 ? .90f : 1;
                        var processing = LensMaterial(filter, objects); processing.SetFloat("_Bandwidth", bandwidth ? 1 : 0); processing.SetFloat("_Gain", gain);
                        if (ParameterHash(processing, "_Gain", "_Bandwidth") != baseFilterHash || processing.GetTexture("_SignalSource") != preGlass || ParameterHash(last) != mainHash) throw new InvalidOperationException("Uncontrolled signal/filter properties");
                        if (brightnessStudy)
                        {
                            if (arm == 0)
                            {
                                gainReference = processing;
                                if (OpticsParameters(processing) != expectedSignal!.filterParameters) throw new InvalidDataException("Old both filter snapshot changed");
                            }
                            if (ParameterHash(processing, "_Gain") != ParameterHash(gainReference!, "_Gain") || processing.GetFloat("_Gain") != gain) throw new InvalidOperationException("Brightness study changed more than gain");
                            string[] before = OpticsParameters(gainReference!).Split('\n'), after = OpticsParameters(processing).Split('\n'); int differences = 0;
                            for (int i = 0; i < before.Length; i++) if (before[i] != after[i])
                            { if (!after[i].StartsWith("_Gain=", StringComparison.Ordinal)) throw new InvalidOperationException("Uncontrolled brightness snapshot difference"); differences++; }
                            if (differences != (arm == 0 ? 0 : 1)) throw new InvalidOperationException("Unexpected brightness property count");
                        }
                        mesh.sharedMesh = flat; camera.allowHDR = true; Color[] filteredPixels = RenderSignalFilter(processing, camera, renderer, filtered);
                        if (arm == 0 && !brightnessStudy)
                        {
                            plateRecord.preGlassFloatIdentityError = PsfMaxDifference(preGlassPixels, filteredPixels);
                            if (plateRecord.preGlassFloatIdentityError != 0) throw new InvalidOperationException("Float filter identity is not exact");
                        }
                        string middle = "intermediate/" + plate + "_" + conditions[arm] + "_filtered.png"; Save(filtered, Path.Combine(output, middle));
                        var probe = LensMaterial(processing, objects); probe.SetFloat("_DiagnosticOutput", 1); Color[] display = RenderSignalFilter(probe, camera, renderer, filtered);
                        int negative = 0, overOne = 0; float min = float.PositiveInfinity, max = float.NegativeInfinity;
                        foreach (Color p in display) for (int c = 0; c < 3; c++) { min = Mathf.Min(min, p[c]); max = Mathf.Max(max, p[c]); if (p[c] < 0) negative++; if (p[c] > 1) overOne++; }
                        RenderSignalFilter(processing, camera, renderer, filtered);
                        mesh.sharedMesh = curved; camera.allowHDR = false; camera.targetTexture = final; CompilePsf(last); renderer.sharedMaterial = last; camera.Render();
                        string file = plate + "_" + conditions[arm] + ".png"; Save(final, Path.Combine(output, file));
                        var capture = new OpticsCapture { plate = plate, source = source, sourceHash = Hash(source), file = file, sha256 = Hash(Path.Combine(output, file)), parameters = OpticsParameters(last), invariantHash = mainHash, width = 1280, height = 1030 };
                        ValidateOpticsImage(Path.Combine(output, file), capture, objects); string[] keywords = last.shaderKeywords; Array.Sort(keywords, StringComparer.Ordinal);
                        var photo = new SignalPhoto { capture = capture, condition = conditions[arm], mainKeywords = string.Join(";", keywords), filterParameters = OpticsParameters(processing),
                            filteredFile = middle, filteredHash = Hash(Path.Combine(output, middle)), gain = gain, bandwidth = bandwidth ? 1 : 0, rawDisplayMin = min, rawDisplayMax = max, negativeDisplayChannels = negative, overOneDisplayChannels = overOne };
                        if (arm == 0)
                        {
                            photo.expectedHash = brightnessStudy ? expectedSignal!.capture.sha256 : expected.capture.sha256; photo.baselineMatches = capture.sha256 == photo.expectedHash;
                            if (!photo.baselineMatches) throw new InvalidDataException("Signal baseline hash differs: " + plate);
                        }
                        var powerOff = LensMaterial(last, objects); powerOff.SetFloat("_ScreenPower", 0); CompilePsf(powerOff); renderer.sharedMaterial = powerOff; camera.Render();
                        Color[] dark = ReadSignalPixels(final); foreach (Color p in dark) photo.powerOffMax = Mathf.Max(photo.powerOffMax, p.r, p.g, p.b);
                        if (photo.powerOffMax != 0) throw new InvalidOperationException("Power-off output not black");
                        manifest.photos.Add(photo);
                    }
                    if (plateRecord.preGlassRenderCount != 1) throw new InvalidOperationException("Pre-glass source rendered more than once"); manifest.plates.Add(plateRecord);
                }
                if (manifest.photos.Count != (brightnessStudy ? 9 : 12)) throw new InvalidDataException("Unexpected final photo count");
                foreach (SignalPhoto photo in manifest.photos) if (Hash(Path.Combine(output, photo.capture.file)) != photo.capture.sha256 || Hash(Path.Combine(output, photo.filteredFile)) != photo.filteredHash) throw new InvalidDataException("Signal output changed after capture");
                foreach (SignalPlate plate in manifest.plates) if (Hash(Path.Combine(output, plate.preGlassFile)) != plate.preGlassHash) throw new InvalidDataException("Pre-glass intermediate changed");
                foreach (string path in Directory.GetFiles(Path.Combine(output, "calibration"), "*.png")) manifest.calibration.images.Add(new StyleFile { path = path, sha256 = Hash(path) });
                File.WriteAllText(Path.Combine(output, "calibration.json"), JsonUtility.ToJson(manifest.calibration, true));
                File.WriteAllText(Path.Combine(output, "manifest.json.tmp"), JsonUtility.ToJson(manifest, true)); File.Move(Path.Combine(output, "manifest.json.tmp"), Path.Combine(output, "manifest.json"));
                Debug.Log(brightnessStudy ? "[CrtSignal] 9 brightness photos; old both gain.90=3/3 exact; gain is the only changed uniform; preGlass once/plate; GPU DC/gain/identity gates passed; shader errors=0 -> " + output
                    : "[CrtSignal] 12 photos; old warpOnly=3/3 exact; default legacy28=3/3; preGlass once/plate; GPU DC/gain/bandwidth/orientation/negative gates passed; shader errors=0 -> " + output);
            }
            finally { ShaderUtil.allowAsyncCompilation = oldAsync; RenderTexture.active = old; for (int i = objects.Count - 1; i >= 0; i--) { if (objects[i] is RenderTexture rt) rt.Release(); UnityEngine.Object.DestroyImmediate(objects[i]); } }
        }

        private static Color[] ReadSignalPixels(RenderTexture target)
        {
            RenderTexture.active = target; var texture = new Texture2D(target.width, target.height, TextureFormat.RGBAFloat, false, true);
            try { texture.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); texture.Apply(); return texture.GetPixels(); }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
        }

        private static Color[] RenderSignalFilter(Material material, Camera camera, Renderer renderer, RenderTexture target)
        { CompilePsf(material); renderer.sharedMaterial = material; camera.targetTexture = target; camera.Render(); return ReadSignalPixels(target); }

        private static Vector3 SignalDisplay(Color linear) => new Vector3(Mathf.LinearToGammaSpace(linear.r), Mathf.LinearToGammaSpace(linear.g), Mathf.LinearToGammaSpace(linear.b));
        private static float SignalMax(Vector3 v) => Mathf.Max(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        private static void CalibrateSignal(Shader screenShader, Material filter, Camera camera, Renderer renderer, string output, SignalCalibration result, List<UnityEngine.Object> objects, bool brightnessStudy)
        {
            const int width = 128, height = 64;
            RenderTexture source = LensTarget(width, height, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear, objects), target = LensTarget(width, height, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear, objects);
            var sampling = new Material(screenShader) { hideFlags = HideFlags.HideAndDontSave }; objects.Add(sampling);
            sampling.SetVector("_LiveScale", new Vector4(1, 1, 0, 0)); sampling.SetVector("_CrtLogicalCrop", new Vector4(1, 1, 0, 0));
            for (int pattern = 0; pattern < 3; pattern++)
            {
                var texture = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp }; objects.Add(texture);
                var pixels = new Color[width * height];
                for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                {
                    Color display = pattern == 0 ? new Color(.65f, .35f, .18f, 1) : pattern == 1 ? (x < width / 2 ? new Color(.65f, .35f, .18f, 1) : new Color(.24f, .50f, .62f, 1))
                        : new Color(.45f + (x % 4 < 2 ? .2f : -.2f), .45f - (x % 4 < 2 ? .2f : -.2f) * (.299f / .587f), .45f, 1);
                    if (pattern == 1 && y > 48 && x < 16) display = new Color(.12f, .27f, .72f, 1);
                    pixels[y * width + x] = display.linear;
                }
                texture.SetPixels(pixels); texture.Apply(); sampling.SetTexture("_LiveTex", texture); camera.targetTexture = source; camera.allowHDR = true;
                Color[] input = RenderLensProbe(sampling, 2, 0, camera, renderer, source, objects); result.uploadMaxError = Mathf.Max(result.uploadMaxError, PsfMaxDifference(pixels, input));
                Save(source, Path.Combine(output, "calibration/pattern" + pattern + "_source.png")); filter.SetTexture("_SignalSource", source);
                Color[] identity = RenderSignalFilter(filter, camera, renderer, target);
                result.identityMaxError = Mathf.Max(result.identityMaxError, PsfMaxDifference(input, identity));
                for (int i = 0; i < input.Length; i++) for (int c = 0; c < 4; c++) if (input[i][c] != identity[i][c]) result.identityDifferingChannels++;
                var reverse = LensMaterial(filter, objects); reverse.SetFloat("_FlipY", 1);
                if (pattern == 1) result.flippedNegativeError = PsfMaxDifference(input, RenderSignalFilter(reverse, camera, renderer, target));
                var bandwidth = LensMaterial(filter, objects); bandwidth.SetFloat("_Bandwidth", 1); Color[] softened = RenderSignalFilter(bandwidth, camera, renderer, target); Save(target, Path.Combine(output, "calibration/pattern" + pattern + "_bandwidth.png"));
                var gain = LensMaterial(filter, objects); gain.SetFloat("_Gain", .9f); Color[] gained = RenderSignalFilter(gain, camera, renderer, target); Save(target, Path.Combine(output, "calibration/pattern" + pattern + "_gain.png"));
                int centerA = height / 2 * width + width / 4, centerB = height / 2 * width + width * 3 / 4;
                if (pattern == 0)
                    for (int i = 0; i < input.Length; i++) result.constantColorMaxError = Mathf.Max(result.constantColorMaxError, SignalMax(SignalDisplay(input[i]) - SignalDisplay(softened[i])));
                if (pattern == 1)
                {
                    Vector3 a = SignalDisplay(input[centerA]), b = SignalDisplay(input[centerB]), af = SignalDisplay(softened[centerA]), bf = SignalDisplay(softened[centerB]);
                    result.broadRegionMaxError = Mathf.Max(SignalMax(a - af), SignalMax(b - bf)); result.broadDifferenceMaxError = SignalMax((a - b) - (af - bf));
                    Vector3 ag = SignalDisplay(gained[centerA]), bg = SignalDisplay(gained[centerB]);
                    for (int c = 0; c < 3; c++)
                    {
                        result.gainRatioMaxError = Mathf.Max(result.gainRatioMaxError, Mathf.Abs(ag[c] / a[c] - .9f), Mathf.Abs(bg[c] / b[c] - .9f));
                        result.regionRatioMaxError = Mathf.Max(result.regionRatioMaxError, Mathf.Abs((ag[c] / bg[c]) / (a[c] / b[c]) - 1));
                    }
                    var wrong = LensMaterial(bandwidth, objects); wrong.SetFloat("_DiagnosticSaturation", .5f); Color[] desaturated = RenderSignalFilter(wrong, camera, renderer, target); Save(target, Path.Combine(output, "calibration/wrong_saturation.png"));
                    result.saturationNegativeError = Mathf.Max(SignalMax(a - SignalDisplay(desaturated[centerA])), SignalMax(b - SignalDisplay(desaturated[centerB])));
                    if (brightnessStudy) foreach (float value in new[] { .90f, .85f, .80f })
                    {
                        var material = LensMaterial(bandwidth, objects); material.SetFloat("_Gain", value); Color[] check = RenderSignalFilter(material, camera, renderer, target);
                        Vector3 ac = SignalDisplay(check[centerA]), bc = SignalDisplay(check[centerB]);
                        var measurement = new SignalGainCalibration { gain = value, colorMaxError = Mathf.Max(SignalMax(ac - a * value), SignalMax(bc - b * value)), normalizedDifferenceMaxError = SignalMax((ac - bc) / value - (a - b)) };
                        for (int c = 0; c < 3; c++)
                        {
                            measurement.gainRatioMaxError = Mathf.Max(measurement.gainRatioMaxError, Mathf.Abs(ac[c] / a[c] - value), Mathf.Abs(bc[c] / b[c] - value));
                            measurement.regionRatioMaxError = Mathf.Max(measurement.regionRatioMaxError, Mathf.Abs((ac[c] / bc[c]) / (a[c] / b[c]) - 1));
                        }
                        if (measurement.colorMaxError > .001f || measurement.normalizedDifferenceMaxError > .002f || measurement.gainRatioMaxError > .002f || measurement.regionRatioMaxError > .002f)
                            throw new InvalidOperationException("Brightness gain/DC/color ratio calibration failed");
                        result.gainChecks.Add(measurement); Save(target, Path.Combine(output, "calibration/gain" + value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "_wide_color.png"));
                    }
                }
                if (pattern == 2)
                {
                    double inputAmplitude = 0, outputAmplitude = 0, yIn = 0, yOut = 0; int count = 0;
                    for (int y = 8; y < height - 8; y++) for (int x = 8; x < width - 8; x++)
                    {
                        Vector3 a = SignalDisplay(input[y * width + x]), b = SignalDisplay(softened[y * width + x]);
                        float ya = Vector3.Dot(a, new Vector3(.299f, .587f, .114f)), yb = Vector3.Dot(b, new Vector3(.299f, .587f, .114f));
                        inputAmplitude += (a.x - ya) * (a.x - ya); outputAmplitude += (b.x - yb) * (b.x - yb); yIn += ya; yOut += yb; count++;
                    }
                    result.inputChromaAmplitude = (float)Math.Sqrt(inputAmplitude / count); result.outputChromaAmplitude = (float)Math.Sqrt(outputAmplitude / count);
                    result.chromaAmplitudeRatio = result.outputChromaAmplitude / result.inputChromaAmplitude; result.lumaDcError = (float)Math.Abs((yIn - yOut) / count);
                }
            }
            if (result.uploadMaxError > .003f || result.identityMaxError != 0 || result.identityDifferingChannels != 0 || result.constantColorMaxError > .001f || result.broadRegionMaxError > .001f || result.broadDifferenceMaxError > .001f
                || result.gainRatioMaxError > .002f || result.regionRatioMaxError > .002f || result.lumaDcError > .001f || result.chromaAmplitudeRatio > .2f
                || result.flippedNegativeError < .1f || result.saturationNegativeError < .05f || result.inputChromaAmplitude < .15f)
                throw new InvalidOperationException("Signal DC/gain/bandwidth/identity/sign calibration failed: " + JsonUtility.ToJson(result));
            result.negativeControlsRejected = 3;
        }
    }
}
