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
                switch (MarkLabelRouter.Classify(label, out string id))
                {
                    // 特別ラベル: 盤面リセット（ラウンド/ブロック跨ぎで卓上を初期配置へ）。mark 行も残る
                    case MarkAction.ResetBoard:
                        FindObjectOfType<BoardReset>()?.ResetBoard();
                        break;
                    // 特別ラベル: ボドゲ切替（reset_board と同じ遠隔導線）。
                    //   curl "http://<hostIP>:7780/mark?label=game_algo"
                    case MarkAction.GameSwitch:
                    {
                        bool ok = FindObjectOfType<GameSwitcher>()?.ServerSetActiveGameById(id) ?? false;
                        Debug.Log($"[TableDuo] MarkServer game 切替 '{id}' → {(ok ? "成功" : "失敗（id 不一致 / GameSwitcher 不在）")}");
                        break;
                    }
                    // 特別ラベル: アルゴ完全ランダム配り直し（山札・手札を permute）。
                    //   curl "http://<hostIP>:7780/mark?label=algo_deal"
                    case MarkAction.AlgoDeal:
                        FindObjectOfType<AlgoDealer>()?.ServerShuffleDeal();
                        break;
                    // 特別ラベル: バンディド完全ランダム配り直し（手札・山札を permute。開始札は除く）。
                    //   curl "http://<hostIP>:7780/mark?label=bandido_deal"
                    case MarkAction.BandidoDeal:
                        FindObjectOfType<BandidoDealer>()?.ServerShuffleDeal();
                        break;
                }
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
                    // 巨大 label による1行肥大化・破損を防ぐ（カンマ/引用符/改行の無害化は SessionCsv.Escape 側）
                    label = MarkLabelRouter.TruncateLabel(label);
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
