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
        [Serializable] private sealed class SoftnessManifest
        {
            public string utc = "", unity = "", shaderHash = "", helperHash = "", psfHelperHash = "", opticsHelperHash = "", previewHash = "";
            public string showHash = "", runtimeProfileHash = "", previousManifestHash = "", pointSourceHash = "";
            public string experiment = "linear PSF mip mode1; calibrated LOD vs calibrated LOD+1; original LiveTex glare current vs zero; all other properties identical";
            public string calibration = "64 isolated white source pixels spanning all 8x8 mip phases, RGB24 sRGB via LoadImage, CPU decoded RGBAFloat linear+mips. Raw PSF 4x ARGBFloat linear; energy and central moments in source-pixel units.";
            public Vector3 baseLod, strongLod;
            public SoftnessPoints basePoints = new SoftnessPoints(), strongPoints = new SoftnessPoints();
            public List<OpticsMap> maps = new List<OpticsMap>();
            public List<SoftnessPhoto> photos = new List<SoftnessPhoto>();
        }
        [Serializable] private sealed class SoftnessPoints
        {
            public Vector3 energy, varianceX, varianceY;
        }
        [Serializable] private sealed class SoftnessPhoto
        {
            public OpticsCapture capture = new OpticsCapture();
            public string baselineFile = "", baselineHash = "";
            public float lodIncrement, glare;
            public Vector3 linearLod;
            public bool baselineMatches;
        }

        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview CRT Softness Matrix", priority = 263)]
        public static void RunSoftnessMatrix()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string output = EditorCliArgs.Get("out") ?? Path.Combine(Output, "processing-softness-01");
            string previousOutput = Path.Combine(Output, "processing-psf-linear-01");
            if (Directory.Exists(output) && Directory.GetFileSystemEntries(output).Length > 0)
                throw new IOException("Softness output must be empty: " + output);
            if (!File.ReadAllText(Path.Combine(root, ".claude/canon/ROUNDS.md")).Contains("強い低域化とglareの除去"))
                throw new InvalidOperationException("Missing pre-render softness prediction");
            string previousFile = Path.Combine(previousOutput, "manifest.json");
            var previous = JsonUtility.FromJson<PsfManifest>(File.ReadAllText(previousFile));
            var cfg = JsonUtility.FromJson<Config>(File.ReadAllText(Path.Combine(root, "tools/web-compositor/show.json")));
            if (previous == null || cfg == null) throw new InvalidDataException("Missing previous manifest or show settings");
            if ((previous.linearLod - new Vector3(1.252441f, 1.050293f, 1.050293f)).sqrMagnitude > 1e-10f)
                throw new InvalidDataException("Previous calibrated LOD changed");
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Art/Shaders/Streaming/ScreenComposite.shader");
            if (shader == null || !shader.isSupported) throw new InvalidOperationException("Screen shader unsupported");
            var manifest = new SoftnessManifest { utc = DateTime.UtcNow.ToString("o"), unity = Application.unityVersion,
                shaderHash = Hash(Path.Combine(root, "Assets/Art/Shaders/Streaming/ScreenComposite.shader")),
                helperHash = Hash(Path.Combine(root, "Assets/Scripts/Streaming/Editor/CrtReferenceSoftnessPreview.cs")),
                psfHelperHash = Hash(Path.Combine(root, "Assets/Scripts/Streaming/Editor/CrtReferencePsfPreview.cs")),
                opticsHelperHash = Hash(Path.Combine(root, "Assets/Scripts/Streaming/Editor/CrtReferenceOpticsPreview.cs")),
                previewHash = Hash(Path.Combine(root, "Assets/Scripts/Streaming/Editor/CrtReferencePreview.cs")),
                runtimeProfileHash = Hash(Path.Combine(root, "Assets/Scripts/Streaming/CrtReferenceScreen.cs")),
                showHash = Hash(Path.Combine(root, "tools/web-compositor/show.json")), previousManifestHash = Hash(previousFile),
                baseLod = previous.linearLod, strongLod = previous.linearLod + Vector3.one };
            if (manifest.shaderHash != previous.shaderHash || manifest.runtimeProfileHash != previous.runtimeProfileHash
                || manifest.showHash != previous.showHash || manifest.psfHelperHash != previous.helperHash)
                throw new InvalidDataException("Pipeline/settings changed since linear-mip baseline");
            foreach (OpticsMap map in previous.maps)
            {
                var importer = AssetImporter.GetAtPath(map.path) as TextureImporter;
                if (!File.Exists(map.path) || Hash(map.path) != map.sha256 || importer == null || EditorJsonUtility.ToJson(importer) != map.importer)
                    throw new InvalidDataException("Map/importer changed: " + map.path);
                manifest.maps.Add(map);
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
                profile.DisableKeyword("CRT_ORDER_DIAGNOSTIC"); profile.EnableKeyword("CRT_PSF_DIAGNOSTIC");
                profile.SetFloat("_CrtPsfMode", 1); profile.SetVector("_CrtPsfSigma", previous.sigma);
                var screen = new GameObject("SoftnessScreen") { hideFlags = HideFlags.HideAndDontSave, layer = 1 }; objects.Add(screen);
                screen.transform.position = new Vector3(1000, 0, 0); screen.transform.localScale = new Vector3(4f / 3f, 1, 1);
                var flat = CrtScreenMesh.BuildMesh(48, 0); objects.Add(flat);
                var curved = CrtScreenMesh.BuildMesh(48, 0.055f); objects.Add(curved);
                var filter = screen.AddComponent<MeshFilter>(); filter.sharedMesh = flat;
                var renderer = screen.AddComponent<MeshRenderer>();
                var go = new GameObject("SoftnessCamera") { hideFlags = HideFlags.HideAndDontSave }; objects.Add(go);
                var camera = go.AddComponent<Camera>(); camera.enabled = false;
                camera.transform.SetPositionAndRotation(new Vector3(1000, 0, -2), Quaternion.identity);
                camera.orthographic = true; camera.orthographicSize = 0.5f;
                camera.nearClipPlane = 0.1f; camera.farClipPlane = 4; camera.cullingMask = 1 << 1;
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
                camera.allowHDR = true; camera.allowMSAA = false; camera.aspect = 4f / 3f;
                var raw = new RenderTexture(2560, 1920, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
                if (!raw.Create()) throw new InvalidOperationException("Cannot create softness calibration RT"); objects.Add(raw); camera.targetTexture = raw;
                var points = SoftnessPattern(); objects.Add(points);
                var linearPoints = LinearPsfTexture(points); objects.Add(linearPoints); profile.SetTexture("_CrtPsfLinearTex", linearPoints);
                File.WriteAllBytes(Path.Combine(output, "source_points.png"), points.EncodeToPNG());
                manifest.pointSourceHash = Hash(Path.Combine(output, "source_points.png"));
                Color[] basePixels = RenderPsfRaw(profile, points, Vector3.zero, previous.lod, camera, renderer, raw, objects, 1, manifest.baseLod);
                manifest.basePoints = MeasureSoftnessPoints(basePixels); Save(raw, Path.Combine(output, "point_base.png"));
                Color[] strongPixels = RenderPsfRaw(profile, points, Vector3.zero, previous.lod, camera, renderer, raw, objects, 1, manifest.strongLod);
                manifest.strongPoints = MeasureSoftnessPoints(strongPixels); Save(raw, Path.Combine(output, "point_lod_plus1.png"));
                File.WriteAllText(Path.Combine(output, "calibration.json"), JsonUtility.ToJson(manifest, true));
                for (int c = 0; c < 3; c++)
                    if (Mathf.Abs(manifest.basePoints.energy[c] - 1) > 0.03f || Mathf.Abs(manifest.strongPoints.energy[c] - 1) > 0.03f
                        || manifest.strongPoints.varianceX[c] <= manifest.basePoints.varianceX[c]
                        || manifest.strongPoints.varianceY[c] <= manifest.basePoints.varianceY[c])
                        throw new InvalidOperationException("LOD+1 did not conserve point energy/increase second moments");
                filter.sharedMesh = curved; screen.transform.localScale = new Vector3(CrtReferenceScreen.CaseAspect, 1, 1);
                camera.allowHDR = false; camera.aspect = CrtReferenceScreen.CaseAspect;
                var final = new RenderTexture(1280, 1030, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                if (!final.Create()) throw new InvalidOperationException("Cannot create softness photo RT"); objects.Add(final); camera.targetTexture = final;
                string[] names = { "plate_A_20260924_172209_b8e1f375.jpg", "plate_B_20260924_172210_4abb5bae.jpg", "plate_C_20260924_172211_a1c7c02f.jpg" };
                foreach (string name in names)
                {
                    string plate = name.Substring(6, 1), source = Path.Combine(root, "tools/web-compositor/captures", name);
                    PsfPhoto baseline = previous.photos.Find(p => p.mode == 1 && p.capture.plate == plate) ?? throw new InvalidDataException("Missing linear-mip baseline");
                    string baselineFile = Path.Combine(previousOutput, baseline.capture.file);
                    if (!File.Exists(source) || Hash(source) != baseline.capture.sourceHash
                        || !File.Exists(baselineFile) || Hash(baselineFile) != baseline.capture.sha256)
                        throw new InvalidDataException("Photo/baseline missing or changed: " + plate);
                    var texture = new Texture2D(2, 2, TextureFormat.RGB24, true, false) { filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp }; objects.Add(texture);
                    if (!texture.LoadImage(File.ReadAllBytes(source)) || texture.width != 640 || texture.height != 480 || texture.format != TextureFormat.RGB24)
                        throw new InvalidDataException("Expected actual 640x480 RGB24 plate");
                    var linearTexture = LinearPsfTexture(texture); objects.Add(linearTexture);
                    profile.SetTexture("_LiveTex", texture); profile.SetTexture("_CrtPsfLinearTex", linearTexture);
                    profile.SetVector("_LiveScale", new Vector4(0.75f, 1, 0, 0)); profile.SetVector("_CgScale", new Vector4(0.75f, 1, 0, 0));
                    profile.SetFloat("_FrameAspect", CrtReferenceScreen.LogicalAspect); profile.SetFloat("_CrtPsfStage", 0);
                    profile.SetVector("_CrtPsfLinearLod", manifest.baseLod); ApplyPost(profile, cfg.post);
                    var feel = CameraFeelFx.Settings.Resolve(cfg.feel);
                    var logic = new CameraFeelLogic { TargetLuma = feel.TargetLuma, FollowHalfLifeSec = feel.FollowSec };
                    float mean = 0; int count = 0;
                    for (int y = 0; y < texture.height; y += texture.height / 16) for (int x = 0; x < texture.width; x += texture.width / 16)
                    { Color p = texture.GetPixel(x, y); mean += p.r * 0.299f + p.g * 0.587f + p.b * 0.114f; count++; }
                    CameraFeelFx.WriteUniforms(profile, feel.NoiseDark, feel.NoiseFixed, logic.SteadyBiasFor(mean / count) * feel.Agc, 0, 0, 0, 0);
                    profile.SetVector("_CrtView", new Vector4(0, 0, -1, 0));
                    float currentGlare = profile.GetFloat("_Glare");
                    if (Mathf.Abs(currentGlare - 0.55f) > 1e-6f) throw new InvalidDataException("Current glare changed");
                    string invariant = ParameterHash(profile, "_CrtPsfLinearLod", "_Glare");
                    for (int lod = 0; lod < 2; lod++) for (int glare = 0; glare < 2; glare++)
                    {
                        var material = new Material(profile) { hideFlags = HideFlags.HideAndDontSave }; objects.Add(material);
                        Vector3 actualLod = lod == 0 ? manifest.baseLod : manifest.strongLod;
                        material.SetVector("_CrtPsfLinearLod", actualLod); material.SetFloat("_Glare", glare == 0 ? currentGlare : 0);
                        if (!material.IsKeywordEnabled("CRT_PSF_DIAGNOSTIC") || material.IsKeywordEnabled("CRT_ORDER_DIAGNOSTIC")
                            || ParameterHash(material, "_CrtPsfLinearLod", "_Glare") != invariant)
                            throw new InvalidOperationException("Softness experiment changed an uncontrolled property");
                        CompilePsf(material); renderer.sharedMaterial = material; camera.Render();
                        string file = plate + "_lod" + lod + (glare == 0 ? "_glare_current.png" : "_glare0.png"); Save(final, Path.Combine(output, file));
                        var capture = new OpticsCapture { plate = plate, source = source, sourceHash = Hash(source), file = file,
                            sha256 = Hash(Path.Combine(output, file)), invariantHash = invariant, parameters = OpticsParameters(material),
                            width = 1280, height = 1030, spread = CrtReferenceScreen.OpticalSpread, convergence = CrtReferenceScreen.OpticalConvergence,
                            spreadMultiplier = 1, convergenceMultiplier = 1 };
                        ValidateOpticsImage(Path.Combine(output, file), capture, objects);
                        bool matches = lod == 0 && glare == 0 && capture.sha256 == baseline.capture.sha256;
                        if (lod == 0 && glare == 0 && !matches) throw new InvalidDataException("Base image differs from previous linear-mip image: " + plate);
                        manifest.photos.Add(new SoftnessPhoto { capture = capture, baselineFile = baselineFile, baselineHash = baseline.capture.sha256,
                            lodIncrement = lod, linearLod = actualLod, glare = material.GetFloat("_Glare"), baselineMatches = matches });
                    }
                }
                foreach (SoftnessPhoto photo in manifest.photos)
                    if (!File.Exists(Path.Combine(output, photo.capture.file)) || Hash(Path.Combine(output, photo.capture.file)) != photo.capture.sha256)
                        throw new InvalidDataException("Softness capture missing or changed");
                File.WriteAllText(Path.Combine(output, "manifest.json.tmp"), JsonUtility.ToJson(manifest, true));
                File.Move(Path.Combine(output, "manifest.json.tmp"), Path.Combine(output, "manifest.json"));
                Debug.Log("[CrtSoftness] 12 photos; 3 base hashes match previous linear mip; point energy conserved; moments increased; shader errors=0 -> " + output);
            }
            finally
            {
                ShaderUtil.allowAsyncCompilation = oldAsync; RenderTexture.active = old;
                for (int i = objects.Count - 1; i >= 0; i--)
                { if (objects[i] is RenderTexture rt) rt.Release(); UnityEngine.Object.DestroyImmediate(objects[i]); }
            }
        }

        private static Texture2D SoftnessPattern()
        {
            var texture = new Texture2D(640, 480, TextureFormat.RGB24, true, false) { filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[640 * 480];
            for (int y = 0; y < 480; y++) for (int x = 0; x < 640; x++)
            {
                bool point = x >= 160 && x <= 391 && (x - 160) % 33 == 0 && y >= 80 && y <= 311 && (y - 80) % 33 == 0;
                pixels[y * 640 + x] = point ? new Color32(255, 255, 255, 255) : new Color32(0, 0, 0, 255);
            }
            texture.SetPixels32(pixels); texture.Apply(true, false); byte[] png = texture.EncodeToPNG(); UnityEngine.Object.DestroyImmediate(texture);
            var loaded = new Texture2D(2, 2, TextureFormat.RGB24, true, false) { filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp };
            if (!loaded.LoadImage(png)) throw new InvalidDataException("Cannot load softness point calibration"); return loaded;
        }

        private static SoftnessPoints MeasureSoftnessPoints(Color[] image)
        {
            var result = new SoftnessPoints(); const int width = 2560, scale = 4;
            for (int c = 0; c < 3; c++)
            {
                double energy = 0, vx = 0, vy = 0;
                for (int py = 80; py <= 311; py += 33) for (int px = 160; px <= 391; px += 33)
                {
                    int x0 = (px - 12) * scale, x1 = (px + 12) * scale, y0 = (py - 12) * scale, y1 = (py + 12) * scale;
                    double mass = 0, mx = 0, my = 0;
                    for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++)
                    { double value = image[y * width + x][c]; mass += value; mx += x * value; my += y * value; }
                    if (mass <= 0) throw new InvalidDataException("Empty softness point image"); mx /= mass; my /= mass;
                    double xx = 0, yy = 0;
                    for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++)
                    { double value = image[y * width + x][c], dx = (x - mx) / scale, dy = (y - my) / scale; xx += value * dx * dx; yy += value * dy * dy; }
                    energy += mass / (scale * scale); vx += xx / mass; vy += yy / mass;
                }
                result.energy[c] = (float)(energy / 64); result.varianceX[c] = (float)(vx / 64); result.varianceY[c] = (float)(vy / 64);
            }
            return result;
        }
    }
}
