#nullable enable
using System.IO;
using System.Text;
using NUnit.Framework;
using TableDuoVr.Net;

namespace TableDuoVr.Tests
{
    /// <summary>
    /// <see cref="MjpegAviWriter"/> が吐く MJPG-AVI（RIFF）の構造検証。fourcc 配置・avih/strh の値・
    /// idx1 のエントリ数とオフセット整合・奇数長フレームの word パディング・0 フレーム/二重 Close の安全性。
    /// </summary>
    public class MjpegAviWriterTests
    {
        private static uint U32(byte[] b, int pos)
            => (uint)(b[pos] | (b[pos + 1] << 8) | (b[pos + 2] << 16) | (b[pos + 3] << 24));

        private static string Fourcc(byte[] b, int pos) => Encoding.ASCII.GetString(b, pos, 4);

        private static int IndexOf(byte[] b, string fourcc, int start = 0)
        {
            byte[] pat = Encoding.ASCII.GetBytes(fourcc);
            for (int i = start; i <= b.Length - 4; i++)
                if (b[i] == pat[0] && b[i + 1] == pat[1] && b[i + 2] == pat[2] && b[i + 3] == pat[3])
                    return i;
            return -1;
        }

        private static byte[] Build(int fps, params int[] frameLengths)
        {
            var ms = new MemoryStream();
            var w = new MjpegAviWriter(ms, 1280, 720, fps);
            foreach (int len in frameLengths)
            {
                var f = new byte[len];
                for (int i = 0; i < len; i++) f[i] = (byte)(i & 0xFF);
                w.WriteFrame(f, len);
            }
            w.Close();
            return ms.ToArray();
        }

        [Test]
        public void Header_Fourccs_InPlace()
        {
            var b = Build(30, 10);
            Assert.AreEqual("RIFF", Fourcc(b, 0));
            Assert.AreEqual("AVI ", Fourcc(b, 8));
            Assert.AreEqual("LIST", Fourcc(b, 12));
            Assert.AreEqual("hdrl", Fourcc(b, 20));
            Assert.Greater(IndexOf(b, "movi"), 0);
            Assert.Greater(IndexOf(b, "idx1"), 0);
        }

        [Test]
        public void MicroSecPerFrame_30fps_Is_33333()
        {
            var b = Build(30, 10);
            int avih = IndexOf(b, "avih");
            Assert.AreEqual(33333u, U32(b, avih + 8)); // avih data 先頭 = dwMicroSecPerFrame
        }

        [Test]
        public void ThreeFrames_TotalFrames_And_IndexCount_Match()
        {
            var b = Build(30, 100, 200, 300);
            int avih = IndexOf(b, "avih");
            Assert.AreEqual(3u, U32(b, avih + 8 + 16)); // avih dwTotalFrames
            int strh = IndexOf(b, "strh");
            Assert.AreEqual(3u, U32(b, strh + 8 + 32));  // strh dwLength
            Assert.AreEqual(30u, U32(b, strh + 8 + 24)); // strh dwRate = fps
            int idx1 = IndexOf(b, "idx1");
            Assert.AreEqual(48u, U32(b, idx1 + 4)); // 3 エントリ × 16 バイト
        }

        [Test]
        public void OddLengthFrames_Padded_And_OffsetsConsistent()
        {
            // 3(奇) → 4(偶) → 5(奇)。パディングで次チャンクが常に偶数境界へ来る
            var b = Build(30, 3, 4, 5);
            int idx1 = IndexOf(b, "idx1");
            uint o0 = U32(b, idx1 + 8 + 0 * 16 + 8), s0 = U32(b, idx1 + 8 + 0 * 16 + 12);
            uint o1 = U32(b, idx1 + 8 + 1 * 16 + 8), s1 = U32(b, idx1 + 8 + 1 * 16 + 12);
            uint o2 = U32(b, idx1 + 8 + 2 * 16 + 8), s2 = U32(b, idx1 + 8 + 2 * 16 + 12);

            Assert.AreEqual(4u, o0);  // 先頭チャンクは 'movi' fourcc +4
            Assert.AreEqual(3u, s0);
            Assert.AreEqual(16u, o1); // 4 + (8 header + 3 data + 1 pad)
            Assert.AreEqual(4u, s1);
            Assert.AreEqual(28u, o2); // 16 + (8 header + 4 data + 0 pad)
            Assert.AreEqual(5u, s2);

            Assert.AreEqual(0u, o0 % 2);
            Assert.AreEqual(0u, o1 % 2);
            Assert.AreEqual(0u, o2 % 2);
        }

        [Test]
        public void IndexEntries_ChunkId_Is_00dc()
        {
            var b = Build(30, 8, 8);
            int idx1 = IndexOf(b, "idx1");
            Assert.AreEqual("00dc", Fourcc(b, idx1 + 8 + 0));
            Assert.AreEqual("00dc", Fourcc(b, idx1 + 8 + 16));
        }

        [Test]
        public void ZeroFrames_ProducesValidFile()
        {
            var b = Build(30);
            Assert.AreEqual("RIFF", Fourcc(b, 0));
            Assert.AreEqual("AVI ", Fourcc(b, 8));
            int avih = IndexOf(b, "avih");
            Assert.AreEqual(0u, U32(b, avih + 8 + 16)); // dwTotalFrames = 0
            int idx1 = IndexOf(b, "idx1");
            Assert.Greater(idx1, 0);
            Assert.AreEqual(0u, U32(b, idx1 + 4));       // idx1 サイズ 0
            Assert.AreEqual((uint)(b.Length - 8), U32(b, 4)); // RIFF サイズ整合
        }

        [Test]
        public void RiffSize_Matches_FileLength()
        {
            var b = Build(30, 50, 60);
            Assert.AreEqual((uint)(b.Length - 8), U32(b, 4));
        }

        [Test]
        public void DoubleClose_NoException()
        {
            var ms = new MemoryStream();
            var w = new MjpegAviWriter(ms, 1280, 720, 30);
            var f = new byte[16];
            w.WriteFrame(f, f.Length);
            w.Close();
            Assert.DoesNotThrow(() => w.Close());
        }
    }
}
