#nullable enable
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Tracking.Tests
{
    /// <summary>
    /// grid セル → course 座標の純関数（<see cref="ZoneLayoutSolver.CellRect"/>）と、登録ビュー用の
    /// 塗りタイル列挙（<see cref="ZoneGridFootprintLogic.BuildCells"/>）を検証する。
    /// SolveGrid とフットプリントが同一座標式を共有する（＝ゾーンと生タイルが一致する）ことも固定する。
    /// </summary>
    public sealed class ZoneGridFootprintTests
    {
        // cell(r,c) 中心の course space 座標（独立実装。row0=北端 z 大、col0=西端 x 小）。
        private static Vector2 CellCenter(int cols, int rows, int r, int c, float tileM)
            => new(-cols * tileM * 0.5f + (c + 0.5f) * tileM,
                    rows * tileM * 0.5f - (r + 0.5f) * tileM);

        [Test]
        public void CellRect_NorthWestCell_HasExpectedCorners()
        {
            // 2x2 / tileM 0.15 → floor 0.3x0.3, halfW=halfD=0.15。NW セル (0,0)。
            ZoneLayoutSolver.CellRect(0, 0, 2, 2, 0.15f,
                out float xLo, out float xHi, out float zLo, out float zHi);
            Assert.That(xLo, Is.EqualTo(-0.15f).Within(1e-5f), "西端");
            Assert.That(xHi, Is.EqualTo(0f).Within(1e-5f));
            Assert.That(zHi, Is.EqualTo(0.15f).Within(1e-5f), "北端");
            Assert.That(zLo, Is.EqualTo(0f).Within(1e-5f));
        }

        [Test]
        public void CellRect_SouthEastCell_HasExpectedCorners()
        {
            ZoneLayoutSolver.CellRect(1, 1, 2, 2, 0.15f,
                out float xLo, out float xHi, out float zLo, out float zHi);
            Assert.That(xLo, Is.EqualTo(0f).Within(1e-5f));
            Assert.That(xHi, Is.EqualTo(0.15f).Within(1e-5f), "東端");
            Assert.That(zHi, Is.EqualTo(0f).Within(1e-5f));
            Assert.That(zLo, Is.EqualTo(-0.15f).Within(1e-5f), "南端");
        }

        [Test]
        public void CellRect_CenterMatchesIndependentFormula()
        {
            const int rows = 3, cols = 4;
            const float tileM = 0.2f;
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    ZoneLayoutSolver.CellRect(r, c, rows, cols, tileM,
                        out float xLo, out float xHi, out float zLo, out float zHi);
                    Vector2 center = new(0.5f * (xLo + xHi), 0.5f * (zLo + zHi));
                    Vector2 expected = CellCenter(cols, rows, r, c, tileM);
                    Assert.That(center.x, Is.EqualTo(expected.x).Within(1e-5f), $"cell({r},{c}) x");
                    Assert.That(center.y, Is.EqualTo(expected.y).Within(1e-5f), $"cell({r},{c}) z");
                }
        }

        [Test]
        public void CellRect_MatchesSolveGridForSingleCell()
        {
            // 中央 1 セルだけ塗る → SolveGrid はそのセル矩形 + overlapHalf の 1 矩形。
            // CellRect の中心・半幅（overlap 抜き）と一致するはず（座標式の共有を固定）。
            var g = new ZoneLayoutSolver.GridLayoutInput
            {
                floorW = 3 * 0.15f,
                floorD = 3 * 0.15f,
                overlapM = 0.08f,
                hysteresisM = 0.12f,
                tileM = 0.15f,
                cols = 3,
                rows = 3,
                cells = ZoneLayoutSolver.ParseGridCells(new[] { "...", ".0.", "..." }, 3, 3),
            };
            var rects = ZoneLayoutSolver.SolveGrid(g);
            Assert.That(rects, Has.Count.EqualTo(1));

            ZoneLayoutSolver.CellRect(1, 1, 3, 3, 0.15f,
                out float xLo, out float xHi, out float zLo, out float zHi);
            Assert.That(rects[0].centerX, Is.EqualTo(0.5f * (xLo + xHi)).Within(1e-5f));
            Assert.That(rects[0].centerZ, Is.EqualTo(0.5f * (zLo + zHi)).Within(1e-5f));
            Assert.That(rects[0].halfX, Is.EqualTo(0.5f * (xHi - xLo) + 0.04f).Within(1e-5f));
            Assert.That(rects[0].halfZ, Is.EqualTo(0.5f * (zHi - zLo) + 0.04f).Within(1e-5f));
        }

        [Test]
        public void BuildCells_PaintedTilesOnly()
        {
            const int rows = 2, cols = 3;
            int[] cells = ZoneLayoutSolver.ParseGridCells(new[] { "0.1", "..2" }, rows, cols);
            List<ZoneGridFootprintLogic.Cell> tiles =
                ZoneGridFootprintLogic.BuildCells(cells, rows, cols, 0.15f);

            Assert.That(tiles, Has.Count.EqualTo(3), "塗られた 3 セルのみ列挙");
            var cams = new List<int>();
            foreach (var t in tiles) cams.Add(t.cam);
            Assert.That(cams, Does.Contain(0));
            Assert.That(cams, Does.Contain(1));
            Assert.That(cams, Does.Contain(2));
        }

        [Test]
        public void BuildCells_SkipsUnassigned_EmptyGridReturnsEmpty()
        {
            int[] cells = ZoneLayoutSolver.ParseGridCells(new[] { "...", "..." }, 2, 3);
            Assert.That(ZoneGridFootprintLogic.BuildCells(cells, 2, 3, 0.15f), Is.Empty);
        }

        [Test]
        public void BuildCells_TileRectMatchesCellRect()
        {
            const int rows = 2, cols = 2;
            int[] cells = ZoneLayoutSolver.ParseGridCells(new[] { "3.", ".." }, rows, cols);
            List<ZoneGridFootprintLogic.Cell> tiles =
                ZoneGridFootprintLogic.BuildCells(cells, rows, cols, 0.15f);

            Assert.That(tiles, Has.Count.EqualTo(1));
            Assert.That(tiles[0].cam, Is.EqualTo(3));
            ZoneLayoutSolver.CellRect(0, 0, rows, cols, 0.15f,
                out float xLo, out float xHi, out float zLo, out float zHi);
            Assert.That(tiles[0].xLo, Is.EqualTo(xLo).Within(1e-6f));
            Assert.That(tiles[0].xHi, Is.EqualTo(xHi).Within(1e-6f));
            Assert.That(tiles[0].zLo, Is.EqualTo(zLo).Within(1e-6f));
            Assert.That(tiles[0].zHi, Is.EqualTo(zHi).Within(1e-6f));
        }

        [Test]
        public void BuildCells_InvalidDims_ReturnEmpty()
        {
            Assert.That(ZoneGridFootprintLogic.BuildCells(System.Array.Empty<int>(), 0, 0, 0.15f), Is.Empty);
            Assert.That(ZoneGridFootprintLogic.BuildCells(new[] { 0 }, 1, 1, 0f), Is.Empty);
            Assert.That(ZoneGridFootprintLogic.BuildCells(null, 2, 2, 0.15f), Is.Empty);
        }
    }
}
