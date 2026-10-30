#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    /// <summary>本番シェーダの開口変換を実写/録画マスク/CGの独立した入力で照合する。</summary>
    public static class CrtReferenceValidation
    {
        [Serializable] private sealed class Probe
        {
            public string name = "";
            public int pixels;
            public float x, y;
        }
        [Serializable] private sealed class Result
        {
            public List<Probe> probes = new List<Probe>();
            public float maxCenterErrorPixels, minAreaRatio;
            public int powerOffMaxByte;
        }

        public static void Run(Camera camera, Renderer surface, RenderTexture target, string output)
        {
            Material original = surface.sharedMaterial;
            var owned = new List<UnityEngine.Object>();
            try
            {
                var mat = new Material(original); owned.Add(mat); surface.sharedMaterial = mat;
                foreach (string key in new[] { "_Exposure", "_ExposureBias", "_Temperature", "_Tint", "_Lift",
                    "_Vignette", "_Glare", "_NoiseDark", "_NoiseFixed", "_Grain", "_Mono", "_CgChromaBlur", "_CrtFieldStrength" })
                    mat.SetFloat(key, 0);
                mat.SetFloat("_Contrast", 1); mat.SetFloat("_Saturation", 1);
                mat.SetFloat("_CrtDisplayContrast", 1); mat.SetFloat("_CrtGradeBypass", 1);
                mat.SetFloat("_CgChromaGain", 1);
                mat.SetVector("_CrtImageAdjustment", Vector4.zero);
                mat.SetVector("_CrtOpticalConvergence", Vector4.zero);
                mat.SetVector("_CrtOpticalSpread", Vector4.zero);
                mat.SetVector("_CrtView", new Vector4(0, 0, -1, 0));
                mat.SetVector("_LiveScale", new Vector4(0.75f, 1, 0, 0));
                mat.SetVector("_OverlayScale", new Vector4(0.75f, 1, 0, 0));
                mat.SetVector("_CgScale", new Vector4(0.75f, 1, 0, 0));

                var live = Plate(256, 192, new Rect(64, 90, 32, 24), Color.black); owned.Add(live);
                var blank = Plate(256, 192, Rect.zero, Color.black); owned.Add(blank);
                var overlay = Plate(256, 192, Rect.zero, Color.white); owned.Add(overlay);
                // 実写内の矩形を論理16:9の中央4:3へ焼いた、別解像度のマスク。
                var mask = Plate(512, 288, new Rect(160, 135, 48, 36), Color.black); owned.Add(mask);
                var cg = Plate(256, 192, new Rect(64, 90, 32, 24), Color.clear); owned.Add(cg);
                var result = new Result();
                for (int stage = 0; stage < 3; stage++)
                {
                    mat.SetTexture("_LiveTex", stage == 0 ? live : blank);
                    mat.SetTexture("_OverlayTex", overlay); mat.SetTexture("_MaskTex", mask);
                    mat.SetTexture("_CgTex", cg);
                    mat.SetFloat("_OverlayStrength", stage == 1 ? 1 : 0);
                    mat.SetFloat("_CgStrength", stage == 2 ? 1 : 0);
                    camera.Render();
                    var pixels = Read(target); owned.Add(pixels);
                    string name = stage == 0 ? "live" : stage == 1 ? "recorded-mask" : "cg";
                    File.WriteAllBytes(Path.Combine(output, "alignment-" + name + ".png"), pixels.EncodeToPNG());
                    Probe probe = Centroid(pixels); probe.name = name; result.probes.Add(probe);
                    if (probe.pixels < 1000) throw new InvalidOperationException("Coordinate probe not drawn: " + name);
                }
                Probe first = result.probes[0]; int min = first.pixels, max = first.pixels;
                foreach (Probe p in result.probes)
                {
                    result.maxCenterErrorPixels = Mathf.Max(result.maxCenterErrorPixels,
                        Vector2.Distance(new Vector2(p.x, p.y), new Vector2(first.x, first.y)));
                    min = Mathf.Min(min, p.pixels); max = Mathf.Max(max, p.pixels);
                }
                result.minAreaRatio = (float)min / max;
                if (result.maxCenterErrorPixels > 2 || result.minAreaRatio < 0.85f)
                    throw new InvalidOperationException("Live/mask/CG coordinate mismatch: " + JsonUtility.ToJson(result));
                mat.SetFloat("_ScreenPower", 0); camera.Render();
                var off = Read(target); owned.Add(off);
                foreach (Color32 p in off.GetPixels32())
                    result.powerOffMaxByte = Math.Max(result.powerOffMaxByte, Math.Max(p.r, Math.Max(p.g, p.b)));
                if (result.powerOffMaxByte > 1) throw new InvalidOperationException("CRT case or glass remains after power off");
                File.WriteAllBytes(Path.Combine(output, "power-off.png"), off.EncodeToPNG());
                string path = Path.Combine(output, "coordinate-validation.json");
                File.WriteAllText(path + ".tmp", JsonUtility.ToJson(result, true));
                if (File.Exists(path)) File.Replace(path + ".tmp", path, null); else File.Move(path + ".tmp", path);
            }
            finally
            {
                surface.sharedMaterial = original;
                for (int i = owned.Count - 1; i >= 0; i--) UnityEngine.Object.DestroyImmediate(owned[i]);
            }
        }

        private static Texture2D Plate(int width, int height, Rect rectangle, Color background)
        {
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, true, false)
                { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear };
            var data = new Color[width * height];
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                data[y * width + x] = rectangle.Contains(new Vector2(x, y)) ? Color.white : background;
            tex.SetPixels(data); tex.Apply(); return tex;
        }
        private static Texture2D Read(RenderTexture target)
        {
            var previous = RenderTexture.active; RenderTexture.active = target;
            try
            {
                var tex = new Texture2D(target.width, target.height, TextureFormat.RGB24, false, false);
                tex.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); tex.Apply(); return tex;
            }
            finally { RenderTexture.active = previous; }
        }
        private static Probe Centroid(Texture2D image)
        {
            var p = new Probe(); double xSum = 0, ySum = 0;
            for (int y = 250; y < 750; y++) for (int x = 250; x < 650; x++)
            {
                Color c = image.GetPixel(x, y);
                if (Mathf.Min(c.r, Mathf.Min(c.g, c.b)) < 0.70f) continue;
                p.pixels++; xSum += x; ySum += y;
            }
            if (p.pixels > 0) { p.x = (float)(xSum / p.pixels); p.y = (float)(ySum / p.pixels); }
            return p;
        }
    }
}
