#nullable enable

using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    /// <summary>
    /// タイトルの版（<c>Assets/Resources/Title/</c>）の取り込み設定を<b>機械で固定する</b>。
    ///
    /// なぜ .meta を手で書かずにここで押さえるか:
    ///   - <b>既定のまま取り込まれると実機で潰れる</b>。Android の既定は ASTC 圧縮で、
    ///     ここは 4 チャンネルが別々の意味を持つマスクなので、圧縮すると混ざる
    ///     （かすれの縁がブロック状にギザつき、朱と白がにじむ）。
    ///   - <b>ミップも要らない</b>。タイトルは常に画面いっぱいなので、ミップは滲ませるだけ。
    ///   - 焼き直し（<c>tools/make-title-art.py</c>）のたびに人が設定を直すことになるし、
    ///     直し忘れは**実機の画を見るまで気づけない**（Editor では綺麗に出る）。
    ///
    /// ⚠ **これはマスクであって絵ではない。** R=主の白墨 / G=朱の墨 / B=溶ける順 / A=添えの白墨。
    /// sRGB 変換が掛かると墨の量が変わるので <c>sRGBTexture = false</c> を必ず立てる。
    /// 焼く側（tools/make-title-art.py）と読む側（TitleGlyph.shader）が対で、片方だけ直すと食い違う。
    /// </summary>
    public sealed class TitleArtImporter : AssetPostprocessor
    {
        private const string Folder = "Assets/Resources/Title/";

        private void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith(Folder, System.StringComparison.Ordinal)) return;
            var ti = (TextureImporter)assetImporter;

            ti.textureType = TextureImporterType.Default;
            ti.textureShape = TextureImporterShape.Texture2D;
            ti.sRGBTexture = false;
            ti.alphaSource = TextureImporterAlphaSource.FromInput;
            // alpha は「透明度」ではなく「距離」。透明度扱いにすると縁の RGB をにじませる処理が入る。
            ti.alphaIsTransparency = false;
            ti.mipmapEnabled = false;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.filterMode = FilterMode.Bilinear;
            ti.anisoLevel = 1;
            ti.npotScale = TextureImporterNPOTScale.None;
            ti.isReadable = false;
            ti.maxTextureSize = 1024;
            ti.textureCompression = TextureImporterCompression.Uncompressed;

            // ⚠ Android は既定を継がずに明示する。継ぐと ASTC へ落ちる機がある。
            var android = ti.GetPlatformTextureSettings("Android");
            android.overridden = true;
            android.maxTextureSize = 1024;
            android.format = TextureImporterFormat.RGBA32;
            android.textureCompression = TextureImporterCompression.Uncompressed;
            ti.SetPlatformTextureSettings(android);
        }
    }
}
