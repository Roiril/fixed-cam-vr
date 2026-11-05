#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    public static partial class CrtTuningServer
    {
        /// <summary>
        /// 空間の歪み（LEDGER 0313）を本編の信号処理で描く。強さ 0 が素の画と 1 画素も違わないこと、
        /// 強さ 1 で画が変わること、場を変えると画が変わることを確かめ、できなければ落ちる。
        /// </summary>
        [MenuItem("Tools/FixedCamVr/Diagnostics/Render Room Warp", priority = 279)]
        public static void RenderRoomWarp()
        {
            string output = EditorCliArgs.Get("out") ?? "G:/マイドライブ/shubie/renders/room-warp";
            if (Directory.Exists(output) && Directory.GetFileSystemEntries(output).Length != 0) throw new IOException("Output must be empty: " + output);
            Directory.CreateDirectory(output);
            bool oldAsync = ShaderUtil.allowAsyncCompilation;
            var pixels = new ushort[RoomWarpField.GridWidth * RoomWarpField.GridHeight * 2];
            var field = new float[pixels.Length];
            var tex = new Texture2D(RoomWarpField.GridWidth, RoomWarpField.GridHeight, TextureFormat.RGHalf, false, true)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                using var tuner = new Tuner();
                Dictionary<string, float> normal = tuner.Resolve(tuner.Values(true));
                foreach (string plate in new[] { "A", "B", "C" })
                {
                    tuner.Material.SetFloat("_RoomWarp", 0f);
                    byte[] baseline = tuner.Render(plate, normal, true);
                    File.WriteAllBytes(Path.Combine(output, plate + "_0_before.png"), baseline);
                    if (!baseline.SequenceEqual(tuner.Render(plate, normal, true)))
                        throw new InvalidDataException("強さ 0 の画が揃わない: " + plate);

                    RoomWarpSpec spec = RoomWarpField.SpecFor(plate) ?? throw new InvalidDataException(plate);
                    byte[]? previous = null;
                    foreach (float t in new[] { 1.2f, 3.6f, 6.0f })
                    {
                        RoomWarpField.FillGrid(spec, t, field);
                        for (int i = 0; i < field.Length; i++) pixels[i] = Mathf.FloatToHalf(field[i]);
                        tex.SetPixelData(pixels, 0);
                        tex.Apply(false, false);
                        tuner.Material.SetTexture("_RoomWarpTex", tex);
                        tuner.Material.SetFloat("_RoomWarpZoom", RoomWarpField.Overscan);
                        tuner.Material.SetFloat("_RoomWarp", 1f);
                        byte[] warped = tuner.Render(plate, normal, true);
                        if (warped.SequenceEqual(baseline)) throw new InvalidDataException("歪みが画に出ていない: " + plate);
                        if (previous != null && warped.SequenceEqual(previous)) throw new InvalidDataException("場が動いていない: " + plate);
                        previous = warped;
                        File.WriteAllBytes(Path.Combine(output, $"{plate}_1_t{t:0.0}.png"), warped);
                    }
                }
            }
            finally
            {
                ShaderUtil.allowAsyncCompilation = oldAsync;
                Object.DestroyImmediate(tex);
            }
            Debug.Log("[RoomWarp] PASS -> " + output);
        }
    }
}
