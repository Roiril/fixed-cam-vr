#nullable enable
using FixedCamVr.Streaming.Cg;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 歩いた跡の上に立つ人形（<c>cgMode:"trail"</c>）が、跡を道のりで引けることを固定する。
    /// ここが崩れると、人形が折り返しで前へ出る・飛び石の番号が戻って向きが決め直される・
    /// 追跡の跳びで部屋の外へ立つ、のどれかが起きる。
    /// </summary>
    public sealed class FootprintTrailTests
    {
        private const float Gap = 1.5f;
        private const float Step = 0.45f;

        // +X へ 0.1m 刻みで歩く（from を含まない・to を含む）。
        private static void WalkX(FootprintTrail t, float z, int fromTenths, int toTenths)
        {
            int dir = toTenths >= fromTenths ? 1 : -1;
            for (int i = fromTenths + dir; dir > 0 ? i <= toTenths : i >= toTenths; i += dir)
                t.Add(new Vector2(i * 0.1f, z));
        }

        [Test]
        public void 五センチ未満の動きは足さない()
        {
            var t = new FootprintTrail();
            t.Add(Vector2.zero);
            t.Add(new Vector2(0.04f, 0f));
            Assert.That(t.Count, Is.EqualTo(1), "足踏み・頭の揺れは跡にしない");
            Assert.That(t.Length, Is.EqualTo(0f));

            // 比べる相手は「直前に足した点」。小さな動きが重なって 5cm を超えたら足す。
            t.Add(new Vector2(0.06f, 0f));
            Assert.That(t.Count, Is.EqualTo(2));
            Assert.That(t.Length, Is.EqualTo(0.06f).Within(1e-5f));
        }

        [Test]
        public void 道のりが足りなければ立たない()
        {
            var t = new FootprintTrail();
            Assert.IsFalse(t.TryPointBehind(Gap, Step, out _, out _), "空なら false");
            t.Add(Vector2.zero);
            WalkX(t, 0f, 0, 10);   // 1.0m
            Assert.IsFalse(t.TryPointBehind(Gap, Step, out _, out int station), "1.5m 後ろはまだ歩いていない");
            Assert.That(station, Is.EqualTo(-1));

            WalkX(t, 0f, 10, 20);  // 2.0m
            Assert.IsTrue(t.TryPointBehind(Gap, Step, out Vector2 xz, out station));
            // 目標 0.5m → station 1 → 道のり 0.45m
            Assert.That(station, Is.EqualTo(1));
            Assert.That(xz.x, Is.EqualTo(0.45f).Within(1e-3f));
        }

        [Test]
        public void 飛び石は刻みごとに一つずつ進み点は折れ線の上()
        {
            var t = new FootprintTrail();
            t.Add(Vector2.zero);
            int last = -1;
            for (int i = 1; i <= 50; i++)
            {
                t.Add(new Vector2(i * 0.1f, 0f));
                if (!t.TryPointBehind(Gap, Step, out Vector2 xz, out int station)) continue;
                if (last >= 0)
                    Assert.That(station - last, Is.InRange(0, 1), $"0.1m 歩いて飛び石が {station - last} 個進んだ（i={i}）");
                last = station;
                Assert.That(xz.x, Is.EqualTo(station * Step).Within(1e-3f), "点は道のり station*step の位置");
                Assert.That(xz.y, Is.EqualTo(0f).Within(1e-5f), "点は折れ線の上");
            }
            // 5.0m 歩いた → 目標 3.5m → station 7
            Assert.That(last, Is.EqualTo(7));
        }

        [Test]
        public void 折れ曲がった道でも角を曲がった先を道のりで引く()
        {
            // +X に 1m、そこから +Z に 1m。道のり 2m。
            var t = new FootprintTrail();
            t.Add(Vector2.zero);
            WalkX(t, 0f, 0, 10);
            for (int i = 1; i <= 10; i++) t.Add(new Vector2(1f, i * 0.1f));

            Assert.IsTrue(t.TryPointBehind(0.5f, 0.5f, out Vector2 xz, out int station));
            Assert.That(station, Is.EqualTo(3));
            Assert.That(xz.x, Is.EqualTo(1f).Within(1e-3f));
            Assert.That(xz.y, Is.EqualTo(0.5f).Within(1e-3f), "道のり 1.5m は角から 0.5m 先");
        }

        [Test]
        public void 折り返した道でも通ってきた順に引く()
        {
            // +X へ 2m 歩いてから 1m 引き返す。いまの位置は x=1.0（道のり 3m）。
            var t = new FootprintTrail();
            t.Add(Vector2.zero);
            WalkX(t, 0f, 0, 20);
            WalkX(t, 0f, 20, 10);
            Assert.That(t.Length, Is.EqualTo(3f).Within(1e-3f));

            // 道のり 0.5m 後ろ ＝ 戻り道の x=1.5（直線で後ろを取ると x=0.5 になり、人形が前へ出る）
            Assert.IsTrue(t.TryPointBehind(0.5f, 0.5f, out Vector2 back, out int st1));
            Assert.That(st1, Is.EqualTo(5));
            Assert.That(back.x, Is.EqualTo(1.5f).Within(1e-3f));

            // 道のり 1.5m 後ろ ＝ 行きの x=1.35（station 3 × 0.45）
            Assert.IsTrue(t.TryPointBehind(Gap, Step, out Vector2 outward, out int st2));
            Assert.That(st2, Is.EqualTo(3));
            Assert.That(outward.x, Is.EqualTo(1.35f).Within(1e-3f));
        }

        [Test]
        public void 一メートルを超える跳びで数え直す()
        {
            var t = new FootprintTrail();
            t.Add(Vector2.zero);
            WalkX(t, 0f, 0, 30);
            Assert.IsTrue(t.TryPointBehind(Gap, Step, out _, out _));
            int epoch = t.Epoch;

            t.Add(new Vector2(3f + 1.2f, 0f));   // 追跡の跳び
            Assert.That(t.Count, Is.EqualTo(1), "跡を捨ててこの点から数え直す");
            Assert.That(t.Length, Is.EqualTo(0f));
            Assert.That(t.Epoch, Is.Not.EqualTo(epoch), "同じ station 番号でも別の場所だと分かるように");
            Assert.IsFalse(t.TryPointBehind(Gap, Step, out _, out _), "数え直した直後は後ろに跡が無い");

            // ちょうど 1.0m は跳びではない
            var u = new FootprintTrail();
            u.Add(Vector2.zero);
            u.Add(new Vector2(1f, 0f));
            Assert.That(u.Count, Is.EqualTo(2));
            Assert.That(u.Length, Is.EqualTo(1f).Within(1e-5f));
        }

        [Test]
        public void 五百十二点を超えても飛び石の番号は巻き戻らない()
        {
            var t = new FootprintTrail();
            t.Add(Vector2.zero);
            int last = -1;
            for (int i = 1; i <= 1000; i++)
            {
                t.Add(new Vector2(i * 0.1f, 0f));
                if (!t.TryPointBehind(Gap, Step, out Vector2 xz, out int station)) continue;
                Assert.That(station, Is.GreaterThanOrEqualTo(last), $"飛び石が巻き戻った（i={i}）");
                last = station;
                Assert.That(xz.x, Is.EqualTo(station * Step).Within(0.01f));
            }
            Assert.That(t.Count, Is.EqualTo(FootprintTrail.Capacity));
            Assert.That(t.Length, Is.EqualTo(100f).Within(0.05f), "捨てた分も道のりに含める");
            Assert.That(last, Is.EqualTo(Mathf.FloorToInt((t.Length - Gap) / Step + 1e-4f)));

            // 捨てた区間は引けない（いまは 100m 地点・残っているのは 48.9m 地点より後ろ）
            Assert.IsFalse(t.TryPointBehind(90f, Step, out _, out _));
        }

        [Test]
        public void Resetで跡を捨てる()
        {
            var t = new FootprintTrail();
            t.Add(Vector2.zero);
            WalkX(t, 0f, 0, 30);
            int epoch = t.Epoch;
            t.Reset();
            Assert.That(t.Count, Is.EqualTo(0));
            Assert.That(t.Length, Is.EqualTo(0f));
            Assert.That(t.Epoch, Is.Not.EqualTo(epoch));
            Assert.IsFalse(t.TryPointBehind(Gap, Step, out _, out _));
        }

        [Test]
        public void 非数の位置は足さない()
        {
            var t = new FootprintTrail();
            t.Add(Vector2.zero);
            t.Add(new Vector2(float.NaN, 0f));
            t.Add(new Vector2(0f, float.PositiveInfinity));
            Assert.That(t.Count, Is.EqualTo(1));
            Assert.That(t.Length, Is.EqualTo(0f));
        }
    }
}
