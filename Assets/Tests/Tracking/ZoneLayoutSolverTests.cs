#nullable enable
using System.Collections.Generic;
using FixedCamVr.Tracking;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Tracking.Tests
{
    /// <summary>
    /// ZoneLayoutSolver（cuts → course space 矩形展開）の純関数検証。
    /// カメラ割当・authored 相当の寸法・コーナー/辺途中のオーバーラップ・隙間なしを固定する。
    /// </summary>
    public sealed class ZoneLayoutSolverTests
    {
        private static readonly ZoneLayoutSolver.CutInput[] CanonicalCuts =
        {
            new() { s = 0.125f, camAfter = 1 }, // 東（SE→NE）
            new() { s = 0.375f, camAfter = 2 }, // 北+西（NE→SW）
            new() { s = 0.875f, camAfter = 0 }, // 南（SW→SE）
        };

        private static ZoneLayoutSolver.ZoneLayoutInput Canonical(float overlap = 0.08f) => new()
        {
            floorW = 1.8f,
            floorD = 1.8f,
            overlapM = overlap,
            hysteresisM = 0.12f,
            cuts = CanonicalCuts,
        };

        private static bool Contains(ZoneLayoutSolver.ZoneRect r, float x, float z)
        {
            return Mathf.Abs(x - r.centerX) <= r.halfX + 1e-4f
                && Mathf.Abs(z - r.centerZ) <= r.halfZ + 1e-4f;
        }

        private static List<ZoneLayoutSolver.ZoneRect> ContainingRects(List<ZoneLayoutSolver.ZoneRect> rects, float x, float z)
        {
            var hits = new List<ZoneLayoutSolver.ZoneRect>();
            foreach (var r in rects) if (Contains(r, x, z)) hits.Add(r);
            return hits;
        }

        [Test]
        public void Solve_EmptyCuts_ReturnsEmpty()
        {
            var input = Canonical();
            input.cuts = System.Array.Empty<ZoneLayoutSolver.CutInput>();
            Assert.That(ZoneLayoutSolver.Solve(input), Is.Empty);
        }

        [Test]
        public void Solve_SingleCut_WholeLoopIsOneCamera()
        {
            var input = Canonical();
            input.cuts = new[] { new ZoneLayoutSolver.CutInput { s = 0f, camAfter = 3 } };
            var rects = ZoneLayoutSolver.Solve(input);

            Assert.That(rects, Is.Not.Empty);
            foreach (var r in rects) Assert.That(r.cameraIndex, Is.EqualTo(3));
            // 4 辺すべてが 1 カメラで覆われる（南の中点・東・北・西の各ループ点）。
            Assert.That(ContainingRects(rects, 0f, -0.7f), Is.Not.Empty, "south");
            Assert.That(ContainingRects(rects, 0.7f, 0f), Is.Not.Empty, "east");
            Assert.That(ContainingRects(rects, 0f, 0.7f), Is.Not.Empty, "north");
            Assert.That(ContainingRects(rects, -0.7f, 0f), Is.Not.Empty, "west");
        }

        [Test]
        public void Solve_CanonicalCuts_MapsCardinalMidpointsToExpectedCameras()
        {
            var rects = ZoneLayoutSolver.Solve(Canonical());

            // ループ辺の中点は 1 ゾーンだけが包含する（コーナーの重なりを避けた点）。
            AssertUniqueCamera(rects, 0f, -0.7f, 0, "south → cam0");
            AssertUniqueCamera(rects, 0.7f, 0f, 1, "east → cam1");
            AssertUniqueCamera(rects, 0f, 0.7f, 2, "north → cam2");
            AssertUniqueCamera(rects, -0.7f, 0f, 2, "west → cam2");
        }

        private static void AssertUniqueCamera(List<ZoneLayoutSolver.ZoneRect> rects, float x, float z, int cam, string msg)
        {
            var hits = ContainingRects(rects, x, z);
            Assert.That(hits, Has.Count.EqualTo(1), $"{msg}: 単一ゾーンが包含するはず");
            Assert.That(hits[0].cameraIndex, Is.EqualTo(cam), msg);
        }

        [Test]
        public void Solve_CanonicalCuts_ProducesOneRectPerEdge()
        {
            var rects = ZoneLayoutSolver.Solve(Canonical());
            // 南1 / 東1 / 北1 / 西1 = 4 矩形。
            Assert.That(rects, Has.Count.EqualTo(4));
        }

        [Test]
        public void Solve_SouthRect_MatchesAuthoredBounds()
        {
            var rects = ZoneLayoutSolver.Solve(Canonical());
            var south = rects.Find(r => r.label.Contains("South"));
            Assert.That(south.label, Is.Not.Null.And.Contains("South"));

            // authored A:South center(0,-0.8) half(1.4,0.55) に対応。
            // Solver は along を ±(floorHalf+OuterMargin)=±1.35 まで延長する。
            Assert.That(south.centerX, Is.EqualTo(0f).Within(1e-3f));
            Assert.That(south.centerZ, Is.EqualTo(-0.8f).Within(1e-3f));
            Assert.That(south.halfX, Is.EqualTo(1.35f).Within(1e-3f));
            Assert.That(south.halfZ, Is.EqualTo(0.55f).Within(1e-3f));
        }

        [Test]
        public void Solve_CornerRegion_IsOverlappedByBothAdjacentCameras()
        {
            var rects = ZoneLayoutSolver.Solve(Canonical());
            // SE コーナー外側 (0.8,-0.8): 南(cam0) と 東(cam1) の両方が覆う。
            var hits = ContainingRects(rects, 0.8f, -0.8f);
            var cams = new HashSet<int>();
            foreach (var h in hits) cams.Add(h.cameraIndex);
            Assert.That(cams, Does.Contain(0), "南 cam0 がコーナーを覆う");
            Assert.That(cams, Does.Contain(1), "東 cam1 がコーナーを覆う");
        }

        [Test]
        public void Solve_MidEdgeCuts_OverlapWidthEqualsOverlapM()
        {
            // 東辺中点(s=0.25) と 西辺中点(s=0.75) にカット。東辺で 2 ゾーンが overlapM 重なる。
            var input = Canonical(0.08f);
            input.cuts = new[]
            {
                new ZoneLayoutSolver.CutInput { s = 0.25f, camAfter = 0 },
                new ZoneLayoutSolver.CutInput { s = 0.75f, camAfter = 1 },
            };
            var rects = ZoneLayoutSolver.Solve(input);

            var east = rects.FindAll(r => r.label.Contains("East"));
            Assert.That(east, Has.Count.EqualTo(2), "東辺は 2 ゾーンに割れる");

            float aLo = Mathf.Min(east[0].centerZ - east[0].halfZ, east[1].centerZ - east[1].halfZ);
            float bHi = Mathf.Max(east[0].centerZ + east[0].halfZ, east[1].centerZ + east[1].halfZ);
            // 各ゾーンの z レンジの交差幅 = overlapM。
            float lo0 = east[0].centerZ - east[0].halfZ, hi0 = east[0].centerZ + east[0].halfZ;
            float lo1 = east[1].centerZ - east[1].halfZ, hi1 = east[1].centerZ + east[1].halfZ;
            float overlap = Mathf.Min(hi0, hi1) - Mathf.Max(lo0, lo1);
            Assert.That(overlap, Is.EqualTo(0.08f).Within(1e-3f), "辺途中カットの重なり幅 = overlapM");
            // 端はコーナーまで届く（合計で東辺全体を覆う）。
            Assert.That(aLo, Is.EqualTo(-1.35f).Within(1e-3f));
            Assert.That(bHi, Is.EqualTo(1.35f).Within(1e-3f));
        }

        [Test]
        public void Solve_UnsortedCuts_ProducesSameResultAsSorted()
        {
            var sorted = ZoneLayoutSolver.Solve(Canonical());

            var input = Canonical();
            input.cuts = new[]
            {
                new ZoneLayoutSolver.CutInput { s = 0.875f, camAfter = 0 },
                new ZoneLayoutSolver.CutInput { s = 0.125f, camAfter = 1 },
                new ZoneLayoutSolver.CutInput { s = 0.375f, camAfter = 2 },
            };
            var unsorted = ZoneLayoutSolver.Solve(input);

            Assert.That(unsorted.Count, Is.EqualTo(sorted.Count));
            for (int i = 0; i < sorted.Count; i++)
            {
                Assert.That(unsorted[i].cameraIndex, Is.EqualTo(sorted[i].cameraIndex), $"rect {i} cam");
                Assert.That(unsorted[i].centerX, Is.EqualTo(sorted[i].centerX).Within(1e-4f), $"rect {i} cx");
                Assert.That(unsorted[i].centerZ, Is.EqualTo(sorted[i].centerZ).Within(1e-4f), $"rect {i} cz");
            }
        }

        [Test]
        public void Solve_CorridorMidline_HasNoGaps()
        {
            var rects = ZoneLayoutSolver.Solve(Canonical());
            const int steps = 40;
            // ループ 4 辺の中線（loopHalf=0.7）を細かくサンプルして、全点がいずれかのゾーンに含まれることを確認。
            for (int i = 0; i <= steps; i++)
            {
                float t = -0.7f + 1.4f * i / steps;
                AssertCovered(rects, t, -0.7f, "south midline");
                AssertCovered(rects, 0.7f, t, "east midline");
                AssertCovered(rects, t, 0.7f, "north midline");
                AssertCovered(rects, -0.7f, t, "west midline");
            }
        }

        private static void AssertCovered(List<ZoneLayoutSolver.ZoneRect> rects, float x, float z, string where)
        {
            Assert.That(ContainingRects(rects, x, z), Is.Not.Empty, $"gap at {where} ({x:F2},{z:F2})");
        }
    }
}
