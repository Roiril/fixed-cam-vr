#nullable enable
using System.Collections.Generic;
using FixedCamVr.Tracking;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

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

        // ---- v2: grid（タイルペイント）モデル ----

        // floorW/D = cols/rows·tileM にして NW 角アンカーと中心が対称になるよう組む（座標推論を容易にする）。
        private static ZoneLayoutSolver.GridLayoutInput Grid(int cols, int rows, string[] cells,
            float tileM = 0.15f, float overlap = 0.08f)
        {
            return new ZoneLayoutSolver.GridLayoutInput
            {
                floorW = cols * tileM,
                floorD = rows * tileM,
                overlapM = overlap,
                hysteresisM = 0.12f,
                tileM = tileM,
                cols = cols,
                rows = rows,
                cells = ZoneLayoutSolver.ParseGridCells(cells, rows, cols),
            };
        }

        // cell(r,c) 中心の course space 座標（テスト検証用の独立実装）。
        private static Vector2 CellCenter(int cols, int rows, int r, int c, float tileM = 0.15f)
        {
            float x = -cols * tileM * 0.5f + (c + 0.5f) * tileM;
            float z = rows * tileM * 0.5f - (r + 0.5f) * tileM;
            return new Vector2(x, z);
        }

        [Test]
        public void ChooseSource_PrefersGridOverCuts()
        {
            Assert.That(ZoneLayoutSolver.ChooseSource(true, true), Is.EqualTo(ZoneLayoutSolver.LayoutSource.Grid));
            Assert.That(ZoneLayoutSolver.ChooseSource(false, true), Is.EqualTo(ZoneLayoutSolver.LayoutSource.Cuts));
            Assert.That(ZoneLayoutSolver.ChooseSource(true, false), Is.EqualTo(ZoneLayoutSolver.LayoutSource.Grid));
            Assert.That(ZoneLayoutSolver.ChooseSource(false, false), Is.EqualTo(ZoneLayoutSolver.LayoutSource.None));
        }

        [Test]
        public void SolveGrid_SingleCameraFull_OneRect()
        {
            var g = Grid(3, 2, new[] { "000", "000" });
            var rects = ZoneLayoutSolver.SolveGrid(g);

            Assert.That(rects, Has.Count.EqualTo(1));
            Assert.That(rects[0].cameraIndex, Is.EqualTo(0));
            // 全域（3x2 タイル）を覆う。overlapM/2 拡張分だけ床端より外へ出る。
            Assert.That(rects[0].centerX, Is.EqualTo(0f).Within(1e-4f));
            Assert.That(rects[0].centerZ, Is.EqualTo(0f).Within(1e-4f));
            Assert.That(rects[0].halfX, Is.EqualTo(3 * 0.15f * 0.5f + 0.04f).Within(1e-4f));
            Assert.That(rects[0].halfZ, Is.EqualTo(2 * 0.15f * 0.5f + 0.04f).Within(1e-4f));
        }

        [Test]
        public void SolveGrid_LShape_TwoRectsCoverArea()
        {
            // 同一カメラの L 字（3x3、右上 2 セルが未割当）。
            //   00.
            //   00.
            //   000
            var g = Grid(3, 3, new[] { "00.", "00.", "000" });
            var rects = ZoneLayoutSolver.SolveGrid(g);

            Assert.That(rects, Has.Count.EqualTo(2), "L 字は 2 矩形へ分解");
            foreach (var r in rects) Assert.That(r.cameraIndex, Is.EqualTo(0));

            // 割当セル 7 個の中心が全ていずれかの矩形に含まれる（過不足なくカバー）。
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                {
                    if (r < 2 && c == 2) continue; // 未割当セル
                    Vector2 p = CellCenter(3, 3, r, c);
                    Assert.That(ContainingRects(rects, p.x, p.y), Is.Not.Empty, $"cell({r},{c}) uncovered");
                }
        }

        [Test]
        public void SolveGrid_AdjacentCameras_OverlapEqualsOverlapM()
        {
            // 1 行 2 列で cam0 | cam1。境界で計 overlapM 重なる。
            var g = Grid(2, 1, new[] { "01" }, overlap: 0.08f);
            var rects = ZoneLayoutSolver.SolveGrid(g);

            Assert.That(rects, Has.Count.EqualTo(2));
            var r0 = rects.Find(r => r.cameraIndex == 0);
            var r1 = rects.Find(r => r.cameraIndex == 1);
            float hi0 = r0.centerX + r0.halfX;   // cam0 右端
            float lo1 = r1.centerX - r1.halfX;   // cam1 左端
            Assert.That(hi0 - lo1, Is.EqualTo(0.08f).Within(1e-4f), "x 方向の重なり幅 = overlapM");
        }

        [Test]
        public void SolveGrid_UnassignedTile_NotCoveredAtCenter()
        {
            // cam0 . cam0（1 行 3 列）。中央 '.' の中心はどのゾーンにも含まれない
            //（overlapM/2=0.04 < tileM/2=0.075 なので拡張しても届かない）。
            var g = Grid(3, 1, new[] { "0.0" }, overlap: 0.08f);
            var rects = ZoneLayoutSolver.SolveGrid(g);

            Assert.That(rects, Has.Count.EqualTo(2), "分断された cam0 は 2 矩形");
            Vector2 mid = CellCenter(3, 1, 0, 1);
            Assert.That(ContainingRects(rects, mid.x, mid.y), Is.Empty, "未割当タイルの中心は無ゾーン");
        }

        [Test]
        public void SolveGrid_DeterministicLabels_PerCamera()
        {
            var g = Grid(3, 1, new[] { "0.0" });
            var rects = ZoneLayoutSolver.SolveGrid(g);
            // 走査順に cam0#0, cam0#1。
            Assert.That(rects[0].label, Is.EqualTo("cam0#0"));
            Assert.That(rects[1].label, Is.EqualTo("cam0#1"));
        }

        [Test]
        public void SolveGrid_MalformedCells_DoesNotThrow()
        {
            // 行数不足・行長超過・未知文字が混在。例外にならず、割当できるタイルだけゾーン化する。
            LogAssert.ignoreFailingMessages = true;
            var g = new ZoneLayoutSolver.GridLayoutInput
            {
                floorW = 3 * 0.15f,
                floorD = 3 * 0.15f,
                overlapM = 0.08f,
                hysteresisM = 0.12f,
                tileM = 0.15f,
                cols = 3,
                rows = 3,
                cells = ZoneLayoutSolver.ParseGridCells(new[] { "0X", "00000", "9.2" }, 3, 3),
            };
            List<ZoneLayoutSolver.ZoneRect> rects = null!;
            Assert.DoesNotThrow(() => { rects = ZoneLayoutSolver.SolveGrid(g); });
            Assert.That(rects, Is.Not.Null);
            // '9' はカメラ範囲外(0..8)なので未割当、'X' も未割当、'2' は cam2。
            foreach (var r in rects) Assert.That(r.cameraIndex, Is.LessThanOrEqualTo(8).And.GreaterThanOrEqualTo(0));
            Assert.That(rects.Exists(r => r.cameraIndex == 2), "'2' タイルは cam2 ゾーンになる");
            LogAssert.ignoreFailingMessages = false;
        }

        [Test]
        public void ParseGridCells_TolerantOfBadInput()
        {
            LogAssert.ignoreFailingMessages = true;
            // rows/cols=2 に対し 1 行だけ・未知文字 'Z'。例外なく長さ 4 の配列（既定 -1）が返る。
            int[] cells = ZoneLayoutSolver.ParseGridCells(new[] { "1Z" }, 2, 2);
            LogAssert.ignoreFailingMessages = false;

            Assert.That(cells.Length, Is.EqualTo(4));
            Assert.That(cells[0], Is.EqualTo(1), "'1' → cam1");
            Assert.That(cells[1], Is.EqualTo(-1), "'Z' → 未割当");
            Assert.That(cells[2], Is.EqualTo(-1), "欠損行 → 未割当");
            Assert.That(cells[3], Is.EqualTo(-1));
        }
    }
}
