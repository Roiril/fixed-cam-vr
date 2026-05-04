#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// multipart/x-mixed-replace MJPEG ストリームを受信し、最新フレームを byte[] で保持する。
    /// メインスレッドの Update() から TryConsumeFrame でテクスチャ更新する想定。
    /// </summary>
    public sealed class MjpegStreamReceiver : IDisposable
    {
        private readonly string _url;
        private readonly TimeSpan _connectTimeout;
        private readonly CancellationTokenSource _cts = new();
        private HttpClient? _http;
        private Task? _loop;

        // 現在の HTTP 接続を中断するための内側 CTS。RequestReconnect で発火。
        // 外側 _cts は dispose 用、内側 _connectionCts は接続毎に張り替え。
        private CancellationTokenSource? _connectionCts;
        private readonly object _connectionCtsLock = new();

        // フレームキュー（最大 3 スロット）。Wi-Fi のバースト到着で単一スロットだと
        // フレームが上書きで失われる問題（実測 RECV_FPS=22 vs PHONE_FPS=30）への対処。
        // 満杯時は最古を捨てて最新を保持（low-latency 優先）。+100ms 程度のレイテンシと引き換えに
        // 取りこぼしを大幅に削減。
        private const int QueueCapacity = 3;
        private readonly Queue<(byte[] data, int length)> _pendingQueue = new(QueueCapacity);
        private readonly object _lock = new();

        // ワーカスレッド書き込み / メインスレッド読み出しのため volatile / lock で同期する。
        private volatile bool _isConnected;
        private volatile string? _lastError;

        public bool IsConnected => _isConnected;
        public string? LastError => _lastError;

        /// <summary>
        /// 現在の接続を破棄して再接続させる。Wi-Fi 輻輳 / TCP バッファ滞留で
        /// 受信が遅延しているときの強制リセット用。
        /// </summary>
        public void RequestReconnect()
        {
            CancellationTokenSource? toCancel;
            lock (_connectionCtsLock) { toCancel = _connectionCts; }
            try { toCancel?.Cancel(); }
            catch (Exception ex) { Debug.LogWarning($"[MJPEG] reconnect cancel failed: {ex.Message}"); }
        }

        public MjpegStreamReceiver(string url, TimeSpan? connectTimeout = null)
        {
            _url = url;
            _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(3);
        }

        public void Start()
        {
            if (_loop != null) return;
            _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            _loop = Task.Run(() => RunLoopAsync(_cts.Token));
        }

        /// <summary>
        /// 最新フレームの JPEG バイト列を取り出す（呼び出し側が消費）。
        /// 戻り値は false なら新フレーム無し。bufferOut に書き出された有効長は lengthOut。
        /// </summary>
        public bool TryConsumeFrame(ref byte[]? bufferOut, out int lengthOut)
        {
            lock (_lock)
            {
                if (_pendingQueue.Count == 0)
                {
                    lengthOut = 0;
                    return false;
                }
                var (data, len) = _pendingQueue.Dequeue();
                if (bufferOut == null || bufferOut.Length < len)
                {
                    bufferOut = new byte[Mathf.NextPowerOfTwo(len)];
                }
                Buffer.BlockCopy(data, 0, bufferOut, 0, len);
                lengthOut = len;
                return true;
            }
        }

        private async Task RunLoopAsync(CancellationToken ct)
        {
            int backoffSec = 1;
            while (!ct.IsCancellationRequested)
            {
                // 接続毎に独立の CTS を張る。RequestReconnect はこの内側 CTS を Cancel して
                // 外側ループは生き残ったまま再接続する。
                using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                lock (_connectionCtsLock) { _connectionCts = connectionCts; }
                bool wasReconnectRequest = false;
                try
                {
                    await ReceiveOnceAsync(connectionCts.Token);
                    backoffSec = 1;
                }
                catch (OperationCanceledException)
                {
                    if (ct.IsCancellationRequested) return;
                    // 外側 ct は生きている → RequestReconnect による中断
                    wasReconnectRequest = true;
                    _isConnected = false;
                    backoffSec = 1;
                }
                catch (Exception ex)
                {
                    _lastError = ex.Message;
                    _isConnected = false;
                    Debug.LogWarning($"[MJPEG] disconnected: {ex.Message}. retry in {backoffSec}s");
                    try { await Task.Delay(TimeSpan.FromSeconds(backoffSec), ct); }
                    catch (OperationCanceledException) { return; }
                    backoffSec = Math.Min(backoffSec * 2, 30);
                }
                finally
                {
                    lock (_connectionCtsLock) { _connectionCts = null; }
                }
                if (wasReconnectRequest)
                {
                    // 即時に張り直し（バックオフ無し）
                    Debug.Log("[MJPEG] reconnect requested, reopening connection");
                }
            }
        }

        private async Task ReceiveOnceAsync(CancellationToken ct)
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(_connectTimeout);

            using var req = new HttpRequestMessage(HttpMethod.Get, _url);
            using var resp = await _http!.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, connectCts.Token);
            resp.EnsureSuccessStatusCode();

            var contentType = resp.Content.Headers.ContentType?.ToString() ?? "";
            string boundary = ParseBoundary(contentType);
            if (string.IsNullOrEmpty(boundary))
            {
                throw new InvalidOperationException($"boundary not found in Content-Type: {contentType}");
            }

            _isConnected = true;
            using var stream = await resp.Content.ReadAsStreamAsync();
            await ParseMultipartAsync(stream, boundary, ct);
        }

        private static string ParseBoundary(string contentType)
        {
            const string key = "boundary=";
            int i = contentType.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return "";
            string b = contentType.Substring(i + key.Length).Trim().Trim('"');
            int semi = b.IndexOf(';');
            if (semi >= 0) b = b.Substring(0, semi);
            return b.StartsWith("--") ? b : "--" + b;
        }

        private async Task ParseMultipartAsync(Stream stream, string boundary, CancellationToken ct)
        {
            byte[] buf = new byte[64 * 1024];
            byte[] acc = new byte[256 * 1024];
            int accLen = 0;
            byte[] boundaryBytes = System.Text.Encoding.ASCII.GetBytes(boundary);

            while (!ct.IsCancellationRequested)
            {
                int read = await stream.ReadAsync(buf, 0, buf.Length, ct);
                if (read <= 0) throw new IOException("stream ended");

                if (accLen + read > acc.Length)
                {
                    int newSize = Math.Max(acc.Length * 2, accLen + read);
                    Array.Resize(ref acc, newSize);
                }
                Buffer.BlockCopy(buf, 0, acc, accLen, read);
                accLen += read;

                int consumed;
                while ((consumed = TryExtractFrame(acc, accLen, boundaryBytes)) > 0)
                {
                    int remain = accLen - consumed;
                    if (remain > 0) Buffer.BlockCopy(acc, consumed, acc, 0, remain);
                    accLen = remain;
                }
            }
        }

        // 先頭から 1 フレーム抜き出して publish。消費したバイト数を返す。0 ならまだ揃っていない。
        private int TryExtractFrame(byte[] acc, int len, byte[] boundary)
        {
            int b1 = IndexOf(acc, 0, len, boundary);
            if (b1 < 0) return 0;
            int searchFrom = b1 + boundary.Length;
            int b2 = IndexOf(acc, searchFrom, len, boundary);
            if (b2 < 0) return 0;

            // ヘッダ終端（\r\n\r\n）を boundary 間で探す
            int headerEnd = IndexOf(acc, searchFrom, b2, CrlfCrlf);
            if (headerEnd < 0) return b2; // ヘッダが取れない → このパートは飛ばす
            int payloadStart = headerEnd + CrlfCrlf.Length;
            int payloadEnd = b2;
            // boundary 直前の \r\n を除外
            if (payloadEnd - 2 > payloadStart && acc[payloadEnd - 2] == 0x0D && acc[payloadEnd - 1] == 0x0A)
                payloadEnd -= 2;
            int payloadLen = payloadEnd - payloadStart;
            if (payloadLen > 0)
            {
                // フレーム毎にコピーを作って enqueue。3 個を超えたら最古を捨てる。
                // バックグラウンドスレッドで毎フレーム allocation だが 30fps × ~25KB なので GC は問題なし。
                var copy = new byte[payloadLen];
                Buffer.BlockCopy(acc, payloadStart, copy, 0, payloadLen);
                lock (_lock)
                {
                    _pendingQueue.Enqueue((copy, payloadLen));
                    while (_pendingQueue.Count > QueueCapacity)
                    {
                        _pendingQueue.Dequeue();
                    }
                }
            }
            return b2;
        }

        private static readonly byte[] CrlfCrlf = { 0x0D, 0x0A, 0x0D, 0x0A };

        private static int IndexOf(byte[] hay, int start, int end, byte[] needle)
        {
            int max = end - needle.Length;
            for (int i = start; i <= max; i++)
            {
                int j = 0;
                while (j < needle.Length && hay[i + j] == needle[j]) j++;
                if (j == needle.Length) return i;
            }
            return -1;
        }

        public void Dispose()
        {
            // 1. キャンセルを通知
            try { _cts.Cancel(); }
            catch (Exception ex) { Debug.LogWarning($"[MJPEG] cts cancel failed: {ex.Message}"); }

            // 2. ワーカが _http を触り終えるのを短時間だけ待つ（無限待ちは Editor を固める）
            if (_loop != null)
            {
                try { _loop.Wait(TimeSpan.FromMilliseconds(500)); }
                catch (AggregateException) { /* タスク内例外は LastError に既に出ている */ }
                catch (Exception ex) { Debug.LogWarning($"[MJPEG] loop join failed: {ex.Message}"); }
            }

            // 3. HttpClient と CTS を破棄
            try { _http?.Dispose(); }
            catch (Exception ex) { Debug.LogWarning($"[MJPEG] http dispose failed: {ex.Message}"); }
            _http = null;

            try { _cts.Dispose(); }
            catch (Exception ex) { Debug.LogWarning($"[MJPEG] cts dispose failed: {ex.Message}"); }
        }
    }
}
