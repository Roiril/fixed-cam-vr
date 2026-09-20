#nullable enable
using System;
using System.Collections.Generic;
using FixedCamVr.Diagnostics;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace FixedCamVr.Streaming.EditorTools
{
    public static class CommsHorrorFontSetup
    {
        public static void Generate()
        {
            const string path = "Assets/Resources/Fonts/CommsHorror SDF.asset";
            var font = AssetDatabase.LoadAssetAtPath<Font>("Assets/Art/Fonts/CommsHorror-Regular.ttf");
            if (font == null) throw new InvalidOperationException("Comms horror source font missing");
            var chars = new SortedSet<char>();
            foreach (ShowLang lang in ShowLanguage.All)
                foreach (char c in CommsPanel.TakeoverLieText(lang)) chars.Add(c);
            chars.Add(' ');
            var asset = TMP_FontAsset.CreateFontAsset(font, 96, 8, GlyphRenderMode.SDFAA,
                1024, 1024, AtlasPopulationMode.Dynamic);
            if (asset == null) throw new InvalidOperationException("Comms horror font bake failed");
            string charset = new string(new List<char>(chars).ToArray());
            if (!asset.TryAddCharacters(charset, out string missing) && !string.IsNullOrEmpty(missing))
                throw new InvalidOperationException("Comms horror missing glyphs: " + missing);
            asset.atlasPopulationMode = AtlasPopulationMode.Static;
            asset.name = "CommsHorror SDF";
            if (AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path) != null) AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(asset, path);
            foreach (var atlas in asset.atlasTextures)
            { atlas.name = asset.name + " Atlas"; AssetDatabase.AddObjectToAsset(atlas, asset); }
            asset.material.name = asset.name + " Material";
            AssetDatabase.AddObjectToAsset(asset.material, asset);
            AssetDatabase.SaveAssets();
            Debug.Log($"[CommsHorrorFont] glyphs={chars.Count} baked={asset.characterTable.Count} missing=0");
        }
    }
}
