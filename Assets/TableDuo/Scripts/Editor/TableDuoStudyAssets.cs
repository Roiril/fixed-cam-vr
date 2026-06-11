#nullable enable
using System.IO;
using UnityEditor;
using UnityEngine;

namespace TableDuoVr.EditorTools
{
    /// <summary>
    /// 調査用生成アセット（カード面テクスチャ・カード裏・目標配置パターン）。
    /// すべて Assets/TableDuo/Generated/ に PNG + Material として永続化（冪等: 既存なら再利用）。
    /// アイコン PNG（黒・透過）を白地に合成して不透明テクスチャにする
    /// — 透過マテリアルの URP 設定をスクリプトでいじる事故を避けるため。
    /// </summary>
    public static class TableDuoStudyAssets
    {
        private const string GenDir = "Assets/TableDuo/Generated";

        public static Material EnsureCardFaceMaterial(string iconAssetPath, string cardId)
        {
            string matPath = $"{GenDir}/Cards/{cardId}.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (existing != null) return existing;

            Directory.CreateDirectory($"{GenDir}/Cards");
            var icon = LoadReadable(iconAssetPath);
            var tex = new Texture2D(256, 256, TextureFormat.RGBA32, false);
            Fill(tex, Color.white);
            DrawBorder(tex, new Color(0.2f, 0.2f, 0.25f), 6);
            if (icon != null) CompositeCentered(tex, icon, 0.62f);
            string texPath = SavePng(tex, $"{GenDir}/Cards/{cardId}.png");
            return CreateUnlitMaterial(matPath, texPath);
        }

        public static Material EnsureCardBackMaterial()
        {
            string matPath = $"{GenDir}/Cards/back.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (existing != null) return existing;

            Directory.CreateDirectory($"{GenDir}/Cards");
            var tex = new Texture2D(256, 256, TextureFormat.RGBA32, false);
            var navy = new Color(0.17f, 0.24f, 0.45f);
            var navy2 = new Color(0.22f, 0.30f, 0.55f);
            for (int y = 0; y < tex.height; y++)
            {
                for (int x = 0; x < tex.width; x++)
                {
                    bool diag = ((x + y) / 16) % 2 == 0;
                    tex.SetPixel(x, y, diag ? navy : navy2);
                }
            }
            DrawBorder(tex, Color.white, 6);
            string texPath = SavePng(tex, $"{GenDir}/Cards/back.png");
            return CreateUnlitMaterial(matPath, texPath);
        }

        /// <summary>目標配置パターン（3×3 グリッドに 赤/緑/青 の丸）を count 種生成。</summary>
        public static Material[] EnsurePatternMaterials(int count)
        {
            Directory.CreateDirectory($"{GenDir}/Patterns");
            var mats = new Material[count];
            // 固定シード相当: パターンはセッション間で同一であるべき（分析の再現性）
            int[][] layouts =
            {
                new[] { 0, 4, 8 },
                new[] { 2, 3, 7 },
                new[] { 1, 6, 5 },
                new[] { 0, 5, 7 },
            };
            Color[] colors = { new(0.85f, 0.25f, 0.2f), new(0.2f, 0.65f, 0.3f), new(0.25f, 0.4f, 0.85f) };

            for (int i = 0; i < count; i++)
            {
                string matPath = $"{GenDir}/Patterns/pattern{i}.mat";
                var existing = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                if (existing != null)
                {
                    mats[i] = existing;
                    continue;
                }
                var tex = new Texture2D(256, 256, TextureFormat.RGBA32, false);
                Fill(tex, Color.white);
                DrawBorder(tex, new Color(0.2f, 0.2f, 0.25f), 6);
                var cells = layouts[i % layouts.Length];
                for (int c = 0; c < 3; c++)
                {
                    int cell = cells[c];
                    int cx = 43 + (cell % 3) * 85;
                    int cy = 43 + (cell / 3) * 85;
                    DrawCircle(tex, cx, cy, 26, colors[c]);
                }
                string texPath = SavePng(tex, $"{GenDir}/Patterns/pattern{i}.png");
                mats[i] = CreateUnlitMaterial(matPath, texPath);
            }
            return mats;
        }

        // ----- helpers -----

        private static Texture2D? LoadReadable(string assetPath)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null)
            {
                Debug.LogWarning($"[TableDuoStudyAssets] アイコンが見つかりません: {assetPath}");
                return null;
            }
            if (!importer.isReadable)
            {
                importer.isReadable = true;
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
        }

        private static void CompositeCentered(Texture2D dst, Texture2D icon, float coverage)
        {
            int size = (int)(dst.width * coverage);
            int offset = (dst.width - size) / 2;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = (float)x / size;
                    float v = (float)y / size;
                    var src = icon.GetPixelBilinear(u, v);
                    if (src.a <= 0.01f) continue;
                    var bg = dst.GetPixel(offset + x, offset + y);
                    dst.SetPixel(offset + x, offset + y, Color.Lerp(bg, new Color(src.r, src.g, src.b), src.a));
                }
            }
        }

        private static void Fill(Texture2D tex, Color c)
        {
            var arr = tex.GetPixels();
            for (int i = 0; i < arr.Length; i++) arr[i] = c;
            tex.SetPixels(arr);
        }

        private static void DrawBorder(Texture2D tex, Color c, int width)
        {
            for (int y = 0; y < tex.height; y++)
            {
                for (int x = 0; x < tex.width; x++)
                {
                    if (x < width || y < width || x >= tex.width - width || y >= tex.height - width)
                    {
                        tex.SetPixel(x, y, c);
                    }
                }
            }
        }

        private static void DrawCircle(Texture2D tex, int cx, int cy, int r, Color c)
        {
            for (int y = -r; y <= r; y++)
            {
                for (int x = -r; x <= r; x++)
                {
                    if (x * x + y * y <= r * r) tex.SetPixel(cx + x, cy + y, c);
                }
            }
        }

        private static string SavePng(Texture2D tex, string path)
        {
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path);
            return path;
        }

        private static Material CreateUnlitMaterial(string matPath, string texPath)
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            var mat = new Material(shader!)
            {
                mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath),
            };
            AssetDatabase.CreateAsset(mat, matPath);
            return mat;
        }
    }
}
