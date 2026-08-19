#nullable enable
using FixedCamVr.Tracking;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Tracking.Tests
{
    /// <summary>
    /// 区間の進みを床の上の距離で測る（<c>canon/LEDGER.md</c> 0093）。
    ///
    /// ⚠ 押さえたい壊れ方:
    /// ① 短い方の軸を進む向きに選ぶ（1.35m の廊下を 0.45m で測る ＝ 入った直後に半ばを過ぎる）
    /// ② 入った矩形だけで測る（貪欲分解が 1 つのカメラを 2 枚に割ると、区間の半ばが手前へずれる）
    /// ③ 入った所ではなく矩形の端を 0 にする（重なりぶん、確定するのは端より内側）
    /// </summary>
    public sealed class ZoneSpanMathTests
    {
        // 現行 layout の区間 C 相当: 1.35m（X）× 0.45m（Z）の廊下、中心 x=+0.225。
        private static SpanBox Corridor(float centerX = 0.225f, int camera = 2)
            => new SpanBox(new Vector3(centerX, 1f, 0.6f), Quaternion.identity,
                           new Vector3(0.675f, 2f, 0.225f), camera);

        [Test]
        public void TravelAxis_TakesTheLongHorizontalSide()
        {
            Vector3 a = ZoneSpanMath.TravelAxis(Quaternion.identity, new Vector3(0.675f, 2f, 0.225f));
            Assert.That(Mathf.Abs(a.x), Is.EqualTo(1f).Within(1e-4f), "長い方（X）が進む向き");
            Assert.That(a.y, Is.EqualTo(0f), "高さは進みではない");
        }

        [Test]
        public void EnteringFromTheWest_MeasuresEastward()
        {
            var boxes = new[] { Corridor() };
            var entry = new Vector3(-0.45f, 1.6f, 0.6f);   // 西端で確定した
            Assert.That(ZoneSpanMath.Solve(boxes, 0, entry, out Vector3 axis,
                                           out float u0, out float u1), Is.True);
            Assert.That(axis.x, Is.EqualTo(1f).Within(1e-4f), "奥（東）へ向かう向きに揃える");

            Assert.That(ZoneSpanMath.Progress01(entry, axis, u0, u1),
                        Is.EqualTo(0f).Within(1e-4f), "入った所が 0");
            Assert.That(ZoneSpanMath.Progress01(new Vector3(0.9f, 1.6f, 0.6f), axis, u0, u1),
                        Is.EqualTo(1f).Within(1e-4f), "奥の端が 1");
            // 入った所（-0.45）と奥の端（+0.9）の中間 ＝ +0.225。
            Assert.That(ZoneSpanMath.Progress01(new Vector3(0.225f, 1.6f, 0.6f), axis, u0, u1),
                        Is.EqualTo(0.5f).Within(1e-3f), "半ばは「入った所と奥の端の中間」");
        }

        [Test]
        public void EnteringFromTheEast_MeasuresWestward()
        {
            var boxes = new[] { Corridor() };
            var entry = new Vector3(0.9f, 1.6f, 0.6f);
            Assert.That(ZoneSpanMath.Solve(boxes, 0, entry, out Vector3 axis,
                                           out float u0, out float u1), Is.True);
            Assert.That(axis.x, Is.EqualTo(-1f).Within(1e-4f), "逆から入ったら逆向きに測る");
            Assert.That(ZoneSpanMath.Progress01(new Vector3(-0.45f, 1.6f, 0.6f), axis, u0, u1),
                        Is.EqualTo(1f).Within(1e-4f));
        }

        /// <summary>
        /// ⚠⚠ <b>入った矩形だけで測らない。</b> <c>SolveGrid</c> の貪欲分解は 1 つのカメラを
        /// 複数の矩形に割ることがある。入った矩形（左半分）だけで測ると、
        /// <b>区間の半ばが本当の 1/4 の所に来る</b>。
        /// </summary>
        [Test]
        public void SplitRects_MeasureTheWholeCamera()
        {
            var boxes = new[]
            {
                new SpanBox(new Vector3(-0.1125f, 1f, 0.6f), Quaternion.identity,
                            new Vector3(0.3375f, 2f, 0.225f), 2),   // 西半分
                new SpanBox(new Vector3(0.5625f, 1f, 0.6f), Quaternion.identity,
                            new Vector3(0.3375f, 2f, 0.225f), 2),   // 東半分
                new SpanBox(new Vector3(-1.0f, 1f, 0.6f), Quaternion.identity,
                            new Vector3(0.3f, 2f, 0.3f), 1),        // 別のカメラ（混ぜない）
            };
            var entry = new Vector3(-0.45f, 1.6f, 0.6f);
            Assert.That(ZoneSpanMath.Solve(boxes, 0, entry, out Vector3 axis,
                                           out float u0, out float u1), Is.True);
            Assert.That(ZoneSpanMath.Progress01(new Vector3(0.225f, 1.6f, 0.6f), axis, u0, u1),
                        Is.EqualTo(0.5f).Within(1e-3f), "2 枚に割れていても半ばは同じ所");
            Assert.That(ZoneSpanMath.Progress01(new Vector3(0.9f, 1.6f, 0.6f), axis, u0, u1),
                        Is.EqualTo(1f).Within(1e-3f), "奥の端は東側の矩形の端");
        }

        [Test]
        public void EnteringAtTheFarEnd_IsNotMeasurable()
        {
            // 入った所が既に奥の端（残り 0.05m）。ここを 0..1 に伸ばすと、数 cm の揺れが
            // 「半ばを過ぎた」になる。測らない ＝ カットの終わりに任せる。
            var boxes = new[] { new SpanBox(Vector3.zero, Quaternion.identity,
                                            new Vector3(0.05f, 2f, 0.02f), 2) };
            Assert.That(ZoneSpanMath.Solve(boxes, 0, new Vector3(0.04f, 1.6f, 0f),
                                           out _, out _, out _), Is.False);
        }

        [Test]
        public void RotatedCourse_MeasuresAlongTheRotatedAxis()
        {
            // 位置合わせで course が 90° 回っている現場でも、進む向きは矩形の長い方。
            Quaternion rot = Quaternion.Euler(0f, 90f, 0f);
            var boxes = new[] { new SpanBox(new Vector3(0f, 1f, 0f), rot,
                                            new Vector3(0.675f, 2f, 0.225f), 2) };
            var entry = new Vector3(0f, 1.6f, 0.675f);   // 回転後の長い軸は Z
            Assert.That(ZoneSpanMath.Solve(boxes, 0, entry, out Vector3 axis,
                                           out float u0, out float u1), Is.True);
            Assert.That(Mathf.Abs(axis.z), Is.EqualTo(1f).Within(1e-3f));
            Assert.That(ZoneSpanMath.Progress01(new Vector3(0f, 1.6f, -0.675f), axis, u0, u1),
                        Is.EqualTo(1f).Within(1e-3f));
        }
    }
}
