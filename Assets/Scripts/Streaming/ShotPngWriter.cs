#nullable enable
using System;
using System.IO;
using System.IO.Compression;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 撮影した RGBA 画素を PNG のバイト列にする純ロジック。
    ///
    /// なぜ自前か: <c>Texture2D.EncodeToPNG</c> はメインスレッド専用で、1280x720 でも Quest では
    /// 数十 ms 止まる。VR で数十 ms のフレーム落ちは体験者に見える。画素の写しだけをメインスレッドで
    /// 取り、圧縮はここで背景スレッドへ逃がす（<see cref="ExperienceShotCapture"/>）。
    ///
    /// ⚠ <c>Texture2D.GetRawTextureData</c> / <c>ReadPixels</c> の行は<b>下から上</b>に並ぶ。
    ///   PNG は上から下なので <paramref name="bottomUp"/> で行を反転して書く。
    /// </summary>
    public static class ShotPngWriter
    {
        private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        /// <summary>
        /// RGBA（1 画素 4 バイト）を PNG にする。<paramref name="rgba"/> は書き換えない。
        /// <paramref name="opaque"/> が true なら alpha を 255 として書く（RGB のみの写真）。
        /// </summary>
        public static byte[] Encode(byte[] rgba, int width, int height, bool bottomUp, bool opaque)
        {
            if (rgba == null) throw new ArgumentNullException(nameof(rgba));
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), "寸法が 0 以下");
            long expected = (long)width * height * 4;
            if (rgba.LongLength != expected)
                throw new ArgumentException($"画素数が寸法と合わない: {rgba.LongLength} != {expected}");

            int stride = width * 4;
            // 各行の先頭に filter byte（0 = None）。Sub / Up を試さないのは、圧縮率より
            // 「撮った直後に終わる速さ」を取るため（写真は数枚で、サイズは問題にならない）。
            var raw = new byte[(stride + 1) * height];
            for (int y = 0; y < height; y++)
            {
                int srcRow = bottomUp ? height - 1 - y : y;
                int src = srcRow * stride;
                int dst = y * (stride + 1);
                raw[dst] = 0;
                Buffer.BlockCopy(rgba, src, raw, dst + 1, stride);
                if (opaque)
                {
                    for (int x = 0; x < width; x++) raw[dst + 1 + x * 4 + 3] = 255;
                }
            }

            using var output = new MemoryStream(raw.Length / 2 + 128);
            output.Write(Signature, 0, Signature.Length);

            var ihdr = new byte[13];
            WriteBigEndian(ihdr, 0, (uint)width);
            WriteBigEndian(ihdr, 4, (uint)height);
            ihdr[8] = 8;   // bit depth
            ihdr[9] = 6;   // colour type: RGBA
            ihdr[10] = 0;  // compression
            ihdr[11] = 0;  // filter
            ihdr[12] = 0;  // interlace
            WriteChunk(output, "IHDR", ihdr, ihdr.Length);

            byte[] zlib = ZlibCompress(raw);
            WriteChunk(output, "IDAT", zlib, zlib.Length);
            WriteChunk(output, "IEND", Array.Empty<byte>(), 0);
            return output.ToArray();
        }

        /// <summary>
        /// premultiplied alpha の RGBA を straight alpha へ戻す（その場で書き換える）。
        /// CG レイヤは「rgb は alpha を掛けた値」で描かれているので、透過 PNG として開くと
        /// 縁が黒ずむ。alpha 0 の画素は rgb も 0 のまま。
        /// </summary>
        public static void UnpremultiplyInPlace(byte[] rgba)
        {
            if (rgba == null) throw new ArgumentNullException(nameof(rgba));
            for (int i = 0; i + 3 < rgba.Length; i += 4)
            {
                int a = rgba[i + 3];
                if (a == 0 || a == 255) continue;
                rgba[i] = (byte)Math.Min(255, (rgba[i] * 255 + a / 2) / a);
                rgba[i + 1] = (byte)Math.Min(255, (rgba[i + 1] * 255 + a / 2) / a);
                rgba[i + 2] = (byte)Math.Min(255, (rgba[i + 2] * 255 + a / 2) / a);
            }
        }

        /// <summary>alpha が 0 でない画素があるか（空の層を「撮れた」と数えないため）。</summary>
        public static bool HasCoverage(byte[] rgba, int minAlpha = 1)
        {
            for (int i = 3; i < rgba.Length; i += 4)
                if (rgba[i] >= minAlpha) return true;
            return false;
        }

        // ---- PNG の部品 ---------------------------------------------------------------

        private static byte[] ZlibCompress(byte[] data)
        {
            using var ms = new MemoryStream(data.Length / 2 + 64);
            // zlib ヘッダ: CMF=0x78（deflate・窓 32K）/ FLG=0x01（FCHECK で 31 の倍数・辞書なし・最速）
            ms.WriteByte(0x78);
            ms.WriteByte(0x01);
            using (var deflate = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                deflate.Write(data, 0, data.Length);
            uint adler = Adler32(data);
            var tail = new byte[4];
            WriteBigEndian(tail, 0, adler);
            ms.Write(tail, 0, 4);
            return ms.ToArray();
        }

        private static void WriteChunk(Stream s, string type, byte[] data, int length)
        {
            var len = new byte[4];
            WriteBigEndian(len, 0, (uint)length);
            s.Write(len, 0, 4);

            var typeBytes = new byte[4];
            for (int i = 0; i < 4; i++) typeBytes[i] = (byte)type[i];
            s.Write(typeBytes, 0, 4);
            if (length > 0) s.Write(data, 0, length);

            uint crc = Crc32Update(0xFFFFFFFFu, typeBytes, 4);
            if (length > 0) crc = Crc32Update(crc, data, length);
            crc ^= 0xFFFFFFFFu;
            var crcBytes = new byte[4];
            WriteBigEndian(crcBytes, 0, crc);
            s.Write(crcBytes, 0, 4);
        }

        private static void WriteBigEndian(byte[] buf, int offset, uint value)
        {
            buf[offset] = (byte)(value >> 24);
            buf[offset + 1] = (byte)(value >> 16);
            buf[offset + 2] = (byte)(value >> 8);
            buf[offset + 3] = (byte)value;
        }

        private static uint[]? _crcTable;

        private static uint Crc32Update(uint crc, byte[] data, int length)
        {
            uint[] table = _crcTable ??= BuildCrcTable();
            for (int i = 0; i < length; i++)
                crc = table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        private static uint Adler32(byte[] data)
        {
            const uint Mod = 65521;
            uint a = 1, b = 0;
            int i = 0;
            while (i < data.Length)
            {
                // 5552 バイトごとに剰余を取れば uint があふれない（zlib の NMAX）。
                int end = Math.Min(i + 5552, data.Length);
                for (; i < end; i++)
                {
                    a += data[i];
                    b += a;
                }
                a %= Mod;
                b %= Mod;
            }
            return (b << 16) | a;
        }
    }
}
