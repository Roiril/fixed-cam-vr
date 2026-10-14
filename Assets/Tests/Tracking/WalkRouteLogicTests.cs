#nullable enable
using System.Collections.Generic;
using System.Linq;
using FixedCamVr.Streaming;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Tracking.Tests
{
    /// <summary>
    /// 歩行ルート生成（<see cref="WalkRouteLogic"/>）の純ロジックを検証する。
    /// Quest のデバッグドライバと Editor の仮想歩行ツールが共有する経路なので、
    /// セル解釈・曲がり角の間引き・周回列・滞在秒の取り方を固定する。
    /// </summary>
    public sealed class WalkRouteLogicTests
    {
        private const float TileM = 0.15f;
        private const float DwellSec = 4f;

        // 2 行 x 3 列。3 台が互いに隣り合う最小配置（4 近傍で 0-1 / 0-2 / 1-2 がそれぞれ接する）。
        //   0 0 1
        //   2 2 1
        // 代表点: 0=(0,0) / 1=(0,2) / 2=(1,0)
        private static ShowGridDef ThreeCameraGrid() => new ShowGridDef
        {
            tileM = TileM,
            rows = 2,
            cols = 3,
            cells = new[] { "001", "221" },
        };

        // cell(r,c) 中心の course space 座標（独立実装。row0=北端 z 大、col0=西端 x 小）。
        private static Vector2 CellCenter(int cols, int rows, int r, int c)
            => new(-cols * TileM * 0.5f + (c + 0.5f) * TileM,
                    rows * TileM * 0.5f - (r + 0.5f) * TileM);

        private static List<WalkRouteLogic.Waypoint> Route(int laps, out string log)
        {
            int[,] map = WalkRouteLogic.BuildCells(ThreeCameraGrid(), out int rows, out int cols);
            return WalkRouteLogic.BuildRoute(map, rows, cols, TileM, new[] { 0, 1, 2 }, laps, DwellSec, out log);
        }

        [Test]
        public void BuildCells_ParsesDigitsAndMapsEverythingElseToUnassigned()
        {
            var g = new ShowGridDef
            {
                rows = 3,
                cols = 4,
                // 行 0: 数字・'x'・範囲外の '9' / 行 1: 短い行（足りない列は '.' 扱い）/ 行 2: null 行
                cells = new[] { "01x9", "8", null! },
            };

            int[,] map = WalkRouteLogic.BuildCells(g, out int rows, out int cols);

            Assert.That(rows, Is.EqualTo(3));
            Assert.That(cols, Is.EqualTo(4));
            Assert.That(map[0, 0], Is.EqualTo(0));
            Assert.That(map[0, 1], Is.EqualTo(1));
            Assert.That(map[0, 2], Is.EqualTo(-1), "数字以外は未割当");
            Assert.That(map[0, 3], Is.EqualTo(-1), "'9' は範囲外（カメラは 0..8）");
            Assert.That(map[1, 0], Is.EqualTo(8));
            Assert.That(map[1, 1], Is.EqualTo(-1), "短い行の不足分は未割当");
            for (int c = 0; c < cols; c++) Assert.That(map[2, c], Is.EqualTo(-1), "null 行は全部未割当");
        }

        [Test]
        public void BuildCells_NegativeDims_ClampToEmpty()
        {
            var g = new ShowGridDef { rows = -2, cols = -3, cells = new[] { "012" } };

            int[,] map = WalkRouteLogic.BuildCells(g, out int rows, out int cols);

            Assert.That(rows, Is.EqualTo(0));
            Assert.That(cols, Is.EqualTo(0));
            Assert.That(map.Length, Is.EqualTo(0));
        }

        [Test]
        public void BuildRoute_EndsAtFirstCameraRepresentative_WithDwellOnlyAtCameraStops()
        {
            List<WalkRouteLogic.Waypoint> route = Route(1, out string log);

            Assert.That(route, Is.Not.Empty);
            WalkRouteLogic.Waypoint last = route[route.Count - 1];
            Vector2 expected = CellCenter(3, 2, 0, 0);   // order[0]=0 の代表点は (0,0)
            Assert.That(last.Camera, Is.EqualTo(0), "最後は order[0] へ戻る（帰りの A）");
            Assert.That(last.Course.x, Is.EqualTo(expected.x).Within(1e-5f));
            Assert.That(last.Course.y, Is.EqualTo(expected.y).Within(1e-5f));

            foreach (WalkRouteLogic.Waypoint wp in route)
            {
                if (wp.Camera >= 0) Assert.That(wp.HoldSec, Is.EqualTo(DwellSec), "カメラ停止点は dwellSec");
                else Assert.That(wp.HoldSec, Is.EqualTo(0f), "曲がり角は止まらない");
            }
            Assert.That(route.Any(w => w.Camera < 0), Is.True, "1->2 は (1,2) で曲がるので角が残る");
            Assert.That(log, Is.EqualTo("0->1 1->2 2->0"));
        }

        [Test]
        public void BuildRoute_TwoLaps_VisitsEachCameraTwice()
        {
            List<WalkRouteLogic.Waypoint> route = Route(2, out _);

            List<int> stops = route.Where(w => w.Camera >= 0).Select(w => w.Camera).ToList();

            Assert.That(stops.Count, Is.EqualTo(6));
            Assert.That(stops.Count(c => c == 0), Is.EqualTo(2));
            Assert.That(stops.Count(c => c == 1), Is.EqualTo(2));
            Assert.That(stops.Count(c => c == 2), Is.EqualTo(2));
            Assert.That(stops, Is.EqualTo(new[] { 1, 2, 0, 1, 2, 0 }), "order を周回し、最後に order[0] へ戻る");
        }

        [Test]
        public void BuildRoute_MissingCameraTiles_ReturnsEmpty()
        {
            // カメラ 7 のタイルが無い → 有効な代表点が 1 つだけになり、経路は作れない。
            var g = new ShowGridDef { tileM = TileM, rows = 1, cols = 2, cells = new[] { "01" } };
            int[,] map = WalkRouteLogic.BuildCells(g, out int rows, out int cols);

            UnityEngine.TestTools.LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("カメラ 7 のタイルが grid に無い"));
            List<WalkRouteLogic.Waypoint> route =
                WalkRouteLogic.BuildRoute(map, rows, cols, TileM, new[] { 0, 7 }, 3, DwellSec, out string log);

            Assert.That(route, Is.Empty);
            Assert.That(log, Is.EqualTo(""));
        }
    }
}
