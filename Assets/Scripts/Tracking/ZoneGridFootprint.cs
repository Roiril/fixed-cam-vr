#nullable enable
using System.Collections.Generic;
using FixedCamVr.Streaming;
using UnityEngine;

namespace FixedCamVr.Tracking
{
    /// <summary>
    /// Web オペレータ卓のフロアマップで塗った各カメラ領域（show.json <c>layout.grid</c>）の**生タイル**を、
    /// 登録モード中（Capture / Verify / Review）にカメラ別色で床へ表示するビュー。
    /// N 点登録の結果が正しいか（塗ったタイルが実世界のどこに落ちるか）を視覚確認するためのもの。
    ///
    /// 塗られたタイル（cam &gt;= 0）だけを **単一 Mesh（頂点色・セルあたり 4 頂点 2 三角形）** +
    /// <c>Sprites/Default</c> の 1 マテリアル 1 Renderer で描く。頂点は build 時に
    /// <see cref="CourseFrame.CourseToWorld"/> でワールド座標へ焼き込む（GameObject の transform は identity）。
    /// 毎フレーム更新はせず、<see cref="CourseFrame.Changed"/>（登録変換の変化・Verify プレビュー含む）と
    /// <see cref="ShowControlClient.LayoutChanged"/> を購読して Rebuild する。
    ///
    /// SerializeField は増やさない（旧シーン YAML 未反映で 0 に読まれる罠回避のため数値は const）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ZoneGridFootprint : MonoBehaviour
    {
        // 床からの浮かせ量 (m)。既存フットプリント Quad（0.02m 級）と同オーダーで z-fight を避ける。
        private const float FloorY = 0.015f;
        // 塗りの不透明度。実世界・ワイヤーフレームを透かして見せる。
        private const float FillAlpha = 0.25f;

        private ShowControlClient? _showControl;
        private CourseFrame? _frame;
        private MeshFilter? _filter;
        private MeshRenderer? _renderer;
        private Mesh? _mesh;
        private Material? _material;
        private bool _subscribed;

        /// <summary>直近 Rebuild で塗りタイルを 1 つ以上描いたか（grid 不在 / 全未割当なら false）。</summary>
        public bool HasTiles { get; private set; }

        /// <summary>
        /// 供給元を注入し、Mesh を組み、イベント購読を開始する。CourseRegistrationController が
        /// 登録入場時（BuildViz）に呼ぶ。再入も安全（前回購読は解除してから張り直す）。
        /// </summary>
        public void Initialize(ShowControlClient? showControl, CourseFrame? frame)
        {
            Unsubscribe();
            _showControl = showControl;
            _frame = frame;
            EnsureComponents();
            Subscribe();
            Rebuild();
        }

        private void EnsureComponents()
        {
            if (_filter == null)
                _filter = GetComponent<MeshFilter>() ?? gameObject.AddComponent<MeshFilter>();
            if (_renderer == null)
            {
                _renderer = GetComponent<MeshRenderer>() ?? gameObject.AddComponent<MeshRenderer>();
                _material = new Material(Shader.Find("Sprites/Default"));
                _renderer.sharedMaterial = _material;
                _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _renderer.receiveShadows = false;
            }
            if (_mesh == null)
            {
                _mesh = new Mesh { name = "ZoneGridFootprint" };
                _filter.sharedMesh = _mesh;
            }
        }

        private void Subscribe()
        {
            if (_subscribed) return;
            if (_frame != null) _frame.Changed += Rebuild;
            if (_showControl != null) _showControl.LayoutChanged += Rebuild;
            _subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!_subscribed) return;
            if (_frame != null) _frame.Changed -= Rebuild;
            if (_showControl != null) _showControl.LayoutChanged -= Rebuild;
            _subscribed = false;
        }

