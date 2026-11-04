#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    public static partial class CrtTuningServer
    {
        [Serializable] private sealed class Selection { public ParameterValues parameters = new ParameterValues(); }
        [Serializable] private sealed class SelectedReport
        {
            public string selection = "", selectionHash = "", webImageHash = "", contourReference = "prior contour only; RGB is compared with the current independently saved Web selection";
            public int selectedInteriorMaxByteError, selectedInteriorPixels, contourDisagreements, darkNoiseChangedPixels, negativeControlMaxByteError;
            public ParameterValues adopted = new ParameterValues();
            public List<string> sources = new List<string>();
            public List<SelectedFrame> frames = new List<SelectedFrame>();
        }
        [Serializable] private sealed class SelectedFrame { public string file = "", sha256 = ""; public float mono, noise, blocks, materialNoise, materialMono, materialBlocks; }

        [MenuItem("Tools/FixedCamVr/Diagnostics/Validate Selected Web Screen", priority = 276)]
        public static void ValidateSelectedProfile()
        {
            string folder = "G:/マイドライブ/shubie/reports/2026-11-01_screen-profile-adoption";
            string output = EditorCliArgs.Get("out") ?? Path.Combine(folder, "gpu");
            if (Directory.Exists(output) && Directory.GetFileSystemEntries(output).Length != 0) throw new IOException("Output must be empty");
            Directory.CreateDirectory(output);
            string selectionPath = Path.Combine(folder, "web-selection.json"), imagePath = Path.Combine(folder, "web-selected-A.png");
            var selection = JsonUtility.FromJson<Selection>(File.ReadAllText(selectionPath));
            var report = new SelectedReport { selection = selectionPath, selectionHash = SelectedHash(selectionPath), webImageHash = SelectedHash(imagePath) };
            foreach (string path in new[] { "Assets/Scripts/Streaming/CrtSignalProfile.cs", "Assets/Scripts/Streaming/CrtSignalProcessor.cs", "Assets/Scripts/Streaming/CameraFeelFx.cs", "Assets/Scripts/Streaming/ScreenDecayLogic.cs", "Assets/Scripts/Streaming/ShowRunDirector.cs", "Assets/Scripts/Streaming/MjpegScreen.cs", "Assets/Scripts/Streaming/Editor/CrtTuningServer.cs", "Assets/Scripts/Streaming/Editor/CrtSelectedProfileValidation.cs", "Assets/Art/Shaders/Streaming/CrtCompositeShared.hlsl", "Assets/Art/Shaders/Streaming/CrtSignalAdjustment.hlsl", "Assets/Art/Shaders/Streaming/CrtSignalFilter.shader", "Assets/Art/Shaders/Streaming/Editor/CrtTuningAdjustment.shader", "Assets/Resources/CrtReference/ScreenMask.png", "tools/web-compositor/show.json" })
                report.sources.Add(path + " " + SelectedHash(path));
            bool oldAsync = ShaderUtil.allowAsyncCompilation;
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                using var tuner = new Tuner();
                report.adopted = tuner.Values(true);
                var expectedValues = tuner.Resolve(selection.parameters); var adoptedValues = tuner.Resolve(report.adopted);
                foreach (string key in expectedValues.Keys)
                    if (Mathf.Abs(expectedValues[key] - adoptedValues[key]) > .00001f) throw new InvalidDataException("Selected value differs: " + key);
                Color32[] web = SelectedPixels(File.ReadAllBytes(imagePath));
                Color32[] contour = SelectedPixels(File.ReadAllBytes("G:/マイドライブ/shubie/reports/2026-11-01_screen-adoption/gpu/A_adopted.png"));
                Color32[]? first = null, last = null;
                foreach (string plate in new[] { "A", "B", "C" })
                {
                    int count = plate == "A" ? 7 : 2;
                    for (int step = 0; step < count; step++)
                    {
                        int position = count == 7 ? step : step * 6;
                        int lap = position / 3 + 1, camera = position % 3;
                        var decay = new ScreenDecayLogic();
                        decay.Tick(.01f, true, lap, 3, 0, noiseTarget: ScreenDecayLogic.NoiseTargetFor(lap, camera, new[] { 0, 1, 2 }));
                        var values = new Dictionary<string, float>(adoptedValues) { ["mono"] = decay.Shown, ["noiseDark"] = decay.NoiseDark };
                        byte[] bytes = tuner.Render(plate, values, true); Color32[] pixels = SelectedPixels(bytes);
                        string file = plate + "_position_" + position + ".png"; File.WriteAllBytes(Path.Combine(output, file), bytes);
                        var frame = new SelectedFrame { file = file, sha256 = SelectedHash(Path.Combine(output, file)), mono = decay.Shown, noise = decay.NoiseDark, blocks = decay.ShownBlocks,
                            materialNoise = tuner.Material.GetFloat("_NoiseDark"), materialMono = tuner.Material.GetFloat("_Mono"), materialBlocks = tuner.Material.GetFloat("_CoarseBlocks") };
                        if (Mathf.Abs(frame.materialNoise - frame.noise) > .00001f || frame.materialMono != frame.mono || frame.materialBlocks != frame.blocks) throw new InvalidDataException("Material dynamics were overwritten");
                        report.frames.Add(frame);
                        for (int i = 0; i < pixels.Length; i++)
                        {
                            if ((pixels[i].a == 255) != (contour[i].a == 255)) report.contourDisagreements++;
                            if (pixels[i].a != 0 && pixels[i].a != 255) throw new InvalidDataException("Translucent interior");
                            if (plate == "A" && position == 0 && pixels[i].a == 255)
                            {
                                report.selectedInteriorPixels++;
                                report.selectedInteriorMaxByteError = Math.Max(report.selectedInteriorMaxByteError, SelectedDifference(pixels[i], web[i]));
                            }
                        }
                        if (plate == "A" && position == 0) first = pixels;
                        if (plate == "A" && position == 6) last = pixels;
                    }
                }
                for (int i = 0; i < first!.Length; i++)
                    if (first[i].a == 255 && Math.Max(first[i].r, Math.Max(first[i].g, first[i].b)) < 128 && SelectedDifference(first[i], last![i]) > 0) report.darkNoiseChangedPixels++;
                // A negative control must reject a different optical setting using the same comparison.
                var wrong = new Dictionary<string, float>(adoptedValues) { ["centerEv"] = 0 };
                Color32[] negative = SelectedPixels(tuner.Render("A", wrong));
                for (int i = 0; i < first.Length; i++) if (first[i].a == 255) report.negativeControlMaxByteError = Math.Max(report.negativeControlMaxByteError, SelectedDifference(negative[i], web[i]));
                File.WriteAllText(Path.Combine(output, "manifest.json"), JsonUtility.ToJson(report, true));
                if (report.selectedInteriorPixels == 0 || report.selectedInteriorMaxByteError > 1 || report.contourDisagreements != 0 || report.darkNoiseChangedPixels == 0 || report.negativeControlMaxByteError <= 1)
                    throw new InvalidDataException("Selected Web/runtime pixel, contour or noise gate failed: " + JsonUtility.ToJson(report));
                Debug.Log("[CrtSelected] PASS Web/runtime interior <=1byte; contour exact; A7/BC2 positions; dynamic material preserved; dark noise visible; negative control rejected -> " + output);
            }
            finally { ShaderUtil.allowAsyncCompilation = oldAsync; }
        }

        private static Color32[] SelectedPixels(byte[] bytes)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            try
            {
                if (!texture.LoadImage(bytes) || texture.width != 1280 || texture.height != 1030) throw new InvalidDataException("Expected 1280x1030 PNG");
                return texture.GetPixels32();
            }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
        }
        private static int SelectedDifference(Color32 a, Color32 b) => Math.Max(Math.Abs(a.r - b.r), Math.Max(Math.Abs(a.g - b.g), Math.Abs(a.b - b.b)));
        private static string SelectedHash(string path)
        { using var hash = SHA256.Create(); return BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant(); }
    }
}
