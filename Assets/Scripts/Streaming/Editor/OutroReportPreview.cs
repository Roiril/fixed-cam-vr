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
        [MenuItem("Tools/FixedCamVr/Preview/Ending record")]
        public static void Run()
        {
            BakeFont();
            var stage = new GameObject("[Ending preview]");
            var cameraGo = new GameObject("[Ending preview camera]");
            var rt = new RenderTexture(1600, 1200, 24);
            var image = new Texture2D(1600, 1200, TextureFormat.RGB24, false);
            var previous = RenderTexture.active;
            try
            {
                stage.transform.position = new Vector3(0, 2000, 0);
                var report = stage.AddComponent<OutroReport>();
                var camera = cameraGo.AddComponent<Camera>();
                camera.transform.position = stage.transform.position;
                camera.fieldOfView = 45;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black;
                camera.cullingMask = 1 << 31;
                camera.targetTexture = rt;
                string dir = Path.Combine(Application.dataPath, "Screenshots/ending");
                Directory.CreateDirectory(dir);
                foreach (ShowLang lang in new[] { ShowLang.Ja, ShowLang.En, ShowLang.Fr })
                    foreach (ShowEndingOutcome outcome in new[] { ShowEndingOutcome.Released, ShowEndingOutcome.Trapped, ShowEndingOutcome.Interrupted })
                    {
                        report.PresentPreview(outcome == ShowEndingOutcome.Released ? 12 : 2, 8, outcome, lang);
                        foreach (var t in stage.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 31;
                        foreach (var t in report.Fields) t.ForceMeshUpdate(true, true);
                        camera.Render();
                        RenderTexture.active = rt;
                        image.ReadPixels(new Rect(0, 0, 1600, 1200), 0, 0);
                        image.Apply();
                        string path = Path.Combine(dir, lang + "-" + outcome + ".png");
                        File.WriteAllBytes(path, image.EncodeToPNG());
                        Debug.Log("[EndingPreview] " + path);
                    }
            }
            finally
            {
                RenderTexture.active = previous;
                Object.DestroyImmediate(stage);
                Object.DestroyImmediate(cameraGo);
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(image);
            }
        }

        // Separate atlas: never rewrite the user's in-progress shared HUD fonts.
        public static void BakeFont()
        {
            var source = AssetDatabase.LoadAssetAtPath<Font>("Assets/Art/Fonts/SourceHanSansJP-Normal.otf");
            if (source == null) throw new InvalidOperationException("Ending font source is missing");
            var chars = new System.Collections.Generic.SortedSet<char>();
            for (char c = ' '; c <= '~'; c++) chars.Add(c);
            foreach (ShowLang lang in new[] { ShowLang.Ja, ShowLang.En, ShowLang.Fr })
                foreach (ShowEndingOutcome outcome in new[] { ShowEndingOutcome.Released, ShowEndingOutcome.Trapped, ShowEndingOutcome.Interrupted })
                    foreach (char c in OutroReportText.Compose(123, 8, outcome, lang) + OutroReportText.Ratio(0, 0))
                        if (!char.IsControl(c)) chars.Add(c);
            var charset = new StringBuilder();
            foreach (char c in chars) charset.Append(c);
            var font = TMP_FontAsset.CreateFontAsset(source, 64, 6, GlyphRenderMode.SDFAA, 2048, 2048, AtlasPopulationMode.Dynamic);
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
