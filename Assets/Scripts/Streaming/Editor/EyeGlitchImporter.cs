#nullable enable
using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    /// <summary>
    /// 闇の目の版（<c>Assets/Resources/Eyes/EyeGlitch.png</c>・<c>tools/make-eye-glitch.py</c> が焼く）の取り込み設定を固定する
    /// （<c>canon/LEDGER.md</c> 0238）。
    ///
    /// <list type="bullet">
    ///   <item><b>mip は要る</b>。目は 4°〜54° と 10 倍以上の幅で出るので、遠い目は版を 1/8 に縮めて引く。
    ///         無いと細かな粒がモアレになる（0076「細かいノイズにしない」の型）</item>
    ///   <item><b>Clamp</b>。目の上下の柱は版の縁の行を伸ばして描くので、Repeat だと反対側の縁が漏れる</item>
    ///   <item><b>alpha は入力から</b>（黒の地は焼く側で alpha 0 にしてある）。前乗算はシェーダがやる</item>
    ///   <item>Android は ASTC 6x6（RGBA・2048）。無圧縮だと 16MB でメモリを食う</item>
    /// </list>
    /// </summary>
    public sealed class EyeGlitchImporter : AssetPostprocessor
    {
        private const string Folder = "Assets/Resources/Eyes/";

        private void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith(Folder, System.StringComparison.Ordinal)) return;
            var ti = (TextureImporter)assetImporter;

            ti.textureType = TextureImporterType.Default;
            ti.textureShape = TextureImporterShape.Texture2D;
            ti.sRGBTexture = true;
            ti.alphaSource = TextureImporterAlphaSource.FromInput;
            ti.alphaIsTransparency = true;
            ti.mipmapEnabled = true;
            ti.mipmapFilter = TextureImporterMipFilter.KaiserFilter;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.filterMode = FilterMode.Trilinear;
            ti.anisoLevel = 4;
            ti.npotScale = TextureImporterNPOTScale.None;
            ti.isReadable = false;
            ti.maxTextureSize = 2048;
            ti.textureCompression = TextureImporterCompression.Compressed;

            var android = ti.GetPlatformTextureSettings("Android");
            android.overridden = true;
            android.maxTextureSize = 2048;
            android.format = TextureImporterFormat.ASTC_6x6;
            android.textureCompression = TextureImporterCompression.Compressed;
            ti.SetPlatformTextureSettings(android);
        }
    }
}
