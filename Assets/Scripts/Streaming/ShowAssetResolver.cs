#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// show.json のアセット URL を実機で開ける絶対 URL へ解決するヘルパ。
    ///
    /// - <c>sa://assets/&lt;file&gt;</c> → <c>StreamingAssets/show/assets/&lt;file&gt;</c>（APK 焼き込み経路）
    /// - <c>sa://&lt;rel&gt;</c>        → <c>StreamingAssets/show/&lt;rel&gt;</c>
    /// - 相対 URL（/masks/x.png 等）  → ShowServerSource で PC の絶対 URL へ（ライブ経路）
    /// - 既に絶対（http/https/file/jar）→ そのまま
    ///
    /// StreamingAssets は Android では jar:file:// パスになる。File I/O は使わず、
    /// テクスチャは UnityWebRequest（jar: 対応）、動画は ScreenOverlayController の
    /// UnityWebRequest DL（jar: も読める）を通す。バイナリ直読みはしない。
    /// </summary>
    public static class ShowAssetResolver
    {
        private const string SaAssetsPrefix = "sa://assets/";
        private const string SaPrefix = "sa://";

        /// <summary>
        /// show.json のアセット URL（sa:// / 相対 / 絶対）を UnityWebRequest / VideoPlayer が
        /// 開ける URL に解決する。server が null（オフライン焼き込み）の場合、相対 URL はそのまま返す。
        /// </summary>
        public static string Resolve(string url, ShowServerSource? server)
        {
            if (string.IsNullOrEmpty(url)) return "";
            if (url.StartsWith(SaAssetsPrefix))
                return StreamingAssetsUri("show/assets/" + url.Substring(SaAssetsPrefix.Length));
            if (url.StartsWith(SaPrefix))
                return StreamingAssetsUri("show/" + url.Substring(SaPrefix.Length));
            if (IsAbsolute(url)) return url;
            return server != null ? server.Absolute(url) : url;
        }

        /// <summary>
        /// StreamingAssets 配下の相対パス（"show/show.json" 等）を UnityWebRequest で開ける URI にする。
        /// Android の streamingAssetsPath は既に jar:file:// スキーム付きなのでそのまま連結する。
        /// Editor / Standalone は素のファイルパスなので file:// を前置する。
        /// </summary>
        public static string StreamingAssetsUri(string relativeUnderStreamingAssets)
        {
            string path = Application.streamingAssetsPath + "/" + relativeUnderStreamingAssets;
            return IsAbsolute(path) ? path : "file://" + path;
        }

        private static bool IsAbsolute(string url)
            => url.StartsWith("http://") || url.StartsWith("https://")
               || url.StartsWith("file://") || url.StartsWith("jar:");
    }
}