        /// <summary>grid の塗りタイルを course→world 変換して単一 Mesh へ焼き直す。grid 不在なら空にする。</summary>
        public void Rebuild()
        {
            HasTiles = false;
            if (_mesh == null || _frame == null) return;
            _mesh.Clear();

            ShowGridDef? grid = _showControl != null && _showControl.Layout != null
                ? _showControl.Layout.grid : null;
            if (grid == null || !grid.HasData()) return;

            int[] cells = ZoneLayoutSolver.ParseGridCells(grid.cells, grid.rows, grid.cols);
            List<ZoneGridFootprintLogic.Cell> tiles =
                ZoneGridFootprintLogic.BuildCells(cells, grid.rows, grid.cols, grid.tileM);
            if (tiles.Count == 0) return;

            int n = tiles.Count;
            var verts = new Vector3[n * 4];
            var colors = new Color[n * 4];
            var tris = new int[n * 6];
            for (int i = 0; i < n; i++)
            {
                ZoneGridFootprintLogic.Cell t = tiles[i];
                int v = i * 4;
                // NW, NE, SE, SW（course space）→ world へ焼き込む。
                verts[v + 0] = _frame.CourseToWorld(new Vector2(t.xLo, t.zHi), FloorY);
                verts[v + 1] = _frame.CourseToWorld(new Vector2(t.xHi, t.zHi), FloorY);
                verts[v + 2] = _frame.CourseToWorld(new Vector2(t.xHi, t.zLo), FloorY);
                verts[v + 3] = _frame.CourseToWorld(new Vector2(t.xLo, t.zLo), FloorY);
                Color col = ZonePalette.ForCamera(t.cam);
                col.a = FillAlpha;
                colors[v + 0] = colors[v + 1] = colors[v + 2] = colors[v + 3] = col;
                int tri = i * 6;
                tris[tri + 0] = v + 0; tris[tri + 1] = v + 1; tris[tri + 2] = v + 2;
                tris[tri + 3] = v + 0; tris[tri + 4] = v + 2; tris[tri + 5] = v + 3;
            }
            _mesh.vertices = verts;
            _mesh.colors = colors;
            _mesh.triangles = tris;
            _mesh.RecalculateBounds();
            HasTiles = true;
        }

        private void OnDestroy()
        {
            Unsubscribe();
            if (_mesh != null) Destroy(_mesh);
            if (_material != null) Destroy(_material);
        }
    }

    /// <summary>
    /// <see cref="ZoneGridFootprint"/> の頂点計算を Unity 描画 API から分離した純ロジック（EditMode テスト用）。
    /// ParseGridCells 済みの int 配列（-1=未割当 / 0..8=カメラ index）から、塗りセルごとの
    /// course space 矩形とカメラ index を列挙する。CourseToWorld 変換と Mesh 構築は呼び出し側に残す。
    /// </summary>
    public static class ZoneGridFootprintLogic
    {
        /// <summary>塗りタイル 1 枚の course space 矩形 + カメラ index。</summary>
        public struct Cell
        {
            public int cam;
            public float xLo;
            public float xHi;
            public float zLo;
            public float zHi;
        }

        /// <summary>
        /// row-major の parse 済み cells（<c>cells[r*cols + c]</c>、-1=未割当）から、cam &gt;= 0 のタイルだけを
        /// 走査順（左上→右下）に列挙する。矩形は <see cref="ZoneLayoutSolver.CellRect"/> と同一式。
        /// 不正な寸法（rows/cols/tileM &lt;= 0）や null cells は空リストを返す。
        /// </summary>
        public static List<Cell> BuildCells(int[]? cells, int rows, int cols, float tileM)
        {
            var list = new List<Cell>();
            if (cells == null || rows <= 0 || cols <= 0 || tileM <= 0f) return list;
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    int idx = r * cols + c;
                    if (idx >= cells.Length) continue;
                    int cam = cells[idx];
                    if (cam < 0) continue; // 未割当タイルは描かない
                    ZoneLayoutSolver.CellRect(r, c, rows, cols, tileM,
                        out float xLo, out float xHi, out float zLo, out float zHi);
                    list.Add(new Cell { cam = cam, xLo = xLo, xHi = xHi, zLo = zLo, zHi = zHi });
                }
            }
            return list;
        }
    }
}
