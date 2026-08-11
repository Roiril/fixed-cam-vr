#nullable enable

using System.Collections.Generic;
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 段 4 で現実が割れるときの<b>セル格子</b>を組む。覆い（<see cref="IntroVeil"/>）が
    /// 1 枚 quad と差し替えて張る（<c>shatter &gt; 0</c> のあいだだけ）。
    ///
    /// <b>1 セル = 独立した 4 頂点の quad</b>。頂点を共有しないので、頂点シェーダで
    /// セルごとに回して飛ばせる。セルの中心は <c>TEXCOORD1.xy</c> に載せて全 4 頂点へ配る
    /// （回転・縮小の中心と、行き先を解く起点になる）。<c>TEXCOORD1.z</c> は
    /// <b>1 = 割れるセル / 0 = 動かない縁取り</b>。
    ///
    /// ⚠ <b>格子は視野ぶんしか敷かない</b>（<see cref="HalfExtentLocal"/>）。覆いの面は
    /// ±73° まで広がっていて、そこまで一様に敷くと 3 倍以上の頂点を捨てることになる。
    /// 外側は動かない縁取り 4 枚で埋める — 表示画角（Quest 3 は水平 ±55° 前後）の外なので、
    /// そこが割れないことは見えない。
    ///
    /// ⚠ <b>1 枚 quad の側には <c>TEXCOORD1</c> が無い</b>。読むと 0 になり
    /// （<c>cell.z = 0</c>）、シェーダは破砕の枝へ入らない ＝ 段 1〜3 の見えは 1 ビットも変わらない。
    /// これが「破砕を足しても既存の段を壊さない」の担保なので、
    /// <b>1 枚 quad にストリームを足さないこと</b>。
    /// </summary>
    public static class IntroVeilShatterMesh
    {
        /// <summary>1 辺のセル数。中心のセルで約 3.6°（＝腕を伸ばした親指の爪くらい）。</summary>
        public const int CellsPerSide = 64;

        /// <summary>
        /// 格子の広がり（覆いのローカル単位・±0.5 が覆いの端 ＝ ±73.3°）。
        /// 0.30 は <c>tan 2.0</c> ＝ <b>±63.4°</b> で、Quest 3 の表示画角（水平 ±55° / 垂直 ±48°）に
        /// 8° 以上の余白がある。
        /// </summary>
        public const float HalfExtentLocal = 0.30f;

        /// <summary>セル数（縁取りは含まない）。テレメトリが「格子を組めたか」を出すのに使う。</summary>
        public static int CellCount => CellsPerSide * CellsPerSide;

        /// <summary>格子 ＋ 縁取りのメッシュを組む。<b>頂点は 16,400 = 16bit index に収まる。</b></summary>
        public static Mesh Build()
        {
            var m = new Mesh { name = "IntroVeilShatterCells" };
            int quads = CellCount + 4;
            var pos = new List<Vector3>(quads * 4);
            var uv = new List<Vector2>(quads * 4);
            var cell = new List<Vector3>(quads * 4);
            var tris = new List<int>(quads * 6);

            const float h = HalfExtentLocal;
            float step = h * 2f / CellsPerSide;
            for (int j = 0; j < CellsPerSide; j++)
            {
                float y0 = -h + j * step;
                float y1 = -h + (j + 1) * step;
                for (int i = 0; i < CellsPerSide; i++)
                {
                    float x0 = -h + i * step;
                    float x1 = -h + (i + 1) * step;
                    AddQuad(pos, uv, cell, tris, x0, y0, x1, y1,
                            new Vector3((x0 + x1) * 0.5f, (y0 + y1) * 0.5f, 1f));
                }
            }

            // 縁取り（動かない）。覆いの端 ±0.5 まで埋めて、割れない外周が黒く抜けないようにする。
            var still = new Vector3(0f, 0f, 0f);
            AddQuad(pos, uv, cell, tris, -0.5f, h, 0.5f, 0.5f, still);      // 上
            AddQuad(pos, uv, cell, tris, -0.5f, -0.5f, 0.5f, -h, still);    // 下
            AddQuad(pos, uv, cell, tris, -0.5f, -h, -h, h, still);          // 左
            AddQuad(pos, uv, cell, tris, h, -h, 0.5f, h, still);            // 右

            m.SetVertices(pos);
            m.SetUVs(0, uv);
            m.SetUVs(1, cell);
            m.SetTriangles(tris, 0);
            // 破砕は頂点シェーダで面外へ飛ばす。既定の bounds だと視錐台カリングで
            // 消えることがあるので、覆いの面いっぱいを常に内包させる。
            m.bounds = new Bounds(Vector3.zero, new Vector3(1.2f, 1.2f, 1.2f));
            m.UploadMeshData(markNoLongerReadable: false);
            return m;
        }

        private static void AddQuad(List<Vector3> pos, List<Vector2> uv, List<Vector3> cell,
                                    List<int> tris, float x0, float y0, float x1, float y1,
                                    Vector3 cellData)
        {
            int b = pos.Count;
            pos.Add(new Vector3(x0, y0, 0f));
            pos.Add(new Vector3(x1, y0, 0f));
            pos.Add(new Vector3(x0, y1, 0f));
            pos.Add(new Vector3(x1, y1, 0f));
            // uv は「覆いの面のどこか」。1 枚 quad と同じ規約（ローカル ±0.5 → uv 0..1）。
            uv.Add(new Vector2(x0 + 0.5f, y0 + 0.5f));
            uv.Add(new Vector2(x1 + 0.5f, y0 + 0.5f));
            uv.Add(new Vector2(x0 + 0.5f, y1 + 0.5f));
            uv.Add(new Vector2(x1 + 0.5f, y1 + 0.5f));
            for (int k = 0; k < 4; k++) cell.Add(cellData);
            tris.Add(b + 0); tris.Add(b + 2); tris.Add(b + 1);
            tris.Add(b + 2); tris.Add(b + 3); tris.Add(b + 1);
        }
    }
}
