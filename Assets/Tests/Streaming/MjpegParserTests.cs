#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// MjpegStreamReceiver のバイト列パーサ（framing / dechunk / boundary / part-header）を
    /// fixture で決定論的に固定する。backlog の実バグクラス（chunked prefix 混入で色チカチカ・
    /// boundary 未検出）を回帰固定する。
    ///
    /// framing / dechunk / boundary / part-header はソース変更ゼロで reflection 駆動する。
    /// ヘッダ検証（ReadResponseHeadersAsync）のみ引数型を NetworkStream→Stream に広げた 1 行 seam に依存し、
    /// MemoryStream fixture で 200 / 401 / 4xx を固定する（NetworkStream は MemoryStream から作れないため）。
    /// </summary>
    public sealed class MjpegParserTests
    {
        private const BindingFlags StaticBF = BindingFlags.NonPublic | BindingFlags.Static;
        private const BindingFlags InstBF = BindingFlags.NonPublic | BindingFlags.Instance;

        private readonly List<IDisposable> _disposables = new();

        [TearDown]
        public void Cleanup()
        {
            foreach (var d in _disposables) { try { d.Dispose(); } catch { } }
            _disposables.Clear();
        }

        // ---- reflection ヘルパ ----

        private static string ParseBoundary(string contentType)
        {
            MethodInfo m = typeof(MjpegStreamReceiver).GetMethod("ParseBoundary", StaticBF)!;
            return (string)m.Invoke(null, new object[] { contentType })!;
        }

        private static Stream NewChunkedReadStream(Stream inner)
        {
            Type t = typeof(MjpegStreamReceiver).GetNestedType("ChunkedReadStream", BindingFlags.NonPublic)!;
            return (Stream)Activator.CreateInstance(
                t, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new object[] { inner }, null)!;
        }

        // 小さいバッファで読み切り、チャンク境界がバッファをまたぐ経路も踏ませる。
        private static byte[] ReadAll(Stream s)
        {
            using var outMs = new MemoryStream();
            byte[] buf = new byte[7];
            int n;
            while ((n = s.Read(buf, 0, buf.Length)) > 0) outMs.Write(buf, 0, n);
            return outMs.ToArray();
        }

        private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

        // HTTP/1.1 chunked transfer encoding のフレーム列を組む（size hex + CRLF + data + CRLF ... 0 CRLF CRLF）。
        private static byte[] BuildChunked(params byte[][] chunks)
        {
            using var ms = new MemoryStream();
            void W(string s) { var b = Ascii(s); ms.Write(b, 0, b.Length); }
            foreach (var c in chunks)
            {
                W(c.Length.ToString("x"));
                W("\r\n");
                ms.Write(c, 0, c.Length);
                W("\r\n");
            }
            W("0\r\n\r\n");
            return ms.ToArray();
        }

        private MjpegStreamReceiver NewReceiver()
        {
            var r = new MjpegStreamReceiver("http://localhost/video");
            _disposables.Add(r);
            return r;
        }

        // ParseMultipartAsync を MemoryStream fixture で回す。フィクスチャ末尾で stream ended になり
        // IOException を投げるが、それは「入力を全部食い切った」正常終了なので飲む。
        // 別種の IOException（accumulator 上限超過）を検出したい場合は返り値の例外を見る。
        private static Exception? RunParseMultipart(MjpegStreamReceiver r, Stream s, string boundary)
        {
            MethodInfo m = typeof(MjpegStreamReceiver).GetMethod("ParseMultipartAsync", InstBF)!;
            var task = (Task)m.Invoke(r, new object[] { s, boundary, CancellationToken.None })!;
            try { task.GetAwaiter().GetResult(); return null; }
            catch (Exception e) { return e; }
        }

        private static bool TryConsume(MjpegStreamReceiver r, out byte[] payload, out long captureNs, out long seq)
        {
            byte[]? buf = null;
            bool ok = r.TryConsumeFrame(ref buf, out int len, out MjpegStreamReceiver.FrameMeta meta);
            if (!ok) { payload = Array.Empty<byte>(); captureNs = 0; seq = 0; return false; }
            payload = new byte[len];
            Array.Copy(buf!, 0, payload, 0, len);
            captureNs = meta.captureNs;
            seq = meta.seq;
            return true;
        }

        // ---- ParseBoundary ----

        [Test]
        public void ParseBoundary_PlainBoundary()
            => Assert.That(ParseBoundary("multipart/x-mixed-replace; boundary=frame"), Is.EqualTo("--frame"));

        [Test]
        public void ParseBoundary_QuotedBoundaryStripped()
            => Assert.That(ParseBoundary("multipart/x-mixed-replace; boundary=\"myB\""), Is.EqualTo("--myB"));

        [Test]
        public void ParseBoundary_TrailingParamsStripped()
            => Assert.That(ParseBoundary("multipart/x-mixed-replace; boundary=frame; charset=utf-8"),
                           Is.EqualTo("--frame"));

        [Test]
        public void ParseBoundary_AlreadyDashPrefixedKept()
            => Assert.That(ParseBoundary("multipart/x-mixed-replace; boundary=--custom"), Is.EqualTo("--custom"));

        [Test]
        public void ParseBoundary_MissingBoundaryReturnsEmpty()
            => Assert.That(ParseBoundary("text/plain"), Is.EqualTo(""));

        // ---- ChunkedReadStream ----

        [Test]
        public void Chunked_SingleChunkRoundtrips()
        {
            byte[] data = { 10, 20, 30, 40, 50 };
            byte[] wire = BuildChunked(data);
            byte[] got = ReadAll(NewChunkedReadStream(new MemoryStream(wire)));
            Assert.That(got, Is.EqualTo(data));
        }

        [Test]
        public void Chunked_MultipleChunksConcatenated()
        {
            byte[] a = { 1, 2, 3 };
            byte[] b = { 4, 5 };
            byte[] c = { 6, 7, 8, 9 };
            byte[] wire = BuildChunked(a, b, c);
            byte[] got = ReadAll(NewChunkedReadStream(new MemoryStream(wire)));
            Assert.That(got, Is.EqualTo(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }));
        }

        [Test]
        public void Chunked_ChunkExtensionSkipped()
        {
            // "5;gzip" のサイズ行の chunk-extension を読み飛ばす。
            using var ms = new MemoryStream();
            void W(string s) { var b = Ascii(s); ms.Write(b, 0, b.Length); }
            W("5;gzip\r\n"); W("HELLO"); W("\r\n"); W("0\r\n\r\n");
            byte[] got = ReadAll(NewChunkedReadStream(new MemoryStream(ms.ToArray())));
            Assert.That(Encoding.ASCII.GetString(got), Is.EqualTo("HELLO"));
        }

        [Test]
        public void Chunked_HexSizeCaseInsensitive()
        {
            byte[] data = new byte[0x0a]; // 10 バイト
            for (int i = 0; i < data.Length; i++) data[i] = (byte)(i + 1);

            byte[] lower = BuildManualChunk("0a", data);
            byte[] upper = BuildManualChunk("0A", data);
            byte[] gotLower = ReadAll(NewChunkedReadStream(new MemoryStream(lower)));
            byte[] gotUpper = ReadAll(NewChunkedReadStream(new MemoryStream(upper)));
            Assert.That(gotLower, Is.EqualTo(data));
            Assert.That(gotUpper, Is.EqualTo(data), "大文字 hex サイズも同値に解釈する");
        }

        // サイズ行を任意の文字列で組む（case / 不正文字テスト用）。
        private static byte[] BuildManualChunk(string sizeLine, byte[] data)
        {
            using var ms = new MemoryStream();
            void W(string s) { var b = Ascii(s); ms.Write(b, 0, b.Length); }
            W(sizeLine); W("\r\n");
            ms.Write(data, 0, data.Length); W("\r\n");
            W("0\r\n\r\n");
            return ms.ToArray();
        }

        [Test]
        public void Chunked_SizeTooLargeThrows()
        {
            // 0x20000000 = 536870912 > MaxFrameBytes(16MB) → IOException。
            byte[] wire = Ascii("20000000\r\n");
            var s = NewChunkedReadStream(new MemoryStream(wire));
            Assert.That(() => ReadAll(s), Throws.TypeOf<IOException>());
        }

        [Test]
        public void Chunked_NonHexCharThrows()
        {
            byte[] wire = Ascii("zz\r\nX\r\n0\r\n\r\n");
            var s = NewChunkedReadStream(new MemoryStream(wire));
            Assert.That(() => ReadAll(s), Throws.TypeOf<IOException>());
        }

        [Test]
        public void Chunked_EmptySizeThrows()
        {
            // 先頭がいきなり CRLF = サイズ桁ゼロ → IOException。
            byte[] wire = Ascii("\r\n");
            var s = NewChunkedReadStream(new MemoryStream(wire));
            Assert.That(() => ReadAll(s), Throws.TypeOf<IOException>());
        }

        // ---- ParseMultipart → TryConsumeFrame ----

        // マルチパートストリーム（3 boundary = 2 フレーム抽出可能）を組む。各 payload の直前に CRLF を置き、
        // トリム経路（末尾 CRLF 剥がし）を踏ませる。
        private static byte[] BuildMultipart(string boundary,
            (string headers, byte[] payload)[] parts)
        {
            using var ms = new MemoryStream();
            void W(string s) { var b = Ascii(s); ms.Write(b, 0, b.Length); }
            foreach (var (headers, payload) in parts)
            {
                W(boundary); W("\r\n");
                W(headers);            // 末尾は "\r\n\r\n"（ヘッダ終端）まで含める
                ms.Write(payload, 0, payload.Length);
                W("\r\n");             // payload と次 boundary の区切り（トリム対象）
            }
            W(boundary); W("\r\n");    // 末尾の閉じ boundary（最後の payload を b2 で確定させる）
            return ms.ToArray();
        }

        [Test]
        public void Multipart_LatestPayloadExtractedAndTrimmed()
        {
            byte[] p1 = { 1, 2, 3 };
            byte[] p2 = { 9, 8, 7, 6 };
            byte[] wire = BuildMultipart("--frame", new[]
            {
                ("Content-Type: image/jpeg\r\n\r\n", p1),
                ("Content-Type: image/jpeg\r\n\r\n", p2),
            });

            var r = NewReceiver();
            RunParseMultipart(r, new MemoryStream(wire), "--frame");

            Assert.That(TryConsume(r, out byte[] payload, out _, out _), Is.True);
            Assert.That(payload, Is.EqualTo(p2), "単一スロット = 最新（2 枚目）payload、末尾 CRLF はトリム");
        }

        [Test]
        public void Multipart_PartHeaderMetaCaseInsensitive()
        {
            byte[] p1 = { 1 };
            byte[] p2 = { 2 };
            byte[] wire = BuildMultipart("--frame", new[]
            {
                ("X-Capture-Ns: 111\r\nX-Frame-Seq: 5\r\n\r\n", p1),
                ("x-CAPTURE-ns: 222\r\nX-Frame-SEQ: 9\r\n\r\n", p2), // 大文字小文字混在
            });

            var r = NewReceiver();
            RunParseMultipart(r, new MemoryStream(wire), "--frame");

            Assert.That(TryConsume(r, out _, out long captureNs, out long seq), Is.True);
            Assert.That(captureNs, Is.EqualTo(222L), "最新パートの X-Capture-Ns（大文字小文字非依存）");
            Assert.That(seq, Is.EqualTo(9L), "最新パートの X-Frame-Seq（大文字小文字非依存）");
        }

        [Test]
        public void Multipart_SingleSlotKeepsOnlyLatest()
        {
            byte[] p1 = { 1, 1 };
            byte[] p2 = { 2, 2 };
            byte[] wire = BuildMultipart("--frame", new[]
            {
                ("Content-Type: image/jpeg\r\n\r\n", p1),
                ("Content-Type: image/jpeg\r\n\r\n", p2),
            });

            var r = NewReceiver();
            RunParseMultipart(r, new MemoryStream(wire), "--frame");

            Assert.That(TryConsume(r, out byte[] first, out _, out _), Is.True);
            Assert.That(first, Is.EqualTo(p2), "2 フレーム流しても最新 1 枚だけ残る");
            Assert.That(TryConsume(r, out _, out _, out _), Is.False, "消費後は空スロット（新フレーム無し）");
        }

        [Test]
        public void Multipart_NoBoundaryJunkOverflowsAccumulator()
        {
            // boundary が二度と来ない巨大ゴミで accumulator が上限超過 → IOException（強制再接続経路）。
            var r = NewReceiver();
            var junk = new JunkStream(20L * 1024 * 1024); // MaxFrameBytes(16MB) を確実に超える
            Exception? ex = RunParseMultipart(r, junk, "--frame");
            Assert.That(ex, Is.TypeOf<IOException>());
            Assert.That(ex!.Message, Does.Contain("accumulator"));
        }

        // 'x' を延々返す Stream（boundary を一切含まない）。total バイトで EOF する。
        private sealed class JunkStream : Stream
        {
            private long _remaining;
            public JunkStream(long total) { _remaining = total; }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_remaining <= 0) return 0;
                int n = (int)Math.Min(count, _remaining);
                for (int i = 0; i < n; i++) buffer[offset + i] = (byte)'x';
                _remaining -= n;
                return n;
            }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
                => Task.FromResult(Read(buffer, offset, count));
        }

        // ---- ReadResponseHeadersAsync（seam 前提: 引数型 Stream）----

        private static (string contentType, string transferEncoding) RunReadHeaders(byte[] response)
        {
            MethodInfo m = typeof(MjpegStreamReceiver).GetMethod("ReadResponseHeadersAsync", StaticBF)!;
            var task = (Task<(string, string)>)m.Invoke(
                null, new object[] { new MemoryStream(response), CancellationToken.None })!;
            return task.GetAwaiter().GetResult();
        }

        private static Exception RunReadHeadersCatch(byte[] response)
        {
            MethodInfo m = typeof(MjpegStreamReceiver).GetMethod("ReadResponseHeadersAsync", StaticBF)!;
            var task = (Task<(string, string)>)m.Invoke(
                null, new object[] { new MemoryStream(response), CancellationToken.None })!;
            try { task.GetAwaiter().GetResult(); return null!; }
            catch (Exception e) { return e; }
        }

        [Test]
        public void ReadHeaders_200ExtractsBoundaryAndTransferEncoding()
        {
            byte[] resp = Ascii(
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: multipart/x-mixed-replace; boundary=frame\r\n" +
                "Transfer-Encoding: chunked\r\n" +
                "\r\n");
            var (contentType, transferEncoding) = RunReadHeaders(resp);
            Assert.That(ParseBoundary(contentType), Is.EqualTo("--frame"));
            Assert.That(transferEncoding.Trim(), Is.EqualTo("chunked"));
        }

        [Test]
        public void ReadHeaders_401ThrowsWithBasicAuthHint()
        {
            byte[] resp = Ascii("HTTP/1.1 401 Unauthorized\r\n\r\n");
            Exception ex = RunReadHeadersCatch(resp);
            Assert.That(ex, Is.TypeOf<InvalidOperationException>());
            Assert.That(ex.Message, Does.Contain("Basic 認証"));
        }

        [Test]
        public void ReadHeaders_5xxThrowsHttpError()
        {
            byte[] resp = Ascii("HTTP/1.1 500 Internal Server Error\r\n\r\n");
            Exception ex = RunReadHeadersCatch(resp);
            Assert.That(ex, Is.TypeOf<InvalidOperationException>());
            Assert.That(ex.Message, Does.Contain("http error"));
        }
    }
}
