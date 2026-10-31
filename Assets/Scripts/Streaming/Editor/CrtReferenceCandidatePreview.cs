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
            public string candidate01ManifestHash = "", changedProperty = "";
            public string baselineManifestHash = "", baselineCondition = "";
            public string support = "Square integer support +/-4 source pixels. Omitted mass compares the unnormalized discrete Gaussian with +/-32; boundary mass is normalized weight on the outer ring. Moments are measured from the rendered impulse, including sampling.";
            public Vector3 baselineSigma, supportOmittedDiscreteMass, supportBoundaryMass, supportKernelVariance, unboundedKernelVariance;
            public Vector4 redAlignedConvergence;
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
            public string candidate01Hash = "", comparisonInvariantHash = "", keywords = "";
            public bool candidate01Matches;
            public string baselineHash = "";
            public bool baselineMatches;
        }

        private enum CandidateVariation { Integrated, RedAligned, GlareRemoved, GaussianSoft, GradeBypass }

        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview Integrated CRT Candidate", priority = 265)]
        public static void RunIntegratedCandidate()
        {
            RunCandidateComparison(CandidateVariation.Integrated);
        }

        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview Red Aligned CRT Candidate", priority = 266)]
        public static void RunRedAlignedCandidate()
        {
            RunCandidateComparison(CandidateVariation.RedAligned);
        }

        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview Glare Removed CRT Candidate", priority = 267)]
        public static void RunGlareRemovedCandidate()
        {
            RunCandidateComparison(CandidateVariation.GlareRemoved);
        }

        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview Gaussian Soft CRT Candidate", priority = 268)]
        public static void RunGaussianSoftCandidate()
        {
            RunCandidateComparison(CandidateVariation.GaussianSoft);
        }

        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview Grade Bypass CRT Candidate", priority = 269)]
        public static void RunGradeBypassCandidate()
        {
            RunCandidateComparison(CandidateVariation.GradeBypass);
        }

        private static void RunCandidateComparison(CandidateVariation variation)
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            bool redAligned = variation == CandidateVariation.RedAligned, glareRemoved = variation == CandidateVariation.GlareRemoved;
            bool gaussianSoft = variation == CandidateVariation.GaussianSoft, comparison = variation != CandidateVariation.Integrated;
            bool gradeBypass = variation == CandidateVariation.GradeBypass, baseline03 = gaussianSoft || variation == CandidateVariation.GradeBypass;
            string comparisonProperty = gradeBypass ? "_CrtGradeBypass" : gaussianSoft ? "_CrtPsfSigma" : glareRemoved ? "_Glare" : "_CrtOpticalConvergence";
            string candidateName = gradeBypass ? "candidate05" : gaussianSoft ? "candidate04" : glareRemoved ? "candidate03" : redAligned ? "candidate02" : "candidate01";
            string baselineName = baseline03 ? "candidate03" : "candidate01", baselineCondition = baseline03 ? "glare_removed" : "integrated";
            string output = EditorCliArgs.Get("out") ?? Path.Combine(Output, "processing-" + candidateName.Insert(9, "-"));
            if (Directory.Exists(output) && Directory.GetFileSystemEntries(output).Length > 0) throw new IOException("Candidate output must be empty: " + output);
            if (!File.ReadAllText(Path.Combine(root, ".claude/canon/ROUNDS.md")).Contains(gradeBypass ? "明暗曲線の適用だけを外す" : gaussianSoft ? "Gaussian幅だけを広げる" : glareRemoved ? "にじみ加算だけを除く" : redAligned ? "赤の位置差だけを戻す" : "評価範囲の訂正と統合候補"))
                throw new InvalidOperationException("Missing pre-render integrated-candidate prediction");
            string baselineFile = Path.Combine(Output, "processing-" + baselineName.Insert(9, "-") + "/manifest.json");
            CandidateManifest? baselineCandidate = comparison ? JsonUtility.FromJson<CandidateManifest>(File.ReadAllText(baselineFile)) : null;
            if (comparison && (baselineCandidate == null || baselineCandidate.photos.Count != (baseline03 ? 9 : 6))) throw new InvalidDataException("Missing " + baselineName + " baseline");
            if (baselineCandidate != null)
                foreach (CandidatePhoto photo in baselineCandidate.photos)
                    if (Hash(Path.Combine(Path.GetDirectoryName(baselineFile)!, photo.capture.file)) != photo.capture.sha256)
                        throw new InvalidDataException("Baseline image changed: " + photo.capture.file);
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
            manifest.baselineSigma = manifest.sigma;
            if (baselineCandidate != null)
            {
                manifest.baselineManifestHash = Hash(baselineFile); manifest.baselineCondition = baselineCondition;
                if (!baseline03) manifest.candidate01ManifestHash = manifest.baselineManifestHash;
                manifest.experiment = gradeBypass
                    ? "Three arms: legacy28, reproduced candidate03, candidate05. Candidate05 changes only _CrtGradeBypass from0 to1; Grade texture is unchanged, and all other properties/keywords match candidate03."
                    : gaussianSoft
                    ? "Three arms: legacy28, reproduced candidate03, candidate04. Candidate04 changes only _CrtPsfSigma from(.85,.65,.65) to(1.4,1.1,1.1); Glare remains0, all other properties/keywords match candidate03."
                    : glareRemoved
                    ? "Three arms: legacy28, reproduced candidate01, candidate03. Candidate03 changes only _Glare from .55 to zero; convergence including red x and all other properties/keywords match candidate01."
                    : "Three arms: legacy28, reproduced candidate01, candidate02. Candidate02 changes only _CrtOpticalConvergence.x to zero; all other properties and keywords match candidate01.";
                manifest.changedProperty = gradeBypass ? "_CrtGradeBypass: 0 -> 1" : gaussianSoft ? "_CrtPsfSigma: (.85,.65,.65,0) -> (1.4,1.1,1.1,0)" : glareRemoved ? "_Glare: 0.55 -> 0" : "_CrtOpticalConvergence.x: " + manifest.convergence.x.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + " -> 0";
                if (glareRemoved) manifest.meaning = "Candidate01 settings are retained, including Gaussian sigma(.85,.65,.65), measured convergence times1.8 (red x retained), ImageAdjustment.w=-.32, Resources Grade, DisplayContrast1.16 and Lift. Only candidate03 glare changes from .55 to0; calibration bypasses glare and retains the previous PSF meaning.";
                if (gaussianSoft) manifest.meaning = "Candidate03 settings retained, including Glare0, convergence times1.8, ImageAdjustment.w=-.32, Resources Grade, DisplayContrast1.16 and Lift. Only Gaussian sigma broadens to(1.4,1.1,1.1) source pixels; support remains +/-4 with positive normalization. Impulse moments intentionally change.";
                if (gradeBypass) manifest.meaning = "Candidate03 settings retained, including Glare0, Gaussian sigma(.85,.65,.65), convergence times1.8, ImageAdjustment.w=-.32, DisplayContrast1.16, Lift and Exposure. Resources Grade texture is unchanged. Only _CrtGradeBypass changes0->1 to skip its application; calibration bypasses Grade and is not a similarity score.";
                if (redAligned) { manifest.redAlignedConvergence = manifest.convergence; manifest.redAlignedConvergence.x = 0; }
                if (manifest.shaderHash != baselineCandidate.shaderHash || manifest.psfHelperHash != baselineCandidate.psfHelperHash
                    || manifest.opticsHelperHash != baselineCandidate.opticsHelperHash || manifest.previewHash != baselineCandidate.previewHash
                    || manifest.showHash != baselineCandidate.showHash || manifest.runtimeProfileHash != baselineCandidate.runtimeProfileHash
                    || manifest.baselineSigma != baselineCandidate.sigma || manifest.convergence != baselineCandidate.convergence || manifest.imageAdjustment != baselineCandidate.imageAdjustment)
                    throw new InvalidDataException("Baseline source/pipeline/settings changed");
            }
            if (gaussianSoft) manifest.sigma = new Vector3(1.4f, 1.1f, 1.1f);
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
                    double sum = 0, normalized = 0, boundary = 0, axisBounded = 0, axisFull = 0, boundedMoment = 0, fullMoment = 0, sigma = manifest.sigma[c];
                    for (int y = -4; y <= 4; y++) for (int x = -4; x <= 4; x++) sum += Math.Exp(-(x * x + y * y) / (2 * sigma * sigma));
                    for (int y = -4; y <= 4; y++) for (int x = -4; x <= 4; x++)
                    {
                        double weight = Math.Exp(-(x * x + y * y) / (2 * sigma * sigma)) / sum; normalized += weight;
                        if (Math.Abs(x) == 4 || Math.Abs(y) == 4) boundary += weight;
                    }
                    for (int x = -32; x <= 32; x++)
                    {
                        double weight = Math.Exp(-x * x / (2 * sigma * sigma)); axisFull += weight; fullMoment += x * x * weight;
                        if (Math.Abs(x) <= 4) { axisBounded += weight; boundedMoment += x * x * weight; }
                    }
                    manifest.supportOmittedDiscreteMass[c] = (float)(1 - axisBounded * axisBounded / (axisFull * axisFull));
                    manifest.supportBoundaryMass[c] = (float)boundary;
                    manifest.supportKernelVariance[c] = (float)(boundedMoment / axisBounded);
                    manifest.unboundedKernelVariance[c] = (float)(fullMoment / axisFull);
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
                string[] excluded = gradeBypass ? new[] { "_CrtPsfMode", "_CrtPsfSigma", "_CrtOpticalConvergence", "_CrtImageAdjustment", "_Glare", "_CrtGradeBypass" }
                    : glareRemoved || gaussianSoft ? new[] { "_CrtPsfMode", "_CrtPsfSigma", "_CrtOpticalConvergence", "_CrtImageAdjustment", "_Glare" }
                    : new[] { "_CrtPsfMode", "_CrtPsfSigma", "_CrtOpticalConvergence", "_CrtImageAdjustment" };
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
                    Material? reproduced = null;
                    for (int arm = 0; arm < (comparison ? 3 : 2); arm++)
                    {
                        var material = new Material(profile) { hideFlags = HideFlags.HideAndDontSave }; objects.Add(material);
                        if (arm > 0)
                        {
                            material.EnableKeyword("CRT_PSF_DIAGNOSTIC"); material.SetFloat("_CrtPsfMode", 2);
                            material.SetVector("_CrtPsfSigma", manifest.baselineSigma); material.SetVector("_CrtOpticalConvergence", manifest.convergence);
                            material.SetVector("_CrtImageAdjustment", manifest.imageAdjustment);
                            if (baseline03) material.SetFloat("_Glare", 0);
                        }
                        if (arm == 2)
                        {
                            if (gradeBypass) material.SetFloat("_CrtGradeBypass", 1);
                            else if (gaussianSoft) material.SetVector("_CrtPsfSigma", manifest.sigma);
                            else if (glareRemoved) material.SetFloat("_Glare", 0);
                            else material.SetVector("_CrtOpticalConvergence", manifest.redAlignedConvergence);
                        }
                        if (ParameterHash(material, excluded) != invariant || material.IsKeywordEnabled("CRT_ORDER_DIAGNOSTIC"))
                            throw new InvalidOperationException("Integrated candidate changed an uncontrolled property");
                        CandidatePhoto? prior = baselineCandidate?.photos.Find(p => p.capture.plate == plate && p.condition == (arm == 0 ? "legacy" : baselineCondition));
                        if (baselineCandidate != null && prior == null) throw new InvalidDataException("Missing baseline arm: " + plate);
                        string snapshot = OpticsParameters(material);
                        if (prior != null && arm < 2 && (snapshot != prior.capture.parameters || material.IsKeywordEnabled("CRT_PSF_DIAGNOSTIC") != prior.diagnosticKeyword))
                            throw new InvalidDataException("Full property/keyword snapshot differs from baseline: " + plate);
                        if (arm == 1) reproduced = material;
                        if (arm == 2)
                        {
                            if (gradeBypass)
                            {
                                if (reproduced!.GetFloat(comparisonProperty) != 0 || material.GetFloat(comparisonProperty) != 1
                                    || ParameterHash(reproduced, comparisonProperty) != ParameterHash(material, comparisonProperty))
                                    throw new InvalidOperationException("Candidate05 changed more than GradeBypass");
                                AssertCandidatePropertyDifference(reproduced, material, prior!.capture.parameters, comparisonProperty);
                            }
                            else if (gaussianSoft)
                            {
                                if (reproduced!.GetVector("_CrtPsfSigma") != (Vector4)manifest.baselineSigma || material.GetVector("_CrtPsfSigma") != (Vector4)manifest.sigma
                                    || ParameterHash(reproduced, comparisonProperty) != ParameterHash(material, comparisonProperty))
                                    throw new InvalidOperationException("Candidate04 changed more than sigma");
                                AssertCandidatePropertyDifference(reproduced, material, prior!.capture.parameters, comparisonProperty);
                            }
                            else if (glareRemoved) AssertGlareRemovedProperties(reproduced!, material, prior!.capture.parameters);
                            else AssertRedAlignedProperties(reproduced!, material, prior!.capture.parameters);
                        }
                        CompilePsf(material); renderer.sharedMaterial = material; camera.Render();
                        string file = plate + (arm == 0 ? "_control.png" : arm == 2 ? "_" + candidateName + ".png" : comparison ? "_" + baselineName + ".png" : "_candidate.png"); Save(final, Path.Combine(output, file));
                        var capture = new OpticsCapture { plate = plate, source = source, sourceHash = Hash(source), file = file,
                            sha256 = Hash(Path.Combine(output, file)), invariantHash = invariant, parameters = OpticsParameters(material), width = 1280, height = 1030,
                            spread = CrtReferenceScreen.OpticalSpread, convergence = material.GetVector("_CrtOpticalConvergence"), spreadMultiplier = 1, convergenceMultiplier = arm == 0 ? 1 : 1.8f };
                        ValidateOpticsImage(Path.Combine(output, file), capture, objects);
                        bool matches = arm == 0 && capture.sha256 == expected.sha256;
                        if (arm == 0 && !matches) throw new InvalidDataException("Legacy control differs from round28: " + plate);
                        bool candidateMatches = arm == 1 && prior != null && capture.sha256 == prior.capture.sha256;
                        if (comparison && arm == 1 && !candidateMatches) throw new InvalidDataException("Reproduced baseline hash differs: " + plate);
                        string[] keywords = material.shaderKeywords; Array.Sort(keywords, StringComparer.Ordinal);
                        manifest.photos.Add(new CandidatePhoto { capture = capture, condition = arm == 0 ? "legacy" : arm == 2 ? gradeBypass ? "grade_bypass" : gaussianSoft ? "gaussian_soft" : glareRemoved ? "glare_removed" : "red_aligned" : baselineCondition,
                            legacyHash = expected.sha256, legacyMatches = matches, diagnosticKeyword = material.IsKeywordEnabled("CRT_PSF_DIAGNOSTIC"), baseLevelMaxError = baseError,
                            candidate01Hash = baseline03 ? "" : prior?.capture.sha256 ?? "", candidate01Matches = !baseline03 && candidateMatches,
                            baselineHash = prior?.capture.sha256 ?? "", baselineMatches = candidateMatches,
                            comparisonInvariantHash = ParameterHash(material, comparisonProperty), keywords = string.Join(";", keywords) });
                    }
                }
                foreach (CandidatePhoto photo in manifest.photos)
                    if (Hash(Path.Combine(output, photo.capture.file)) != photo.capture.sha256) throw new InvalidDataException("Candidate photo changed after capture");
                File.WriteAllText(Path.Combine(output, "manifest.json.tmp"), JsonUtility.ToJson(manifest, true)); File.Move(Path.Combine(output, "manifest.json.tmp"), Path.Combine(output, "manifest.json"));
                Debug.Log(gradeBypass ? "[CrtCandidate] 9 photos; 3 controls match round28; 3 candidate03 hashes match; only GradeBypass changed0->1; normalized PSF/base-level gates passed; shader errors=0 -> " + output
                    : gaussianSoft ? "[CrtCandidate] 9 photos; 3 controls match round28; 3 candidate03 hashes match; only Gaussian sigma changed; normalized PSF/support/base-level gates passed; shader errors=0 -> " + output
                    : glareRemoved ? "[CrtCandidate] 9 photos; 3 controls match round28; 3 candidate01 hashes match; only Glare changed .55->0; normalized PSF/base-level gates passed; shader errors=0 -> " + output
                    : redAligned ? "[CrtCandidate] 9 photos; 3 controls match round28; 3 candidate01 hashes match; only convergence.x changed; normalized PSF/base-level gates passed; shader errors=0 -> " + output
                    : "[CrtCandidate] 6 photos; 3 controls match round28; normalized PSF impulse/base-level gates passed; shader errors=0 -> " + output);
            }
            finally
            {
                ShaderUtil.allowAsyncCompilation = oldAsync; RenderTexture.active = old;
                for (int i = objects.Count - 1; i >= 0; i--)
                { if (objects[i] is RenderTexture rt) rt.Release(); UnityEngine.Object.DestroyImmediate(objects[i]); }
            }
        }

        private static void AssertRedAlignedProperties(Material candidate01, Material candidate02, string previousSnapshot)
        {
            Vector4 before = candidate01.GetVector("_CrtOpticalConvergence"), after = candidate02.GetVector("_CrtOpticalConvergence");
            if (before.x == 0 || after.x != 0 || before.y != after.y || before.z != after.z || before.w != after.w
                || ParameterHash(candidate01, "_CrtOpticalConvergence") != ParameterHash(candidate02, "_CrtOpticalConvergence"))
                throw new InvalidOperationException("Candidate02 changed more than convergence.x");
            AssertCandidatePropertyDifference(candidate01, candidate02, previousSnapshot, "_CrtOpticalConvergence");
        }

        private static void AssertGlareRemovedProperties(Material candidate01, Material candidate03, string previousSnapshot)
        {
            if (candidate01.GetFloat("_Glare") != .55f || candidate03.GetFloat("_Glare") != 0
                || ParameterHash(candidate01, "_Glare") != ParameterHash(candidate03, "_Glare"))
                throw new InvalidOperationException("Candidate03 changed more than Glare");
            AssertCandidatePropertyDifference(candidate01, candidate03, previousSnapshot, "_Glare");
        }

        private static void AssertCandidatePropertyDifference(Material candidate01, Material candidate02, string previousSnapshot, string property)
        {
            string[] first = candidate01.shaderKeywords, second = candidate02.shaderKeywords;
            Array.Sort(first, StringComparer.Ordinal); Array.Sort(second, StringComparer.Ordinal);
            if (string.Join(";", first) != string.Join(";", second)) throw new InvalidOperationException("Candidate keywords differ");
            var previousLines = previousSnapshot.Split('\n'); var currentLines = OpticsParameters(candidate02).Split('\n');
            if (previousLines.Length != currentLines.Length) throw new InvalidOperationException("Property snapshot lengths differ");
            int changed = 0;
            for (int i = 0; i < previousLines.Length; i++)
                if (previousLines[i] != currentLines[i])
                {
                    if (!previousLines[i].StartsWith(property + "=", StringComparison.Ordinal)
                        || !currentLines[i].StartsWith(property + "=", StringComparison.Ordinal))
                        throw new InvalidOperationException("Uncontrolled snapshot difference: " + currentLines[i]);
                    changed++;
                }
            if (changed != 1) throw new InvalidOperationException("Expected exactly one changed property");
        }
    }
}
