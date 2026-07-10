#nullable enable
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// LAN ホスト自動発見の受信側。UDP :7778 で HostBeacon のブロードキャストを待ち、
    /// 受信したら送信元 IP を <see cref="Found"/> で通知する（メインスレッドへマーシャル済み）。
    /// タイムアウトで諦めない — ホストが後から立っても、被ったまま待てば繋がる。
    /// 受信は背景スレッド（ブロッキング Receive）。OnApplicationPause で止め、復帰で再開する
    /// （Quest のバックグラウンド制限対策）。
    /// </summary>
    public sealed class HostDiscovery : MonoBehaviour
    {
        /// <summary>ホストを発見した（ip, ngoPort, sceneHashMatches）。メインスレッドで発火。</summary>
        public event Action<string, ushort, bool>? Found;

        private UdpClient? _udp;
        private Thread? _thread;
        private volatile bool _running;
        // Resources.Load はメインスレッド専用なので Begin() で先読みしてスレッドへ渡す
        // （受信ループ内で読むと必ず例外で即死し、発見できなくなる — 2026-07-10 実害）
        private string _localSceneHash = "";
        // 受信スレッド → メインスレッドへの受け渡し（最新 1 件で十分）
        private volatile string? _pendingIp;
        private volatile int _pendingPort;
        private volatile bool _pendingHashMatch;

        public void Begin()
        {
            if (_running) return;
            _localSceneHash = TableDuoBuildInfo.SceneHash; // メインスレッドで先読み（Resources.Load 制約）
            try
            {
                _udp = new UdpClient();
                _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udp.Client.Bind(new IPEndPoint(IPAddress.Any, HostBeacon.BeaconPort));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TableDuo] HostDiscovery 起動失敗（手動 IP のみで続行）: {e.Message}");
                _udp = null;
                return;
            }
            _running = true;
            _thread = new Thread(ReceiveLoop) { IsBackground = true, Name = "TDV-HostDiscovery" };
            _thread.Start();
            Debug.Log($"[TableDuo] HostDiscovery 開始 :{HostBeacon.BeaconPort}（ホストのビーコン待ち）");
        }

        public void Stop()
        {
            _running = false;
            _udp?.Close(); // ブロッキング Receive を解除
            _udp = null;
            _thread = null;
        }

        private void ReceiveLoop()
        {
            string localHash = _localSceneHash;
            var any = new IPEndPoint(IPAddress.Any, 0);
            while (_running && _udp != null)
            {
                byte[] data;
                try
                {
                    data = _udp.Receive(ref any);
                }
                catch
                {
                    break; // Close された（Stop/一時停止）
                }
                string text;
                try { text = Encoding.UTF8.GetString(data); } catch { continue; }
                // "TDVB1|<port>|<sceneHash>"
                var parts = text.Split('|');
                if (parts.Length < 2 || parts[0] != HostBeacon.Magic) continue;
                if (!ushort.TryParse(parts[1], out ushort port)) continue;
                string hash = parts.Length >= 3 ? parts[2] : "";
                bool match = string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(localHash) || hash == localHash;
                _pendingIp = any.Address.ToString();
                _pendingPort = port;
                _pendingHashMatch = match;
            }
        }

        private void Update()
        {
            string? ip = _pendingIp;
            if (ip == null) return;
            _pendingIp = null;
            Found?.Invoke(ip, (ushort)_pendingPort, _pendingHashMatch);
        }

        private void OnApplicationPause(bool paused)
        {
            // Quest でスリープ/バックグラウンドに落ちたらスレッドを畳み、復帰で再開
            if (paused) Stop();
            else if (!_running && enabled) Begin();
        }

        private void OnDestroy() => Stop();
    }
}
