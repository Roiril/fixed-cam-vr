#nullable enable
using System.IO;
using FixedCamVr.Streaming.Recording;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 3 周目 A の早送り版（<c>canon/LEDGER.md</c> 0272）が乗るデータの約束。
    ///   - 録画の速さ <c>recRate</c> の丸め（卓の timeline-model.js の recRateOf と対）
    ///   - <c>recRate</c> / <c>recContinue</c> / <c>record.enterStartSegments</c> が show.json から読める
    ///     （JsonUtility は知らないキーを黙って捨てるので、名前の食い違いは実機で何も起きない形で出る）
    ///   - 入った瞬間から録る区間は、区間の頭から残る（末尾 3 秒にならない）
    /// </summary>
    public sealed class RecFastForwardTests
    {
        [Test]
        public void 録画の速さは0以下で等速_範囲外は丸める()
        {
            Assert.That(TakeSchema.ResolveRecRate(0f), Is.EqualTo(1f));
            Assert.That(TakeSchema.ResolveRecRate(-3f), Is.EqualTo(1f));
            Assert.That(TakeSchema.ResolveRecRate(float.NaN), Is.EqualTo(1f));
            Assert.That(TakeSchema.ResolveRecRate(3f), Is.EqualTo(3f));
            Assert.That(TakeSchema.ResolveRecRate(0.1f), Is.EqualTo(TakeSchema.MinRecRate));
            Assert.That(TakeSchema.ResolveRecRate(40f), Is.EqualTo(TakeSchema.MaxRecRate));
        }

        [Test]
        public void カットの速さと続きから流す指定がshow_jsonから読める()
        {
            var s = JsonUtility.FromJson<ShowStepDef>(
                "{\"source\":\"rec\",\"camera\":0,\"recLap\":2,\"recRate\":3,\"recContinue\":true}");
            Assert.That(s.recRate, Is.EqualTo(3f));
            Assert.That(s.recContinue, Is.True);

            var plain = JsonUtility.FromJson<ShowStepDef>("{\"source\":\"rec\",\"camera\":0,\"recLap\":2}");
            Assert.That(plain.recRate, Is.EqualTo(0f), "書いていなければ等速（0）");
            Assert.That(plain.recContinue, Is.False, "書いていなければ頭から開く");
        }

        [Test]
        public void 入った瞬間から録る区間は周とカメラの組で指す()
        {
            var r = JsonUtility.FromJson<ShowRecordDef>(
                "{\"enabled\":true,\"laps\":[1,2],\"enterStartSegments\":[\"L2C0\"]}");
            Assert.That(r.StartsAtEnter(2, 0), Is.True);
            Assert.That(r.StartsAtEnter(1, 0), Is.False, "1 周目 A は導入のぶん長いので指していない");
            Assert.That(r.StartsAtEnter(2, 1), Is.False);

            var none = JsonUtility.FromJson<ShowRecordDef>("{\"enabled\":true,\"laps\":[1,2]}");
            Assert.That(none.StartsAtEnter(2, 0), Is.False, "キーが無い古い show.json は従来どおり");
        }

        [Test]
        public void 入った瞬間に起点を打つと区間の頭から残る()
        {
            string dir = Path.Combine(Path.GetTempPath(), "fixedcam_ff_test_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string path = Path.Combine(dir, "enter.mjr");
                // 末尾 3 秒の設定でも、起点を 0 に打てば 10 秒ぶん全部残る。
                var w = new SegmentRecordWriter(path, new SegmentRecordWriter.Limits(1 << 22, 0f, 3f));
                w.MarkStart(0);
                byte[] jpeg = new byte[256];
                for (int pts = 0; pts <= 10_000; pts += 100) w.TryAppend(jpeg, jpeg.Length, pts);
                w.Dispose();

                RecordedSegmentFormat.FrameRef[] idx;
                using (var fs = File.OpenRead(path)) idx = RecordedSegmentFormat.BuildIndex(fs);
                Assert.That(idx.Length, Is.EqualTo(101), "区間の頭から残っていない");
                Assert.That(RecordedSegmentFormat.DurationSec(idx), Is.EqualTo(10f).Within(1e-3f));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
