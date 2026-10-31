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
        [Serializable] private sealed class CandidateManifest
        {
            public string utc = "", unity = "", shaderHash = "", helperHash = "", psfHelperHash = "", opticsHelperHash = "", previewHash = "";
            public string showHash = "", runtimeProfileHash = "", legacyManifestHash = "", previousManifestHash = "", pointSourceHash = "";
            public string experiment = "One integrated candidate, not a factor-isolated comparison. Legacy controls use original mip and must match round28 hashes.";
            public string meaning = "Gaussian sigma is in 640x480 source pixels, smaller than the previous width-matched Gaussian; convergence is the measured reference-pixel RGB position offset times 1.8; only reference ImageAdjustment.w changes from -.52 to -.32. Grade Resources, DisplayContrast1.16, Glare.55 and all post/source/maps/camera remain fixed.";
            public string psf = "Positive normalized 9x9 Gaussian, integer 1-source-pixel spacing, support +/-4, mip0 of dedicated RGBAFloat linear texture. Original LiveTex remains for glare and non-PSF effects. Raw impulse calibration bypasses CA/Grade/glass; moments intentionally change.";
            public string scope = "Evaluation excludes the photographed room/furniture and includes casing, glass, and image treatment together. Calibration measurements are not similarity scores.";
            public Vector3 sigma, normalizedKernelMass;
            public Vector4 convergence, imageAdjustment;
            public PsfMeasurement legacyPoints = new PsfMeasurement(), candidatePoints = new PsfMeasurement();
            public List<OpticsMap> maps = new List<OpticsMap>();
            public List<CandidatePhoto> photos = new List<CandidatePhoto>();
        }
        [Serializable] private sealed class CandidatePhoto
        {
            public OpticsCapture capture = new OpticsCapture();
            public string condition = "", legacyHash = "";
            public bool legacyMatches, diagnosticKeyword;
            public float baseLevelMaxError;
        }

        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview Integrated CRT Candidate", priority = 265)]
        public static void RunIntegratedCandidate()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string output = EditorCliArgs.Get("out") ?? Path.Combine(Output, "processing-candidate-01");
            if (Directory.Exists(output) && Directory.GetFileSystemEntries(output).Length > 0) throw new IOException("Candidate output must be empty: " + output);
            if (!File.ReadAllText(Path.Combine(root, ".claude/canon/ROUNDS.md")).Contains("評価範囲の訂正と統合候補"))
                throw new InvalidOperationException("Missing pre-render integrated-candidate prediction");
            string previousFile = Path.Combine(Output, "processing-psf-linear-01/manifest.json"), legacyFile = Path.Combine(Output, "round-28/manifest.json");
            var previous = JsonUtility.FromJson<PsfManifest>(File.ReadAllText(previousFile));
            var legacy = JsonUtility.FromJson<Manifest>(File.ReadAllText(legacyFile));
            var cfg = JsonUtility.FromJson<Config>(File.ReadAllText(Path.Combine(root, "tools/web-compositor/show.json")));
            if (previous == null || legacy == null || cfg == null || JsonUtility.ToJson(cfg.post) != JsonUtility.ToJson(legacy.post))
                throw new InvalidDataException("Missing baseline/settings or post differs from round28");
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Art/Shaders/Streaming/ScreenComposite.shader");
            if (shader == null || !shader.isSupported) throw new InvalidOperationException("Screen shader unsupported");
            Vector4 adjustment = CrtReferenceScreen.ImageAdjustment; adjustment.w = -.32f;
            var manifest = new CandidateManifest { utc = DateTime.UtcNow.ToString("o"), unity = Application.unityVersion,
                shaderHash = Hash(Path.Combine(root, "Assets/Art/Shaders/Streaming/ScreenComposite.shader")),
                helperHash = Hash(Path.Combine(root, "Assets/Scripts/Streaming/Editor/CrtReferenceCandidatePreview.cs")),
                psfHelperHash = Hash(Path.Combine(root, "Assets/Scripts/Streaming/Editor/CrtReferencePsfPreview.cs")),
                opticsHelperHash = Hash(Path.Combine(root, "Assets/Scripts/Streaming/Editor/CrtReferenceOpticsPreview.cs")),
                previewHash = Hash(Path.Combine(root, "Assets/Scripts/Streaming/Editor/CrtReferencePreview.cs")),
                showHash = Hash(Path.Combine(root, "tools/web-compositor/show.json")), runtimeProfileHash = Hash(Path.Combine(root, "Assets/Scripts/Streaming/CrtReferenceScreen.cs")),
                previousManifestHash = Hash(previousFile), legacyManifestHash = Hash(legacyFile), sigma = new Vector3(.85f, .65f, .65f),
                convergence = CrtReferenceScreen.OpticalConvergence * 1.8f, imageAdjustment = adjustment };
            if (manifest.shaderHash != previous.shaderHash || manifest.psfHelperHash != previous.helperHash || manifest.showHash != previous.showHash
                || manifest.runtimeProfileHash != legacy.runtimeProfileHash) throw new InvalidDataException("Pipeline/settings changed since validated baseline");
            foreach (OpticsMap map in previous.maps)
            {
                var importer = AssetImporter.GetAtPath(map.path) as TextureImporter;
                if (!File.Exists(map.path) || Hash(map.path) != map.sha256 || importer == null || EditorJsonUtility.ToJson(importer) != map.importer)
                    throw new InvalidDataException("Map/importer changed: " + map.path);
                manifest.maps.Add(map);
            }
            Directory.CreateDirectory(output);
            var objects = new List<UnityEngine.Object>(); RenderTexture old = RenderTexture.active; bool oldAsync = ShaderUtil.allowAsyncCompilation;
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                var profile = new Material(shader) { hideFlags = HideFlags.HideAndDontSave }; objects.Add(profile);
                if (!CrtReferenceScreen.Apply(profile)) throw new InvalidOperationException("Missing CRT maps");
                profile.DisableKeyword("CRT_ORDER_DIAGNOSTIC"); profile.DisableKeyword("CRT_PSF_DIAGNOSTIC");
                var screen = new GameObject("IntegratedCandidateScreen") { hideFlags = HideFlags.HideAndDontSave, layer = 1 }; objects.Add(screen);
                screen.transform.position = new Vector3(1000, 0, 0); screen.transform.localScale = new Vector3(4f / 3f, 1, 1);
                var flat = CrtScreenMesh.BuildMesh(48, 0); objects.Add(flat); var curved = CrtScreenMesh.BuildMesh(48, .055f); objects.Add(curved);
                var filter = screen.AddComponent<MeshFilter>(); filter.sharedMesh = flat; var renderer = screen.AddComponent<MeshRenderer>();
                var go = new GameObject("IntegratedCandidateCamera") { hideFlags = HideFlags.HideAndDontSave }; objects.Add(go);
                var camera = go.AddComponent<Camera>(); camera.enabled = false;
                camera.transform.SetPositionAndRotation(new Vector3(1000, 0, -2), Quaternion.identity);
                camera.orthographic = true; camera.orthographicSize = .5f; camera.nearClipPlane = .1f; camera.farClipPlane = 4;
                camera.cullingMask = 1 << 1; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
                camera.allowHDR = true; camera.allowMSAA = false; camera.aspect = 4f / 3f;
                var raw = new RenderTexture(2560, 1920, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
                if (!raw.Create()) throw new InvalidOperationException("Cannot create candidate calibration RT"); objects.Add(raw); camera.targetTexture = raw;
                var points = PsfPattern(false); objects.Add(points); var linearPoints = LinearPsfTexture(points); objects.Add(linearPoints);
                profile.SetTexture("_CrtPsfLinearTex", linearPoints);
                File.WriteAllBytes(Path.Combine(output, "source_points.png"), points.EncodeToPNG()); manifest.pointSourceHash = Hash(Path.Combine(output, "source_points.png"));
                manifest.legacyPoints = MeasurePsfPoints(RenderPsfRaw(profile, points, Vector3.zero, previous.lod, camera, renderer, raw, objects, 0), Vector3.zero);
                Save(raw, Path.Combine(output, "point_control.png"));
                manifest.candidatePoints = MeasurePsfPoints(RenderPsfRaw(profile, points, manifest.sigma, previous.lod, camera, renderer, raw, objects, 2), manifest.sigma);
                Save(raw, Path.Combine(output, "point_candidate.png"));
                for (int c = 0; c < 3; c++)
                {
                    double sum = 0, normalized = 0, sigma = manifest.sigma[c];
                    for (int y = -4; y <= 4; y++) for (int x = -4; x <= 4; x++) sum += Math.Exp(-(x * x + y * y) / (2 * sigma * sigma));
                    for (int y = -4; y <= 4; y++) for (int x = -4; x <= 4; x++) normalized += Math.Exp(-(x * x + y * y) / (2 * sigma * sigma)) / sum;
                    manifest.normalizedKernelMass[c] = (float)normalized;
                    if (Math.Abs(normalized - 1) > 1e-12 || Mathf.Abs(manifest.candidatePoints.energy[c] - 1) > .03f
                        || manifest.candidatePoints.pointSecondaryRise[c] > .05f) throw new InvalidOperationException("Candidate Gaussian normalization/impulse calibration failed");
                }
                File.WriteAllText(Path.Combine(output, "calibration.json"), JsonUtility.ToJson(manifest, true));
                filter.sharedMesh = curved; screen.transform.localScale = new Vector3(CrtReferenceScreen.CaseAspect, 1, 1);
                camera.allowHDR = false; camera.aspect = CrtReferenceScreen.CaseAspect;
                var final = new RenderTexture(1280, 1030, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                if (!final.Create()) throw new InvalidOperationException("Cannot create candidate photo RT"); objects.Add(final); camera.targetTexture = final;
                string[] names = { "plate_A_20260924_172209_b8e1f375.jpg", "plate_B_20260924_172210_4abb5bae.jpg", "plate_C_20260924_172211_a1c7c02f.jpg" };
                string[] excluded = { "_CrtPsfMode", "_CrtPsfSigma", "_CrtOpticalConvergence", "_CrtImageAdjustment" };
                foreach (string name in names)
                {
                    string plate = name.Substring(6, 1), source = Path.Combine(root, "tools/web-compositor/captures", name);
                    Capture expected = legacy.captures.Find(c => c.file == plate + "_front.png") ?? throw new InvalidDataException("Missing legacy baseline");
                    if (!File.Exists(source) || Hash(source) != expected.sourceHash) throw new InvalidDataException("Source photo changed: " + plate);
                    var texture = new Texture2D(2, 2, TextureFormat.RGB24, true, false) { filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp }; objects.Add(texture);
                    if (!texture.LoadImage(File.ReadAllBytes(source)) || texture.width != 640 || texture.height != 480 || texture.format != TextureFormat.RGB24)
                        throw new InvalidDataException("Expected actual 640x480 RGB24 source");
                    var linear = LinearPsfTexture(texture); objects.Add(linear); profile.SetTexture("_CrtPsfLinearTex", linear);
                    camera.targetTexture = raw; camera.allowHDR = true;
                    Color[] baseOriginal = RenderPsfRaw(profile, texture, Vector3.zero, Vector3.zero, camera, renderer, raw, objects, 0, Vector3.zero);
                    Color[] baseLinear = RenderPsfRaw(profile, texture, Vector3.zero, Vector3.zero, camera, renderer, raw, objects, 1, Vector3.zero);
                    float baseError = PsfMaxDifference(baseOriginal, baseLinear);
                    if (baseError > .003f) throw new InvalidDataException("Candidate source base-level decode mismatch: " + plate);
                    camera.targetTexture = final; camera.allowHDR = false;
                    profile.SetTexture("_LiveTex", texture); profile.SetVector("_LiveScale", new Vector4(.75f, 1, 0, 0));
                    profile.SetVector("_CgScale", new Vector4(.75f, 1, 0, 0)); profile.SetFloat("_FrameAspect", CrtReferenceScreen.LogicalAspect);
                    profile.SetVector("_CrtView", new Vector4(0, 0, -1, 0)); ApplyPost(profile, cfg.post);
                    var feel = CameraFeelFx.Settings.Resolve(cfg.feel); var logic = new CameraFeelLogic { TargetLuma = feel.TargetLuma, FollowHalfLifeSec = feel.FollowSec };
                    float mean = 0; int count = 0;
                    for (int y = 0; y < texture.height; y += texture.height / 16) for (int x = 0; x < texture.width; x += texture.width / 16)
                    { Color p = texture.GetPixel(x, y); mean += p.r * .299f + p.g * .587f + p.b * .114f; count++; }
                    CameraFeelFx.WriteUniforms(profile, feel.NoiseDark, feel.NoiseFixed, logic.SteadyBiasFor(mean / count) * feel.Agc, 0, 0, 0, 0);
                    if (Mathf.Abs(profile.GetFloat("_Glare") - .55f) > 1e-6f || Mathf.Abs(profile.GetFloat("_CrtDisplayContrast") - 1.16f) > 1e-6f)
                        throw new InvalidDataException("Fixed glare/display contrast changed");
                    string invariant = ParameterHash(profile, excluded);
                    for (int arm = 0; arm < 2; arm++)
                    {
                        var material = new Material(profile) { hideFlags = HideFlags.HideAndDontSave }; objects.Add(material);
                        if (arm == 1)
                        {
                            material.EnableKeyword("CRT_PSF_DIAGNOSTIC"); material.SetFloat("_CrtPsfMode", 2);
                            material.SetVector("_CrtPsfSigma", manifest.sigma); material.SetVector("_CrtOpticalConvergence", manifest.convergence);
                            material.SetVector("_CrtImageAdjustment", manifest.imageAdjustment);
                        }
                        if (ParameterHash(material, excluded) != invariant || material.IsKeywordEnabled("CRT_ORDER_DIAGNOSTIC"))
                            throw new InvalidOperationException("Integrated candidate changed an uncontrolled property");
                        CompilePsf(material); renderer.sharedMaterial = material; camera.Render();
                        string file = plate + (arm == 0 ? "_control.png" : "_candidate.png"); Save(final, Path.Combine(output, file));
                        var capture = new OpticsCapture { plate = plate, source = source, sourceHash = Hash(source), file = file,
                            sha256 = Hash(Path.Combine(output, file)), invariantHash = invariant, parameters = OpticsParameters(material), width = 1280, height = 1030,
                            spread = CrtReferenceScreen.OpticalSpread, convergence = material.GetVector("_CrtOpticalConvergence"), spreadMultiplier = 1, convergenceMultiplier = arm == 0 ? 1 : 1.8f };
                        ValidateOpticsImage(Path.Combine(output, file), capture, objects);
                        bool matches = arm == 0 && capture.sha256 == expected.sha256;
                        if (arm == 0 && !matches) throw new InvalidDataException("Legacy control differs from round28: " + plate);
                        manifest.photos.Add(new CandidatePhoto { capture = capture, condition = arm == 0 ? "legacy" : "integrated",
                            legacyHash = expected.sha256, legacyMatches = matches, diagnosticKeyword = material.IsKeywordEnabled("CRT_PSF_DIAGNOSTIC"), baseLevelMaxError = baseError });
                    }
                }
                foreach (CandidatePhoto photo in manifest.photos)
                    if (Hash(Path.Combine(output, photo.capture.file)) != photo.capture.sha256) throw new InvalidDataException("Candidate photo changed after capture");
                File.WriteAllText(Path.Combine(output, "manifest.json.tmp"), JsonUtility.ToJson(manifest, true)); File.Move(Path.Combine(output, "manifest.json.tmp"), Path.Combine(output, "manifest.json"));
                Debug.Log("[CrtCandidate] 6 photos; 3 controls match round28; normalized PSF impulse/base-level gates passed; shader errors=0 -> " + output);
            }
            finally
            {
                ShaderUtil.allowAsyncCompilation = oldAsync; RenderTexture.active = old;
                for (int i = objects.Count - 1; i >= 0; i--)
                { if (objects[i] is RenderTexture rt) rt.Release(); UnityEngine.Object.DestroyImmediate(objects[i]); }
            }
        }
    }
}
