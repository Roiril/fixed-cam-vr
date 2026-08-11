#nullable enable

using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    /// <summary>
    /// タイトルの距離場テクスチャ（<c>Assets/Resources/Title/</c>）の取り込み設定を<b>機械で固定する</b>。
    ///
    /// なぜ .meta を手で書かずにここで押さえるか:
    ///   - <b>既定のまま取り込まれると実機で潰れる</b>。Android の既定は ASTC 圧縮で、
    ///     距離場を圧縮すると縁が段になる（字の輪郭がブロック状にギザつく）。
    ///   - <b>ミップも要らない</b>。タイトルは常に画面いっぱいなので、ミップは滲ませるだけ。
    ///   - 焼き直し（<c>tools/make-title-sdf.py</c>）のたびに人が設定を直すことになるし、
    ///     直し忘れは**実機の画を見るまで気づけない**（Editor では綺麗に出る）。
    ///
    /// ⚠ 距離場は **alpha に入っている**。Unity は RGB にだけ sRGB 変換を掛けるので、
    /// RGB に入れると <c>sRGBTexture</c> の設定 1 つで縁の太さが変わる。alpha なら起きない。
    /// 焼く側（make-title-sdf.py）と読む側（TitleGlyph.shader の <c>.a</c>）が対。
    /// </summary>
    public sealed class TitleSdfImporter : AssetPostprocessor
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
