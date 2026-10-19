#nullable enable
using System;
using System.IO;

namespace FixedCamVr.Streaming.Recording
{
    /// <summary>
    /// 端末内録画コンテナ <c>.mjr</c>（MJPEG Recording）の形式定義。
    ///
    /// <code>
    /// "MJR1"                4B   マジック
    /// u32 widthPx           4B   参考値（0 = 不明）
    /// u32 heightPx          4B   参考値（0 = 不明）
    /// --- フレーム（可変個・EOF まで繰り返し）---
    /// u32 len               4B   JPEG バイト長
    /// u32 ptsMs             4B   区間先頭からの提示時刻 (ms)
    /// u8[len]                    JPEG 本体
    /// </code>
    ///
    /// **なぜ mp4 でなく JPEG 列か**: Quest 上の H.264 エンコードは MediaCodec を JNI で叩く実装になり、
    /// コストとリスクが見合わない。JPEG のまま持てば再エンコード劣化ゼロで、再生は既に実績のある
    /// <c>Texture2D.LoadImage</c>（ライブ映像と同じ経路）をそのまま使える。
    ///
    /// **末尾は必ず壊れうる前提で読む**。録画中にアプリが落ちる / 容量上限で打ち切られるのは正常系なので、
    /// 中途半端なフレームは黙って捨てる（例外にしない）。フレーム数はヘッダに持たない。
    ///
    /// 純粋（UnityEngine 非依存）。EditMode テストは <see cref="MemoryStream"/> で往復できる。
    /// </summary>
    public static class RecordedSegmentFormat
    {
        public const int HeaderBytes = 12;
        public const int FrameHeaderBytes = 8;

        /// <summary>1 フレームの上限 (byte)。これを超える長さフィールドは破損とみなして読み止める。</summary>
        public const int MaxFrameBytes = 8 * 1024 * 1024;

        private static readonly byte[] Magic = { (byte)'M', (byte)'J', (byte)'R', (byte)'1' };

        /// <summary>ヘッダを書く。<paramref name="widthPx"/> / <paramref name="heightPx"/> は参考値（0 可）。</summary>
        public static void WriteHeader(Stream dest, int widthPx, int heightPx)
        {
            Span<byte> head = stackalloc byte[HeaderBytes];
            Magic.CopyTo(head);
            WriteU32(head.Slice(4), (uint)Math.Max(0, widthPx));
            WriteU32(head.Slice(8), (uint)Math.Max(0, heightPx));
            dest.Write(head);
        }

        /// <summary>ヘッダを読む。マジック不一致 / 長さ不足なら false（ファイルは無かったものとして扱う）。</summary>
        public static bool TryReadHeader(Stream src, out int widthPx, out int heightPx)
        {
            widthPx = 0;
            heightPx = 0;
            Span<byte> head = stackalloc byte[HeaderBytes];
            if (!ReadExactly(src, head)) return false;
            for (int i = 0; i < Magic.Length; i++)
                if (head[i] != Magic[i]) return false;
            widthPx = (int)ReadU32(head.Slice(4));
            heightPx = (int)ReadU32(head.Slice(8));
            return true;
        }

        private static readonly byte[] ProgressMagic = { (byte)'P', (byte)'R', (byte)'G', (byte)'1' };

        /// <summary>
        /// 進み具合のファイル（<c>.mjr.prog</c>・`canon/LEDGER.md` 0274）を書く。
        /// <code>"PRG1" 4B / u32 count 4B / f32[count]（.mjr のコマと同じ順。NaN = 位置が取れなかったコマ）</code>
        /// </summary>
        public static void WriteProgress(Stream dest, float[] progress)
        {
            dest.Write(ProgressMagic, 0, ProgressMagic.Length);
            var bw = new BinaryWriter(dest);
            bw.Write((uint)progress.Length);
            foreach (float p in progress) bw.Write(p);
            bw.Flush();
        }

        /// <summary>
        /// 進み具合のファイルを読む。壊れている・コマ数が <paramref name="expectedCount"/> と違うなら null
        /// （呼び出し側は時間どおりに流す）。
        /// </summary>
        public static float[]? ReadProgress(Stream src, int expectedCount)
        {
            try
            {
                var magic = new byte[4];
                if (src.Read(magic, 0, 4) != 4) return null;
                for (int i = 0; i < 4; i++) if (magic[i] != ProgressMagic[i]) return null;
                var br = new BinaryReader(src);
                uint n = br.ReadUInt32();
                if (n != (uint)expectedCount) return null;
                var p = new float[n];
                for (int i = 0; i < n; i++) p[i] = br.ReadSingle();
                return p;
            }
            catch (EndOfStreamException) { return null; }
            catch (IOException) { return null; }
        }

