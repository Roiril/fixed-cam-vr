#nullable enable
using System.Collections.Generic;
using FixedCamVr.Streaming;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Tracking.Tests
{
    /// <summary>
    /// 位置合わせ検証ビューの供給シーム（<see cref="ShowControlClient.SetLayoutForPreview"/>）と
    /// <see cref="ZoneGridFootprint"/> の world 焼き込みを、実 GameObject で組んで検証する。
    /// grid 生タイルが CourseFrame の非恒等登録変換で正しくワールドへ落ちること、LayoutChanged で
    /// 自動 Rebuild されること、Edit Mode teardown（DestroyImmediate 分岐）が例外を出さないことを固定する。
    /// </summary>
    public sealed class RegistrationVizTests
    {
        // ZoneGridFootprint 内部の床浮かせ量 const（FloorY）と一致させる。
        private const float FloorY = 0.015f;
        private const float FillAlpha = 0.25f;

        private static ShowLayoutDef GridLayout(string[] cells, int rows, int cols, float tileM = 0.15f)
            => new()
            {
                rev = 1,
                grid = new ShowGridDef { tileM = tileM, cols = cols, rows = rows, cells = cells },
            };

        [Test]
        public void Footprint_BakesTilesToWorld_WithNonIdentityRegistration()
        {
            var frameGo = new GameObject("frame");
            var showGo = new GameObject("show");
            var footGo = new GameObject("foot");
            try
            {
                var frame = frameGo.AddComponent<CourseFrame>();
                // 非恒等の剛体登録（save:false ＝ registration.json は書かない）。
                frame.SetRegistration(new Vector2(0.3f, -0.2f), 15f, save: false);

                var show = showGo.AddComponent<ShowControlClient>();
                string[] cells = { "0.1", "..2" };
                show.SetLayoutForPreview(GridLayout(cells, 2, 3));

                var foot = footGo.AddComponent<ZoneGridFootprint>();
                foot.Initialize(show, frame);

                Assert.That(foot.HasTiles, Is.True, "塗りタイルがあるので HasTiles=true");

                var mesh = footGo.GetComponent<MeshFilter>().sharedMesh;
                Assert.That(mesh, Is.Not.Null);

                int[] parsed = ZoneLayoutSolver.ParseGridCells(cells, 2, 3);
                List<ZoneGridFootprintLogic.Cell> tiles =
                    ZoneGridFootprintLogic.BuildCells(parsed, 2, 3, 0.15f);
                Assert.That(tiles, Has.Count.EqualTo(3));
                Assert.That(mesh.vertexCount, Is.EqualTo(tiles.Count * 4));

                Vector3[] verts = mesh.vertices;
                Color[] colors = mesh.colors;

                // 先頭タイルと最終タイルの 4 頂点が CourseToWorld 期待値に一致するか（NW,NE,SE,SW 順）。
                AssertTileVerts(verts, frame, tiles[0], 0);
                AssertTileVerts(verts, frame, tiles[tiles.Count - 1], tiles.Count - 1);

                foreach (Color col in colors)
                    Assert.That(col.a, Is.EqualTo(FillAlpha).Within(1e-4f), "塗りの alpha は 0.25");
            }
            finally
            {
                Object.DestroyImmediate(footGo);
                Object.DestroyImmediate(showGo);
                Object.DestroyImmediate(frameGo);
            }
        }

        private static void AssertTileVerts(Vector3[] verts, CourseFrame frame,
            ZoneGridFootprintLogic.Cell t, int tileIndex)
        {
            int v = tileIndex * 4;
            Vector3[] expected =
            {
                frame.CourseToWorld(new Vector2(t.xLo, t.zHi), FloorY), // NW
                frame.CourseToWorld(new Vector2(t.xHi, t.zHi), FloorY), // NE
                frame.CourseToWorld(new Vector2(t.xHi, t.zLo), FloorY), // SE
                frame.CourseToWorld(new Vector2(t.xLo, t.zLo), FloorY), // SW
            };
            for (int k = 0; k < 4; k++)
            {
                Assert.That(verts[v + k].x, Is.EqualTo(expected[k].x).Within(1e-4f), $"tile{tileIndex} v{k} x");
                Assert.That(verts[v + k].y, Is.EqualTo(expected[k].y).Within(1e-4f), $"tile{tileIndex} v{k} y");
                Assert.That(verts[v + k].z, Is.EqualTo(expected[k].z).Within(1e-4f), $"tile{tileIndex} v{k} z");
            }
        }

        [Test]
        public void SetLayoutForPreview_FiresLayoutChanged_TriggersRebuild()
        {
            var frameGo = new GameObject("frame");
            var showGo = new GameObject("show");
            var footGo = new GameObject("foot");
            try
            {
                var frame = frameGo.AddComponent<CourseFrame>();
                var show = showGo.AddComponent<ShowControlClient>();
                // 初期レイアウト: 塗りタイル 1 枚 → 頂点 4。
                show.SetLayoutForPreview(GridLayout(new[] { "0.", ".." }, 2, 2));

                var foot = footGo.AddComponent<ZoneGridFootprint>();
                foot.Initialize(show, frame);
                var mesh = footGo.GetComponent<MeshFilter>().sharedMesh;
                Assert.That(mesh.vertexCount, Is.EqualTo(4), "1 タイル → 4 頂点");

                // レイアウト差し替え → LayoutChanged 発火 → footprint が自動 Rebuild。
                show.SetLayoutForPreview(GridLayout(new[] { "01", ".." }, 2, 2));
                Assert.That(mesh.vertexCount, Is.EqualTo(8), "2 タイル → 8 頂点（自動 Rebuild 済み）");

                // 空レイアウトへ → タイルなし。
                show.SetLayoutForPreview(GridLayout(new[] { "..", ".." }, 2, 2));
                Assert.That(foot.HasTiles, Is.False);
                Assert.That(mesh.vertexCount, Is.EqualTo(0));
            }
            finally
            {
                Object.DestroyImmediate(footGo);
                Object.DestroyImmediate(showGo);
                Object.DestroyImmediate(frameGo);
            }
        }
    }
}
