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
        [Serializable] private sealed class FadedLensManifest
        {
            public string utc = "", unity = "", shaderHash = "", baselineShaderHash = "", baselineManifestHash = "", legacyManifestHash = "", showHash = "", runtimeProfileHash = "";
            public string experiment = "Candidate03 baseline; fadeOnly, warpOnly and both. Four changed numeric components for fade (three property lines), one for warp; no runtime/default adoption.";
            public string meaning = "Fade: ImageAdjustment.z=-.02 with post saturation.52 =>.50; w=-.22 with post temperature.48 =>.26; ChromaKill0 removes dark-only ISP desaturation; DisplayContrast1.08. Warp: inverse p'=p/(1+.06*dot(p,p)/2), p=(logicalUV-.5)/crop*2. All other candidate03 values are fixed: Glare0, Grade applied, Gaussian(.85,.65,.65), convergence measured*1.8, image EV1.9/contrast-.1, Lift and post unchanged. Screen-space casing/glass/aperture/OSD are not lens-warped. Mono does not disable this physical lens. Power-off composition is unchanged.";
            public string calibrationMeaning = "Synthetic non-symmetric grid/points are instruments only, never photo substitutes. GPU coordinate probe uses actual crop(.75,1); layer sampler alignment uses unit contain/crop to isolate common logical coordinates. Live/overlay/mask use existing SampleBase; CG uses existing CgUvOf/SampleCgSoft. Probe outputs precede color/casing/OSD. Float readback contains half shader output precision. Zero, positive, negative and unwarped negative-control comparisons calibrate the meter.";
            public Vector3 sigma = new Vector3(.85f, .65f, .65f);
            public PsfMeasurement psf = new PsfMeasurement();
            public LensCalibration calibration = new LensCalibration();
            public List<StyleFile> helpers = new List<StyleFile>();
            public List<OpticsMap> maps = new List<OpticsMap>();
            public List<FadedLensPhoto> photos = new List<FadedLensPhoto>();
        }
        [Serializable] private sealed class StyleFile { public string path = "", sha256 = ""; }
        [Serializable] private sealed class FadedLensPhoto
        {
            public OpticsCapture capture = new OpticsCapture();
            public string condition = "", keywords = "", expectedHash = "", styleOffHash = "", keywordOffNonzeroHash = "";
            public bool baselineMatches, legacyMatches;
            public int changedPropertyLines, changedNumericComponents;
            public float baseLevelMaxError;
        }
        [Serializable] private sealed class LensCalibration
        {
            public float identityMaxError, warpMaxError, unwarpedNegativeError, oppositeSignNegativeError;
            public float screenUvWarpDifference, monoCoordinateDifference, powerCoordinateDifference;
            public float centerInverseDerivative, peripheralInverseDerivative, peripheralExpectedDerivative, peripheralStretch;
            public float theoreticalCornerInverseDerivative = (1 - .06f) / (1.06f * 1.06f), theoreticalCornerStretch = 1.06f * 1.06f / (1 - .06f);
            public float overlayLiveMaxError, maskLiveMaxError, cgLiveMaxError, pointMaxPositionErrorPixels;
            public int monotonicFailures, outOfRangePixels;
            public List<LensPoint> points = new List<LensPoint>();
            public List<StyleFile> images = new List<StyleFile>();
        }
        [Serializable] private sealed class LensPoint
        {
            public Vector2 sourcePixel, expectedPixel, actualPixel;
            public float radialOutwardPixels;
        }

        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview Faded CRT Lens Matrix", priority = 270)]
        public static void RunFadedLensMatrix()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string output = EditorCliArgs.Get("out") ?? Path.Combine(Output, "processing-faded-lens-01");
            if (Directory.Exists(output) && Directory.GetFileSystemEntries(output).Length != 0) throw new IOException("Faded lens output must be empty: " + output);
            if (!File.ReadAllText(Path.Combine(root, ".claude/canon/ROUNDS.md")).Contains("色あせと周辺ほど引き延ばされる歪み"))
                throw new InvalidOperationException("Missing 0296 pre-render prediction");
            string baselineFile = Path.Combine(Output, "processing-candidate-03/manifest.json"), legacyFile = Path.Combine(Output, "round-28/manifest.json");
            var baseline = JsonUtility.FromJson<CandidateManifest>(File.ReadAllText(baselineFile));
            var legacy = JsonUtility.FromJson<Manifest>(File.ReadAllText(legacyFile));
            var cfg = JsonUtility.FromJson<Config>(File.ReadAllText(Path.Combine(root, "tools/web-compositor/show.json")));
            if (baseline == null || baseline.photos.Count != 9 || legacy == null || cfg == null || JsonUtility.ToJson(cfg.post) != JsonUtility.ToJson(legacy.post))
                throw new InvalidDataException("Missing candidate03/legacy/post baseline");
            foreach (CandidatePhoto photo in baseline.photos)
                if (Hash(Path.Combine(Path.GetDirectoryName(baselineFile)!, photo.capture.file)) != photo.capture.sha256) throw new InvalidDataException("Candidate03 image changed");
            var manifest = new FadedLensManifest { utc = DateTime.UtcNow.ToString("o"), unity = Application.unityVersion,
                shaderHash = Hash(Path.Combine(root, "Assets/Art/Shaders/Streaming/ScreenComposite.shader")), baselineShaderHash = baseline.shaderHash,
                baselineManifestHash = Hash(baselineFile), legacyManifestHash = Hash(legacyFile), showHash = Hash(Path.Combine(root, "tools/web-compositor/show.json")),
                runtimeProfileHash = Hash(Path.Combine(root, "Assets/Scripts/Streaming/CrtReferenceScreen.cs")) };
            if (manifest.showHash != baseline.showHash || manifest.runtimeProfileHash != baseline.runtimeProfileHash
                || Hash(Path.Combine(root, "Assets/Scripts/Streaming/Editor/CrtReferencePsfPreview.cs")) != baseline.psfHelperHash
                || Hash(Path.Combine(root, "Assets/Scripts/Streaming/Editor/CrtReferenceOpticsPreview.cs")) != baseline.opticsHelperHash
                || Hash(Path.Combine(root, "Assets/Scripts/Streaming/Editor/CrtReferencePreview.cs")) != baseline.previewHash)
                throw new InvalidDataException("Fixed source pipeline changed");
            foreach (string path in Directory.GetFiles(Path.Combine(root, "Assets/Scripts/Streaming/Editor"), "CrtReference*.cs").OrderBy(p => p, StringComparer.Ordinal))
                manifest.helpers.Add(new StyleFile { path = path, sha256 = Hash(path) });
            foreach (OpticsMap map in baseline.maps)
            {
                var importer = AssetImporter.GetAtPath(map.path) as TextureImporter;
                if (!File.Exists(map.path) || Hash(map.path) != map.sha256 || importer == null || EditorJsonUtility.ToJson(importer) != map.importer)
                    throw new InvalidDataException("Map/importer changed: " + map.path);
                manifest.maps.Add(map);
            }
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Art/Shaders/Streaming/ScreenComposite.shader");
            if (shader == null || !shader.isSupported) throw new InvalidOperationException("Screen shader unsupported");
            Directory.CreateDirectory(output); Directory.CreateDirectory(Path.Combine(output, "calibration"));
            var objects = new List<UnityEngine.Object>(); RenderTexture old = RenderTexture.active; bool oldAsync = ShaderUtil.allowAsyncCompilation;
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                var profile = new Material(shader) { hideFlags = HideFlags.HideAndDontSave }; objects.Add(profile);
                if (!CrtReferenceScreen.Apply(profile)) throw new InvalidOperationException("Missing reference maps");
                profile.DisableKeyword("CRT_ORDER_DIAGNOSTIC"); profile.DisableKeyword("CRT_PSF_DIAGNOSTIC"); profile.DisableKeyword("CRT_STYLE_DIAGNOSTIC");
                var screen = new GameObject("FadedLensScreen") { hideFlags = HideFlags.HideAndDontSave, layer = 1 }; objects.Add(screen);
                screen.transform.position = new Vector3(1000, 0, 0); screen.transform.localScale = new Vector3(4f / 3f, 1, 1);
                var flat = CrtScreenMesh.BuildMesh(48, 0); objects.Add(flat); var curved = CrtScreenMesh.BuildMesh(48, .055f); objects.Add(curved);
                var filter = screen.AddComponent<MeshFilter>(); filter.sharedMesh = flat; var renderer = screen.AddComponent<MeshRenderer>();
                var go = new GameObject("FadedLensCamera") { hideFlags = HideFlags.HideAndDontSave }; objects.Add(go); var camera = go.AddComponent<Camera>(); camera.enabled = false;
                camera.transform.SetPositionAndRotation(new Vector3(1000, 0, -2), Quaternion.identity);
                camera.orthographic = true; camera.orthographicSize = .5f; camera.nearClipPlane = .1f; camera.farClipPlane = 4;
                camera.cullingMask = 1 << 1; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
                camera.allowHDR = true; camera.allowMSAA = false; camera.aspect = 4f / 3f;
                var meter = LensTarget(640, 480, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear, objects); camera.targetTexture = meter;
                CalibrateFadedLens(shader, camera, renderer, meter, output, manifest.calibration, objects);
                var raw = LensTarget(2560, 1920, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear, objects); camera.targetTexture = raw;
                var points = PsfPattern(false); objects.Add(points); var linearPoints = LinearPsfTexture(points); objects.Add(linearPoints);
                profile.SetTexture("_CrtPsfLinearTex", linearPoints);
                manifest.psf = MeasurePsfPoints(RenderPsfRaw(profile, points, manifest.sigma, Vector3.zero, camera, renderer, raw, objects, 2), manifest.sigma);
                Save(raw, Path.Combine(output, "calibration/psf_points.png"));
                for (int c = 0; c < 3; c++) if (Mathf.Abs(manifest.psf.energy[c] - 1) > .03f || manifest.psf.pointSecondaryRise[c] > .05f)
                    throw new InvalidOperationException("Fixed Gaussian calibration failed");
                filter.sharedMesh = curved; screen.transform.localScale = new Vector3(CrtReferenceScreen.CaseAspect, 1, 1);
                camera.allowHDR = false; camera.aspect = CrtReferenceScreen.CaseAspect;
                var final = LensTarget(1280, 1030, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB, objects); camera.targetTexture = final;
                string[] names = { "plate_A_20260924_172209_b8e1f375.jpg", "plate_B_20260924_172210_4abb5bae.jpg", "plate_C_20260924_172211_a1c7c02f.jpg" };
                foreach (string name in names)
                {
                    string plate = name.Substring(6, 1), source = Path.Combine(root, "tools/web-compositor/captures", name);
                    CandidatePhoto expected = baseline.photos.Find(p => p.capture.plate == plate && p.condition == "glare_removed") ?? throw new InvalidDataException("Missing candidate03 plate");
                    Capture oldExpected = legacy.captures.Find(c => c.file == plate + "_front.png") ?? throw new InvalidDataException("Missing legacy plate");
                    if (Hash(source) != expected.capture.sourceHash || Hash(source) != oldExpected.sourceHash) throw new InvalidDataException("Source photo changed");
                    var texture = new Texture2D(2, 2, TextureFormat.RGB24, true, false) { filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp }; objects.Add(texture);
                    if (!texture.LoadImage(File.ReadAllBytes(source)) || texture.width != 640 || texture.height != 480 || texture.format != TextureFormat.RGB24)
                        throw new InvalidDataException("Expected actual 640x480 RGB24 photo");
                    var linear = LinearPsfTexture(texture); objects.Add(linear); profile.SetTexture("_CrtPsfLinearTex", linear);
                    camera.targetTexture = raw; camera.allowHDR = true;
                    Color[] baseOriginal = RenderPsfRaw(profile, texture, Vector3.zero, Vector3.zero, camera, renderer, raw, objects, 0, Vector3.zero);
                    float baseError = PsfMaxDifference(baseOriginal, RenderPsfRaw(profile, texture, Vector3.zero, Vector3.zero, camera, renderer, raw, objects, 1, Vector3.zero));
                    if (baseError > .003f) throw new InvalidDataException("Photo linear decode mismatch");
                    camera.targetTexture = final; camera.allowHDR = false;
                    profile.SetTexture("_LiveTex", texture); profile.SetVector("_LiveScale", new Vector4(.75f, 1, 0, 0)); profile.SetVector("_CgScale", new Vector4(.75f, 1, 0, 0));
                    profile.SetFloat("_FrameAspect", CrtReferenceScreen.LogicalAspect); profile.SetVector("_CrtView", new Vector4(0, 0, -1, 0)); ApplyPost(profile, cfg.post);
                    var feel = CameraFeelFx.Settings.Resolve(cfg.feel); var logic = new CameraFeelLogic { TargetLuma = feel.TargetLuma, FollowHalfLifeSec = feel.FollowSec };
                    float mean = 0; int count = 0;
                    for (int y = 0; y < texture.height; y += texture.height / 16) for (int x = 0; x < texture.width; x += texture.width / 16)
                    { Color p = texture.GetPixel(x, y); mean += p.r * .299f + p.g * .587f + p.b * .114f; count++; }
                    CameraFeelFx.WriteUniforms(profile, feel.NoiseDark, feel.NoiseFixed, logic.SteadyBiasFor(mean / count) * feel.Agc, 0, 0, 0, 0);
                    var legacyMaterial = LensMaterial(profile, objects);
                    CandidatePhoto oldSnapshot = baseline.photos.Find(p => p.capture.plate == plate && p.condition == "legacy") ?? throw new InvalidDataException("Missing legacy snapshot");
                    AssertOldLensSnapshot(legacyMaterial, oldSnapshot.capture.parameters);
                    FadedLensPhoto legacyPhoto = CaptureFadedLens(legacyMaterial, "legacy", plate, source, baseError, camera, renderer, final, output, objects);
                    legacyPhoto.expectedHash = oldExpected.sha256; legacyPhoto.legacyMatches = legacyPhoto.capture.sha256 == oldExpected.sha256;
                    if (!legacyPhoto.legacyMatches) throw new InvalidDataException("Legacy differs from round28"); manifest.photos.Add(legacyPhoto);
                    var standard = LensMaterial(profile, objects); standard.EnableKeyword("CRT_PSF_DIAGNOSTIC"); standard.SetFloat("_CrtPsfMode", 2);
                    standard.SetVector("_CrtPsfSigma", manifest.sigma); standard.SetVector("_CrtOpticalConvergence", baseline.convergence);
                    standard.SetVector("_CrtImageAdjustment", baseline.imageAdjustment); standard.SetFloat("_Glare", 0);
                    AssertOldLensSnapshot(standard, expected.capture.parameters);
                    CompilePsf(standard); renderer.sharedMaterial = standard; camera.Render();
                    string offFile = Path.Combine(output, "calibration/" + plate + "_style_off.png"); Save(final, offFile); string offHash = Hash(offFile);
                    if (offHash != expected.capture.sha256) throw new InvalidDataException("Style-off candidate03 differs");
                    var offNonzero = LensMaterial(standard, objects); offNonzero.SetFloat("_CrtLensWarp", .06f);
                    CompilePsf(offNonzero); renderer.sharedMaterial = offNonzero; camera.Render();
                    string disabledFile = Path.Combine(output, "calibration/" + plate + "_keyword_off_nonzero.png"); Save(final, disabledFile);
                    if (Hash(disabledFile) != offHash) throw new InvalidDataException("Disabled style keyword is not identity");
                    standard.EnableKeyword("CRT_STYLE_DIAGNOSTIC");
                    string[] conditions = { "baseline03", "fadeOnly", "warpOnly", "both" };
                    for (int arm = 0; arm < conditions.Length; arm++)
                    {
                        bool fade = arm == 1 || arm == 3, warp = arm >= 2;
                        var material = LensMaterial(standard, objects);
                        if (fade)
                        {
                            Vector4 adjustment = material.GetVector("_CrtImageAdjustment"); adjustment.z = -.02f; adjustment.w = -.22f;
                            material.SetVector("_CrtImageAdjustment", adjustment); material.SetFloat("_ChromaKill", 0); material.SetFloat("_CrtDisplayContrast", 1.08f);
                        }
                        if (warp) material.SetFloat("_CrtLensWarp", .06f);
                        AssertFadedLensDifference(standard, material, fade, warp);
                        FadedLensPhoto photo = CaptureFadedLens(material, conditions[arm], plate, source, baseError, camera, renderer, final, output, objects);
                        photo.changedPropertyLines = (fade ? 3 : 0) + (warp ? 1 : 0); photo.changedNumericComponents = (fade ? 4 : 0) + (warp ? 1 : 0);
                        if (arm == 0)
                        {
                            photo.expectedHash = expected.capture.sha256; photo.baselineMatches = photo.capture.sha256 == expected.capture.sha256;
                            photo.styleOffHash = offHash; photo.keywordOffNonzeroHash = Hash(disabledFile);
                            if (!photo.baselineMatches) throw new InvalidDataException("Style-on zero coefficient differs from candidate03");
                        }
                        manifest.photos.Add(photo);
                    }
                }
                if (manifest.photos.Count != 15) throw new InvalidDataException("Expected fifteen final photos");
                foreach (FadedLensPhoto photo in manifest.photos)
                    if (Hash(Path.Combine(output, photo.capture.file)) != photo.capture.sha256) throw new InvalidDataException("Final photo missing or changed");
                foreach (string path in Directory.GetFiles(Path.Combine(output, "calibration"), "*.png"))
                    manifest.calibration.images.Add(new StyleFile { path = path, sha256 = Hash(path) });
                File.WriteAllText(Path.Combine(output, "calibration.json"), JsonUtility.ToJson(manifest.calibration, true));
                File.WriteAllText(Path.Combine(output, "manifest.json.tmp"), JsonUtility.ToJson(manifest, true)); File.Move(Path.Combine(output, "manifest.json.tmp"), Path.Combine(output, "manifest.json"));
                Debug.Log("[CrtFadedLens] 15 photos; legacy28=3/3 candidate03=3/3; keyword-off and zero identities; GPU lens/layer/PSF gates passed; shader errors=0 -> " + output);
            }
            finally
            {
                ShaderUtil.allowAsyncCompilation = oldAsync; RenderTexture.active = old;
                for (int i = objects.Count - 1; i >= 0; i--) { if (objects[i] is RenderTexture rt) rt.Release(); UnityEngine.Object.DestroyImmediate(objects[i]); }
            }
        }

        private static Material LensMaterial(Material source, List<UnityEngine.Object> objects)
        { var material = new Material(source) { hideFlags = HideFlags.HideAndDontSave }; objects.Add(material); return material; }

        private static RenderTexture LensTarget(int width, int height, RenderTextureFormat format, RenderTextureReadWrite readWrite, List<UnityEngine.Object> objects)
        {
            var target = new RenderTexture(width, height, 24, format, readWrite);
            if (!target.Create()) throw new InvalidOperationException("Cannot create lens RT"); objects.Add(target); return target;
        }

        private static FadedLensPhoto CaptureFadedLens(Material material, string condition, string plate, string source, float baseError,
            Camera camera, Renderer renderer, RenderTexture target, string output, List<UnityEngine.Object> objects)
        {
            CompilePsf(material); renderer.sharedMaterial = material; camera.Render();
            string file = plate + "_" + condition + ".png"; Save(target, Path.Combine(output, file));
            var capture = new OpticsCapture { plate = plate, source = source, sourceHash = Hash(source), file = file, sha256 = Hash(Path.Combine(output, file)),
                parameters = OpticsParameters(material), invariantHash = ParameterHash(material, "_CrtImageAdjustment", "_ChromaKill", "_CrtDisplayContrast", "_CrtLensWarp"), width = target.width, height = target.height,
                spread = material.GetVector("_CrtOpticalSpread"), convergence = material.GetVector("_CrtOpticalConvergence") };
            ValidateOpticsImage(Path.Combine(output, file), capture, objects);
            string[] keywords = material.shaderKeywords; Array.Sort(keywords, StringComparer.Ordinal);
            return new FadedLensPhoto { capture = capture, condition = condition, baseLevelMaxError = baseError, keywords = string.Join(";", keywords) };
        }

        private static void AssertOldLensSnapshot(Material material, string expected)
        {
            if (material.GetFloat("_CrtLensWarp") != 0 || material.GetFloat("_CrtStyleProbe") != 0 || material.IsKeywordEnabled("CRT_STYLE_DIAGNOSTIC"))
                throw new InvalidOperationException("Legacy diagnostic defaults changed");
            string current = string.Join("\n", OpticsParameters(material).Split('\n').Where(line => !line.StartsWith("_CrtLensWarp=", StringComparison.Ordinal) && !line.StartsWith("_CrtStyleProbe=", StringComparison.Ordinal)));
            if (current != expected) throw new InvalidDataException("Candidate03 full property snapshot changed");
        }

        private static void AssertFadedLensDifference(Material baseline, Material actual, bool fade, bool warp)
        {
            Vector4 a = baseline.GetVector("_CrtImageAdjustment"), b = actual.GetVector("_CrtImageAdjustment");
            if (a.x != b.x || a.y != b.y || b.z != (fade ? -.02f : a.z) || b.w != (fade ? -.22f : a.w)
                || actual.GetFloat("_ChromaKill") != (fade ? 0 : baseline.GetFloat("_ChromaKill"))
                || actual.GetFloat("_CrtDisplayContrast") != (fade ? 1.08f : baseline.GetFloat("_CrtDisplayContrast"))
                || actual.GetFloat("_CrtLensWarp") != (warp ? .06f : 0)
                || ParameterHash(actual, "_CrtImageAdjustment", "_ChromaKill", "_CrtDisplayContrast", "_CrtLensWarp") != ParameterHash(baseline, "_CrtImageAdjustment", "_ChromaKill", "_CrtDisplayContrast", "_CrtLensWarp"))
                throw new InvalidOperationException("Faded lens matrix changed an uncontrolled component");
            string[] first = baseline.shaderKeywords, second = actual.shaderKeywords; Array.Sort(first, StringComparer.Ordinal); Array.Sort(second, StringComparer.Ordinal);
            if (string.Join(";", first) != string.Join(";", second)) throw new InvalidOperationException("Matrix keywords differ");
            string[] before = OpticsParameters(baseline).Split('\n'), after = OpticsParameters(actual).Split('\n'); int changed = 0;
            if (before.Length != after.Length) throw new InvalidDataException("Matrix property count differs");
            for (int i = 0; i < before.Length; i++) if (before[i] != after[i]) changed++;
            if (changed != (fade ? 3 : 0) + (warp ? 1 : 0)) throw new InvalidOperationException("Unexpected changed property count");
        }

        private static Color[] RenderLensProbe(Material profile, int stage, float warp, Camera camera, Renderer renderer, RenderTexture target, List<UnityEngine.Object> objects)
        {
            var material = LensMaterial(profile, objects); material.EnableKeyword("CRT_STYLE_DIAGNOSTIC"); material.SetFloat("_CrtStyleProbe", stage); material.SetFloat("_CrtLensWarp", warp);
            CompilePsf(material); renderer.sharedMaterial = material; camera.Render(); RenderTexture.active = target;
            var readback = new Texture2D(target.width, target.height, TextureFormat.RGBAFloat, false, true);
            try { readback.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); readback.Apply(); return readback.GetPixels(); }
            finally { UnityEngine.Object.DestroyImmediate(readback); }
        }

        private static Vector2 LensInverse(Vector2 p, float k) => p / (1 + k * p.sqrMagnitude * .5f);

        private static void CalibrateFadedLens(Shader shader, Camera camera, Renderer renderer, RenderTexture target, string output, LensCalibration result, List<UnityEngine.Object> objects)
        {
            var profile = new Material(shader) { hideFlags = HideFlags.HideAndDontSave }; objects.Add(profile);
            profile.SetFloat("_CrtReference", 1); profile.SetVector("_CrtAperture", new Vector4(0, 0, 1, 1)); profile.SetVector("_CrtLogicalCrop", new Vector4(.75f, 1, 0, 0));
            profile.SetVector("_LiveScale", new Vector4(1, 1, 0, 0)); profile.SetVector("_OverlayScale", new Vector4(1, 1, 0, 0)); profile.SetVector("_CgScale", new Vector4(1, 1, 0, 0));
            profile.SetFloat("_CgSoften", 0); profile.SetFloat("_Glare", 0);
            profile.SetTexture("_LiveTex", LensPattern(false, objects));
            var normal = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true); objects.Add(normal); normal.SetPixel(0, 0, new Color(.5f, .5f, 1, 1)); normal.Apply(); profile.SetTexture("_CrtNormalTex", normal);
            Color[] zero = RenderLensProbe(profile, 1, 0, camera, renderer, target, objects);
            Save(target, Path.Combine(output, "calibration/coordinates_zero.png"));
            Color[] positive = RenderLensProbe(profile, 1, .06f, camera, renderer, target, objects);
            Save(target, Path.Combine(output, "calibration/coordinates_positive.png"));
            Color[] negative = RenderLensProbe(profile, 1, -.06f, camera, renderer, target, objects);
            Save(target, Path.Combine(output, "calibration/coordinates_opposite_sign.png"));
            int width = target.width, height = target.height;
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                int i = y * width + x; Vector2 p = new Vector2((x + .5f) / width * 2 - 1, (y + .5f) / height * 2 - 1);
                Vector2 uv0 = new Vector2(.5f + p.x * .375f, .5f + p.y * .5f), q = LensInverse(p, .06f), uv = new Vector2(.5f + q.x * .375f, .5f + q.y * .5f);
                result.identityMaxError = Mathf.Max(result.identityMaxError, Mathf.Abs(zero[i].r - uv0.x), Mathf.Abs(zero[i].g - uv0.y));
                result.warpMaxError = Mathf.Max(result.warpMaxError, Mathf.Abs(positive[i].r - uv.x), Mathf.Abs(positive[i].g - uv.y));
                result.unwarpedNegativeError = Mathf.Max(result.unwarpedNegativeError, Mathf.Abs(zero[i].r - uv.x), Mathf.Abs(zero[i].g - uv.y));
                result.oppositeSignNegativeError = Mathf.Max(result.oppositeSignNegativeError, Mathf.Abs(negative[i].r - uv.x), Mathf.Abs(negative[i].g - uv.y));
                result.screenUvWarpDifference = Mathf.Max(result.screenUvWarpDifference, Mathf.Abs(positive[i].b - zero[i].b), Mathf.Abs(positive[i].a - zero[i].a));
                if (positive[i].r < .125f || positive[i].r > .875f || positive[i].g < 0 || positive[i].g > 1) result.outOfRangePixels++;
                if (x > 0 && positive[i].r <= positive[i - 1].r) result.monotonicFailures++;
                if (y > 0 && positive[i].g <= positive[i - width].g) result.monotonicFailures++;
            }
            profile.SetFloat("_Mono", 1); result.monoCoordinateDifference = PsfMaxDifference(positive, RenderLensProbe(profile, 1, .06f, camera, renderer, target, objects)); profile.SetFloat("_Mono", 0);
            profile.SetFloat("_ScreenPower", 0); result.powerCoordinateDifference = PsfMaxDifference(positive, RenderLensProbe(profile, 1, .06f, camera, renderer, target, objects)); profile.SetFloat("_ScreenPower", 1);
            result.centerInverseDerivative = LensGpuDerivative(positive, width, height, -.03f, .03f, out _);
            result.peripheralInverseDerivative = LensGpuDerivative(positive, width, height, .90f, .94f, out float expectedDerivative);
            result.peripheralExpectedDerivative = expectedDerivative; result.peripheralStretch = 1 / result.peripheralInverseDerivative;
            if (result.identityMaxError > .0007f || result.warpMaxError > .0007f || result.unwarpedNegativeError < .02f || result.oppositeSignNegativeError < .04f
                || result.outOfRangePixels != 0 || result.monotonicFailures != 0 || result.screenUvWarpDifference != 0 || result.monoCoordinateDifference != 0 || result.powerCoordinateDifference != 0
                || Mathf.Abs(result.centerInverseDerivative - 1) > .02f || Mathf.Abs(result.peripheralInverseDerivative - expectedDerivative) > .03f || result.peripheralStretch <= 1.1f)
                throw new InvalidOperationException("GPU lens coordinate/sign/monotonic/identity calibration failed: " + JsonUtility.ToJson(result));
            profile.SetVector("_CrtLogicalCrop", new Vector4(1, 1, 0, 0));
            var black = LensSolid(Color.black, objects); var white = LensSolid(Color.white, objects);
            for (int pattern = 0; pattern < 2; pattern++)
            {
                Texture2D texture = LensPattern(pattern == 1, objects); string name = pattern == 0 ? "grid" : "points";
                File.WriteAllBytes(Path.Combine(output, "calibration/source_" + name + ".png"), texture.EncodeToPNG());
                profile.SetTexture("_LiveTex", texture); profile.SetTexture("_OverlayTex", black); profile.SetTexture("_MaskTex", black); profile.SetFloat("_OverlayStrength", 0);
                Color[] unwarped = RenderLensProbe(profile, 2, 0, camera, renderer, target, objects); Save(target, Path.Combine(output, "calibration/" + name + "_zero.png"));
                Color[] live = RenderLensProbe(profile, 2, .06f, camera, renderer, target, objects); Save(target, Path.Combine(output, "calibration/" + name + "_live.png"));
                RenderLensProbe(profile, 2, -.06f, camera, renderer, target, objects); Save(target, Path.Combine(output, "calibration/" + name + "_opposite_sign.png"));
                profile.SetTexture("_LiveTex", black); profile.SetTexture("_OverlayTex", texture); profile.SetTexture("_MaskTex", white); profile.SetFloat("_OverlayStrength", 1);
                Color[] overlay = RenderLensProbe(profile, 2, .06f, camera, renderer, target, objects); Save(target, Path.Combine(output, "calibration/" + name + "_overlay.png"));
                profile.SetTexture("_OverlayTex", white); profile.SetTexture("_MaskTex", texture);
                Color[] mask = RenderLensProbe(profile, 2, .06f, camera, renderer, target, objects); Save(target, Path.Combine(output, "calibration/" + name + "_mask.png"));
                profile.SetTexture("_CgTex", texture);
                Color[] cg = RenderLensProbe(profile, 3, .06f, camera, renderer, target, objects); Save(target, Path.Combine(output, "calibration/" + name + "_cg.png"));
                result.overlayLiveMaxError = Mathf.Max(result.overlayLiveMaxError, PsfMaxDifference(live, overlay));
                result.maskLiveMaxError = Mathf.Max(result.maskLiveMaxError, PsfMaxDifference(live, mask));
                result.cgLiveMaxError = Mathf.Max(result.cgLiveMaxError, PsfMaxDifference(live, cg));
                if (pattern == 1) MeasureLensPointPositions(live, width, height, result);
            }
            if (result.overlayLiveMaxError > .002f || result.maskLiveMaxError > .002f || result.cgLiveMaxError > .002f || result.pointMaxPositionErrorPixels > .35f)
                throw new InvalidOperationException("GPU layer/point calibration failed: " + JsonUtility.ToJson(result));
        }

        private static float LensGpuDerivative(Color[] image, int width, int height, float lo, float hi, out float expected)
        {
            int x0 = Mathf.Clamp((int)((lo + 1) * .5f * width), 0, width - 1), x1 = Mathf.Clamp((int)((hi + 1) * .5f * width), 0, width - 1);
            int y0 = Mathf.Clamp((int)((lo + 1) * .5f * height), 0, height - 1), y1 = Mathf.Clamp((int)((hi + 1) * .5f * height), 0, height - 1);
            Vector2 p0 = new Vector2((x0 + .5f) / width * 2 - 1, (y0 + .5f) / height * 2 - 1), p1 = new Vector2((x1 + .5f) / width * 2 - 1, (y1 + .5f) / height * 2 - 1);
            Color a = image[y0 * width + x0], b = image[y1 * width + x1]; Vector2 delta = p1 - p0;
            Vector2 measured = new Vector2((b.r - a.r) / .375f, (b.g - a.g) / .5f);
            expected = Vector2.Dot(LensInverse(p1, .06f) - LensInverse(p0, .06f), delta) / delta.sqrMagnitude;
            return Vector2.Dot(measured, delta) / delta.sqrMagnitude;
        }

        private static Texture2D LensSolid(Color color, List<UnityEngine.Object> objects)
        {
            var texture = new Texture2D(640, 480, TextureFormat.RGBAFloat, false, true) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            objects.Add(texture); var pixels = new Color[640 * 480];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = color;
            texture.SetPixels(pixels); texture.Apply(); return texture;
        }

        private static readonly Vector2[] LensPointSources = { new Vector2(320, 240), new Vector2(510, 360), new Vector2(100, 330), new Vector2(480, 100), new Vector2(140, 90) };

        private static Texture2D LensPattern(bool points, List<UnityEngine.Object> objects)
        {
            var texture = new Texture2D(640, 480, TextureFormat.RGBAFloat, false, true) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp }; objects.Add(texture);
            var pixels = new Color[640 * 480]; int[] columns = { 31, 93, 188, 321, 459, 557, 611 }, rows = { 23, 87, 179, 241, 353, 438 };
            for (int y = 0; y < 480; y++) for (int x = 0; x < 640; x++)
            {
                float value;
                if (points)
                {
                    value = 0; foreach (Vector2 p in LensPointSources) value += Mathf.Exp(-((x - p.x) * (x - p.x) + (y - p.y) * (y - p.y)) / 12.5f);
                }
                else
                {
                    bool line = columns.Any(c => Math.Abs(x - c) <= 1) || rows.Any(r => Math.Abs(y - r) <= 1);
                    value = line ? .85f : .12f + .1f * x / 640 + .04f * y / 480;
                    if (x >= 70 && x <= 108 && y >= 390 && y <= 420) value = .65f;
                    if (x >= 480 && x <= 495 && y >= 40 && y <= 97) value = .45f;
                }
                pixels[y * 640 + x] = new Color(value, value, value, 1);
            }
            texture.SetPixels(pixels); texture.Apply(); return texture;
        }

        private static void MeasureLensPointPositions(Color[] image, int width, int height, LensCalibration result)
        {
            foreach (Vector2 source in LensPointSources)
            {
                Vector2 p = new Vector2((source.x + .5f) / width * 2 - 1, (source.y + .5f) / height * 2 - 1);
                float factor = 2 / (1 + Mathf.Sqrt(1 - 2 * .06f * p.sqrMagnitude)); Vector2 q = p * factor;
                Vector2 expected = new Vector2((q.x + 1) * .5f * width - .5f, (q.y + 1) * .5f * height - .5f);
                double mass = 0, mx = 0, my = 0;
                for (int y = Mathf.Max(0, (int)expected.y - 11); y <= Mathf.Min(height - 1, (int)expected.y + 12); y++)
                    for (int x = Mathf.Max(0, (int)expected.x - 11); x <= Mathf.Min(width - 1, (int)expected.x + 12); x++)
                    { double value = image[y * width + x].r; mass += value; mx += x * value; my += y * value; }
                if (mass <= 0) throw new InvalidDataException("Empty lens point");
                Vector2 actual = new Vector2((float)(mx / mass), (float)(my / mass)); float outward = Vector2.Dot(actual - source, (source - new Vector2(width * .5f - .5f, height * .5f - .5f)).normalized);
                if (p.magnitude > .1f && outward <= 0) throw new InvalidOperationException("Peripheral lens point moved inward");
                result.pointMaxPositionErrorPixels = Mathf.Max(result.pointMaxPositionErrorPixels, Vector2.Distance(expected, actual));
                result.points.Add(new LensPoint { sourcePixel = source, expectedPixel = expected, actualPixel = actual, radialOutwardPixels = outward });
            }
        }
    }
}
