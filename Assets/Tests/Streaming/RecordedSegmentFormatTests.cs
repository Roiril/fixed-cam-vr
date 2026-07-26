#nullable enable
using System.IO;
using FixedCamVr.Streaming.Recording;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 端末内録画コンテナ（.mjr）の純ロジック。**末尾は必ず壊れうる前提**（録画中のアプリ終了・
    /// 容量上限での打ち切り）なので、壊れた尾を黙って捨てて読めることを固定する。
    /// </summary>
    public sealed class RecordedSegmentFormatTests
    {
        private static byte[] Jpeg(int n, byte fill)
        {
            var b = new byte[n];
            for (int i = 0; i < n; i++) b[i] = fill;
            return b;
        }

        private static MemoryStream Build(params (int len, byte fill, int ptsMs)[] frames)
        {
            var ms = new MemoryStream();
            RecordedSegmentFormat.WriteHeader(ms, 640, 360);
            foreach ((int len, byte fill, int ptsMs) f in frames)
                RecordedSegmentFormat.WriteFrame(ms, Jpeg(f.len, f.fill), f.len, f.ptsMs);
            ms.Position = 0;
            return ms;
        }

        [Test]
        public void RoundTrip_PreservesFrameOffsetsAndPts()
        {
            using MemoryStream ms = Build((16, 1, 0), (32, 2, 100), (8, 3, 250));
            RecordedSegmentFormat.FrameRef[] idx = RecordedSegmentFormat.BuildIndex(ms);

            Assert.That(idx.Length, Is.EqualTo(3));
            Assert.That(idx[0].ptsMs, Is.EqualTo(0));
            Assert.That(idx[1].ptsMs, Is.EqualTo(100));
            Assert.That(idx[2].length, Is.EqualTo(8));
            // 2 枚目の本体はヘッダ 12 + (8+16) + 8 = 44 の位置から。
            Assert.That(idx[1].offset,
                Is.EqualTo(RecordedSegmentFormat.HeaderBytes
                          + RecordedSegmentFormat.FrameHeaderBytes + 16
                          + RecordedSegmentFormat.FrameHeaderBytes));
        }

        [Test]
        public void TruncatedTail_IsDroppedSilently()
        {
            using MemoryStream full = Build((16, 1, 0), (32, 2, 100));
            byte[] bytes = full.ToArray();
            // 最後のフレームを途中で切る（アプリが落ちた / 容量で打ち切られた状態）。
            using var cut = new MemoryStream(bytes, 0, bytes.Length - 10);
            RecordedSegmentFormat.FrameRef[] idx = RecordedSegmentFormat.BuildIndex(cut);

            Assert.That(idx.Length, Is.EqualTo(1), "壊れた末尾は捨て、読めるところまでを返す");
            Assert.That(idx[0].ptsMs, Is.EqualTo(0));
        }

        [Test]
        public void HeaderOnly_OrGarbage_YieldsEmptyIndex()
        {
            using var headerOnly = new MemoryStream();
            RecordedSegmentFormat.WriteHeader(headerOnly, 0, 0);
            headerOnly.Position = 0;
            Assert.That(RecordedSegmentFormat.BuildIndex(headerOnly).Length, Is.EqualTo(0));

            using var garbage = new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 });
            Assert.That(RecordedSegmentFormat.BuildIndex(garbage).Length, Is.EqualTo(0),
                "マジックが違うファイルは「録画なし」として扱う（例外にしない）");
        }

        [Test]
        public void NonMonotonicPts_IsDropped()
        {
            // 時計飛びで pts が戻ったフレームは捨てる（再生順が壊れないように）。
            using MemoryStream ms = Build((8, 1, 0), (8, 2, 200), (8, 3, 100), (8, 4, 300));
            RecordedSegmentFormat.FrameRef[] idx = RecordedSegmentFormat.BuildIndex(ms);

            Assert.That(idx.Length, Is.EqualTo(3));
            Assert.That(idx[2].ptsMs, Is.EqualTo(300));
        }

        [Test]
        public void SeekIndex_FindsFrameAtOrBeforeElapsed_AndHandlesRewind()
        {
            using MemoryStream ms = Build((8, 1, 0), (8, 2, 100), (8, 3, 200));
            RecordedSegmentFormat.FrameRef[] idx = RecordedSegmentFormat.BuildIndex(ms);

            Assert.That(RecordedSegmentFormat.SeekIndex(idx, 0, -1), Is.EqualTo(0));
            Assert.That(RecordedSegmentFormat.SeekIndex(idx, 150, 0), Is.EqualTo(1));
            Assert.That(RecordedSegmentFormat.SeekIndex(idx, 9999, 1), Is.EqualTo(2));
            Assert.That(RecordedSegmentFormat.SeekIndex(idx, 50, 2), Is.EqualTo(0),
                "巻き戻しでも前方走査のヒントに引きずられない");
        }

        [Test]
        public void DurationSec_IsLastPts()
        {
            using MemoryStream ms = Build((8, 1, 0), (8, 2, 2500));
            RecordedSegmentFormat.FrameRef[] idx = RecordedSegmentFormat.BuildIndex(ms);
            Assert.That(RecordedSegmentFormat.DurationSec(idx), Is.EqualTo(2.5f).Within(1e-4f));
            Assert.That(RecordedSegmentFormat.DurationSec(System.Array.Empty<RecordedSegmentFormat.FrameRef>()),
                Is.EqualTo(0f));
        }

        [Test]
        public void Writer_DropsFramesBeyondFpsCapAndCapacity()
        {
            string path = Path.Combine(Path.GetTempPath(), "fixedcamvr_rec_test.mjr");
            try
            {
                // fpsCap=10（=100ms 間隔）/ 容量 1MB。
                using (var w = new SegmentRecordWriter(path, new SegmentRecordWriter.Limits(1024 * 1024, 10f)))
                {
                    Assert.That(w.TryAppend(Jpeg(64, 1), 64, 0), Is.True);
                    Assert.That(w.TryAppend(Jpeg(64, 2), 64, 30), Is.False, "fpsCap 内の連写は間引く");
                    Assert.That(w.TryAppend(Jpeg(64, 3), 64, 120), Is.True);
                }
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
                RecordedSegmentFormat.FrameRef[] idx = RecordedSegmentFormat.BuildIndex(fs);
                Assert.That(idx.Length, Is.EqualTo(2));
                Assert.That(idx[1].ptsMs, Is.EqualTo(120));
            }
            finally
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            }
        }
    }
}
