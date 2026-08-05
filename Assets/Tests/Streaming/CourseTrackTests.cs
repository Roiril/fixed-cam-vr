#nullable enable
using FixedCamVr.Streaming.Cg;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 人形の立ち位置が「腕・向きと同じ時刻」を読めることを固定する。
    /// ここが崩れると、歩きながら振り向いたときに体の位置と向きが別々の時刻を指す。
    /// </summary>
    public sealed class CourseTrackTests
    {
        [Test]
        public void 空なら答えない()
        {
            var t = new CourseTrack();
            Assert.IsFalse(t.TrySample(1f, out _), "呼び出し側が「いま」の値へ落ちられるように false");
        }

        [Test]
        public void 過去の時点を補間して返す()
        {
            var t = new CourseTrack();
            t.Push(10.0f, new Vector2(0f, 0f));
            t.Push(10.2f, new Vector2(0.2f, 0f));

            Assert.IsTrue(t.TrySample(10.1f, out Vector2 mid));
            Assert.That(mid.x, Is.EqualTo(0.1f).Within(1e-4f));
        }

        [Test]
        public void 歩いている最中は現在地とずれる()
        {
            // 1 m/s で +X へ歩く。0.15 秒前は 15cm 後ろ。
            var t = new CourseTrack();
            for (int i = 0; i <= 30; i++) t.Push(i / 30f, new Vector2(i / 30f, 0f));

            Assert.IsTrue(t.TrySample(1f - 0.15f, out Vector2 past));
            Assert.That(past.x, Is.EqualTo(0.85f).Within(1e-3f));
        }

        [Test]
        public void 立ち止まっていれば差は出ない()
        {
            var t = new CourseTrack();
            for (int i = 0; i <= 30; i++) t.Push(i / 30f, new Vector2(2f, -1f));

            Assert.IsTrue(t.TrySample(1f - 0.15f, out Vector2 past));
            Assert.That(Vector2.Distance(past, new Vector2(2f, -1f)), Is.LessThan(1e-4f));
        }

        [Test]
        public void 履歴より古い時刻は端で頭打ちする()
        {
            // 外挿しない（暴れる方が害が大きい）。
            var t = new CourseTrack();
            t.Push(5f, new Vector2(1f, 1f));
            t.Push(6f, new Vector2(2f, 2f));

            Assert.IsTrue(t.TrySample(-100f, out Vector2 old));
            Assert.That(Vector2.Distance(old, new Vector2(1f, 1f)), Is.LessThan(1e-4f));

            Assert.IsTrue(t.TrySample(100f, out Vector2 fut));
            Assert.That(Vector2.Distance(fut, new Vector2(2f, 2f)), Is.LessThan(1e-4f));
        }

        [Test]
        public void 容量を超えても古い方から捨てて壊れない()
        {
            var t = new CourseTrack();
            for (int i = 0; i < CourseTrack.Capacity * 3; i++) t.Push(i * 0.01f, new Vector2(i, 0f));

            Assert.That(t.Count, Is.EqualTo(CourseTrack.Capacity));
            int last = CourseTrack.Capacity * 3 - 1;
            Assert.IsTrue(t.TrySample(last * 0.01f, out Vector2 newest));
            Assert.That(newest.x, Is.EqualTo(last).Within(1e-3f));
        }

        [Test]
        public void 捨てれば答えなくなる()
        {
            var t = new CourseTrack();
            t.Push(1f, Vector2.one);
            t.Clear();
            Assert.IsFalse(t.TrySample(1f, out _));
        }
    }
}
