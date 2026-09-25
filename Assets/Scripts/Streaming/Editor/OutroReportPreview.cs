#nullable enable
using System;
using System.IO;
using System.Text;
using FixedCamVr.Diagnostics;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using Object = UnityEngine.Object;

namespace FixedCamVr.Streaming.EditorTools
{
    public static class OutroReportPreview
    {
        private const string FontPath = "Assets/Resources/Fonts/OutroReport SDF.asset";
        private const int Width = 1920, Height = 1080;
        [MenuItem("Tools/FixedCamVr/Preview/Ending record")]
        public static void Run()
        {
            ValidateCaptureRendering();
            BakeFont();
            var stage = new GameObject("[Ending preview]");
            var cameraGo = new GameObject("[Ending preview camera]");
            var rt = new RenderTexture(Width, Height, 24);
            var image = new Texture2D(Width, Height, TextureFormat.RGB24, false);
            Texture2D? released = null, trapped = null;
            var previous = RenderTexture.active;
            try
            {
                stage.transform.position = new Vector3(0, 2000, 0);
                var report = stage.AddComponent<OutroReport>();
                ShowCompositePreview.CreateEndingSamples(out released, out trapped);
                report.SetEndingImages(released, trapped);
                var camera = cameraGo.AddComponent<Camera>();
                camera.transform.position = stage.transform.position;
                camera.fieldOfView = 42;
                camera.transform.rotation = Quaternion.Euler(2, 0, 0);
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black;
                camera.cullingMask = 1 << 31;
                camera.targetTexture = rt;
                string dir = Path.Combine(Application.dataPath, "Screenshots/ending");
                Directory.CreateDirectory(dir);
                var evidence = new StringBuilder("language,outcome,field,characters,shown,glyphs,queue,missingPixels\n");
                var blank = new Color32[Width * Height];
                int missingTotal = 0;
                foreach (ShowLang lang in new[] { ShowLang.Ja, ShowLang.En, ShowLang.Fr })
                    foreach (ShowEndingOutcome outcome in new[] { ShowEndingOutcome.Released, ShowEndingOutcome.Trapped, ShowEndingOutcome.Interrupted })
                    {
                        report.PresentPreview(outcome == ShowEndingOutcome.Released ? 12 : 2, 8, outcome, lang);
                        foreach (var t in stage.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 31;
                        foreach (var t in report.Fields)
                            t.ForceMeshUpdate(true, true);
                        camera.Render();
                        RenderTexture.active = rt;
                        image.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
                        image.Apply();
                        string path = Path.Combine(dir, lang + "-" + outcome + ".png");
                        File.WriteAllBytes(path, image.EncodeToPNG());
                        Debug.Log("[EndingPreview] " + path);
                        Color32[] pixels = image.GetPixels32();
                        foreach (var t in report.Fields)
                        {
                            int missingPixels = MissingGlyphPixels(t, camera, pixels, out int glyphs);
                            if (MissingGlyphPixels(t, camera, blank, out _) != glyphs)
                                throw new InvalidOperationException("Glyph pixel probe did not detect the blank calibration image");
                            missingTotal += missingPixels;
                            evidence.AppendLine($"{lang},{outcome},{t.name},{t.textInfo.characterCount},{t.maxVisibleCharacters},{glyphs},{t.fontMaterial.renderQueue},{missingPixels}");
                        }
                    }
                File.WriteAllText(Path.Combine(dir, "layout.csv"), evidence.ToString(), new UTF8Encoding(false));
                if (missingTotal != 0)
                    throw new InvalidOperationException($"Ending preview has {missingTotal} glyphs without rendered pixels; inspect layout.csv");
            }
            finally
            {
                RenderTexture.active = previous;
                Object.DestroyImmediate(stage);
                Object.DestroyImmediate(cameraGo);
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(image);
                if (released != null) Object.DestroyImmediate(released);
                if (trapped != null) Object.DestroyImmediate(trapped);
            }
        }

        private static void ValidateCaptureRendering()
        {
            var source = new Texture2D(64, 64, TextureFormat.RGBA32, false);
            var material = new Material(Shader.Find("FixedCamVr/ScreenComposite"));
            var rt = new RenderTexture(128, 72, 0);
            var readback = new Texture2D(128, 72, TextureFormat.RGBA32, false);
            var previous = RenderTexture.active;
            try
            {
                // Asymmetric image detects flips and a black output. Null material is the negative control.
                for (int y = 0; y < 64; y++)
                for (int x = 0; x < 64; x++)
                    source.SetPixel(x, y, y < 32 ? (x < 32 ? Color.red : Color.green)
                        : (x < 32 ? Color.blue : Color.white));
                source.Apply();
                material.SetTexture("_LiveTex", source);
                material.SetVector("_LiveScale", new Vector4(1, 1, 0, 0));
                material.SetFloat("_ScreenPower", 1);
                material.SetFloat("_ScreenCollapse", 0);
                if (EndingFrameCapture.CaptureComposite(null, rt))
                    throw new InvalidOperationException("Capture accepted a missing material");
                if (!EndingFrameCapture.CaptureComposite(material, rt))
                    throw new InvalidOperationException("Capture rejected a valid material");
                RenderTexture.active = rt;
                readback.ReadPixels(new Rect(0, 0, 128, 72), 0, 0);
                readback.Apply();
                Color red = readback.GetPixel(32, 18), green = readback.GetPixel(96, 18);
                Color blue = readback.GetPixel(32, 54), white = readback.GetPixel(96, 54);
                if (!(red.r > .3f && red.r > red.g * 2 && green.g > .3f && green.g > green.r * 2
                      && blue.b > .3f && blue.b > blue.r * 2 && white.r > .3f && white.g > .3f))
                    throw new InvalidOperationException($"Capture orientation/content failed: {red} {green} {blue} {white}");
                for (int y = 0; y < 64; y++)
                for (int x = 0; x < 64; x++) source.SetPixel(x, y, Color.black);
                source.Apply();
                readback.ReadPixels(new Rect(0, 0, 128, 72), 0, 0);
                readback.Apply();
                if (readback.GetPixel(32, 18) != red)
                    throw new InvalidOperationException("Saved shot changed when the live source changed");
                string dir = Path.Combine(Application.dataPath, "../Logs/ending-roll-preview");
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, "capture-calibration.png"), readback.EncodeToPNG());
                Debug.Log("[EndingPreview] Capture calibration passed: four quadrants rendered in order; null material rejected; saved pixels survive live-source mutation");
            }
            finally
            {
                RenderTexture.active = previous;
                Object.DestroyImmediate(source);
                Object.DestroyImmediate(material);
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(readback);
            }
        }

