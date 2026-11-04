#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    public static partial class CrtReferencePreview
    {
        private const int PsfScale = 4, PsfWidth = 640, PsfHeight = 480;
        [Serializable] private sealed class PsfManifest
        {
            public string utc = "", unity = "", shaderHash = "", helperHash = "", previewHash = "", opticsHelperHash = "", runtimeProfileHash = "", showHash = "";
            public string calibration = "16 isolated white source pixels at all 4x4 mip phases; 640x480 RGB24 sRGB via LoadImage like photos; raw PSF only, no Grade/CA/glass; 2560x1920 ARGBFloat linear readback. Linear PSF source is CPU sRGB-decode into RGBAFloat linear+mips. Gaussian is dense normalized 9x9 at 1 source-pixel spacing, mip0.";
            public string fitting = "Legacy encoded mip is the unchanged control; its point-energy attenuation is reported, not preserved. Linear mip LOD and Gaussian RGB sigma independently fit the legacy mean central second moments by 10-step bisection. Both linear candidates must conserve energy near 1 and match widths within 2%. LiveTex remains original for glare/chroma/other effects; only SampleLivePsf reads the separate linear texture.";
            public string pointSourceHash = "", edgeSourceHash = "";
            public float grayBaseError, grayWrongFlagError;
            public Vector3 lod, sigma, linearLod;
            public PsfMeasurement control = new PsfMeasurement(), candidate = new PsfMeasurement();
            public PsfMeasurement linearMip = new PsfMeasurement();
            public PsfMeasurement linearTextureProbe = new PsfMeasurement();
            public List<PsfMipValue> mipProbe = new List<PsfMipValue>();
            public List<PsfMeasurement> fittingTrials = new List<PsfMeasurement>();
            public List<PsfMeasurement> linearMipTrials = new List<PsfMeasurement>();
            public List<OpticsMap> maps = new List<OpticsMap>();
            public List<PsfPhoto> photos = new List<PsfPhoto>();
        }
        [Serializable] private sealed class PsfMipValue
        {
            public int level;
            public float srgbTextureCpuValue, linearTextureCpuValue, decodedSrgbValue;
        }
        [Serializable] private sealed class PsfMeasurement
        {
            public Vector3 sigma, lod, energy, varianceX, varianceY, angularFourth, pointSecondaryRise;
            public Vector3 edgeNegativeSlope, edgeSecondaryRise, edgeTangentRippleRms;
        }
        [Serializable] private sealed class PsfPhoto
        {
            public OpticsCapture capture = new OpticsCapture();
            public bool gaussianKeyword;
            public int mode;
            public float baseLevelMaxError;
        }

        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview CRT PSF Shape", priority = 262)]
        public static void RunPsfShape()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string output = EditorCliArgs.Get("out") ?? Path.Combine(Output, "processing-psf-linear-01");
            if (Directory.Exists(output) && Directory.GetFileSystemEntries(output).Length > 0)
                throw new IOException("PSF output must be empty: " + output);
            if (!File.ReadAllText(Path.Combine(root, ".claude/canon/ROUNDS.md")).Contains("mipから点広がり形状を交換"))
                throw new InvalidOperationException("Missing pre-render PSF-shape prediction");
            string baselinePath = Path.Combine(Output, "round-28/manifest.json");
            var baseline = JsonUtility.FromJson<Manifest>(File.ReadAllText(baselinePath));
            var cfg = JsonUtility.FromJson<Config>(File.ReadAllText(Path.Combine(root, "tools/web-compositor/show.json")));
            if (baseline == null || cfg == null || JsonUtility.ToJson(baseline.post) != JsonUtility.ToJson(cfg.post))
                throw new InvalidDataException("Post parameters differ from round 28");
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Art/Shaders/Streaming/ScreenComposite.shader");
            if (shader == null || !shader.isSupported) throw new InvalidOperationException("Screen shader unsupported");
            var manifest = new PsfManifest { utc = DateTime.UtcNow.ToString("o"), unity = Application.unityVersion,
                shaderHash = Hash(Path.Combine(root, "Assets/Art/Shaders/Streaming/ScreenComposite.shader")),
                helperHash = Hash(Path.Combine(root, "Assets/Scripts/Streaming/Editor/CrtReferencePsfPreview.cs")),
                previewHash = Hash(Path.Combine(root, "Assets/Scripts/Streaming/Editor/CrtReferencePreview.cs")),
                opticsHelperHash = Hash(Path.Combine(root, "Assets/Scripts/Streaming/Editor/CrtReferenceOpticsPreview.cs")),
                runtimeProfileHash = Hash(Path.Combine(root, "Assets/Scripts/Streaming/CrtReferenceScreen.cs")),
                showHash = Hash(Path.Combine(root, "tools/web-compositor/show.json")) };
            if (manifest.runtimeProfileHash != baseline.runtimeProfileHash) throw new InvalidDataException("Runtime profile changed");
            string[] mapNames = { "Frame.png", "CaseColor.jpg", "Reflectance.png", "Glass.png", "Normal.png", "Grade.png" };
            string[] hashes = { baseline.frameHash, baseline.caseColorHash, baseline.reflectanceHash, baseline.glassHash, baseline.normalHash, baseline.gradeHash };
            for (int i = 0; i < mapNames.Length; i++)
            {
                string path = "Assets/Resources/CrtReference/" + mapNames[i];
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (Hash(path) != hashes[i] || importer == null || importer.sRGBTexture != (i < 2) || !importer.mipmapEnabled
                    || importer.npotScale != TextureImporterNPOTScale.None || importer.textureCompression != TextureImporterCompression.Uncompressed)
                    throw new InvalidDataException("Map/importer changed: " + path);
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
                if (!CrtReferenceScreen.Apply(profile)) throw new InvalidOperationException("Missing CRT maps");
                profile.DisableKeyword("CRT_ORDER_DIAGNOSTIC"); profile.DisableKeyword("CRT_PSF_DIAGNOSTIC");
                var screen = new GameObject("PsfScreen") { hideFlags = HideFlags.HideAndDontSave, layer = 1 }; objects.Add(screen);
                screen.transform.position = new Vector3(1000, 0, 0);
                var flat = CrtScreenMesh.BuildMesh(48, 0); objects.Add(flat);
                var curved = CrtScreenMesh.BuildMesh(48, 0.055f); objects.Add(curved);
                var filter = screen.AddComponent<MeshFilter>(); filter.sharedMesh = flat;
                screen.transform.localScale = new Vector3(4f / 3f, 1, 1);
                var renderer = screen.AddComponent<MeshRenderer>();
                var go = new GameObject("PsfCamera") { hideFlags = HideFlags.HideAndDontSave }; objects.Add(go);
                var camera = go.AddComponent<Camera>(); camera.enabled = false;
                camera.transform.SetPositionAndRotation(new Vector3(1000, 0, -2), Quaternion.identity);
                camera.orthographic = true; camera.orthographicSize = 0.5f;
                camera.nearClipPlane = 0.1f; camera.farClipPlane = 4; camera.cullingMask = 1 << 1;
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
                camera.allowHDR = true; camera.allowMSAA = false; camera.aspect = 4f / 3f;
                var raw = new RenderTexture(PsfWidth * PsfScale, PsfHeight * PsfScale, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
                if (!raw.Create()) throw new InvalidOperationException("Cannot create PSF calibration RT"); objects.Add(raw);
                camera.targetTexture = raw;
                var points = PsfPattern(false); objects.Add(points);
                var edge = PsfPattern(true); objects.Add(edge);
                var linearPoints = PsfPattern(false, true); objects.Add(linearPoints);
                var correctLinearPoints = LinearPsfTexture(points); objects.Add(correctLinearPoints);
                var correctLinearEdge = LinearPsfTexture(edge); objects.Add(correctLinearEdge);
                profile.SetTexture("_CrtPsfLinearTex", correctLinearPoints);
                File.WriteAllBytes(Path.Combine(output, "source_points.png"), points.EncodeToPNG());
                File.WriteAllBytes(Path.Combine(output, "source_edge.png"), edge.EncodeToPNG());
                manifest.pointSourceHash = Hash(Path.Combine(output, "source_points.png"));
                manifest.edgeSourceHash = Hash(Path.Combine(output, "source_edge.png"));
                Vector4 spread = CrtReferenceScreen.OpticalSpread;
                for (int c = 0; c < 3; c++) manifest.lod[c] = Mathf.Max(0, Mathf.Log(spread[c] * PsfWidth / (1280 * CrtReferenceScreen.Aperture.width) * 3.4641f, 2));
                Color[] controlPoints = RenderPsfRaw(profile, points, Vector3.zero, manifest.lod, camera, renderer, raw, objects);
                Save(raw, Path.Combine(output, "point_control.png"));
                manifest.control = MeasurePsfPoints(controlPoints, Vector3.zero);
                manifest.linearTextureProbe = MeasurePsfPoints(RenderPsfRaw(profile, linearPoints, Vector3.zero, manifest.lod, camera, renderer, raw, objects), Vector3.zero);
                for (int level = 0; level <= 2; level++)
                {
                    float srgb = points.GetPixel(240 >> level, 160 >> level, level).r;
                    manifest.mipProbe.Add(new PsfMipValue { level = level, srgbTextureCpuValue = srgb,
                        linearTextureCpuValue = linearPoints.GetPixel(240 >> level, 160 >> level, level).r,
                        decodedSrgbValue = Mathf.GammaToLinearSpace(srgb) });
                }
                File.WriteAllText(Path.Combine(output, "mip-probe.json"), JsonUtility.ToJson(manifest, true));
                Vector3 lodLow = Vector3.zero, lodHigh = Vector3.one * 3;
                for (int iteration = 0; iteration < 10; iteration++)
                {
                    Vector3 trial = (lodLow + lodHigh) * 0.5f;
                    PsfMeasurement measurement = MeasurePsfPoints(RenderPsfRaw(profile, points, Vector3.zero, manifest.lod, camera, renderer, raw, objects, 1, trial), Vector3.zero);
                    measurement.lod = trial; manifest.linearMipTrials.Add(measurement);
                    for (int c = 0; c < 3; c++)
                        if (measurement.varianceX[c] + measurement.varianceY[c] < manifest.control.varianceX[c] + manifest.control.varianceY[c]) lodLow[c] = trial[c];
                        else lodHigh[c] = trial[c];
                }
                manifest.linearLod = (lodLow + lodHigh) * 0.5f;
                manifest.linearMip = MeasurePsfPoints(RenderPsfRaw(profile, points, Vector3.zero, manifest.lod, camera, renderer, raw, objects, 1, manifest.linearLod), Vector3.zero);
                manifest.linearMip.lod = manifest.linearLod;
                Save(raw, Path.Combine(output, "point_linear_mip.png"));
                Vector3 low = Vector3.one * 0.05f, high = Vector3.one * 3f;
                for (int iteration = 0; iteration < 10; iteration++)
                {
                    Vector3 trial = (low + high) * 0.5f;
                    PsfMeasurement measurement = MeasurePsfPoints(RenderPsfRaw(profile, points, trial, manifest.lod, camera, renderer, raw, objects), trial);
                    manifest.fittingTrials.Add(measurement);
                    for (int c = 0; c < 3; c++)
                        if (measurement.varianceX[c] + measurement.varianceY[c] < manifest.control.varianceX[c] + manifest.control.varianceY[c]) low[c] = trial[c];
                        else high[c] = trial[c];
                }
                manifest.sigma = (low + high) * 0.5f;
                Color[] candidatePoints = RenderPsfRaw(profile, points, manifest.sigma, manifest.lod, camera, renderer, raw, objects);
                Save(raw, Path.Combine(output, "point_candidate.png"));
                manifest.candidate = MeasurePsfPoints(candidatePoints, manifest.sigma);
                profile.SetTexture("_CrtPsfLinearTex", correctLinearEdge);
                Color[] controlEdge = RenderPsfRaw(profile, edge, Vector3.zero, manifest.lod, camera, renderer, raw, objects);
                Save(raw, Path.Combine(output, "edge_control.png")); MeasurePsfEdge(controlEdge, manifest.control);
                Color[] linearEdge = RenderPsfRaw(profile, edge, Vector3.zero, manifest.lod, camera, renderer, raw, objects, 1, manifest.linearLod);
                Save(raw, Path.Combine(output, "edge_linear_mip.png")); MeasurePsfEdge(linearEdge, manifest.linearMip);
                Color[] candidateEdge = RenderPsfRaw(profile, edge, manifest.sigma, manifest.lod, camera, renderer, raw, objects);
                Save(raw, Path.Combine(output, "edge_candidate.png")); MeasurePsfEdge(candidateEdge, manifest.candidate);
                var gray = new Texture2D(640, 480, TextureFormat.RGB24, true, false); objects.Add(gray);
                var grayPixels = new Color32[640 * 480]; for (int i = 0; i < grayPixels.Length; i++) grayPixels[i] = new Color32(128, 128, 128, 255);
                gray.SetPixels32(grayPixels); gray.Apply(true, false);
                var grayLinear = LinearPsfTexture(gray); objects.Add(grayLinear);
                var grayWrongFlag = new Texture2D(2, 2, TextureFormat.RGB24, true, true); objects.Add(grayWrongFlag);
                if (!grayWrongFlag.LoadImage(gray.EncodeToPNG())) throw new InvalidDataException("Cannot load wrong-flag calibration");
                profile.SetTexture("_CrtPsfLinearTex", grayLinear);
                Color[] grayControl = RenderPsfRaw(profile, gray, Vector3.zero, Vector3.zero, camera, renderer, raw, objects, 0, Vector3.zero);
                Save(raw, Path.Combine(output, "gray_original.png"));
                Color[] grayCorrect = RenderPsfRaw(profile, gray, Vector3.zero, Vector3.zero, camera, renderer, raw, objects, 1, Vector3.zero);
                Save(raw, Path.Combine(output, "gray_linear_correct.png")); manifest.grayBaseError = PsfMaxDifference(grayControl, grayCorrect);
                Color[] grayWrong = RenderPsfRaw(profile, grayWrongFlag, Vector3.zero, Vector3.zero, camera, renderer, raw, objects, 0, Vector3.zero);
                Save(raw, Path.Combine(output, "gray_wrong_flag.png")); manifest.grayWrongFlagError = PsfMaxDifference(grayControl, grayWrong);
                if (manifest.grayBaseError > 0.003f || manifest.grayWrongFlagError < 0.1f) throw new InvalidOperationException("Base-level color calibration failed");
                File.WriteAllText(Path.Combine(output, "calibration.json"), JsonUtility.ToJson(manifest, true));
                for (int c = 0; c < 3; c++)
                {
                    float controlVariance = manifest.control.varianceX[c] + manifest.control.varianceY[c];
                    float candidateVariance = manifest.candidate.varianceX[c] + manifest.candidate.varianceY[c];
                    float linearVariance = manifest.linearMip.varianceX[c] + manifest.linearMip.varianceY[c];
                    if (Mathf.Abs(manifest.linearMip.energy[c] - 1) > 0.03f || Mathf.Abs(manifest.candidate.energy[c] - 1) > 0.03f
                        || Mathf.Abs(candidateVariance / controlVariance - 1) > 0.02f
                        || Mathf.Abs(linearVariance / controlVariance - 1) > 0.02f
                        || manifest.candidate.pointSecondaryRise[c] > 0.05f || manifest.candidate.edgeSecondaryRise[c] > 0.05f
                        || manifest.candidate.edgeNegativeSlope[c] > 0.001f)
                        throw new InvalidOperationException("PSF calibration failed; photos were not rendered. Channel " + c);
                }
                Debug.Log("[CrtPsf] calibration passed; sigma=" + manifest.sigma + " energy=" + manifest.candidate.energy);
                filter.sharedMesh = curved; screen.transform.localScale = new Vector3(CrtReferenceScreen.CaseAspect, 1, 1);
                camera.allowHDR = false; camera.aspect = CrtReferenceScreen.CaseAspect;
                var final = new RenderTexture(1280, 1030, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                if (!final.Create()) throw new InvalidOperationException("Cannot create photo RT"); objects.Add(final); camera.targetTexture = final;
                string[] names = { "plate_A_20260924_172209_b8e1f375.jpg", "plate_B_20260924_172210_4abb5bae.jpg", "plate_C_20260924_172211_a1c7c02f.jpg" };
                foreach (string name in names)
                {
                    string plate = name.Substring(6, 1), source = Path.Combine(root, "tools/web-compositor/captures", name);
                    Capture expected = baseline.captures.Find(c => c.file == plate + "_front.png") ?? throw new InvalidDataException("Missing baseline");
                    if (!File.Exists(source) || Hash(source) != expected.sourceHash) throw new InvalidDataException("Photo changed: " + source);
                    var texture = new Texture2D(2, 2, TextureFormat.RGB24, true, false) { filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp }; objects.Add(texture);
                    if (!texture.LoadImage(File.ReadAllBytes(source)) || texture.width != 640 || texture.height != 480 || texture.format != TextureFormat.RGB24)
                        throw new InvalidDataException("Expected actual 640x480 RGB24 photo");
                    var linearTexture = LinearPsfTexture(texture); objects.Add(linearTexture);
                    profile.SetTexture("_CrtPsfLinearTex", linearTexture);
                    camera.targetTexture = raw; camera.allowHDR = true;
                    Color[] baseOriginal = RenderPsfRaw(profile, texture, Vector3.zero, Vector3.zero, camera, renderer, raw, objects, 0, Vector3.zero);
                    Color[] baseLinear = RenderPsfRaw(profile, texture, Vector3.zero, Vector3.zero, camera, renderer, raw, objects, 1, Vector3.zero);
                    float baseError = PsfMaxDifference(baseOriginal, baseLinear);
                    if (baseError > 0.003f) throw new InvalidDataException("Photo base-level decode mismatch: " + plate + " " + baseError);
                    camera.targetTexture = final; camera.allowHDR = false;
                    profile.SetTexture("_LiveTex", texture); profile.SetVector("_LiveScale", new Vector4(0.75f, 1, 0, 0));
                    profile.SetVector("_CgScale", new Vector4(0.75f, 1, 0, 0)); profile.SetFloat("_FrameAspect", CrtReferenceScreen.LogicalAspect);
                    ApplyPost(profile, cfg.post);
                    var feel = CameraFeelFx.Settings.Resolve(cfg.feel);
                    var logic = new CameraFeelLogic { TargetLuma = feel.TargetLuma, FollowHalfLifeSec = feel.FollowSec };
                    float mean = 0; int count = 0;
                    for (int y = 0; y < texture.height; y += texture.height / 16) for (int x = 0; x < texture.width; x += texture.width / 16)
                    { Color p = texture.GetPixel(x, y); mean += p.r * 0.299f + p.g * 0.587f + p.b * 0.114f; count++; }
                    CameraFeelFx.WriteUniforms(profile, feel.NoiseDark, feel.NoiseFixed, logic.SteadyBiasFor(mean / count) * feel.Agc, 0, 0, 0, 0);
                    profile.SetVector("_CrtView", new Vector4(0, 0, -1, 0));
                    profile.SetVector("_CrtPsfSigma", manifest.sigma); profile.SetFloat("_CrtPsfStage", 0);
                    profile.SetVector("_CrtPsfLinearLod", manifest.linearLod);
                    string invariant = ParameterHash(profile, "_CrtPsfMode");
                    for (int arm = 0; arm < 3; arm++)
                    {
                        var material = new Material(profile) { hideFlags = HideFlags.HideAndDontSave }; objects.Add(material);
                        material.SetFloat("_CrtPsfMode", arm);
                        if (arm > 0) material.EnableKeyword("CRT_PSF_DIAGNOSTIC");
                        if (ParameterHash(material, "_CrtPsfMode") != invariant || material.IsKeywordEnabled("CRT_ORDER_DIAGNOSTIC")) throw new InvalidOperationException("Uncontrolled PSF photo property");
                        CompilePsf(material); renderer.sharedMaterial = material; camera.Render();
                        string file = plate + (arm == 0 ? "_control.png" : arm == 1 ? "_linear_mip.png" : "_gaussian.png"); Save(final, Path.Combine(output, file));
                        var capture = new OpticsCapture { plate = plate, source = source, sourceHash = Hash(source), file = file,
                            sha256 = Hash(Path.Combine(output, file)), invariantHash = invariant, parameters = OpticsParameters(material),
                            width = 1280, height = 1030, spreadMultiplier = 1, convergenceMultiplier = 1,
                            spread = CrtReferenceScreen.OpticalSpread, convergence = CrtReferenceScreen.OpticalConvergence };
                        ValidateOpticsImage(Path.Combine(output, file), capture, objects);
                        if (arm == 0 && !(capture.currentHashMatches = capture.sha256 == expected.sha256)) throw new InvalidDataException("Control differs from round 28: " + plate);
                        manifest.photos.Add(new PsfPhoto { capture = capture, gaussianKeyword = arm > 0, mode = arm, baseLevelMaxError = baseError });
                    }
                }
                foreach (PsfPhoto photo in manifest.photos)
                    if (Hash(Path.Combine(output, photo.capture.file)) != photo.capture.sha256) throw new InvalidDataException("Photo changed after capture");
                File.WriteAllText(Path.Combine(output, "manifest.json.tmp"), JsonUtility.ToJson(manifest, true));
                File.Move(Path.Combine(output, "manifest.json.tmp"), Path.Combine(output, "manifest.json"));
                Debug.Log("[CrtPsf] 9 photos; all controls match round28; shader errors=0 -> " + output);
            }
            finally
            {
                ShaderUtil.allowAsyncCompilation = oldAsync; RenderTexture.active = old;
                for (int i = objects.Count - 1; i >= 0; i--)
                { if (objects[i] is RenderTexture rt) rt.Release(); UnityEngine.Object.DestroyImmediate(objects[i]); }
            }
        }

        private static Texture2D PsfPattern(bool edge, bool linear = false)
        {
            var texture = new Texture2D(PsfWidth, PsfHeight, TextureFormat.RGB24, true, false) { filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[PsfWidth * PsfHeight];
            for (int y = 0; y < PsfHeight; y++) for (int x = 0; x < PsfWidth; x++)
            {
                bool white = edge ? x + 0.67f * y >= 480 : x >= 240 && x <= 339 && (x - 240) % 33 == 0 && y >= 160 && y <= 259 && (y - 160) % 33 == 0;
                pixels[y * PsfWidth + x] = white ? new Color32(255, 255, 255, 255) : new Color32(0, 0, 0, 255);
            }
            texture.SetPixels32(pixels); texture.Apply(true, false);
            // Use the same LoadImage mip-generation path as the actual JPG plates.
            byte[] png = texture.EncodeToPNG(); UnityEngine.Object.DestroyImmediate(texture);
            var loaded = new Texture2D(2, 2, TextureFormat.RGB24, true, linear) { filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp };
            if (!loaded.LoadImage(png)) throw new InvalidDataException("Cannot load generated calibration pattern");
            return loaded;
        }

        private static void CompilePsf(Material material)
        {
            ShaderUtil.CompilePass(material, 0, true);
            foreach (var message in ShaderUtil.GetShaderMessages(material.shader))
                if (message.severity == ShaderCompilerMessageSeverity.Error) throw new InvalidOperationException(message.message);
            if (!ShaderUtil.IsPassCompiled(material, 0)) throw new InvalidOperationException("PSF pass not compiled");
        }

        private static Texture2D LinearPsfTexture(Texture2D source)
        {
            var texture = new Texture2D(source.width, source.height, TextureFormat.RGBAFloat, true, true)
            { filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp };
            Color[] pixels = source.GetPixels(0);
            for (int i = 0; i < pixels.Length; i++) pixels[i] = pixels[i].linear;
            texture.SetPixels(pixels); texture.Apply(true, false); return texture;
        }

        private static float PsfMaxDifference(Color[] a, Color[] b)
        {
            if (a.Length != b.Length) throw new InvalidDataException("Mismatched raw readback sizes");
            float error = 0;
            for (int i = 0; i < a.Length; i++) for (int c = 0; c < 3; c++) error = Mathf.Max(error, Mathf.Abs(a[i][c] - b[i][c]));
            return error;
        }

        private static Color[] RenderPsfRaw(Material profile, Texture texture, Vector3 sigma, Vector3 lod,
            Camera camera, Renderer renderer, RenderTexture target, List<UnityEngine.Object> objects, int mode = -1, Vector3? linearLod = null)
        {
            var material = new Material(profile) { hideFlags = HideFlags.HideAndDontSave }; objects.Add(material);
            material.EnableKeyword("CRT_PSF_DIAGNOSTIC"); material.SetFloat("_CrtPsfStage", 1);
            material.SetFloat("_CrtPsfMode", mode >= 0 ? mode : sigma.sqrMagnitude > 0.0001f ? 2 : 0);
            material.SetVector("_CrtPsfLinearLod", linearLod ?? lod);
            material.SetVector("_CrtPsfSigma", sigma); material.SetVector("_CrtPsfLod", lod); material.SetTexture("_LiveTex", texture);
            CompilePsf(material); renderer.sharedMaterial = material; camera.Render(); RenderTexture.active = target;
            var readback = new Texture2D(target.width, target.height, TextureFormat.RGBAFloat, false, true);
            try { readback.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); readback.Apply(); return readback.GetPixels(); }
            finally { UnityEngine.Object.DestroyImmediate(readback); }
        }

        private static PsfMeasurement MeasurePsfPoints(Color[] image, Vector3 sigma)
        {
            var result = new PsfMeasurement { sigma = sigma };
            int width = PsfWidth * PsfScale;
            for (int c = 0; c < 3; c++)
            {
                double energy = 0, vx = 0, vy = 0, angular = 0; float secondary = 0;
                for (int py = 160; py <= 259; py += 33) for (int px = 240; px <= 339; px += 33)
                {
                    int x0 = (px - 12) * PsfScale, x1 = (px + 12) * PsfScale;
                    int y0 = (py - 12) * PsfScale, y1 = (py + 12) * PsfScale;
                    double mass = 0, mx = 0, my = 0;
                    for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++)
                    { double value = image[y * width + x][c]; mass += value; mx += x * value; my += y * value; }
                    if (mass <= 0) throw new InvalidDataException("Empty point image"); mx /= mass; my /= mass;
                    double xx = 0, yy = 0, xxxx = 0, yyyy = 0, xxyy = 0;
                    for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++)
                    {
                        double value = image[y * width + x][c], dx = (x - mx) / PsfScale, dy = (y - my) / PsfScale;
                        xx += value * dx * dx; yy += value * dy * dy;
                        xxxx += value * dx * dx * dx * dx; yyyy += value * dy * dy * dy * dy; xxyy += value * dx * dx * dy * dy;
                    }
                    energy += mass / (PsfScale * PsfScale); vx += xx / mass; vy += yy / mass;
                    angular += (xxxx + yyyy - 6 * xxyy) / Math.Max(xxxx + yyyy + 2 * xxyy, 1e-9);
                    var row = new float[x1 - x0]; var column = new float[y1 - y0];
                    for (int x = x0; x < x1; x++) row[x - x0] = image[Mathf.RoundToInt((float)my) * width + x][c];
                    for (int y = y0; y < y1; y++) column[y - y0] = image[y * width + Mathf.RoundToInt((float)mx)][c];
                    secondary = Mathf.Max(secondary, PsfOutwardRise(row), PsfOutwardRise(column));
                }
                result.energy[c] = (float)(energy / 16); result.varianceX[c] = (float)(vx / 16); result.varianceY[c] = (float)(vy / 16);
                result.angularFourth[c] = (float)(angular / 16); result.pointSecondaryRise[c] = secondary;
            }
            return result;
        }

        private static void MeasurePsfEdge(Color[] image, PsfMeasurement result)
        {
            Vector2 normal = new Vector2(1, 0.67f).normalized, tangent = new Vector2(-normal.y, normal.x), origin = new Vector2(319.2f, 240);
            for (int c = 0; c < 3; c++)
            {
                var profile = new float[129]; double ripple = 0;
                for (int i = 0; i < profile.Length; i++)
                {
                    float distance = (i - 64) * 0.125f; double sum = 0, squares = 0;
                    for (int j = -160; j <= 160; j++)
                    {
                        Vector2 p = origin + normal * distance + tangent * (j * 0.5f);
                        double value = PsfRead(image, p, c); sum += value; squares += value * value;
                    }
                    profile[i] = (float)(sum / 321); if (Mathf.Abs(distance) <= 4) ripple += Math.Max(0, squares / 321 - profile[i] * profile[i]);
                }
                var derivative = new float[profile.Length - 1]; float negative = 0;
                for (int i = 0; i < derivative.Length; i++) { derivative[i] = (profile[i + 1] - profile[i]) / 0.125f; negative = Mathf.Max(negative, -derivative[i]); }
                result.edgeNegativeSlope[c] = negative; result.edgeSecondaryRise[c] = PsfOutwardRise(derivative);
                result.edgeTangentRippleRms[c] = (float)Math.Sqrt(ripple / 65);
            }
        }

        private static float PsfRead(Color[] image, Vector2 source, int channel)
        {
            float x = (source.x + 0.5f) * PsfScale - 0.5f, y = (source.y + 0.5f) * PsfScale - 0.5f;
            int ix = Mathf.FloorToInt(x), iy = Mathf.FloorToInt(y), width = PsfWidth * PsfScale;
            return Mathf.Lerp(Mathf.Lerp(image[iy * width + ix][channel], image[iy * width + ix + 1][channel], x - ix),
                Mathf.Lerp(image[(iy + 1) * width + ix][channel], image[(iy + 1) * width + ix + 1][channel], x - ix), y - iy);
        }

        private static float PsfOutwardRise(float[] values)
        {
            int peak = 0; for (int i = 1; i < values.Length; i++) if (values[i] > values[peak]) peak = i;
            float rise = 0;
            for (int direction = -1; direction <= 1; direction += 2)
            {
                float minimum = values[peak];
                for (int i = peak + direction; i >= 0 && i < values.Length; i += direction)
                { rise = Mathf.Max(rise, values[i] - minimum); minimum = Mathf.Min(minimum, values[i]); }
            }
            return rise / Mathf.Max(values[peak], 1e-9f);
        }
    }
}
