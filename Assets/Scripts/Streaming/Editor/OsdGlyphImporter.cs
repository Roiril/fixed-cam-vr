#nullable enable

using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    /// <summary>
    /// 時刻表示の版（<c>Assets/Resources/Osd/</c>）の取り込み設定を<b>機械で固定する</b>
    /// （<c>canon/LEDGER.md</c> 0108）。焼くのは <c>tools/make-osd-font.py</c>。
    ///
    /// ⚠ <see cref="CommsFaceImporter"/> と違う所が 3 つある:
    /// <list type="bullet">
    /// <item><b><c>isReadable</c> を立てる</b>。この版は GPU では 1 度も引かれない —
    ///       <see cref="ScreenOsd"/> が <c>GetPixels32</c> で CPU へ読み、いまの時刻と周回の
    ///       26 セルぶんを別のテクスチャへ敷き直す。落ちていると実機でだけ例外になり、
    ///       時計が丸ごと出ない。</item>
    /// <item><b>ミップを作らない</b>。CPU で読むだけなので要らない
    ///       （縮小のちらつきは、実際に貼られる敷き直し先の側がミップで面倒を見る）。</item>
    /// <item><b><c>sRGBTexture</c> を立てる</b>。これはマスクではなく<b>色そのもの</b>
    ///       （生成りの字＋暗い縁）。false にすると字が沈んで縁と混ざる。</item>
    /// </list>
    ///
    /// ⚠ 圧縮しない。セル単位でコピーするので、ブロック圧縮が掛かると
    ///   セルの境目に隣の字の破片が滲む（<c>GetPixels32</c> は展開後を返すが、
    ///   展開の時点で既に混ざっている）。
    /// ⚠ <b>版の幅が <c>maxTextureSize</c>（1024）を超えると縮小されてセル幅が狂う。</b>
    ///   いまは 22 セル × 24px ＝ 528px。字を足すときはここを見る。
    /// </summary>
    public sealed class OsdGlyphImporter : AssetPostprocessor
    {
        private const string Folder = "Assets/Resources/Osd/";

        private void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith(Folder, System.StringComparison.Ordinal)) return;
            var ti = (TextureImporter)assetImporter;

            ti.textureType = TextureImporterType.Default;
            ti.textureShape = TextureImporterShape.Texture2D;
            ti.sRGBTexture = true;
            ti.alphaSource = TextureImporterAlphaSource.FromInput;
            ti.alphaIsTransparency = true;
            ti.mipmapEnabled = false;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.filterMode = FilterMode.Bilinear;
            ti.anisoLevel = 1;
            ti.npotScale = TextureImporterNPOTScale.None;
            ti.isReadable = true;
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
