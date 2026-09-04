#nullable enable
using System.Collections.Generic;
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

        /// <summary>
        /// 焼き込みの対応表（卓の URL → <c>sa://</c> URL）で 1 本を読み替える。
        ///
        /// ⚠⚠ **端末キャッシュは焼き込みより優先される**ので、卓に一度でも繋いだ機は
        ///   「卓を指す相対 URL」を持ったまま再起動する。卓が落ちているとその URL は解決できない。
        ///   対応表は <c>export_build.bake</c> が焼き込み show.json の <c>assetMap</c> に載せる。
        ///
        /// ⚠ <b>表に無いものは触らない</b>（最後の焼き込み後に卓で足した素材は従来どおり卓を指す）。
        ///   既に <c>sa://</c> のもの・外部の絶対 URL も、表に載らないので自然に素通しになる。
        /// </summary>
        /// <returns>読み替えたら true。<paramref name="baked"/> は false のとき元の値。</returns>
        public static bool TryRemapToBaked(IReadOnlyDictionary<string, string>? map, string? url, out string baked)
        {
            baked = url ?? "";
            if (map == null || map.Count == 0) return false;
            if (string.IsNullOrEmpty(url)) return false;
            if (!map.TryGetValue(url!, out string? found)) return false;
            if (string.IsNullOrEmpty(found) || found == url) return false;
            baked = found!;
            return true;
        }

        private static bool IsAbsolute(string url)
            => url.StartsWith("http://") || url.StartsWith("https://")
               || url.StartsWith("file://") || url.StartsWith("jar:");
    }
}
