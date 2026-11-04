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
        [Serializable] private sealed class ToneManifest
        {
            public string utc = "", unity = "", shaderHash = "", helperHash = "", psfHelperHash = "", opticsHelperHash = "", previewHash = "";
            public string showHash = "", runtimeProfileHash = "", previousManifestHash = "";
            public string experiment = "Only _CrtGradeTex changes. Linear PSF mip mode1 and calibrated LOD; Glare=.55; same source/maps/post/RGB offsets/camera/order as previous linear-mip baseline.";
            public string distribution = "Final sRGB PNG display luma rounded from .299R+.587G+.114B; fixed aperture ROI with original Frame alpha<.05. Black=0..15, middle=16..239, white=240..255; full 256-bin histogram and count differences from current LUT.";
            public Vector3 linearLod;
            public List<OpticsMap> maps = new List<OpticsMap>();
            public List<ToneLut> luts = new List<ToneLut>();
            public List<TonePhoto> photos = new List<TonePhoto>();
        }
        [Serializable] private sealed class ToneLut
        {
            public string name = "", formula = "", png = "", pngHash = "", bytes = "", byteHash = "", rgbaBytes = "", rgbaHash = "";
            public string format = "", filter = "", wrap = "";
            public int black, white, width, height, mipCount, aniso;
            public float mipBias;
            public bool linear, monotone, endpoints, formulaBytesMatch, textureBytesMatch, pngBytesMatch;
            public int[] values = new int[256];
        }
        [Serializable] private sealed class TonePhoto
        {
            public OpticsCapture capture = new OpticsCapture();
            public string lut = "", baselineFile = "", baselineHash = "";
            public bool baselineMatches;
            public ToneDistribution distribution = new ToneDistribution();
            public int blackDelta, middleDelta, whiteDelta;
        }
        [Serializable] private sealed class ToneDistribution
        {
            public int pixels, black, middle, white;
            public double mean;
            public int[] histogram = new int[256];
        }

        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview CRT Tone Matrix", priority = 264)]
        public static void RunToneMatrix()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string output = EditorCliArgs.Get("out") ?? Path.Combine(Output, "processing-tone-01");
            string previousOutput = Path.Combine(Output, "processing-psf-linear-01");
            if (Directory.Exists(output) && Directory.GetFileSystemEntries(output).Length > 0) throw new IOException("Tone output must be empty: " + output);
            if (!File.ReadAllText(Path.Combine(root, ".claude/canon/ROUNDS.md")).Contains("明暗曲線の対照"))
                throw new InvalidOperationException("Missing pre-render tone prediction");
            string previousFile = Path.Combine(previousOutput, "manifest.json");
            var previous = JsonUtility.FromJson<PsfManifest>(File.ReadAllText(previousFile));
            var cfg = JsonUtility.FromJson<Config>(File.ReadAllText(Path.Combine(root, "tools/web-compositor/show.json")));
            if (previous == null || cfg == null) throw new InvalidDataException("Missing previous manifest/settings");
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Art/Shaders/Streaming/ScreenComposite.shader");
            if (shader == null || !shader.isSupported) throw new InvalidOperationException("Screen shader unsupported");
            var manifest = new ToneManifest { utc = DateTime.UtcNow.ToString("o"), unity = Application.unityVersion,
                shaderHash = Hash(Path.Combine(root, "Assets/Art/Shaders/Streaming/ScreenComposite.shader")),
                helperHash = Hash(Path.Combine(root, "Assets/Scripts/Streaming/Editor/CrtReferenceTonePreview.cs")),
                psfHelperHash = Hash(Path.Combine(root, "Assets/Scripts/Streaming/Editor/CrtReferencePsfPreview.cs")),
                opticsHelperHash = Hash(Path.Combine(root, "Assets/Scripts/Streaming/Editor/CrtReferenceOpticsPreview.cs")),
                previewHash = Hash(Path.Combine(root, "Assets/Scripts/Streaming/Editor/CrtReferencePreview.cs")),
                showHash = Hash(Path.Combine(root, "tools/web-compositor/show.json")),
                runtimeProfileHash = Hash(Path.Combine(root, "Assets/Scripts/Streaming/CrtReferenceScreen.cs")),
                previousManifestHash = Hash(previousFile), linearLod = previous.linearLod };
            if (manifest.shaderHash != previous.shaderHash || manifest.showHash != previous.showHash
                || manifest.runtimeProfileHash != previous.runtimeProfileHash || manifest.psfHelperHash != previous.helperHash)
                throw new InvalidDataException("Pipeline/settings changed since baseline");
            foreach (OpticsMap map in previous.maps)
            {
                var importer = AssetImporter.GetAtPath(map.path) as TextureImporter;
                if (!File.Exists(map.path) || Hash(map.path) != map.sha256 || importer == null || EditorJsonUtility.ToJson(importer) != map.importer)
                    throw new InvalidDataException("Reference map/importer changed: " + map.path);
                manifest.maps.Add(map);
            }
            Directory.CreateDirectory(output);
            var objects = new List<UnityEngine.Object>();
            RenderTexture old = RenderTexture.active; bool oldAsync = ShaderUtil.allowAsyncCompilation;
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                var profile = new Material(shader) { hideFlags = HideFlags.HideAndDontSave }; objects.Add(profile);
                if (!CrtReferenceScreen.Apply(profile)) throw new InvalidOperationException("Missing reference maps");
                profile.DisableKeyword("CRT_ORDER_DIAGNOSTIC"); profile.EnableKeyword("CRT_PSF_DIAGNOSTIC");
                profile.SetFloat("_CrtPsfMode", 1); profile.SetFloat("_CrtPsfStage", 0);
                profile.SetVector("_CrtPsfLinearLod", previous.linearLod); profile.SetVector("_CrtPsfSigma", previous.sigma);
                var currentGrade = profile.GetTexture("_CrtGradeTex") as Texture2D;
                if (currentGrade == null || currentGrade.width != 256 || currentGrade.height != 1) throw new InvalidDataException("Unexpected current Grade dimensions");
                var currentData = new Texture2D(2, 2, TextureFormat.RGBA32, false, true); objects.Add(currentData);
                if (!currentData.LoadImage(File.ReadAllBytes(Path.Combine(root, "Assets/Resources/CrtReference/Grade.png")))) throw new InvalidDataException("Cannot read current Grade data");
                var luts = new Texture2D[3]; luts[0] = currentGrade;
                manifest.luts.Add(SaveToneLut(currentGrade, currentData.GetPixels32(0), "current", -1, -1, output));
                for (int i = 1; i < 3; i++)
                {
                    int black = i == 1 ? 16 : 32, white = i == 1 ? 240 : 224;
                    var pixels = new Color32[256];
                    for (int x = 0; x < 256; x++)
                    {
                        double t = Math.Max(0, Math.Min(1, (x - black) / (double)(white - black)));
                        byte value = checked((byte)Math.Round(255 * t * t * (3 - 2 * t), MidpointRounding.AwayFromZero));
                        pixels[x] = new Color32(value, value, value, 255);
                    }
                    var texture = new Texture2D(256, 1, TextureFormat.RGBA32, currentGrade.mipmapCount > 1, true)
                    { filterMode = currentGrade.filterMode, wrapModeU = currentGrade.wrapModeU, wrapModeV = currentGrade.wrapModeV,
                        wrapModeW = currentGrade.wrapModeW, anisoLevel = currentGrade.anisoLevel, mipMapBias = currentGrade.mipMapBias };
                    objects.Add(texture); texture.SetPixels32(pixels, 0); texture.Apply(true, false); luts[i] = texture;
                    if (texture.mipmapCount != currentGrade.mipmapCount || texture.format != TextureFormat.RGBA32)
                        throw new InvalidDataException("LUT mip/format mismatch");
                    manifest.luts.Add(SaveToneLut(texture, pixels, black + "_" + white, black, white, output));
                }
                var frame = new Texture2D(2, 2, TextureFormat.RGBA32, false, true); objects.Add(frame);
                if (!frame.LoadImage(File.ReadAllBytes(Path.Combine(root, "Assets/Resources/CrtReference/Frame.png")))) throw new InvalidDataException("Cannot read ROI frame alpha");
                bool[] roi = ToneRoi(frame, 1280, 1030);
                var screen = new GameObject("ToneScreen") { hideFlags = HideFlags.HideAndDontSave, layer = 1 }; objects.Add(screen);
                screen.transform.position = new Vector3(1000, 0, 0); screen.transform.localScale = new Vector3(CrtReferenceScreen.CaseAspect, 1, 1);
                var mesh = CrtScreenMesh.BuildMesh(48, 0.055f); objects.Add(mesh); screen.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = screen.AddComponent<MeshRenderer>();
                var go = new GameObject("ToneCamera") { hideFlags = HideFlags.HideAndDontSave }; objects.Add(go);
                var camera = go.AddComponent<Camera>(); camera.enabled = false;
                camera.transform.SetPositionAndRotation(new Vector3(1000, 0, -2), Quaternion.identity);
                camera.orthographic = true; camera.orthographicSize = 0.5f; camera.nearClipPlane = 0.1f; camera.farClipPlane = 4;
                camera.cullingMask = 1 << 1; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
                camera.allowHDR = false; camera.allowMSAA = false; camera.aspect = CrtReferenceScreen.CaseAspect;
                var target = new RenderTexture(1280, 1030, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                if (!target.Create()) throw new InvalidOperationException("Cannot create tone RT"); objects.Add(target); camera.targetTexture = target;
                string[] names = { "plate_A_20260924_172209_b8e1f375.jpg", "plate_B_20260924_172210_4abb5bae.jpg", "plate_C_20260924_172211_a1c7c02f.jpg" };
                foreach (string name in names)
                {
                    string plate = name.Substring(6, 1), source = Path.Combine(root, "tools/web-compositor/captures", name);
                    PsfPhoto baseline = previous.photos.Find(p => p.mode == 1 && p.capture.plate == plate) ?? throw new InvalidDataException("Missing tone baseline");
                    string baselineFile = Path.Combine(previousOutput, baseline.capture.file);
                    if (!File.Exists(source) || Hash(source) != baseline.capture.sourceHash
                        || !File.Exists(baselineFile) || Hash(baselineFile) != baseline.capture.sha256) throw new InvalidDataException("Source/baseline changed: " + plate);
                    var texture = new Texture2D(2, 2, TextureFormat.RGB24, true, false) { filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp }; objects.Add(texture);
                    if (!texture.LoadImage(File.ReadAllBytes(source)) || texture.width != 640 || texture.height != 480 || texture.format != TextureFormat.RGB24)
                        throw new InvalidDataException("Expected actual 640x480 RGB24 plate");
                    var linear = LinearPsfTexture(texture); objects.Add(linear); profile.SetTexture("_LiveTex", texture); profile.SetTexture("_CrtPsfLinearTex", linear);
                    profile.SetVector("_LiveScale", new Vector4(.75f, 1, 0, 0)); profile.SetVector("_CgScale", new Vector4(.75f, 1, 0, 0));
                    profile.SetFloat("_FrameAspect", CrtReferenceScreen.LogicalAspect); profile.SetVector("_CrtView", new Vector4(0, 0, -1, 0)); ApplyPost(profile, cfg.post);
                    var feel = CameraFeelFx.Settings.Resolve(cfg.feel);
                    var logic = new CameraFeelLogic { TargetLuma = feel.TargetLuma, FollowHalfLifeSec = feel.FollowSec };
                    float mean = 0; int count = 0;
                    for (int y = 0; y < texture.height; y += texture.height / 16) for (int x = 0; x < texture.width; x += texture.width / 16)
                    { Color p = texture.GetPixel(x, y); mean += p.r * .299f + p.g * .587f + p.b * .114f; count++; }
                    CameraFeelFx.WriteUniforms(profile, feel.NoiseDark, feel.NoiseFixed, logic.SteadyBiasFor(mean / count) * feel.Agc, 0, 0, 0, 0);
                    if (Mathf.Abs(profile.GetFloat("_Glare") - .55f) > 1e-6f) throw new InvalidDataException("Current Glare changed");
                    string invariant = ParameterHash(profile, "_CrtGradeTex"); ToneDistribution? baseDistribution = null;
                    for (int condition = 0; condition < 3; condition++)
                    {
                        var material = new Material(profile) { hideFlags = HideFlags.HideAndDontSave }; objects.Add(material); material.SetTexture("_CrtGradeTex", luts[condition]);
                        if (ParameterHash(material, "_CrtGradeTex") != invariant || !material.IsKeywordEnabled("CRT_PSF_DIAGNOSTIC")
                            || material.IsKeywordEnabled("CRT_ORDER_DIAGNOSTIC")) throw new InvalidOperationException("Tone matrix changed uncontrolled property");
                        CompilePsf(material); renderer.sharedMaterial = material; camera.Render();
                        string file = plate + "_tone_" + manifest.luts[condition].name + ".png"; Save(target, Path.Combine(output, file));
                        var capture = new OpticsCapture { plate = plate, source = source, sourceHash = Hash(source), file = file,
                            sha256 = Hash(Path.Combine(output, file)), invariantHash = invariant, parameters = OpticsParameters(material), width = 1280, height = 1030,
                            spread = CrtReferenceScreen.OpticalSpread, convergence = CrtReferenceScreen.OpticalConvergence, spreadMultiplier = 1, convergenceMultiplier = 1 };
                        ValidateOpticsImage(Path.Combine(output, file), capture, objects);
                        ToneDistribution distribution = MeasureTone(OpticsPixels(Path.Combine(output, file), objects), roi);
                        if (condition == 0) baseDistribution = distribution;
                        bool matches = condition == 0 && capture.sha256 == baseline.capture.sha256;
                        if (condition == 0 && !matches) throw new InvalidDataException("Current tone differs from linear-mip baseline: " + plate);
                        manifest.photos.Add(new TonePhoto { capture = capture, lut = manifest.luts[condition].name, baselineFile = baselineFile,
                            baselineHash = baseline.capture.sha256, baselineMatches = matches, distribution = distribution,
                            blackDelta = distribution.black - baseDistribution!.black, middleDelta = distribution.middle - baseDistribution.middle,
                            whiteDelta = distribution.white - baseDistribution.white });
                    }
                }
                foreach (TonePhoto photo in manifest.photos)
                    if (Hash(Path.Combine(output, photo.capture.file)) != photo.capture.sha256) throw new InvalidDataException("Tone photo changed after capture");
                File.WriteAllText(Path.Combine(output, "manifest.json.tmp"), JsonUtility.ToJson(manifest, true));
                File.Move(Path.Combine(output, "manifest.json.tmp"), Path.Combine(output, "manifest.json"));
                Debug.Log("[CrtTone] 9 photos; 3 current hashes match previous linear mip; 2 LUTs verified at all 256 values; shader errors=0 -> " + output);
            }
            finally
            {
                ShaderUtil.allowAsyncCompilation = oldAsync; RenderTexture.active = old;
                for (int i = objects.Count - 1; i >= 0; i--)
                { if (objects[i] is RenderTexture rt) rt.Release(); UnityEngine.Object.DestroyImmediate(objects[i]); }
            }
        }

        private static ToneLut SaveToneLut(Texture2D texture, Color32[] pixels, string name, int black, int white, string output)
        {
            if (pixels.Length != 256) throw new InvalidDataException("Expected 256 LUT entries");
            var lut = new ToneLut { name = name, black = black, white = white, width = texture.width, height = texture.height,
                mipCount = texture.mipmapCount, format = texture.format.ToString(), filter = texture.filterMode.ToString(),
                wrap = texture.wrapModeU + "/" + texture.wrapModeV + "/" + texture.wrapModeW, aniso = texture.anisoLevel, mipBias = texture.mipMapBias,
                linear = !UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsSRGBFormat(texture.graphicsFormat),
                formula = black < 0 ? "current Resources Grade.png" : "round(255*t*t*(3-2*t)), t=saturate((x-black)/(white-black)); half-up rounding",
                monotone = true, endpoints = pixels[0].r == 0 && pixels[255].r == 255, formulaBytesMatch = true,
                textureBytesMatch = true, pngBytesMatch = true };
            var bytes = new byte[256]; var rgba = new byte[256 * 4];
            Color32[] actual = black < 0 ? pixels : texture.GetPixels32(0);
            for (int x = 0; x < 256; x++)
            {
                Color32 p = pixels[x]; lut.values[x] = bytes[x] = p.r;
                if (p.r != p.g || p.r != p.b) throw new InvalidDataException("Non-neutral LUT entry");
                if (x > 0 && p.r < pixels[x - 1].r) lut.monotone = false;
                if (actual[x].r != p.r || actual[x].g != p.g || actual[x].b != p.b || (black >= 0 && actual[x].a != 255)) lut.textureBytesMatch = false;
                rgba[x * 4] = p.r; rgba[x * 4 + 1] = p.g; rgba[x * 4 + 2] = p.b; rgba[x * 4 + 3] = p.a;
                if (black >= 0)
                {
                    long d = white - black, n = Math.Max(0, Math.Min(white - black, x - black));
                    long numerator = 255L * n * n * (3 * d - 2 * n), denominator = d * d * d;
                    int expected = (int)((2 * numerator + denominator) / (2 * denominator));
                    if (p.r != expected) lut.formulaBytesMatch = false;
                }
            }
            lut.bytes = "lut_" + name + ".bin"; lut.rgbaBytes = "lut_" + name + "_rgba.bin"; lut.png = "lut_" + name + ".png";
            File.WriteAllBytes(Path.Combine(output, lut.bytes), bytes); File.WriteAllBytes(Path.Combine(output, lut.rgbaBytes), rgba);
            if (black < 0) File.Copy("Assets/Resources/CrtReference/Grade.png", Path.Combine(output, lut.png));
            else File.WriteAllBytes(Path.Combine(output, lut.png), texture.EncodeToPNG());
            lut.byteHash = Hash(Path.Combine(output, lut.bytes)); lut.rgbaHash = Hash(Path.Combine(output, lut.rgbaBytes)); lut.pngHash = Hash(Path.Combine(output, lut.png));
            var readback = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            try
            {
                if (!readback.LoadImage(File.ReadAllBytes(Path.Combine(output, lut.png)))) throw new InvalidDataException("Cannot reload LUT PNG");
                Color32[] read = readback.GetPixels32(0);
                if (read.Length != 256) lut.pngBytesMatch = false;
                else for (int x = 0; x < 256; x++) if (read[x].r != bytes[x] || read[x].g != bytes[x] || read[x].b != bytes[x]) lut.pngBytesMatch = false;
            }
            finally { UnityEngine.Object.DestroyImmediate(readback); }
            if (!lut.linear || !lut.textureBytesMatch || !lut.pngBytesMatch || (black >= 0 && (!lut.monotone || !lut.endpoints || !lut.formulaBytesMatch)))
                throw new InvalidDataException("LUT validation failed: " + name);
            return lut;
        }

        private static bool[] ToneRoi(Texture2D frame, int width, int height)
        {
            var roi = new bool[width * height]; int count = 0; Rect aperture = CrtReferenceScreen.Aperture;
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                var uv = new Vector2((x + .5f) / width, (y + .5f) / height);
                if (aperture.Contains(uv) && frame.GetPixelBilinear(uv.x, uv.y).a < .05f) { roi[y * width + x] = true; count++; }
            }
            if (count < width * height / 2) throw new InvalidDataException("Tone ROI is unexpectedly small"); return roi;
        }

        private static ToneDistribution MeasureTone(Color32[] pixels, bool[] roi)
        {
            if (pixels.Length != roi.Length) throw new InvalidDataException("Tone ROI size mismatch");
            var result = new ToneDistribution(); long sum = 0;
            for (int i = 0; i < pixels.Length; i++) if (roi[i])
            {
                int value = Mathf.Clamp(Mathf.RoundToInt(.299f * pixels[i].r + .587f * pixels[i].g + .114f * pixels[i].b), 0, 255);
                result.histogram[value]++; result.pixels++; sum += value;
                if (value <= 15) result.black++; else if (value >= 240) result.white++; else result.middle++;
            }
            result.mean = sum / (double)result.pixels; return result;
        }
    }
}