        /// <summary>
        /// 進み具合 <paramref name="target"/> に合うコマを選ぶ: 進み具合が target 以下のコマのうち
        /// 最も進んだもの。1 つも無ければ 0（入口のコマ）。NaN のコマは選ばない。
        /// </summary>
        public static int IndexAtProgress(float[] progress, float target)
        {
            int best = -1;
            for (int i = 0; i < progress.Length; i++)
            {
                float p = progress[i];
                if (float.IsNaN(p) || p > target) continue;
                if (best < 0 || p > progress[best]) best = i;
            }
            return best < 0 ? 0 : best;
        }

        /// <summary>フレーム 1 枚を書く。</summary>
        public static void WriteFrame(Stream dest, byte[] jpeg, int length, int ptsMs)
        {
            if (length <= 0 || length > MaxFrameBytes) return;
            Span<byte> head = stackalloc byte[FrameHeaderBytes];
            WriteU32(head, (uint)length);
            WriteU32(head.Slice(4), (uint)Math.Max(0, ptsMs));
            dest.Write(head);
            dest.Write(jpeg, 0, length);
        }

        /// <summary>1 フレームの所在。<see cref="BuildIndex"/> が返す。</summary>
        public readonly struct FrameRef
        {
            public readonly long offset;   // JPEG 本体の先頭（ヘッダ 8B を除いた位置）
            public readonly int length;
            public readonly int ptsMs;

            public FrameRef(long offset, int length, int ptsMs)
            {
                this.offset = offset;
                this.length = length;
                this.ptsMs = ptsMs;
            }
        }

        /// <summary>
        /// ストリームを走査してフレーム索引を作る（JPEG 本体は読まない＝メモリを食わない）。
        /// 壊れた末尾はそこで打ち切る。pts が単調増加しないフレームは捨てる（時計飛びで再生順が壊れないように）。
        /// </summary>
        public static FrameRef[] BuildIndex(Stream src, int maxFrames = 100_000)
        {
            if (!TryReadHeader(src, out _, out _)) return Array.Empty<FrameRef>();

            var list = new System.Collections.Generic.List<FrameRef>(256);
            Span<byte> head = stackalloc byte[FrameHeaderBytes];
            long pos = HeaderBytes;
            long len = src.Length;
            int lastPts = -1;

            while (list.Count < maxFrames && pos + FrameHeaderBytes <= len)
            {
                src.Position = pos;
                if (!ReadExactly(src, head)) break;
                int frameLen = (int)ReadU32(head);
                int ptsMs = (int)ReadU32(head.Slice(4));
                if (frameLen <= 0 || frameLen > MaxFrameBytes) break;      // 破損
                long body = pos + FrameHeaderBytes;
                if (body + frameLen > len) break;                          // 途中で切れている
                if (ptsMs > lastPts)
                {
                    list.Add(new FrameRef(body, frameLen, ptsMs));
                    lastPts = ptsMs;
                }
                pos = body + frameLen;
            }
            return list.ToArray();
        }

        /// <summary>索引の末尾 pts（秒）。空なら 0。</summary>
        public static float DurationSec(FrameRef[] index)
            => index.Length == 0 ? 0f : index[index.Length - 1].ptsMs / 1000f;

        /// <summary>
        /// 経過時間に対応するフレームの索引位置を返す（pts &lt;= elapsedMs の最後）。
        /// <paramref name="from"/> は前回の位置（前方走査だけで済ませるためのヒント）。
        /// </summary>
        public static int SeekIndex(FrameRef[] index, int elapsedMs, int from)
        {
            if (index.Length == 0) return -1;
            int i = from < 0 ? 0 : Math.Min(from, index.Length - 1);
            // 巻き戻し（ループ・シーク）にも対応するため、必要なら後方へも下がる。
            while (i > 0 && index[i].ptsMs > elapsedMs) i--;
            while (i + 1 < index.Length && index[i + 1].ptsMs <= elapsedMs) i++;
            return index[i].ptsMs <= elapsedMs ? i : -1;
        }

        private static void WriteU32(Span<byte> dest, uint v)
        {
            dest[0] = (byte)(v & 0xFF);
            dest[1] = (byte)((v >> 8) & 0xFF);
            dest[2] = (byte)((v >> 16) & 0xFF);
            dest[3] = (byte)((v >> 24) & 0xFF);
        }

        private static uint ReadU32(ReadOnlySpan<byte> src)
            => src[0] | ((uint)src[1] << 8) | ((uint)src[2] << 16) | ((uint)src[3] << 24);

        private static bool ReadExactly(Stream src, Span<byte> dest)
        {
            int got = 0;
            while (got < dest.Length)
            {
                int n = src.Read(dest.Slice(got));
                if (n <= 0) return false;
                got += n;
            }
            return true;
        }
    }
}
