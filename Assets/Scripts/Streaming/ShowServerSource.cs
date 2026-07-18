#nullable enable
using System;
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

        // ---- 実行時オーバーライド（発見プロトコル由来）--------------------------------
        // DiscoveryClient が role="show-server" の announce から解決した PC 卓の現在 IP を、
        // 焼き込み .asset を書き換えずに実行時だけ反映する（CameraSource と同じ [NonSerialized] 方式）。
        // 焼き込み host が現場と不一致（PC の DHCP 変動）でも、announce を拾えば張り替えられる。
        [NonSerialized] private bool _hasOverride;
        [NonSerialized] private string _ovrHost = "";
        [NonSerialized] private int _ovrPort;

        private string EffectiveHost => _hasOverride ? _ovrHost : host;
        private int EffectivePort => _hasOverride ? _ovrPort : port;

        /// <summary>現在の実効ホスト。</summary>
        public string Host => EffectiveHost;
        /// <summary>現在の実効ポート。</summary>
        public int Port => EffectivePort;
        /// <summary>接続先の変化検知キー（host:port）。ループ張り直し要否の判定に使う。</summary>
        public string Endpoint => $"{EffectiveHost}:{EffectivePort}";

        /// <summary>
        /// 発見で解決した PC 卓の現在 IP を実行時に反映する。host が空 / port 不正なら override 解除。
        /// </summary>
        public void ApplyRuntimeEndpoint(string host, int port)
        {
            if (string.IsNullOrEmpty(host) || port <= 0) { ClearRuntimeEndpoint(); return; }
            _hasOverride = true;
            _ovrHost = host;
            _ovrPort = port;
        }

        public void ClearRuntimeEndpoint() => _hasOverride = false;

        public string BuildUrl(string path)
        {
            if (!path.StartsWith("/")) path = "/" + path;
            return $"http://{EffectiveHost}:{EffectivePort}{path}";
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
