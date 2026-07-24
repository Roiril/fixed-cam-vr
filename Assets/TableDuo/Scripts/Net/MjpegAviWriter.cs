#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TableDuoVr.Net
{
    /// <summary>
    /// MJPEG を格納する AVI（RIFF）コンテナの最小 writer。UnityEngine 非依存の純ロジックで
    /// EditMode テスト対象（テストは MemoryStream に書いて検証する）。
    /// <see cref="SpectatorRecorder"/> が「俯瞰」「人役 FPV」の 2 本を並行して書き出す。
    ///
    /// 各フレームは JPEG バイト列そのままを movi 内の '00dc' チャンクとして追記し、<see cref="Close"/> 時に
    /// RIFF / avih / strh / movi のサイズと idx1（フレーム索引）を後追いで patch する。
    /// 奇数長のチャンクは 1 バイトパディングで word（偶数）境界に整列させる（AVI 仕様要件）。
    /// biCompression='MJPG' の MJPG-AVI は Windows 標準プレイヤー・VLC 等がそのまま再生できる。
    /// </summary>
    public sealed class MjpegAviWriter : IDisposable
    {
        // hdrl / strl の LIST サイズは中身が固定長なので定数（後述の書き込み順と一致）。
        private const uint HdrlListSize = 192; // 'hdrl'(4) + avih(8+56) + strl LIST(8+116)
        private const uint StrlListSize = 116; // 'strl'(4) + strh(8+56) + strf(8+40)

        private readonly Stream _stream;
        private readonly BinaryWriter _bw;
        private readonly int _width;
        private readonly int _height;
        private readonly int _fps;

        // Close 時に patch する DWORD のファイル位置
        private long _riffSizePos;
        private long _avihTotalFramesPos;
        private long _avihBufferPos;
        private long _strhLengthPos;
        private long _strhBufferPos;
        private long _moviListSizePos;
        private long _moviFourccPos;   // idx1 のオフセット基準（'movi' fourcc 位置）

        private readonly List<(uint offset, uint size)> _index = new();
        private int _frameCount;
        private uint _maxFrameSize;
        private bool _closed;

        public MjpegAviWriter(Stream stream, int width, int height, int fps)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            if (!stream.CanSeek)
                throw new ArgumentException("seek 可能な Stream が必要（Close 時に size を patch するため）", nameof(stream));
            _width = width < 1 ? 1 : width;
            _height = height < 1 ? 1 : height;
            _fps = fps < 1 ? 1 : fps;
            // BinaryWriter は uint/ushort を常にリトルエンディアンで書く。stream は呼び出し側所有なので leaveOpen。
            _bw = new BinaryWriter(_stream, new UTF8Encoding(false), leaveOpen: true);
            WriteHeaders();
        }

        /// <summary>1 フレーム（JPEG バイト列の先頭 length バイト）を movi へ追記する。</summary>
        public void WriteFrame(byte[] jpeg, int length)
        {
            if (_closed) throw new InvalidOperationException("Close 済みの writer には書き込めません");
            if (jpeg == null) throw new ArgumentNullException(nameof(jpeg));
            if (length < 0 || length > jpeg.Length)
                throw new ArgumentOutOfRangeException(nameof(length));

            long chunkPos = _stream.Position;
            Fourcc("00dc");
            U32((uint)length);
            _bw.Write(jpeg, 0, length);
            if ((length & 1) == 1) _bw.Write((byte)0); // 奇数長は 1 バイトパディングで word 整列

            uint offset = (uint)(chunkPos - _moviFourccPos); // 'movi' fourcc 相対（先頭チャンク=4）
            _index.Add((offset, (uint)length));
            if ((uint)length > _maxFrameSize) _maxFrameSize = (uint)length;
            _frameCount++;
        }

        /// <summary>idx1 を書き出し、全サイズフィールドを patch して確定する。二重呼び出しは無害。</summary>
        public void Close()
        {
            if (_closed) return;
            _closed = true;

            long idx1Pos = _stream.Position;
            Fourcc("idx1");
            U32((uint)(_index.Count * 16));
            foreach (var (offset, size) in _index)
            {
                Fourcc("00dc");
                U32(0x10);   // dwFlags = AVIIF_KEYFRAME
                U32(offset); // dwChunkOffset（'movi' 相対）
                U32(size);   // dwChunkLength
            }
            long end = _stream.Position;

            // 後追い patch（seek → 上書き）
            PatchU32(_moviListSizePos, (uint)(idx1Pos - (_moviListSizePos + 4))); // movi LIST の実サイズ
            PatchU32(_riffSizePos, (uint)(end - 8));                             // RIFF 全体サイズ
            PatchU32(_avihTotalFramesPos, (uint)_frameCount);
            PatchU32(_avihBufferPos, _maxFrameSize);
            PatchU32(_strhLengthPos, (uint)_frameCount);
            PatchU32(_strhBufferPos, _maxFrameSize);

            _stream.Seek(end, SeekOrigin.Begin);
            _bw.Flush();
            _stream.Flush();
        }

        public void Dispose() => Close();

        // ── ヘッダ書き込み（movi 直前まで。サイズは placeholder を書いて位置を控える）──────────
        private void WriteHeaders()
        {
            uint microPerFrame = (uint)(1_000_000 / _fps);
            uint biSizeImage = (uint)(_width * _height * 3);

            Fourcc("RIFF");
            _riffSizePos = _stream.Position; U32(0);      // patch: RIFF サイズ
            Fourcc("AVI ");

            // LIST hdrl
            Fourcc("LIST");
            U32(HdrlListSize);
            Fourcc("hdrl");

            // avih（MainAVIHeader・56 バイト）
            Fourcc("avih");
            U32(56);
            U32(microPerFrame);                            // dwMicroSecPerFrame
            U32(0);                                        // dwMaxBytesPerSec
            U32(0);                                        // dwPaddingGranularity
            U32(0x10);                                     // dwFlags = AVIF_HASINDEX
            _avihTotalFramesPos = _stream.Position; U32(0);// dwTotalFrames（patch）
            U32(0);                                        // dwInitialFrames
            U32(1);                                        // dwStreams
            _avihBufferPos = _stream.Position; U32(0);     // dwSuggestedBufferSize（patch）
            U32((uint)_width);                             // dwWidth
            U32((uint)_height);                            // dwHeight
            U32(0); U32(0); U32(0); U32(0);                // dwReserved[4]

            // LIST strl
            Fourcc("LIST");
            U32(StrlListSize);
            Fourcc("strl");

            // strh（AVIStreamHeader・56 バイト）
            Fourcc("strh");
            U32(56);
            Fourcc("vids");                                // fccType
            Fourcc("MJPG");                                // fccHandler
            U32(0);                                        // dwFlags
            U16(0);                                        // wPriority
            U16(0);                                        // wLanguage
            U32(0);                                        // dwInitialFrames
            U32(1);                                        // dwScale
            U32((uint)_fps);                               // dwRate（= dwScale あたりのフレーム数 = fps）
            U32(0);                                        // dwStart
            _strhLengthPos = _stream.Position; U32(0);     // dwLength（patch: 総フレーム数）
            _strhBufferPos = _stream.Position; U32(0);     // dwSuggestedBufferSize（patch）
            U32(0);                                        // dwQuality
            U32(0);                                        // dwSampleSize
            U16(0); U16(0);                                // rcFrame.left, top
            U16((ushort)_width); U16((ushort)_height);     // rcFrame.right, bottom

            // strf（BITMAPINFOHEADER・40 バイト）
            Fourcc("strf");
            U32(40);
            U32(40);                                       // biSize
            U32((uint)_width);                             // biWidth
            U32((uint)_height);                            // biHeight
            U16(1);                                        // biPlanes
            U16(24);                                       // biBitCount
            Fourcc("MJPG");                                // biCompression
            U32(biSizeImage);                              // biSizeImage
            U32(0); U32(0);                                // biXPelsPerMeter, biYPelsPerMeter
            U32(0); U32(0);                                // biClrUsed, biClrImportant

            // LIST movi（ここからフレームチャンクを追記）
            Fourcc("LIST");
            _moviListSizePos = _stream.Position; U32(0);   // patch: movi LIST サイズ
            _moviFourccPos = _stream.Position;
            Fourcc("movi");
        }

        private void PatchU32(long pos, uint value)
        {
            _stream.Seek(pos, SeekOrigin.Begin);
            U32(value);
        }

        private void Fourcc(string s)
        {
            _bw.Write((byte)s[0]);
            _bw.Write((byte)s[1]);
            _bw.Write((byte)s[2]);
            _bw.Write((byte)s[3]);
        }

        private void U32(uint v) => _bw.Write(v);
        private void U16(ushort v) => _bw.Write(v);
    }
}
