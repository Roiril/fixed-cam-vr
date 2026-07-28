#nullable enable
using System.IO;
using FixedCamVr.Streaming.Recording;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 区間録画の書き出し（<see cref="SegmentRecordWriter"/>）。容器の読み書きそのものは
    /// <c>RecordedSegmentFormatTests</c> が持つので、ここは**書き手の約束**だけを固定する:
    ///   - 積んだフレームが .mjr として読み戻せる
    ///   - 容量上限を超えない（超えた分は捨てて体験を止めない）
    ///   - <c>WrittenBytes</c> が実ファイルサイズと一致する（ラン全体の容量配分がこれに乗る）
    ///   - fpsCap で間引く
    /// </summary>
    public sealed class SegmentRecordWriterTests
    {
        private string _dir = "";

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "fixedcam_rec_test_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch { /* 後始末の失敗でテストを落とさない */ }
        }

        private string Path_(string name) => Path.Combine(_dir, name);

        // 長さだけ意味がある擬似 JPEG（容器はバイト列の中身を見ない）。
        private static byte[] Blob(int len)
        {
            var b = new byte[len];
            for (int i = 0; i < len; i++) b[i] = (byte)(i & 0xFF);
            return b;
        }

        private static RecordedSegmentFormat.FrameRef[] IndexOf(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            return RecordedSegmentFormat.BuildIndex(fs);
        }

        [Test]
        public void 積んだフレームが読み戻せる()
        {
            string path = Path_("a.mjr");
            var w = new SegmentRecordWriter(path, new SegmentRecordWriter.Limits(1 << 20, 0f));
            byte[] jpeg = Blob(512);
            for (int i = 0; i < 8; i++) Assert.That(w.TryAppend(jpeg, jpeg.Length, i * 100), Is.True);
            w.Dispose();

            RecordedSegmentFormat.FrameRef[] idx = IndexOf(path);
            Assert.That(idx.Length, Is.EqualTo(8));
            for (int i = 0; i < idx.Length; i++)
            {
                Assert.That(idx[i].ptsMs, Is.EqualTo(i * 100));
                Assert.That(idx[i].length, Is.EqualTo(512));
            }
            Assert.That(RecordedSegmentFormat.DurationSec(idx), Is.EqualTo(0.7f).Within(1e-4f));
        }

        [Test]
        public void WrittenBytes_が実ファイルサイズと一致する()
        {
            string path = Path_("b.mjr");
            var w = new SegmentRecordWriter(path, new SegmentRecordWriter.Limits(1 << 20, 0f));
            byte[] jpeg = Blob(300);
            for (int i = 0; i < 5; i++) w.TryAppend(jpeg, jpeg.Length, i * 66);
            w.Dispose();

            long expected = RecordedSegmentFormat.HeaderBytes
                            + 5 * (RecordedSegmentFormat.FrameHeaderBytes + 300);
            Assert.That(w.WrittenBytes, Is.EqualTo(expected));
            Assert.That(new FileInfo(path).Length, Is.EqualTo(w.WrittenBytes));
        }

        [Test]
        public void 容量上限を超えたら打ち切る()
        {
            const int len = 1000;
            int per = RecordedSegmentFormat.FrameHeaderBytes + len;
            // ちょうど 3 枚ぶんだけ入る上限（4 枚目は必ず溢れる）。
            long max = RecordedSegmentFormat.HeaderBytes + 3L * per;

            string path = Path_("c.mjr");
            var w = new SegmentRecordWriter(path, new SegmentRecordWriter.Limits(max, 0f));
            byte[] jpeg = Blob(len);
            for (int i = 0; i < 12; i++) w.TryAppend(jpeg, jpeg.Length, i * 40);
            w.Dispose();

            Assert.That(IndexOf(path).Length, Is.EqualTo(3), "上限を超える分は書かない");
            Assert.That(w.WrittenBytes, Is.LessThanOrEqualTo(max));
            Assert.That(w.Capped, Is.True, "打ち切ったことを呼び出し側へ伝える");
        }

        [Test]
        public void 上限ゼロ以下は既定値へ倒す()
        {
            // 「無制限」にすると端末を食い潰す。0 以下は既定 64MB。
            var limits = new SegmentRecordWriter.Limits(0, 0f);
            Assert.That(limits.maxBytes, Is.EqualTo(64L * 1024 * 1024));
            Assert.That(limits.minFrameIntervalSec, Is.EqualTo(0f), "fpsCap 0 = 間引きなし");
        }

        [Test]
        public void fpsCap_で間引く()
        {
            string path = Path_("d.mjr");
            // 10fps = 100ms 間隔。50ms 刻みで積むと 1 枚おきに落ちる。
            var w = new SegmentRecordWriter(path, new SegmentRecordWriter.Limits(1 << 20, 10f));
            byte[] jpeg = Blob(64);
            var accepted = new System.Collections.Generic.List<int>();
            for (int pts = 0; pts <= 200; pts += 50)
                if (w.TryAppend(jpeg, jpeg.Length, pts)) accepted.Add(pts);
            w.Dispose();

            Assert.That(accepted, Is.EqualTo(new[] { 0, 100, 200 }));
            Assert.That(IndexOf(path).Length, Is.EqualTo(3));
        }
    }
}
