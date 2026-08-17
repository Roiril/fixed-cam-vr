#nullable enable

using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    /// <summary>
    /// AIエージェントの顔の版（<c>Assets/Resources/Comms/</c>）の取り込み設定を<b>機械で固定する</b>。
    ///
    /// 焼くのは <c>tools/make-comms-face.py</c>。**RGB は白で固定・A だけが墨の量**なので、
    /// 圧縮すると alpha の縁がブロック状にギザつき、髪の 1 束が滲んで溶ける
    /// （版の 1 辺は 256 で、実機ではさらに 110 画素そこそこまで縮む）。
    ///
    /// ⚠ <see cref="TitleArtImporter"/> と違う所が 2 つある:
    /// <list type="bullet">
    /// <item><b>ミップを作る</b>。題字は常に画面いっぱいだが、この版は 256 → 110 と
    ///       半分以下へ縮むので、ミップが無いと髪の線がちらつく（縮小のエイリアス）。</item>
    /// <item><b><c>alphaIsTransparency</c> を立てる</b>。ここの A は本当に透明度なので、
    ///       透明な所へ RGB を滲ませる処理を通してよい（RGB は一様に白なので実害は無いが、
    ///       立てておかないと版を差し替えたときに縁が暗くなる）。</item>
    /// </list>
    /// </summary>
    public sealed class CommsFaceImporter : AssetPostprocessor
    {
        private const string Folder = "Assets/Resources/Comms/";

        private void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith(Folder, System.StringComparison.Ordinal)) return;
            var ti = (TextureImporter)assetImporter;

            ti.textureType = TextureImporterType.Default;
            ti.textureShape = TextureImporterShape.Texture2D;
            // ⚠ 絵ではなくマスク。sRGB 変換を掛けると墨の量が変わる。
            ti.sRGBTexture = false;
            ti.alphaSource = TextureImporterAlphaSource.FromInput;
            ti.alphaIsTransparency = true;
            ti.mipmapEnabled = true;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.filterMode = FilterMode.Trilinear;
            ti.anisoLevel = 1;
            ti.npotScale = TextureImporterNPOTScale.None;
            ti.isReadable = false;
            ti.maxTextureSize = 512;
            ti.textureCompression = TextureImporterCompression.Uncompressed;

            // ⚠ Android は既定を継がずに明示する。継ぐと ASTC へ落ちる機がある。
            var android = ti.GetPlatformTextureSettings("Android");
            android.overridden = true;
            android.maxTextureSize = 512;
            android.format = TextureImporterFormat.RGBA32;
            android.textureCompression = TextureImporterCompression.Uncompressed;
            ti.SetPlatformTextureSettings(android);
        }
    }
}
