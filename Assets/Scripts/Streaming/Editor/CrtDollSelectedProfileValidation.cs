#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    public static partial class CrtTuningServer
    {
        [Serializable] private sealed class DollSelectedReport
        {
            public string selection = "", selectionHash = "", webImageHash = "";
            public string boundaryPolicy = "RGB comparison uses common opaque interior eroded by 2px; UI cutout alpha and runtime discard boundaries are reported separately";
            public int interiorPixels, interiorMaxByteError, alphaDisagreements, boundaryPixels;
            public int nonBoundaryAlphaDisagreements;
            public int normalInteriorPixels, normalInteriorMaxByteError, negativeControlMaxByteError;
            public ParameterValues adopted = new ParameterValues();
            public List<string> sources = new List<string>();
            public List<SelectedFrame> frames = new List<SelectedFrame>();
        }

        [MenuItem("Tools/FixedCamVr/Diagnostics/Validate Selected Doll Web Screen", priority = 277)]
        public static void ValidateDollSelectedProfile()
        {
            string folder = "G:/マイドライブ/shubie/reports/2026-11-01_doll-profile-adoption";
            string output = EditorCliArgs.Get("out") ?? Path.Combine(folder, "gpu");
            if (Directory.Exists(output) && Directory.GetFileSystemEntries(output).Length != 0) throw new IOException("Output must be empty");
            Directory.CreateDirectory(output);
            string selectionPath = Path.Combine(folder, "web-selection.json"), imagePath = Path.Combine(folder, "web-selected-A.png");
            var selection = JsonUtility.FromJson<Selection>(File.ReadAllText(selectionPath));
            var report = new DollSelectedReport { selection = selectionPath, selectionHash = SelectedHash(selectionPath), webImageHash = SelectedHash(imagePath) };
            foreach (string path in new[] { "Assets/Scripts/Streaming/CrtSignalProfile.cs", "Assets/Scripts/Streaming/CrtSignalProcessor.cs", "Assets/Scripts/Streaming/CameraFeelFx.cs", "Assets/Scripts/Streaming/ScreenDecayLogic.cs", "Assets/Scripts/Streaming/ShowRunDirector.cs", "Assets/Scripts/Streaming/Editor/CrtTuningServer.cs", "Assets/Scripts/Streaming/Editor/ShowCompositePreview.CrtTuning.cs", "Assets/Scripts/Streaming/Editor/CrtDollSelectedProfileValidation.cs", "Assets/Art/Shaders/Streaming/ScreenComposite.shader", "Assets/Art/Shaders/Streaming/CrtCompositeShared.hlsl", "Assets/Resources/CrtReference/ScreenMask.png" })
                report.sources.Add(path + " " + SelectedHash(path));
            bool oldAsync = ShaderUtil.allowAsyncCompilation;
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                using var tuner = new Tuner();
                report.adopted = tuner.Values(true, "transformed");
                var expected = tuner.Resolve(selection.parameters); var adopted = tuner.Resolve(report.adopted);
                if (expected.Count != 23 || adopted.Count != 23) throw new InvalidDataException("Expected all 23 selected parameters");
                foreach (string key in expected.Keys)
                    if (Mathf.Abs(expected[key] - adopted[key]) > .00001f) throw new InvalidDataException("Selected doll value differs: " + key);
                Color32[] web = SelectedPixels(File.ReadAllBytes(imagePath));
                Color32[]? first = null;
                foreach (string plate in new[] { "A", "B", "C" })
                {
                    var decay = new ScreenDecayLogic();
                    decay.Tick(.01f, true, 3, 3, 0, dollShowing: true, dollTransitioning: true, dollTransition01: .5f, noiseTarget: CrtSignalProfile.EndNoiseDark);
                    var values = new Dictionary<string, float>(adopted) { ["mono"] = decay.Shown, ["noiseDark"] = decay.NoiseDark };
                    byte[] bytes = tuner.Render(plate, values, true, source: "cg_doll", dollProfileBlend: decay.DollProfileBlend);
                    CheckDollMaterial(tuner.Material, values, .5f);
                    string file = plate + "_morph_half.png"; File.WriteAllBytes(Path.Combine(output, file), bytes);
                    report.frames.Add(DollFrame(file, output, decay, tuner.Material));
                    decay.Tick(.01f, true, 3, 3, 0, dollShowing: true, noiseTarget: CrtSignalProfile.EndNoiseDark);
                    values["mono"] = decay.Shown; values["noiseDark"] = decay.NoiseDark;
                    bytes = tuner.Render(plate, values, true, source: "cg_doll", dollProfileBlend: decay.DollProfileBlend);
                    CheckDollMaterial(tuner.Material, values, 1);
                    file = plate + "_doll_adopted.png"; File.WriteAllBytes(Path.Combine(output, file), bytes);
                    report.frames.Add(DollFrame(file, output, decay, tuner.Material));
                    if (plate == "A") first = SelectedPixels(bytes);
                    decay.Release(); decay.Tick(ScreenDecayLogic.ReleaseSec * .5f, false, 4, 3, 0);
                    values["mono"] = decay.Shown; values["noiseDark"] = decay.NoiseDark;
                    bytes = tuner.Render(plate, values, true, source: "cg_doll", dollProfileBlend: decay.DollProfileBlend);
                    CheckDollMaterial(tuner.Material, values, .5f);
                    file = plate + "_release_half.png"; File.WriteAllBytes(Path.Combine(output, file), bytes);
                    report.frames.Add(DollFrame(file, output, decay, tuner.Material));
                    decay.Tick(ScreenDecayLogic.ReleaseSec, false, 4, 3, 0);
                    values["mono"] = decay.Shown; values["noiseDark"] = decay.NoiseDark;
                    bytes = tuner.Render(plate, values, true, source: "cg_doll", dollProfileBlend: decay.DollProfileBlend);
                    CheckDollMaterial(tuner.Material, values, 0);
                    file = plate + "_release_complete.png"; File.WriteAllBytes(Path.Combine(output, file), bytes);
                    report.frames.Add(DollFrame(file, output, decay, tuner.Material));
                }
                CompareDollInterior(first!, web, out report.interiorPixels, out report.interiorMaxByteError, out report.alphaDisagreements, out report.boundaryPixels);
                for (int y = 2; y < 1028; y++) for (int x = 2; x < 1278; x++)
                {
                    int i = y * 1280 + x;
                    if (first![i].a == web[i].a) continue;
                    bool edge = false;
                    for (int dy = -2; !edge && dy <= 2; dy++) for (int dx = -2; !edge && dx <= 2; dx++)
                    {
                        int j = (y + dy) * 1280 + x + dx;
                        edge = (web[j].a > 127) != (web[i].a > 127) || (first[j].a > 127) != (first[i].a > 127);
                    }
                    if (!edge) report.nonBoundaryAlphaDisagreements++;
                }
                string normalPath = "G:/マイドライブ/shubie/reports/2026-11-01_screen-profile-adoption/web-selected-A.png";
                var normal = tuner.Resolve(tuner.Values(true));
                byte[] normalBytes = tuner.Render("A", normal, true);
                CheckDollMaterial(tuner.Material, normal, 0);
                File.WriteAllBytes(Path.Combine(output, "A_normal_unchanged.png"), normalBytes);
                CompareDollInterior(SelectedPixels(normalBytes), SelectedPixels(File.ReadAllBytes(normalPath)), out report.normalInteriorPixels, out report.normalInteriorMaxByteError, out _, out _);
                var wrong = new Dictionary<string, float>(adopted) { ["centerEv"] = 0 };
                Color32[] negative = SelectedPixels(tuner.Render("A", wrong, source: "cg_doll"));
                CompareDollInterior(negative, web, out _, out report.negativeControlMaxByteError, out _, out _);
                File.WriteAllText(Path.Combine(output, "manifest.json"), JsonUtility.ToJson(report, true));
                if (report.interiorPixels < 100000 || report.normalInteriorPixels < 100000 || report.interiorMaxByteError > 1 || report.normalInteriorMaxByteError > 1 || report.nonBoundaryAlphaDisagreements != 0 || report.negativeControlMaxByteError <= 1)
                    throw new InvalidDataException("Doll selected pixel or normal regression gate failed: " + JsonUtility.ToJson(report));
                Debug.Log("[CrtDollSelected] PASS 23 selected values; raw CG A/B/C; shared interior <=1byte; normal unchanged; morph/hold/release material; negative control rejected -> " + output);
            }
            finally { ShaderUtil.allowAsyncCompilation = oldAsync; }
        }

        private static SelectedFrame DollFrame(string file, string output, ScreenDecayLogic decay, Material material)
            => new SelectedFrame { file = file, sha256 = SelectedHash(Path.Combine(output, file)), mono = decay.Shown, noise = decay.NoiseDark, blocks = decay.ShownBlocks,
                materialNoise = material.GetFloat("_NoiseDark"), materialMono = material.GetFloat("_Mono"), materialBlocks = material.GetFloat("_CoarseBlocks") };

        private static void CheckDollMaterial(Material material, Dictionary<string, float> values, float blend)
        {
            void Check(string property, float expected)
            {
                if (Mathf.Abs(material.GetFloat(property) - expected) > .00001f) throw new InvalidDataException("Doll material differs: " + property);
            }
            Check("_CrtDollProfileBlend", blend); Check("_Mono", values["mono"]); Check("_NoiseDark", values["noiseDark"]);
            Check("_CoarseBlocks", ScreenDecayLogic.BlocksFor(values["mono"]));
            foreach (string key in new[] { "exposure", "contrast", "saturation", "temperature", "tint", "lift", "vignette" })
                Check("_" + char.ToUpperInvariant(key[0]) + key.Substring(1), values[key]);
            Check("_CrtSignalGain", values["signalGain"]); Check("_CrtSignalBandwidth", values["bandwidth"]);
            Check("_CrtLensWarp", values["lensWarp"]);
            foreach (string key in new[] { "centerEv", "centerRadius", "centerFalloff", "tintStrength", "tintCoolness" })
                Check("_Crt" + char.ToUpperInvariant(key[0]) + key.Substring(1), values[key]);
            Check("_Grain", Mathf.Lerp(CrtSignalProfile.Grain, CrtSignalProfile.DollGrain, blend));
            Check("_Scanline", Mathf.Lerp(CrtSignalProfile.Scanline, CrtSignalProfile.DollScanline, blend));
            Check("_NoiseFixed", Mathf.Lerp(CrtSignalProfile.NoiseFixed, CrtSignalProfile.DollNoiseFixed, blend));
            Vector4 sigma = Vector3.Lerp(CrtSignalProfile.Sigma, CrtSignalProfile.DollSigma, blend);
            if ((material.GetVector("_CrtPsfSigma") - sigma).sqrMagnitude > .0000001f) throw new InvalidDataException("Doll PSF differs");
            if ((material.GetVector("_CrtOpticalConvergence") - CrtReferenceScreen.OpticalConvergence * values["convergenceMultiplier"]).sqrMagnitude > .0000001f)
                throw new InvalidDataException("Doll convergence differs");
        }

        private static void CompareDollInterior(Color32[] actual, Color32[] expected, out int count, out int maximum, out int alphaDisagreements, out int boundary)
        {
            count = maximum = alphaDisagreements = boundary = 0;
            for (int y = 0; y < 1030; y++) for (int x = 0; x < 1280; x++)
            {
                int i = y * 1280 + x;
                if (actual[i].a != expected[i].a) alphaDisagreements++;
                if (actual[i].a == 0 && expected[i].a == 0) continue;
                bool inside = x >= 2 && x < 1278 && y >= 2 && y < 1028;
                for (int dy = -2; inside && dy <= 2; dy++) for (int dx = -2; inside && dx <= 2; dx++)
                {
                    int j = (y + dy) * 1280 + x + dx;
                    if (actual[j].a != 255 || expected[j].a != 255) inside = false;
                }
                if (!inside) { boundary++; continue; }
                count++; maximum = Math.Max(maximum, SelectedDifference(actual[i], expected[i]));
            }
        }
    }
}
