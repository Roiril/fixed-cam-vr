#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// Web オペレータ卓サーバ（tools/web-compositor/capture-server.py）の接続先設定。
    /// CameraSource と同じ「現場で host を書き換える」運用（host の変更はコミットしない）。
    /// Editor + Link 運用ならサーバと同じ PC なので 127.0.0.1 のままでよい。
    /// Quest 単体ビルドでは PC の LAN IP に書き換える。
    /// </summary>
    [CreateAssetMenu(menuName = "FixedCamVr/Show Server", fileName = "ShowServer")]
    public sealed class ShowServerSource : ScriptableObject
    {
        [SerializeField] private string host = "127.0.0.1";
        [SerializeField] private int port = 8099;

        public string BuildUrl(string path)
        {
            if (!path.StartsWith("/")) path = "/" + path;
            return $"http://{host}:{port}{path}";
        }

        /// <summary>show.json 内の相対 URL（/masks/x.png 等）を絶対 URL へ。既に絶対ならそのまま。</summary>
        public string Absolute(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            if (url.StartsWith("http://") || url.StartsWith("https://")) return url;
            return BuildUrl(url);
        }
    }
}