        // Mesh state alone does not prove that the saved image contains the text.
        private static int MissingGlyphPixels(TMP_Text text, Camera camera, Color32[] pixels, out int glyphs)
        {
            glyphs = 0;
            int missing = 0;
            for (int c = 0; c < text.textInfo.characterCount; c++)
            {
                var ch = text.textInfo.characterInfo[c];
                if (!ch.isVisible) continue;
                glyphs++;
                var lo = camera.WorldToScreenPoint(text.transform.TransformPoint(ch.bottomLeft));
                var hi = camera.WorldToScreenPoint(text.transform.TransformPoint(ch.topRight));
                bool ink = false;
                for (int y = Mathf.Max(0, Mathf.FloorToInt(lo.y)); y < Mathf.Min(Height, Mathf.CeilToInt(hi.y)) && !ink; y++)
                for (int x = Mathf.Max(0, Mathf.FloorToInt(lo.x)); x < Mathf.Min(Width, Mathf.CeilToInt(hi.x)); x++)
                {
                    Color32 p = pixels[y * Width + x];
                    if (p.r > 30 || p.g > 30 || p.b > 30) { ink = true; break; }
                }
                if (!ink) missing++;
            }
            return missing;
        }

        // Separate atlas: never rewrite the user's in-progress shared HUD fonts.
        public static void BakeFont()
        {
            var source = AssetDatabase.LoadAssetAtPath<Font>("Assets/Art/Fonts/SourceHanSerifJP-Regular.otf");
            if (source == null) throw new InvalidOperationException("Ending font source is missing");
            var chars = new System.Collections.Generic.SortedSet<char>();
            for (char c = ' '; c <= '~'; c++) chars.Add(c);
            foreach (ShowLang lang in new[] { ShowLang.Ja, ShowLang.En, ShowLang.Fr })
                foreach (ShowEndingOutcome outcome in new[] { ShowEndingOutcome.Released, ShowEndingOutcome.Trapped, ShowEndingOutcome.Interrupted })
                    foreach (char c in OutroReportText.Compose(1234567890, 8, outcome, lang))
                        if (!char.IsControl(c)) chars.Add(c);
            var charset = new StringBuilder();
            foreach (char c in chars) charset.Append(c);
            var font = TMP_FontAsset.CreateFontAsset(source, 96, 8, GlyphRenderMode.SDFAA, 2048, 2048, AtlasPopulationMode.Dynamic);
            if (!font.TryAddCharacters(charset.ToString(), out string missing))
                throw new InvalidOperationException("Missing ending glyphs: " + missing);
            font.atlasPopulationMode = AtlasPopulationMode.Static;
            if (AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontPath) != null) AssetDatabase.DeleteAsset(FontPath);
            font.name = "OutroReport SDF";
            AssetDatabase.CreateAsset(font, FontPath);
            foreach (var tex in font.atlasTextures)
            {
                tex.name = font.name + " Atlas";
                AssetDatabase.AddObjectToAsset(tex, font);
            }
            font.material.name = font.name + " Material";
            AssetDatabase.AddObjectToAsset(font.material, font);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[EndingPreview] Baked " + chars.Count + " glyphs in isolated ending atlas");
        }
    }
}
