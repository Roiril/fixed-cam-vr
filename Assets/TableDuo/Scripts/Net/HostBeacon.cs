#nullable enable
using System;
using System.Net;
using System.Net.Sockets;
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// ホスト存在通知の UDP ブロードキャストビーコン（LAN ホスト自動発見の送信側）。
    /// ConnectionManager.StartHost 成功時に開始され、1Hz で
    /// 「TDVB1|&lt;NGOポート&gt;|&lt;sceneHash&gt;」を UDP :7778 へ broadcast する。
    /// クライアント（HostDiscovery）はこれを受けて送信元 IP へ自動接続する。
    /// sceneHash はビルド時に BuildVariants が焼き込む（host/client のシーン構成不一致の検出用 —
    /// 2026-07-10「古い host に掴み RPC を捨てられる」事故の見える化）。
    /// </summary>
    public sealed class HostBeacon : MonoBehaviour
    {
        public const int BeaconPort = 7778;
        public const string Magic = HostBeaconMessage.Magic;
        private const float IntervalSeconds = 1f;

        private UdpClient? _udp;
        private byte[]? _payload;
        private float _nextSend;

        /// <summary>ビーコン送信を開始する（ngoPort = NGO の listen ポート）。</summary>
        public void Begin(ushort ngoPort)
        {
            string hash = TableDuoBuildInfo.SceneHash;
            _payload = HostBeaconMessage.Build(ngoPort, hash);
            try
            {
                _udp = new UdpClient { EnableBroadcast = true };
                Debug.Log($"[TableDuo] HostBeacon 開始 :{BeaconPort}（1Hz broadcast, sceneHash={hash}）");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TableDuo] HostBeacon 起動失敗（自動発見なしで続行）: {e.Message}");
                _udp = null;
            }
        }

        private void Update()
        {
            if (_udp == null || _payload == null || Time.unscaledTime < _nextSend) return;
            _nextSend = Time.unscaledTime + IntervalSeconds;
            try
            {
                _udp.Send(_payload, _payload.Length, new IPEndPoint(IPAddress.Broadcast, BeaconPort));
            }
            catch (Exception e)
            {
                // 一時的な送信失敗（Wi-Fi 切替等）は次周期に任せる。連続失敗でもホスト機能自体は無傷
                Debug.LogWarning($"[TableDuo] HostBeacon 送信失敗: {e.Message}");
            }
        }

        private void OnDestroy()
        {
            _udp?.Close();
            _udp = null;
        }
    }
}
