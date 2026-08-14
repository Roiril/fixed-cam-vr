#nullable enable
using System.IO;
using FixedCamVr.Streaming.Recording;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 区間録画の書き出し（<see cref="SegmentRecordWriter"/>）。容器の読み書きそのものは
    /// <c>RecordedSegmentFormatTests</c> が持つので、ここは**書き手の約束**だけを固定する:
    ///   - 残るのは**切り替えの前後**（tailSec 秒前 〜 postSec 秒後。区間の頭ではない）
    ///   - 追い録りしても切り替え前が押し出されない（トリムの基準が切り替えの瞬間で凍る）
    ///   - 残った先頭の pts が 0 へ振り直される（再生が空回りしない）
    ///   - 容量上限を超えない（超える分は古い側を落として体験を止めない）
    ///   - <c>WrittenBytes</c> が実ファイルサイズと一致する（ラン全体の容量配分がこれに乗る）
    ///   - fpsCap で間引く
    ///
    /// **なぜ末尾か**は <see cref="SegmentRecordWriter"/> の docstring（3 周目の再生開始位置と
    /// CG 人形の立ち位置が重なる問題）。ここを頭に戻すと、その不具合がそのまま戻る。
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
        public void 末尾に収まる分は全部読み戻せる()
        {
            string path = Path_("a.mjr");
            var w = new SegmentRecordWriter(path, new SegmentRecordWriter.Limits(1 << 20, 0f, 3f));
            byte[] jpeg = Blob(512);
            for (int i = 0; i < 8; i++) Assert.That(w.TryAppend(jpeg, jpeg.Length, i * 100), Is.True);
            w.Dispose();   // 0..700ms = 3 秒に収まるので 1 枚も落ちない

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
        public void 区間が長引いても末尾だけが残る()
        {
            string path = Path_("tail.mjr");
            // 末尾 3 秒。10fps 相当（100ms 刻み）で 30 秒ぶん積む。
            var w = new SegmentRecordWriter(path, new SegmentRecordWriter.Limits(1 << 22, 0f, 3f));
            byte[] jpeg = Blob(256);
            for (int pts = 0; pts <= 30_000; pts += 100) w.TryAppend(jpeg, jpeg.Length, pts);
            w.Dispose();

            RecordedSegmentFormat.FrameRef[] idx = IndexOf(path);
            // 末尾 3 秒 = 27000..30000ms の 31 枚（両端を含む）。
            Assert.That(idx.Length, Is.EqualTo(31), "末尾 3 秒ぶんだけが残る");
            Assert.That(RecordedSegmentFormat.DurationSec(idx), Is.EqualTo(3f).Within(1e-3f));
            Assert.That(w.Capped, Is.False, "時間で落とすのは正常動作であって打ち切りではない");
        }

        [Test]
        public void 残った先頭の_pts_が_0_へ振り直される()
        {
            string path = Path_("rebase.mjr");
            var w = new SegmentRecordWriter(path, new SegmentRecordWriter.Limits(1 << 22, 0f, 1f));
            byte[] jpeg = Blob(128);
            for (int pts = 0; pts <= 10_000; pts += 500) w.TryAppend(jpeg, jpeg.Length, pts);
            w.Dispose();

            RecordedSegmentFormat.FrameRef[] idx = IndexOf(path);
            // 振り直さないと RecordedFramePlayer が先頭 9 秒を空回りする（絵が出ない間ができる）。
            Assert.That(idx[0].ptsMs, Is.EqualTo(0), "先頭は必ず 0");
            Assert.That(idx[idx.Length - 1].ptsMs, Is.EqualTo(1000), "末尾 1 秒ぶんが 0..1000ms になる");
        }

        [Test]
        public void WrittenBytes_が実ファイルサイズと一致する()
        {
            string path = Path_("b.mjr");
            var w = new SegmentRecordWriter(path, new SegmentRecordWriter.Limits(1 << 20, 0f, 3f));
            byte[] jpeg = Blob(300);
            for (int i = 0; i < 5; i++) w.TryAppend(jpeg, jpeg.Length, i * 66);
            w.Dispose();

            long expected = RecordedSegmentFormat.HeaderBytes
                            + 5 * (RecordedSegmentFormat.FrameHeaderBytes + 300);
            Assert.That(w.WrittenBytes, Is.EqualTo(expected));
            Assert.That(new FileInfo(path).Length, Is.EqualTo(w.WrittenBytes));
        }

        [Test]
        public void 容量が足りなければ末尾がさらに縮む()
        {
            const int len = 1000;
            int per = RecordedSegmentFormat.FrameHeaderBytes + len;
            // ちょうど 3 枚ぶんだけ入る上限。
            long max = RecordedSegmentFormat.HeaderBytes + 3L * per;

            string path = Path_("c.mjr");
            var w = new SegmentRecordWriter(path, new SegmentRecordWriter.Limits(max, 0f, 60f));
            byte[] jpeg = Blob(len);
            for (int i = 0; i < 12; i++) w.TryAppend(jpeg, jpeg.Length, i * 40);
            w.Dispose();

            RecordedSegmentFormat.FrameRef[] idx = IndexOf(path);
            Assert.That(idx.Length, Is.EqualTo(3), "上限に収まる枚数だけ残る");
            Assert.That(w.WrittenBytes, Is.LessThanOrEqualTo(max));
            Assert.That(w.Capped, Is.True, "末尾を丸ごと残せなかったことを呼び出し側へ伝える");
            // **残るのは新しい側**（頭から打ち切る旧方式との違い）。
            Assert.That(idx[idx.Length - 1].ptsMs - idx[0].ptsMs, Is.EqualTo(80));
        }

        [Test]
        public void 上限ゼロ以下は既定値へ倒す()
        {
            // 「無制限」にすると端末を食い潰す。0 以下は既定 64MB / 末尾 3 秒。
            var limits = new SegmentRecordWriter.Limits(0, 0f, 0f);
            Assert.That(limits.maxBytes, Is.EqualTo(64L * 1024 * 1024));
            Assert.That(limits.minFrameIntervalSec, Is.EqualTo(0f), "fpsCap 0 = 間引きなし");
            Assert.That(limits.tailSec, Is.EqualTo(SegmentRecordWriter.DefaultTailSec));
        }

        // ---- 追い録り（切り替えの後も録り続ける）----

        [Test]
        public void 切り替えの前後が残る()
        {
            string path = Path_("post.mjr");
            // 切り替え前 3 秒。10fps 相当（100ms 刻み）。
            var w = new SegmentRecordWriter(path, new SegmentRecordWriter.Limits(1 << 22, 0f, 3f));
            byte[] jpeg = Blob(256);
            for (int pts = 0; pts <= 20_000; pts += 100) w.TryAppend(jpeg, jpeg.Length, pts);

            w.BeginPostRoll(20_000, 2f);            // ここでカメラが切り替わった
            Assert.That(w.PostRolling, Is.True);
            for (int pts = 20_100; pts <= 22_000; pts += 100) w.TryAppend(jpeg, jpeg.Length, pts);
            w.Dispose();

            RecordedSegmentFormat.FrameRef[] idx = IndexOf(path);
            // 17000..22000ms = 51 枚（両端を含む）。切り替え前 3 秒 + 切り替え後 2 秒。
            Assert.That(idx.Length, Is.EqualTo(51));
            Assert.That(RecordedSegmentFormat.DurationSec(idx), Is.EqualTo(5f).Within(1e-3f),
                "切り替えの瞬間で切ると、映像の中の自分が角を曲がり切る前に終わる");
        }

        [Test]
        public void 追い録りしても切り替え前は押し出されない()
        {
            string path = Path_("anchor.mjr");
            var w = new SegmentRecordWriter(path, new SegmentRecordWriter.Limits(1 << 22, 0f, 3f));
            byte[] jpeg = Blob(128);
            for (int pts = 0; pts <= 10_000; pts += 100) w.TryAppend(jpeg, jpeg.Length, pts);
            w.BeginPostRoll(10_000, 2f);
            for (int pts = 10_100; pts <= 12_000; pts += 100) w.TryAppend(jpeg, jpeg.Length, pts);
            w.Dispose();

            RecordedSegmentFormat.FrameRef[] idx = IndexOf(path);
            // トリムの基準を「いま」のままにすると、追い録りした 2 秒ぶんだけ切り替え前が
            // 押し出されて 1 秒しか残らない（＝この機能を足したせいで元の絵が減る）。
            Assert.That(RecordedSegmentFormat.DurationSec(idx), Is.EqualTo(5f).Within(1e-3f));
            Assert.That(idx[0].ptsMs, Is.EqualTo(0), "先頭は必ず 0 へ振り直す");
        }

        [Test]
        public void 追い録りの上限を過ぎたフレームは受けない()
        {
            string path = Path_("postcap.mjr");
            var w = new SegmentRecordWriter(path, new SegmentRecordWriter.Limits(1 << 22, 0f, 3f));
            byte[] jpeg = Blob(64);
            w.TryAppend(jpeg, jpeg.Length, 0);
            w.BeginPostRoll(0, 1f);
            Assert.That(w.TryAppend(jpeg, jpeg.Length, 900), Is.True);
            // 呼び出し側（SegmentRecorder）が閉じ忘れても窓は伸びない。
            Assert.That(w.TryAppend(jpeg, jpeg.Length, 1500), Is.False);
            w.Dispose();

            Assert.That(RecordedSegmentFormat.DurationSec(IndexOf(path)), Is.EqualTo(0.9f).Within(1e-3f));
        }

        [Test]
        public void 追い録りの開始は一度だけ受ける()
        {
            string path = Path_("postonce.mjr");
            var w = new SegmentRecordWriter(path, new SegmentRecordWriter.Limits(1 << 22, 0f, 3f));
            byte[] jpeg = Blob(64);
            for (int pts = 0; pts <= 5_000; pts += 100) w.TryAppend(jpeg, jpeg.Length, pts);
            w.BeginPostRoll(5_000, 2f);
            w.BeginPostRoll(6_000, 10f);   // 2 度目は無視（基準が動くと切り替え前が削れる）
            for (int pts = 5_100; pts <= 9_000; pts += 100) w.TryAppend(jpeg, jpeg.Length, pts);
            w.Dispose();

            Assert.That(RecordedSegmentFormat.DurationSec(IndexOf(path)), Is.EqualTo(5f).Within(1e-3f));
        }

        [Test]
        public void fpsCap_で間引く()
        {
            string path = Path_("d.mjr");
            // 10fps = 100ms 間隔。50ms 刻みで積むと 1 枚おきに落ちる。
            var w = new SegmentRecordWriter(path, new SegmentRecordWriter.Limits(1 << 20, 10f, 3f));
            byte[] jpeg = Blob(64);
            var accepted = new System.Collections.Generic.List<int>();
            for (int pts = 0; pts <= 200; pts += 50)
                if (w.TryAppend(jpeg, jpeg.Length, pts)) accepted.Add(pts);
            w.Dispose();

            Assert.That(accepted, Is.EqualTo(new[] { 0, 100, 200 }));
            Assert.That(IndexOf(path).Length, Is.EqualTo(3));
        }

        [Test]
        public void 一枚も積まなければファイルを作らない()
        {
            string path = Path_("empty.mjr");
            var w = new SegmentRecordWriter(path, new SegmentRecordWriter.Limits(1 << 20, 0f, 3f));
            w.Dispose();

            Assert.That(File.Exists(path), Is.False, "ヘッダだけの空ファイルを残さない");
            Assert.That(w.WrittenFrames, Is.EqualTo(0));
        }
    }
}
