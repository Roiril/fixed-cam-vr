#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// multipart/x-mixed-replace MJPEG ストリームを受信し、最新フレームを 1 スロットで保持する。
    /// メインスレッドの Update() から TryConsumeFrame でテクスチャ更新する想定。
    ///
    /// 低遅延設計:
    /// - 生 Socket で HTTP GET を直接送り、NoDelay / SO_RCVBUF を完全制御（Unity Mono BCL に
    ///   SocketsHttpHandler が無いため HttpClient ベースは使わない）
    /// - キュー深度 = 1（最新のみ）。受信側でも古フレームは即破棄して滞留させない
    /// - パート毎に X-Capture-Ns / X-Frame-Seq を抜き出して、消費側で歯抜け / 古フレ判定可能
    /// - フレームバッファはダブルバッファリング（受信側 / 消費側で所有権 swap、コピー無し）
    /// </summary>
    public sealed class MjpegStreamReceiver : IDisposable
    {
        public readonly struct FrameMeta
        {
            public readonly long captureNs;   // streamer 側 monotonic ns
            public readonly long seq;          // 連番
            public readonly long receivedTickMs; // 受信側 wall-clock ms（古フレ判定用）
            public readonly long connection;

            public FrameMeta(long captureNs, long seq, long receivedTickMs, long connection = 0)
            {
                this.captureNs = captureNs;
                this.seq = seq;
                this.receivedTickMs = receivedTickMs;
                this.connection = connection;
            }
        }

        // フレームバッファ／chunk サイズの上限。境界が二度と来ない・巨大 chunk を吐く
        // 不正ストリームで acc が無制限に伸びて OOM/クラッシュするのを防ぐ。
        // 超過したら例外 → backoff 再接続で復旧する（実フレームは ~100KB なので 16MB は十分余裕）。
        private const int MaxFrameBytes = 16 * 1024 * 1024;

        private readonly string _url;
        private readonly string? _basicAuthToken;
        private readonly TimeSpan _connectTimeout;
        private readonly CancellationTokenSource _cts = new();
        private Task? _loop;
        // host 未設定のカメラでは URL が組めないので null になりうる。詳細はコンストラクタの注記。
        private readonly Uri? _uri;

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

        // RequestReconnect 起因の cancel と、connect timeout 等の異常系 cancel を区別する。
        // これが立っていない OCE/ODE は「意図しない切断」なので backoff を適用する。
        private volatile bool _reconnectRequested;

        // この接続試行で「接続確立（ヘッダのパース成功 = _isConnected 立ち上げ）」まで到達したか。
        // 確立していた接続の切断は、次リトライを backoff リセット（1s）から始めるために使う。
        // ループ毎に false へ戻す。
        private volatile bool _connectionEstablished;

        public bool IsConnected => _isConnected;
        private long _connectionSerial;
        public long ConnectionSerial => Interlocked.Read(ref _connectionSerial);
        public string? LastError => _lastError;

        public void RequestReconnect()
        {
            CancellationTokenSource? toCancel;
            lock (_connectionCtsLock) { toCancel = _connectionCts; }
            _reconnectRequested = true;
            try { toCancel?.Cancel(); }
            catch (Exception ex) { Debug.LogWarning($"[MJPEG] reconnect cancel failed: {ex.Message}"); }
        }

        public MjpegStreamReceiver(string url, TimeSpan? connectTimeout = null, string? basicAuthToken = null)
        {
            _url = url;
            // ⚠ **URL が組めなくても例外を投げない。**
            // host 未設定のカメラ（現場に置いていない枠）では "http://:8080/video" になり
            // `new Uri` が UriFormatException を投げる。ところが CameraStreamRegistry.Awake は
            // 全カメラを 1 本の for で作るので、**1 台が投げると残り全部が初期化されない**
            // （2026-07-31 実害: Phone04 の焼き込み host を空にしたら 3 台とも con=0 になり、
            //  体験が丸ごと砂嵐になった。空 host は「接続しないのが正しい」状態であって異常ではない）。
            // 接続しないまま生かしておけば、後から show.json / discovery で host が入ったときに
            // CameraStream.ReapplyConnection が作り直す。
            Uri.TryCreate(url, UriKind.Absolute, out _uri);
            _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(3);
            _basicAuthToken = basicAuthToken;
        }

        public void Start()
        {
            if (_loop != null) return;
            if (_uri == null) return;  // 接続先が無い。黙って何もしない（IsConnected は false のまま）
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
                _connectionEstablished = false; // この試行の確立フラグをリセット
                using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                lock (_connectionCtsLock) { _connectionCts = connectionCts; }
                bool wasReconnectRequest = false;
                try
                {
                    await ReceiveOnceAsync(connectionCts.Token);
                    backoffSec = 1;
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
                {
                    // RequestReconnect() が connectionCts を cancel → socket.Close() した時、
                    // 進行中の ReadAsync は OperationCanceledException または
                    // ObjectDisposedException を投げる。_reconnectRequested が立っている時だけ
                    // 「意図した再接続」としてクリーン扱い（backoff リセット・即時リトライ）。
                    // 立っていない OCE/ODE は connect timeout 等の異常系なので、
                    // 通常エラーと同じく _lastError + 指数バックオフを適用する
                    // （旧実装は全 OCE/ODE を再接続要求扱いにしており、サーバ不在時に
                    //   バックオフ無しの永久リトライ + 誤 INFO ログになっていた）。
                    if (ct.IsCancellationRequested) return;
                    _isConnected = false;
                    if (_reconnectRequested)
                    {
                        _reconnectRequested = false;
                        wasReconnectRequest = true;
                        backoffSec = 1;
                    }
                    else
                    {
                        // 確立していた接続の切断なら backoff を 1s に戻してから倍化判定する。
                        if (_connectionEstablished) backoffSec = 1;
                        _lastError = "connect timeout / connection aborted";
                        Debug.LogWarning($"[MJPEG] disconnected (timeout/abort). retry in {backoffSec}s");
                        bool interrupted;
                        try { interrupted = await DelayWithReconnect(backoffSec, ct); }
                        catch (OperationCanceledException) { return; }
                        backoffSec = interrupted ? 1 : Math.Min(backoffSec * 2, 30);
                    }
                }
                catch (Exception ex)
                {
                    _isConnected = false;
                    // RequestReconnect が CTS null の隙間（接続間）に呼ばれると cancel は no-op で
                    // フラグだけ残り、例外が OCE/ODE 以外（IOException 等）でここへ落ちることがある。
                    // その場合も「意図した再接続」として消費し即リトライする（フラグを残すと後続の
                    // 無関係な OCE が再接続要求と誤認され backoff を 1 回スキップする）。
                    if (_reconnectRequested)
                    {
                        _reconnectRequested = false;
                        wasReconnectRequest = true;
                        backoffSec = 1;
                    }
                    else
                    {
                        // 確立していた接続の切断なら backoff を 1s に戻してから倍化判定する。
                        if (_connectionEstablished) backoffSec = 1;
                        _lastError = ex.Message;
                        Debug.LogWarning($"[MJPEG] disconnected: {ex.Message}. retry in {backoffSec}s");
                        bool interrupted;
                        try { interrupted = await DelayWithReconnect(backoffSec, ct); }
                        catch (OperationCanceledException) { return; }
                        backoffSec = interrupted ? 1 : Math.Min(backoffSec * 2, 30);
                    }
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

        /// <summary>
        /// バックオフ待機。待機中も CTS を <see cref="_connectionCts"/> に公開し、RequestReconnect
        /// （stall watchdog 等）が待機（最大 30s）を即座に中断できるようにする。中断されたら
        /// _reconnectRequested を消費して true を返す（呼び出し側は backoffSec=1 で即リトライ）。
        /// 外側 ct（Dispose）由来の cancel は OperationCanceledException として上へ伝播する。
        /// </summary>
        private async Task<bool> DelayWithReconnect(int backoffSec, CancellationToken ct)
        {
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (_connectionCtsLock) { _connectionCts = waitCts; }
            try
            {
                // 公開直前に立った reconnect 要求を取りこぼさない（cancel が waitCts に届く前のレース対策）。
                if (_reconnectRequested) { _reconnectRequested = false; return true; }
                await Task.Delay(TimeSpan.FromSeconds(backoffSec), waitCts.Token);
                return false;
            }
            catch (OperationCanceledException)
            {
                if (ct.IsCancellationRequested) throw; // 外側 cancel（Dispose）は上へ
                _reconnectRequested = false;           // RequestReconnect 由来: 消費して即リトライ
                return true;
            }
            finally
            {
                lock (_connectionCtsLock) { _connectionCts = null; }
            }
        }

        private async Task ReceiveOnceAsync(CancellationToken ct)
        {
            // 生 Socket で接続して NoDelay / SO_RCVBUF を立てる。
            // Unity Mono BCL は SocketsHttpHandler を持たないため HttpClient 経由は不可。
            using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,                  // Nagle 抑止
                ReceiveBufferSize = 64 * 1024,   // kernel 受信バッファ縮小（滞留フレーム抑制）
                SendBufferSize = 16 * 1024,
            };

            // ★ socket.Close は「接続確立後も」ct（RequestReconnect / Dispose）に反応させる。
            //   Unity Mono の NetworkStream.ReadAsync は進行中の read を token では中断しないため、
            //   完全 stall（Wi-Fi 切断・スマホスリープで RST が来ない）時に Close だけが
            //   ReadAsync を破れる唯一の手段。register を connect フェーズ限定にすると
            //   lag 検出の RequestReconnect も ReapplyConnection の Dispose も stall を破れない。
            using var closeOnCancel = ct.Register(() => { try { socket.Close(); } catch { } });

            // connect timeout は別 CTS で socket.Close を重ねる（timeout でも Close で中断）。
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(_connectTimeout);

            // ホスト名解決 + 接続。OperationCanceledException はそのまま伝播させる。
            Uri uri = _uri!;  // Start() が null をはじいているのでここには来ない
            string host = uri.Host;
            int port = uri.Port > 0 ? uri.Port : 80;
            using (connectCts.Token.Register(() => { try { socket.Close(); } catch { } }))
            {
                await socket.ConnectAsync(host, port);
            }
            connectCts.Token.ThrowIfCancellationRequested();

            // HTTP/1.1 GET を手書きで送る（最小ヘッダ）
            string path = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;
            string authLine = _basicAuthToken == null ? "" : $"Authorization: Basic {_basicAuthToken}\r\n";
            string reqLine =
                $"GET {path} HTTP/1.1\r\n" +
                $"Host: {host}:{port}\r\n" +
                "User-Agent: fixed-cam-vr/1.0\r\n" +
                "Accept: multipart/x-mixed-replace, image/jpeg, */*\r\n" +
                authLine +
                "Connection: close\r\n" +
                "\r\n";
            byte[] reqBytes = Encoding.ASCII.GetBytes(reqLine);
            await socket.SendAsync(new ArraySegment<byte>(reqBytes), SocketFlags.None);

            using var stream = new NetworkStream(socket, ownsSocket: false);

            // レスポンスヘッダを CRLF CRLF まで読み、Content-Type の boundary と
            // Transfer-Encoding を抜く。NanoHTTPD の newChunkedResponse は
            // Transfer-Encoding: chunked で返してくるため、生 Socket では明示的に
            // デチャンクが必要（HttpClient なら自動）。デチャンクを忘れると hex の
            // チャンク長プレフィックスが multipart 中に混入して JPEG を破壊する
            // → スクリーン映像が R/G/B/黒 にちらつく症状になる。
            var (contentType, transferEncoding) = await ReadResponseHeadersAsync(stream, ct);
            string boundary = ParseBoundary(contentType);
            if (string.IsNullOrEmpty(boundary))
            {
                throw new InvalidOperationException($"boundary not found in Content-Type: {contentType}");
            }

            Interlocked.Increment(ref _connectionSerial);
            _isConnected = true;
            _connectionEstablished = true; // 以降の切断は backoff リセット（次リトライ 1s）対象
            Stream readStream = stream;
            bool useChunked = string.Equals(transferEncoding.Trim(), "chunked", StringComparison.OrdinalIgnoreCase);
            if (useChunked) readStream = new ChunkedReadStream(stream);
            // 接続毎に 1 行だけ。診断用: chunked / boundary が想定通りか目視確認するため。
            Debug.Log($"[MJPEG] connected to {_url} ContentType=\"{contentType}\" TransferEncoding=\"{transferEncoding}\" useChunked={useChunked}");
            await ParseMultipartAsync(readStream, boundary, ct);
        }

        // ステータス行 + ヘッダを CRLFCRLF まで読み、Content-Type と Transfer-Encoding を返す。
        // CRLFCRLF が見つかるまで 1 バイトずつ読む（ヘッダ全体は通常 <1KB なので問題なし）。
        private static async Task<(string contentType, string transferEncoding)> ReadResponseHeadersAsync(Stream stream, CancellationToken ct)
        {
            var sb = new StringBuilder(512);
            byte[] one = new byte[1];
            int matched = 0;
            byte[] pat = { 0x0D, 0x0A, 0x0D, 0x0A };
            while (matched < pat.Length)
            {
                int n = await stream.ReadAsync(one, 0, 1, ct);
                if (n <= 0) throw new IOException("unexpected EOF in response headers");
                byte b = one[0];
                sb.Append((char)b);
                if (b == pat[matched]) matched++;
                else if (b == pat[0]) matched = 1;
                else matched = 0;
                if (sb.Length > 16 * 1024) throw new InvalidOperationException("response headers too large");
            }

            string raw = sb.ToString();
            int firstLineEnd = raw.IndexOf("\r\n", StringComparison.Ordinal);
            if (firstLineEnd < 0) throw new InvalidOperationException("malformed response");
            string statusLine = raw.Substring(0, firstLineEnd);
            string[] parts = statusLine.Split(' ');
            int statusCode = 0;
            if (parts.Length < 2 || !int.TryParse(parts[1], out statusCode) || statusCode < 200 || statusCode >= 300)
            {
                if (statusCode == 401)
                    throw new InvalidOperationException($"http error: {statusLine} (Basic 認証が必要。CameraSource の username/password を設定する)");
                throw new InvalidOperationException($"http error: {statusLine}");
            }

            string contentType = "";
            string transferEncoding = "";
            int p = firstLineEnd + 2;
            while (p < raw.Length)
            {
                int e = raw.IndexOf("\r\n", p, StringComparison.Ordinal);
                if (e < 0 || e == p) break;
                string line = raw.Substring(p, e - p);
                int colon = line.IndexOf(':');
                if (colon > 0)
                {
                    string key = line.Substring(0, colon).Trim();
                    string val = line.Substring(colon + 1).Trim();
                    if (string.Equals(key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                        contentType = val;
                    else if (string.Equals(key, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                        transferEncoding = val;
                }
                p = e + 2;
            }
            return (contentType, transferEncoding);
        }

        /// <summary>
        /// HTTP/1.1 chunked transfer encoding を透過的にデコードする読み取り Stream。
        /// 形式: <c>[hex size]\r\n[size bytes of data]\r\n... 0\r\n\r\n</c>。
        /// HttpClient を捨てて生 Socket にした際、これを忘れると hex prefix が
        /// 配信ペイロードに混入して JPEG が破損する（色のチカチカ症状）。
        /// </summary>
        private sealed class ChunkedReadStream : Stream
        {
            private readonly Stream _inner;
            private int _bytesLeftInChunk;
            private bool _eof;
            // チャンク境界パース用の再利用バッファ。チャンク毎の new をゼロにする
            // （30fps × 数チャンク/フレームで毎秒百個規模の小アロケーションになるため）。
            private readonly byte[] _one = new byte[1];
            private readonly byte[] _crlf = new byte[2];

            public ChunkedReadStream(Stream inner) { _inner = inner; }

            public override bool CanRead => true;
            public override bool CanWrite => false;
            public override bool CanSeek => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count)
                => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            {
                if (_eof || count <= 0) return 0;
                if (_bytesLeftInChunk == 0)
                {
                    int size = await ReadChunkSizeAsync(ct);
                    if (size == 0)
                    {
                        // 0\r\n の後の最終 CRLF（trailers 無しの簡易対応）
                        await ReadCrlfAsync(ct);
                        _eof = true;
                        return 0;
                    }
                    _bytesLeftInChunk = size;
                }
                int toRead = Math.Min(count, _bytesLeftInChunk);
                int n = await _inner.ReadAsync(buffer, offset, toRead, ct);
                if (n <= 0) { _eof = true; return 0; }
                _bytesLeftInChunk -= n;
                if (_bytesLeftInChunk == 0)
                {
                    // chunk data 直後の CRLF
                    await ReadCrlfAsync(ct);
                }
                return n;
            }

            private async Task<int> ReadChunkSizeAsync(CancellationToken ct)
            {
                // hex を int に直接累算し、StringBuilder / string を確保しない。
                // long で受けてから範囲検証する（"80000000" が負値に丸まるのを防ぐ）。
                long size = 0;
                int digits = 0;
                bool sawCR = false;
                bool inExtension = false; // ';' 以降の chunk extension は読み飛ばす
                while (true)
                {
                    int n = await _inner.ReadAsync(_one, 0, 1, ct);
                    if (n <= 0) throw new IOException("EOF in chunk size");
                    byte b = _one[0];
                    if (sawCR && b == 0x0A) break;
                    sawCR = false;
                    if (b == 0x0D) { sawCR = true; continue; }
                    if (inExtension) continue;
                    if (b == (byte)';') { inExtension = true; continue; }
                    int d;
                    if (b >= (byte)'0' && b <= (byte)'9') d = b - (byte)'0';
                    else if (b >= (byte)'a' && b <= (byte)'f') d = b - (byte)'a' + 10;
                    else if (b >= (byte)'A' && b <= (byte)'F') d = b - (byte)'A' + 10;
                    else if (b == (byte)' ' || b == (byte)'\t') continue; // 前後空白は無視
                    else throw new IOException($"invalid chunk size char: 0x{b:X2}");
                    size = size * 16 + d;
                    if (++digits > 8 || size > MaxFrameBytes)
                        throw new IOException($"invalid chunk size: too large ({size})");
                }
                if (digits == 0) throw new IOException("empty chunk size");
                return (int)size;
            }

            private async Task ReadCrlfAsync(CancellationToken ct)
            {
                int got = 0;
                while (got < 2)
                {
                    int n = await _inner.ReadAsync(_crlf, got, 2 - got, ct);
                    if (n <= 0) throw new IOException("EOF on chunk CRLF");
                    got += n;
                }
                if (_crlf[0] != 0x0D || _crlf[1] != 0x0A) throw new IOException("expected CRLF after chunk");
            }
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
                    if (newSize > MaxFrameBytes)
                        throw new IOException(
                            $"MJPEG accumulator exceeded {MaxFrameBytes} bytes (boundary not found / corrupt stream) — forcing reconnect");
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
                _latestMeta = new FrameMeta(captureNs, seq, NowMs(), ConnectionSerial);
                _hasLatest = true;
            }
            return b2;
        }

        // 受信スレッドから呼ばれるため Unity API を避け、プロセス起動からの monotonic ms を使う。
        // （Time.realtimeSinceStartup* は main thread 前提でバージョンによっては警告が出る）
        // 同じ時間基準を CameraStream も利用するため public 公開。
        private static readonly Stopwatch _sw = Stopwatch.StartNew();
        public static long NowMs() => _sw.ElapsedMilliseconds;

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
            // HUD 等に「切断済みなのに Connected」が残らないよう即時反映する。
            _isConnected = false;
            try { _cts.Cancel(); }
            catch (Exception ex) { Debug.LogWarning($"[MJPEG] cts cancel failed: {ex.Message}"); }

            // ★ main スレッドをブロックしない。受信ループは ct を見て自走終了する。
            //   ここで _loop.Wait(500ms) すると、ReapplyConnection（show.json の IP 差し替え）が
            //   stall 中のカメラに対して呼ばれた時に最大 500ms のフレームヒッチになる
            //   ── IP 差し替えはまさにカメラが固まった現場で使うので、最も踏みやすい場面で固まる。
            //   _cts.Cancel() は ct.Register 経由で socket.Close を即発火するため、
            //   完全 stall 中の ReadAsync も即座に破れる。_cts はリンク CTS が
            //   残っている間に dispose すると ObjectDisposedException になるため、ループ完了後
            //   （背景スレッド）に dispose する。
            var loop = _loop;
            if (loop != null)
            {
                loop.ContinueWith(t =>
                {
                    _ = t.Exception; // UnobservedTaskException 抑止のため観測する
                    try { _cts.Dispose(); } catch { }
                }, TaskScheduler.Default);
            }
            else
            {
                try { _cts.Dispose(); } catch { }
            }
        }
    }
}
