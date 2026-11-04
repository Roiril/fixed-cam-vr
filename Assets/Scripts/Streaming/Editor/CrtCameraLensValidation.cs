#nullable enable
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    public static partial class CrtTuningServer
    {
        /// <summary>カメラごとのレンズを本編の信号処理で描く（LEDGER 0308）。A と B は前の版との差を見る対照。</summary>
        [MenuItem("Tools/FixedCamVr/Diagnostics/Render Camera Lens", priority = 278)]
        public static void RenderCameraLens()
        {
            string output = EditorCliArgs.Get("out") ?? "G:/マイドライブ/shubie/reports/2026-11-05_cam-c-lens-study/gpu";
            if (Directory.Exists(output) && Directory.GetFileSystemEntries(output).Length != 0) throw new IOException("Output must be empty: " + output);
            Directory.CreateDirectory(output);
            bool oldAsync = ShaderUtil.allowAsyncCompilation;
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                using var tuner = new Tuner();
                Dictionary<string, float> normal = tuner.Resolve(tuner.Values(true));
                foreach (string plate in new[] { "A", "B", "C" })
                {
                    int camera = plate[0] - 'A';
                    var decay = new ScreenDecayLogic();
                    decay.Tick(.01f, true, 1, 3, 0, noiseTarget: ScreenDecayLogic.NoiseTargetFor(1, 0, new[] { 0, 1, 2 }));
                    var values = new Dictionary<string, float>(normal) { ["mono"] = decay.Shown, ["noiseDark"] = decay.NoiseDark };
                    Write(output, plate + "_position_0.png", tuner.Render(plate, values, true));
                    AssertLens(tuner.Material, plate == CameraLensLogic.FisheyeCameraId, plate);
                }

                Dictionary<string, float> doll = tuner.Resolve(tuner.Values(true, "transformed"));
                foreach (string plate in new[] { "B", "C" })
                {
                    Write(output, plate + "_doll.png", tuner.Render(plate, doll, true, source: "cg_doll", dollProfileBlend: 1));
                    AssertLens(tuner.Material, plate == CameraLensLogic.FisheyeCameraId, plate + " doll");
                }

                Dictionary<string, float> pov = tuner.Resolve(tuner.Values(true, "doll"));
                Write(output, "C_pov_0.png", tuner.Render("C", pov, true, source: "pov_0"));
                AssertLens(tuner.Material, false, "C pov");
            }
            finally { ShaderUtil.allowAsyncCompilation = oldAsync; }
            Debug.Log("[CameraLens] PASS center x" + CameraLensLogic.CenterScale.ToString("F3") + " -> " + output);
        }

        private static void Write(string folder, string file, byte[] bytes) => File.WriteAllBytes(Path.Combine(folder, file), bytes);

        private static void AssertLens(Material material, bool expected, string label)
        {
            if (material.GetVector(CameraLensLogic.PropertyId).x > 0 != expected)
                throw new InvalidDataException("Lens is wrong on " + label);
        }
    }
}
