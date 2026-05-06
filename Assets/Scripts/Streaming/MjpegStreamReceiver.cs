#nullable enable
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// multipart/x-mixed-replace MJPEG ストリームを受信し、最新フレームを 1 スロットで保持する。
    /// メインスレッドの Update() から TryConsumeFrame でテクスチャ更新する想定。
    ///
    /// 低遅延設計:
    /// - キュー深度 = 1（最新のみ）。受信側でも古フレームは即破棄して滞留させない
    /// - SocketsHttpHandler.ConnectCallback で TCP_NODELAY を立て Nagle 抑止
    /// - SO_RCVBUF を 64KB に縮小し kernel バッファ滞留を抑制
    /// - パート毎に X-Capture-Ns / X-Frame-Seq を抜き出して、消費側で「now-capture > 100ms なら捨てる」判定可能
    /// - フレームバッファはダブルバッファリング（受信側 / 消費側で所有権 swap、コピー無し）
    /// </summary>
    public sealed class MjpegStreamReceiver : IDisposable
    {
        public readonly struct FrameMeta
        {
            public readonly long captureNs;   // streamer 側 monotonic ns
            public readonly long seq;          // 連番
            public readonly long receivedTickMs; // 受信側 wall-clock ms（古フレ判定用）

            public FrameMeta(long captureNs, long seq, long receivedTickMs)
            {
                this.captureNs = captureNs;
                this.seq = seq;
                this.receivedTickMs = receivedTickMs;
            }
        }

        private readonly string _url;
        private readonly TimeSpan _connectTimeout;
        private readonly CancellationTokenSource _cts = new();
        private HttpClient? _http;
        private Task? _loop;

        private CancellationTokenSource? _connectionCts;
        private readonly object _connectionCtsLock = new();

        // 単一スロット最新フレーム。受信スレッドが新フレを書き、Tick が読む。
        // 所有権 swap: TryConsumeFrame は _latestBuf を取り出し、引数で受け取った
        // バッファを「次の受信で使う空きバッファ」として返却する → 受信側はそれを再利用し
        // 一切 new せずに済む。
        private byte[]? _latestBuf;
        private int _latestLen;
        private FrameMeta _latestMeta;
        private bool _hasLatest;

        // 受信側で再利用する空きバッファ（消費側から返却されたもの）
        private byte[]? _spareBuf;

        private readonly object _slotLock = new();

        private volatile bool _isConnected;
        private volatile string? _lastError;

        public bool IsConnected => _isConnected;
        public string? LastError => _lastError;

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

            // SocketsHttpHandler.ConnectCallback で生 Socket に低遅延オプションを立てる。
            // - NoDelay: Nagle 無効化（送信側 ACK 待ちを排除）
            // - ReceiveBufferSize=64KB: kernel 受信バッファを抑え、滞留フレームを溜め込まない
            //   （既定 256KB-1MB 前後だと数フレーム蓄積される）
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                ConnectCallback = async (ctx, ct) =>
                {
                    var s = new Socket(SocketType.Stream, ProtocolType.Tcp)
                    {
                        NoDelay = true,
                        ReceiveBufferSize = 64 * 1024,
                        SendBufferSize = 16 * 1024,
                    };
                    try
                    {
                        await s.ConnectAsync(ctx.DnsEndPoint, ct);
                        return new NetworkStream(s, ownsSocket: true);
                    }
                    catch
                    {
                        s.Dispose();
                        throw;
                    }
                },
            };
            _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            _loop = Task.Run(() => RunLoopAsync(_cts.Token));
        }

        /// <summary>
        /// 最新フレームを受け取る。consumer は再利用可能な空きバッファを recycleBuf として渡し、
        /// 戻り値で渡された frameBuf を使い終わったら次回呼び出し時に渡し直す（再利用）。
        /// 新フレーム無しなら false。
        /// </summary>
        public bool TryConsumeFrame(ref byte[]? frameBuf, out int length, out FrameMeta meta)
        {
            lock (_slotLock)
            {
                if (!_hasLatest || _latestBuf == null)
                {
                    length = 0;
                    meta = default;
                    return false;
                }
                // swap: 呼び出し側から渡された buf を spare として保持し、最新フレーム buf を渡す
                var prev = frameBuf;
                frameBuf = _latestBuf;
                length = _latestLen;
                meta = _latestMeta;

                // spare に置く（受信側が次フレームでそのまま使える）
                _spareBuf = prev;
                _latestBuf = null;
                _hasLatest = false;
                return true;
            }
        }

        private async Task RunLoopAsync(CancellationToken ct)
        {
            int backoffSec = 1;
            while (!ct.IsCancellationRequested)
            {
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

            int headerEnd = IndexOf(acc, searchFrom, b2, CrlfCrlf);
            if (headerEnd < 0) return b2;
            int headerStart = searchFrom;
            int payloadStart = headerEnd + CrlfCrlf.Length;
            int payloadEnd = b2;
            if (payloadEnd - 2 > payloadStart && acc[payloadEnd - 2] == 0x0D && acc[payloadEnd - 1] == 0x0A)
                payloadEnd -= 2;
            int payloadLen = payloadEnd - payloadStart;
            if (payloadLen <= 0) return b2;

            // パートヘッダから X-Capture-Ns / X-Frame-Seq を抜く
            long captureNs = 0L, seq = 0L;
            ParsePartHeaders(acc, headerStart, headerEnd, out captureNs, out seq);

            // 受信側でも「最新だけ」保持する。
            // 古い未消費フレームがある場合はそのまま捨てる（ここでバッファ swap してアロケーション最小化）。
            lock (_slotLock)
            {
                byte[] target;
                if (_latestBuf != null && _latestBuf.Length >= payloadLen)
                {
                    // 既存最新がまだ消費されていないが、新しい方を優先 → 既存 buf を再利用
                    target = _latestBuf;
                }
                else if (_spareBuf != null && _spareBuf.Length >= payloadLen)
                {
                    target = _spareBuf;
                    _spareBuf = null;
                }
                else
                {
                    // 必要サイズに合わせて確保（power-of-two で次回以降の伸長を抑制）
                    target = new byte[Mathf.NextPowerOfTwo(payloadLen)];
                }
                Buffer.BlockCopy(acc, payloadStart, target, 0, payloadLen);
                _latestBuf = target;
                _latestLen = payloadLen;
                _latestMeta = new FrameMeta(captureNs, seq, NowMs());
                _hasLatest = true;
            }
            return b2;
        }

        private static long NowMs() => (long)(Time.realtimeSinceStartupAsDouble * 1000.0);

        // ヘッダ領域 (headerStart..headerEnd) から X-Capture-Ns / X-Frame-Seq を読む。
        // 単純な ASCII スキャン（依存ゼロ・GC 圧無し）。見つからなければ 0。
        private static void ParsePartHeaders(byte[] acc, int headerStart, int headerEnd, out long captureNs, out long seq)
        {
            captureNs = 0;
            seq = 0;
            int i = headerStart;
            while (i < headerEnd)
            {
                int lineEnd = IndexOf(acc, i, headerEnd, Crlf);
                if (lineEnd < 0) lineEnd = headerEnd;

                if (StartsWithIgnoreCase(acc, i, lineEnd, XCaptureNsKey))
                    captureNs = ParseLongAfterColon(acc, i + XCaptureNsKey.Length, lineEnd);
                else if (StartsWithIgnoreCase(acc, i, lineEnd, XFrameSeqKey))
                    seq = ParseLongAfterColon(acc, i + XFrameSeqKey.Length, lineEnd);

                i = lineEnd + Crlf.Length;
            }
        }

        private static long ParseLongAfterColon(byte[] acc, int from, int end)
        {
            int p = from;
            while (p < end && (acc[p] == (byte)':' || acc[p] == (byte)' ' || acc[p] == (byte)'\t')) p++;
            long v = 0;
            while (p < end)
            {
                byte c = acc[p];
                if (c < (byte)'0' || c > (byte)'9') break;
                v = v * 10 + (c - (byte)'0');
                p++;
            }
            return v;
        }

        private static bool StartsWithIgnoreCase(byte[] acc, int start, int end, byte[] needle)
        {
            if (end - start < needle.Length) return false;
            for (int j = 0; j < needle.Length; j++)
            {
                byte a = acc[start + j];
                byte b = needle[j];
                if (a >= (byte)'A' && a <= (byte)'Z') a = (byte)(a + 32);
                if (b >= (byte)'A' && b <= (byte)'Z') b = (byte)(b + 32);
                if (a != b) return false;
            }
            return true;
        }

        private static readonly byte[] CrlfCrlf = { 0x0D, 0x0A, 0x0D, 0x0A };
        private static readonly byte[] Crlf = { 0x0D, 0x0A };
        private static readonly byte[] XCaptureNsKey = System.Text.Encoding.ASCII.GetBytes("x-capture-ns");
        private static readonly byte[] XFrameSeqKey  = System.Text.Encoding.ASCII.GetBytes("x-frame-seq");

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
            try { _cts.Cancel(); }
            catch (Exception ex) { Debug.LogWarning($"[MJPEG] cts cancel failed: {ex.Message}"); }

            if (_loop != null)
            {
                try { _loop.Wait(TimeSpan.FromMilliseconds(500)); }
                catch (AggregateException) { }
                catch (Exception ex) { Debug.LogWarning($"[MJPEG] loop join failed: {ex.Message}"); }
            }

            try { _http?.Dispose(); }
            catch (Exception ex) { Debug.LogWarning($"[MJPEG] http dispose failed: {ex.Message}"); }
            _http = null;

            try { _cts.Dispose(); }
            catch (Exception ex) { Debug.LogWarning($"[MJPEG] cts dispose failed: {ex.Message}"); }
        }
    }
}
