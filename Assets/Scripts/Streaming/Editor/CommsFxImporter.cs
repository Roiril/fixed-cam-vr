#nullable enable

using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    /// <summary>
    /// 連絡の面の髪の版（<c>Assets/Resources/CommsFx/</c>・焼くのは <c>tools/comms-creep/bake.py</c>）の
    /// 取り込み設定を機械で固定する。
    ///
    /// 版は絵ではなく数値（R = 到達順 / G = 房の芯までの深さ / B = 房そのもの）で、シェーダが閾値で切る。
    /// <list type="bullet">
    /// <item><b>sRGB を外す</b>。掛けると到達順が曲がり、髪の伸びる速さが根元と先で変わる。</item>
    /// <item><b>圧縮しない</b>。ASTC のブロックがそのまま前線の段になる。</item>
    /// <item><b>u は Repeat・v は Clamp</b>。版は [原画 | 左右反転] なので u の継ぎ目は無い。v を繰り返すと根元が先の上に出る。</item>
    /// </list>
    /// </summary>
    public sealed class CommsFxImporter : AssetPostprocessor
    {
        private const string Folder = "Assets/Resources/CommsFx/";

        private void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith(Folder, System.StringComparison.Ordinal)) return;
            var ti = (TextureImporter)assetImporter;

            ti.textureType = TextureImporterType.Default;
            ti.textureShape = TextureImporterShape.Texture2D;
            ti.sRGBTexture = false;
            ti.alphaSource = TextureImporterAlphaSource.None;
            ti.mipmapEnabled = true;
            ti.wrapModeU = TextureWrapMode.Repeat;
            ti.wrapModeV = TextureWrapMode.Clamp;
            ti.filterMode = FilterMode.Trilinear;
            ti.anisoLevel = 1;
            ti.npotScale = TextureImporterNPOTScale.None;
            ti.isReadable = false;
            ti.maxTextureSize = 1024;
            ti.textureCompression = TextureImporterCompression.Uncompressed;

            var android = ti.GetPlatformTextureSettings("Android");
            android.overridden = true;
            android.maxTextureSize = 1024;
            android.format = TextureImporterFormat.RGB24;
            android.textureCompression = TextureImporterCompression.Uncompressed;
            ti.SetPlatformTextureSettings(android);
        }
    }
}
