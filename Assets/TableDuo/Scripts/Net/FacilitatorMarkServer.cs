#nullable enable
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading;
using Unity.Netcode;
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// ファシリテータ用フェーズマーク受付（ホストのみ）。
    /// PC から `curl "http://&lt;hostIP&gt;:7780/mark?label=phase2"` で SessionLogger にイベント行を打つ。
    /// 全記録ストリームの突合に使う（study-protocol.md）。LAN 内利用前提。
    /// </summary>
    public sealed class FacilitatorMarkServer : MonoBehaviour
    {
        [SerializeField] private int port = 7780;
        [SerializeField] private SessionLogger? logger;

        private HttpListener? _listener;
        private Thread? _thread;
        private readonly ConcurrentQueue<string> _marks = new();

        private void Update()
        {
            var nm = NetworkManager.Singleton;
            bool shouldRun = nm != null && nm.IsListening && nm.IsServer;
            if (shouldRun && _listener == null) StartServer();
            if (!shouldRun && _listener != null) StopServer();

            while (_marks.TryDequeue(out string? label))
            {
                if (logger == null) logger = FindObjectOfType<SessionLogger>();
                logger?.LogEvent("mark", label);
            }
        }

        private void OnDisable() => StopServer();

        private void StartServer()
        {
            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://*:{port}/");
                _listener.Start();
                _thread = new Thread(Loop) { IsBackground = true };
                _thread.Start();
                Debug.Log($"[TableDuo] MarkServer 起動 port={port}（curl http://<hostIP>:{port}/mark?label=xxx）");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TableDuo] MarkServer 起動失敗: {e.Message}");
                _listener = null;
            }
        }

        private void StopServer()
        {
            try
            {
                _listener?.Stop();
                _listener?.Close();
            }
            catch (Exception)
            {
                // 停止時の例外は無視
            }
            _listener = null;
            _thread = null;
        }

        private void Loop()
        {
            var listener = _listener;
            while (listener != null && listener.IsListening)
            {
                try
                {
                    var ctx = listener.GetContext();
                    string label = ctx.Request.QueryString["label"] ?? "unlabeled";
                    _marks.Enqueue(label);
                    var buf = System.Text.Encoding.UTF8.GetBytes($"marked: {label}\n");
                    ctx.Response.ContentLength64 = buf.Length;
                    ctx.Response.OutputStream.Write(buf, 0, buf.Length);
                    ctx.Response.Close();
                }
                catch (Exception)
                {
                    // Stop() で抜ける
                    return;
                }
            }
        }
    }
}
