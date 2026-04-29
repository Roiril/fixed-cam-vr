#nullable enable
using System;
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

        // フレームバッファ（ダブルバッファ）。書き込み側はワーカ、読み出しはメインスレッド。
        private byte[]? _pending;
        private int _pendingLength;
        private readonly object _lock = new();

        public bool IsConnected { get; private set; }
        public string? LastError { get; private set; }

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
                if (_pending == null)
                {
                    lengthOut = 0;
                    return false;
                }
                if (bufferOut == null || bufferOut.Length < _pendingLength)
                {
                    bufferOut = new byte[Mathf.NextPowerOfTwo(_pendingLength)];
                }
                Buffer.BlockCopy(_pending, 0, bufferOut, 0, _pendingLength);
                lengthOut = _pendingLength;
                _pending = null;
                return true;
            }
        }

        private async Task RunLoopAsync(CancellationToken ct)
        {
            int backoffSec = 1;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await ReceiveOnceAsync(ct);
                    backoffSec = 1;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    IsConnected = false;
                    Debug.LogWarning($"[MJPEG] disconnected: {ex.Message}. retry in {backoffSec}s");
                    try { await Task.Delay(TimeSpan.FromSeconds(backoffSec), ct); }
                    catch (OperationCanceledException) { return; }
                    backoffSec = Math.Min(backoffSec * 2, 30);
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

            IsConnected = true;
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
                lock (_lock)
                {
                    if (_pending == null || _pending.Length < payloadLen)
                        _pending = new byte[Mathf.NextPowerOfTwo(payloadLen)];
                    Buffer.BlockCopy(acc, payloadStart, _pending, 0, payloadLen);
                    _pendingLength = payloadLen;
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
            try { _cts.Cancel(); } catch { }
            try { _http?.Dispose(); } catch { }
            _cts.Dispose();
        }
    }
}
