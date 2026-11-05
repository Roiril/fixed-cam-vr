using System;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    public sealed class RoomWarpTests
    {
        // tools/space-warp-study/export_table.py が試作の式から出した値。式を変えたら書き出し直す。
        [TestCase("A", -0.6, 0.5, 1.7, 0.03277, 0.03913)]
        [TestCase("A", 0.7, -0.7, 3.3, 0.06581, -0.06704)]
        [TestCase("A", 0.84, 0.84, 5.9, -0.10666, -0.02459)]
        [TestCase("B", -0.6, 0.5, 1.7, -0.05899, -0.00604)]
        [TestCase("B", 0.7, -0.7, 3.3, -0.00301, -0.0714)]
        [TestCase("C", -0.6, 0.5, 1.7, -0.05533, 0.05144)]
        [TestCase("C", 0.7, -0.7, 3.3, 0.09668, -0.107)]
        public void 試作と同じ値になる(string camera, double x, double y, double t, double expectX, double expectY)
        {
            RoomWarpField.Evaluate(RoomWarpField.SpecFor(camera)!, x, y, t, out double dx, out double dy);

            Assert.That(dx, Is.EqualTo(expectX).Within(.0015));
            Assert.That(dy, Is.EqualTo(expectY).Within(.0015));
        }

        [Test]
        public void 知らないカメラには掛けない()
        {
            Assert.That(RoomWarpField.SpecFor("D"), Is.Null);
            Assert.That(RoomWarpField.SpecFor(null), Is.Null);
        }

        [TestCase("A")]
        [TestCase("B")]
        [TestCase("C")]
        public void 写真の外を引かない(string camera)
        {
            var rg = new float[RoomWarpField.GridWidth * RoomWarpField.GridHeight * 2];
            for (double t = 0; t < 30; t += .37)
            {
                RoomWarpField.FillGrid(RoomWarpField.SpecFor(camera)!, t, rg);
                for (int k = 0; k < rg.Length; k += 2)
                {
                    double size = Math.Sqrt(rg[k] * rg[k] + rg[k + 1] * rg[k + 1]) * 2.0;
                    Assert.That(size, Is.LessThanOrEqualTo(RoomWarpField.Limit + 1e-5));
                }
            }
        }

        [TestCase("A")]
        [TestCase("B")]
        [TestCase("C")]
        public void 画が裏返らない(string camera)
        {
            int w = RoomWarpField.GridWidth, h = RoomWarpField.GridHeight;
            var rg = new float[w * h * 2];
            double cell = 1.0 / (w * h);
            for (double t = 0; t < 30; t += .37)
            {
                RoomWarpField.FillGrid(RoomWarpField.SpecFor(camera)!, t, rg);
                for (int j = 0; j < h - 1; j++)
                {
                    for (int i = 0; i < w - 1; i++)
                    {
                        int a = (j * w + i) * 2, b = a + 2, c = a + w * 2;
                        double ax = RoomWarpField.Overscan * 2.0 / w + (rg[b] - rg[a]) * 2.0, ay = (rg[b + 1] - rg[a + 1]) * 2.0;
                        double bx = (rg[c] - rg[a]) * 2.0, by = RoomWarpField.Overscan * 2.0 / h + (rg[c + 1] - rg[a + 1]) * 2.0;
                        double unit = RoomWarpField.Overscan * RoomWarpField.Overscan * 4.0 * cell;
                        Assert.That((ax * by - ay * bx) / unit, Is.GreaterThan(.2), $"t={t} i={i} j={j}");
                    }
                }
            }
        }

        [TestCase("A")]
        [TestCase("B")]
        [TestCase("C")]
        public void 中央は止まり外が動く(string camera)
        {
            RoomWarpSpec s = RoomWarpField.SpecFor(camera)!;
            double center = 0, rim = 0;
            int n = 0;
            for (double t = 0; t < 30; t += .5, n++)
            {
                RoomWarpField.Evaluate(s, 0, 0, t, out double cx, out double cy);
                center += Math.Sqrt(cx * cx + cy * cy);
                foreach (var (x, y) in new[] { (-.8, 0.0), (.8, 0.0), (0.0, -.8), (0.0, .8) })
                {
                    RoomWarpField.Evaluate(s, x, y, t, out double rx, out double ry);
                    rim += Math.Sqrt(rx * rx + ry * ry) / 4;
                }
            }

            Assert.That(rim / n, Is.GreaterThan(center / n * 4));
        }

        [Test]
        public void 始まる前と終わった後は0()
        {
            var logic = new RoomWarpLogic();

            Assert.That(logic.Tick(1f, true), Is.Zero);
            logic.Begin();
            Assert.That(logic.Tick(RoomWarpLogic.RampSec, true), Is.EqualTo(1f));
            logic.NotifyCovered();
            Assert.That(logic.Tick(1f, true), Is.Zero);
        }

        [Test]
        public void 砂嵐の間に立ち上がる()
        {
            var logic = new RoomWarpLogic();

            logic.Begin();
            float half = logic.Tick(RoomWarpLogic.RampSec / 2f, true);

            Assert.That(half, Is.EqualTo(.5f).Within(.001f));
        }

        [Test]
        public void 許可の無いフレームは0で時刻も進まない()
        {
            var logic = new RoomWarpLogic();
            logic.Begin();
            logic.Tick(1f, true);

            Assert.That(logic.Tick(5f, false), Is.Zero);
            Assert.That(logic.Elapsed, Is.EqualTo(1f));
        }

        [Test]
        public void 暗転の後は同じランで戻らない()
        {
            var logic = new RoomWarpLogic();
            logic.Begin();
            logic.NotifyCovered();

            logic.Begin();

            Assert.That(logic.Tick(1f, true), Is.Zero);
        }

        [Test]
        public void 始まる前の暗転では終わらない()
        {
            var logic = new RoomWarpLogic();

            logic.NotifyCovered();
            logic.Begin();

            Assert.That(logic.Tick(RoomWarpLogic.RampSec, true), Is.EqualTo(1f));
        }

        [Test]
        public void 次のランでまた始められる()
        {
            var logic = new RoomWarpLogic();
            logic.Begin();
            logic.NotifyCovered();

            logic.Reset();
            logic.Begin();

            Assert.That(logic.Tick(RoomWarpLogic.RampSec, true), Is.EqualTo(1f));
        }
    }
}
