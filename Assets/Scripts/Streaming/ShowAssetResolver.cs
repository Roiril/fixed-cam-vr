#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// show.json のアセット URL を実機で開ける絶対 URL へ解決するヘルパ。
    /// Player は sa:// だけを許す。Editor の著作プレビューでは卓の URL も解決する。
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
        /// show.json のアセット URL を UnityWebRequest / VideoPlayer が開ける URL に解決する。
        /// Player では同梱以外を空文字にする。
        /// </summary>
        public static string Resolve(string url, ShowServerSource? server)
        {
            if (string.IsNullOrEmpty(url)) return "";
            if (!Application.isEditor) return ResolveBakedOnly(url);
            if (url.StartsWith(SaAssetsPrefix))
                return StreamingAssetsUri("show/assets/" + url.Substring(SaAssetsPrefix.Length));
            if (url.StartsWith(SaPrefix))
                return StreamingAssetsUri("show/" + url.Substring(SaPrefix.Length));
            if (IsAbsolute(url)) return url;
            return server != null ? server.Absolute(url) : url;
        }

        /// <summary>Player の演出素材は show/ 配下の同梱ファイルだけを参照する。</summary>
        public static string ResolveBakedOnly(string url)
        {
            if (string.IsNullOrEmpty(url) || !url.StartsWith(SaPrefix, StringComparison.Ordinal)) return "";
            string encoded = url.Substring(SaPrefix.Length);
            string relative;
            try { relative = Uri.UnescapeDataString(encoded); }
            catch (UriFormatException) { return ""; }
            // 区切り文字の符号化や二重符号化は、検証後の再解釈で show/ の外へ出られる。
            if (encoded.Split('/').Length != relative.Split('/').Length) return "";
            if (string.IsNullOrEmpty(relative) || relative.StartsWith("/", System.StringComparison.Ordinal)
                || relative.Contains("\\") || relative.Contains(":") || relative.Contains("?")
                || relative.Contains("#") || relative.Contains("%")) return "";
            string[] parts = relative.Split('/');
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                if (part.Length == 0 || part == "." || part == "..") return "";
                foreach (char c in part) if (char.IsControl(c)) return "";
                try { parts[i] = Uri.EscapeDataString(part); }
                catch (UriFormatException) { return ""; }
            }
            return StreamingAssetsUri("show/" + string.Join("/", parts));
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
