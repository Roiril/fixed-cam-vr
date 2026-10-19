#nullable enable
using System.IO;
using FixedCamVr.Streaming.Recording;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 3 周目 A の位置合わせ（`canon/LEDGER.md` 0274）の約束。
    ///   - 進み具合は L 字の角を回る角度だけで決まる（始めの壁 0・二等分線 0.5・終わりの壁 1・距離は見ない）
    ///   - 2 周目 A は前へ進んだコマだけ積む（戻ったぶんは積まない）。追い録りは時間どおり
    ///   - 採らない試行は書き出さない（ファイルが無い ＝ 次に入ったとき録り直せる）
    ///   - 再生側は体験者の進み具合以下で最も進んだコマを選ぶ（録る側と同じ物差し）
    /// 壁は show.json の実値（角 (-0.5, 0.5)・w1 は +x へ・w2 は -z へ 0.955m）。
    /// </summary>
    public sealed class RecPositionFollowTests
    {
        private static ShowRoomWallDef[] Walls() => new[]
        {
            new ShowRoomWallDef { id = "w1", x1 = -0.5f, z1 = 0.5f, x2 = 0.455f, z2 = 0.5f },
            new ShowRoomWallDef { id = "w2", x1 = -0.5f, z1 = 0.5f, x2 = -0.5f, z2 = -0.455f },
        };

        private string _dir = "";

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "fixedcam_follow_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        [Test]
        public void 進み具合は角を回る角度で決まり距離は見ない()
        {
            Assert.That(CornerProgress.TryFromWalls(Walls(), "w1", "w2", out CornerProgress cp), Is.True);
            Assert.That(cp.At(0.5f, 0.5f), Is.EqualTo(0f).Within(1e-4f), "w1 の向き");
            Assert.That(cp.At(-0.5f, -0.5f), Is.EqualTo(1f).Within(1e-4f), "w2 の向き");
            Assert.That(cp.At(0f, 0f), Is.EqualTo(0.5f).Within(1e-4f), "二等分線 ＝ 中央の線");
            Assert.That(cp.At(0.6f, -0.6f), Is.EqualTo(0.5f).Within(1e-4f), "二等分線の上なら遠くても 0.5（奥行きは見ない）");
            Assert.That(float.IsNaN(cp.At(-0.5f, 0.5f)), Is.True, "角の真上は向きが決まらない");
        }

        [Test]
        public void 壁の向きを逆に指せば進み具合も逆になる()
        {
            Assert.That(CornerProgress.TryFromWalls(Walls(), "w2", "w1", out CornerProgress cp), Is.True);
            Assert.That(cp.At(0.5f, 0.5f), Is.EqualTo(1f).Within(1e-4f));
            Assert.That(cp.At(-0.5f, -0.5f), Is.EqualTo(0f).Within(1e-4f));
        }

        [Test]
        public void 角を共有しない壁では物差しを組まない()
        {
            var apart = new[]
            {
                new ShowRoomWallDef { id = "w1", x1 = 0f, z1 = 0f, x2 = 1f, z2 = 0f },
                new ShowRoomWallDef { id = "w2", x1 = 0f, z1 = 1f, x2 = 0f, z2 = 2f },
            };
            Assert.That(CornerProgress.TryFromWalls(apart, "w1", "w2", out _), Is.False);
            Assert.That(CornerProgress.TryFromWalls(Walls(), "w1", "nope", out _), Is.False);
        }

        [Test]
        public void 前へ進んだコマだけ積み_戻ったぶんは積まない_追い録りは時間どおり()
        {
            string path = Path.Combine(_dir, "prog.mjr");
            var w = new SegmentRecordWriter(path, new SegmentRecordWriter.Limits(1 << 22, 0f, 3f));
            w.MarkStart(0);
            w.EnableProgress();
            byte[] jpeg = new byte[64];
            float[] walk = { 0.00f, 0.10f, 0.10f, 0.20f, 0.15f, 0.05f, 0.20f, 0.30f, 0.60f };
            int pts = 0;
            foreach (float p in walk) { w.TryAppend(jpeg, jpeg.Length, pts, p); pts += 100; }
            Assert.That(w.MaxProgress, Is.EqualTo(0.6f).Within(1e-5f));
            w.BeginPostRoll(pts, 5f);
            w.TryAppend(jpeg, jpeg.Length, pts, float.NaN);
            w.TryAppend(jpeg, jpeg.Length, pts + 100, float.NaN);
            w.Dispose();

            RecordedSegmentFormat.FrameRef[] idx;
            using (var fs = File.OpenRead(path)) idx = RecordedSegmentFormat.BuildIndex(fs);
            // 0, 0.1, 0.2, 0.3, 0.6 の 5 枚 ＋ 追い録り 2 枚。
            Assert.That(idx.Length, Is.EqualTo(7), "戻った・留まったコマまで積んでいる");
            float[]? prog;
            using (var ps = File.OpenRead(SegmentRecordWriter.ProgressPath(path)))
                prog = RecordedSegmentFormat.ReadProgress(ps, idx.Length);
            Assert.That(prog, Is.Not.Null, "進み具合のファイルがコマ数と揃っていない");
            Assert.That(prog![0], Is.EqualTo(0f));
            Assert.That(prog[4], Is.EqualTo(0.6f).Within(1e-5f));
            Assert.That(float.IsNaN(prog[5]) && float.IsNaN(prog[6]), Is.True, "追い録りは位置で選ばない");
        }

        [Test]
        public void 捨てた試行はファイルを作らない()
        {
            string path = Path.Combine(_dir, "discard.mjr");
            var w = new SegmentRecordWriter(path, new SegmentRecordWriter.Limits(1 << 22, 0f, 3f));
            w.MarkStart(0);
            w.EnableProgress();
            byte[] jpeg = new byte[64];
            w.TryAppend(jpeg, jpeg.Length, 0, 0.1f);
            w.TryAppend(jpeg, jpeg.Length, 100, 0.2f);
            w.Discard();
            w.Dispose();
            Assert.That(File.Exists(path), Is.False, "採らない試行が書き出された（次に入ったとき録り直せない）");
            Assert.That(File.Exists(SegmentRecordWriter.ProgressPath(path)), Is.False);
            Assert.That(w.Discarded, Is.True);
        }

        [Test]
        public void 進み具合以下で最も進んだコマを選ぶ()
        {
            float[] prog = { -0.1f, 0.1f, 0.2f, 0.3f, 0.6f, float.NaN };
            Assert.That(RecordedSegmentFormat.IndexAtProgress(prog, 0.25f), Is.EqualTo(2));
            Assert.That(RecordedSegmentFormat.IndexAtProgress(prog, 0.3f), Is.EqualTo(3));
            Assert.That(RecordedSegmentFormat.IndexAtProgress(prog, 5f), Is.EqualTo(4), "NaN（追い録り）は選ばない");
            Assert.That(RecordedSegmentFormat.IndexAtProgress(prog, -1f), Is.EqualTo(0), "どれより手前でも入口のコマ");
        }

        [Test]
        public void 位置で録った録画は進み具合からコマの時刻を引ける()
        {
            string path = Path.Combine(_dir, "play.mjr");
            var w = new SegmentRecordWriter(path, new SegmentRecordWriter.Limits(1 << 22, 0f, 3f));
            w.MarkStart(0);
            w.EnableProgress();
            byte[] jpeg = new byte[64];
            w.TryAppend(jpeg, jpeg.Length, 0, 0.0f);
            w.TryAppend(jpeg, jpeg.Length, 400, 0.2f);
            w.TryAppend(jpeg, jpeg.Length, 900, 0.5f);
            w.Dispose();

            using RecordedFramePlayer? player = RecordedFramePlayer.Open(path);
            Assert.That(player, Is.Not.Null);
            Assert.That(player!.HasProgress, Is.True);
            Assert.That(player.ClockAtProgress(0.3f), Is.EqualTo(0.4f).Within(1e-4f));
            Assert.That(player.ClockAtProgress(0.5f), Is.EqualTo(0.9f).Within(1e-4f));
        }

        [Test]
        public void 時間で録った録画は進み具合を持たない()
        {
            string path = Path.Combine(_dir, "time.mjr");
            var w = new SegmentRecordWriter(path, new SegmentRecordWriter.Limits(1 << 22, 0f, 3f));
            w.TryAppend(new byte[64], 64, 0);
            w.Dispose();
            using RecordedFramePlayer? player = RecordedFramePlayer.Open(path);
            Assert.That(player!.HasProgress, Is.False);
            Assert.That(player.ClockAtProgress(0.3f), Is.EqualTo(-1f), "呼び出し側が時間どおりに倒せるよう -1");
        }

        [Test]
        public void 位置で録る設定がshow_jsonから読める()
        {
            var r = JsonUtility.FromJson<ShowRecordDef>(
                "{\"enabled\":true,\"progress\":{\"segments\":[\"L2C0\"],\"fromWall\":\"w1\",\"toWall\":\"w2\",\"adoptAt\":0.5}}");
            Assert.That(r.progress.Applies(2, 0), Is.True);
            Assert.That(r.progress.Applies(1, 0), Is.False);
            Assert.That(r.progress.adoptAt, Is.EqualTo(0.5f));
            var s = JsonUtility.FromJson<ShowStepDef>("{\"source\":\"rec\",\"recFollow\":true}");
            Assert.That(s.recFollow, Is.True);
            var none = JsonUtility.FromJson<ShowRecordDef>("{\"enabled\":true}");
            Assert.That(none.progress == null || !none.progress.Applies(2, 0), Is.True, "キーが無い show.json は従来どおり");
        }
    }
}
