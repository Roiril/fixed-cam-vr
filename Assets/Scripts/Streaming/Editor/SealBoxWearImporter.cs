#nullable enable

using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    /// <summary>
    /// 封印の箱の地（<c>Assets/Resources/Intro/</c>）の取り込み設定を<b>機械で固定する</b>。
    /// 焼くのは <c>tools/make-sealbox-tex.py</c>、読むのは <c>SealedBox.shader</c>。
    ///
    /// タイトルの版（<see cref="TitleArtImporter"/>）と要件が 3 つ違う:
    ///   - <b>Repeat が要る</b>。箱の側面 4 枚は 1 枚のシートとして巻いてあり、
    ///     この地はその上をタイルする（Clamp にすると端が引き伸ばされて縞になる）
    ///   - <b>ミップが要る</b>。体験者は 3m から 0.5m まで近づく。ミップ無しだと
    ///     遠景で研磨目がモアレを起こして「デジタルなちらつき」になる（避けたかったものそのもの）
    ///   - <b>圧縮してよい</b>。4 チャンネルは意味を持つが、地のむら・熾の宿り所はどれも
    ///     なめらかな場で、1/255 の精度を要求しない（題字のマスクとはここが違う）
    /// </summary>
    public sealed class SealBoxWearImporter : AssetPostprocessor
    {
        private const string Folder = "Assets/Resources/Intro/";

        private void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith(Folder, System.StringComparison.Ordinal)) return;
            var ti = (TextureImporter)assetImporter;

            ti.textureType = TextureImporterType.Default;
            ti.textureShape = TextureImporterShape.Texture2D;
            // ⚠ 4 チャンネルは色ではなく量（むら・目・磨耗・熾）。sRGB を通すと量が歪む。
            ti.sRGBTexture = false;
            ti.alphaSource = TextureImporterAlphaSource.FromInput;
            ti.alphaIsTransparency = false;
            ti.mipmapEnabled = true;
            ti.wrapMode = TextureWrapMode.Repeat;
            ti.filterMode = FilterMode.Trilinear;
            ti.anisoLevel = 4;      // 面を斜めから見る（体験者は箱の脇を歩く）
            ti.npotScale = TextureImporterNPOTScale.None;
            ti.isReadable = false;
            ti.maxTextureSize = 512;

            var android = ti.GetPlatformTextureSettings("Android");
            android.overridden = true;
            android.maxTextureSize = 512;
            android.format = TextureImporterFormat.ASTC_6x6;
            ti.SetPlatformTextureSettings(android);
        }
    }
}
