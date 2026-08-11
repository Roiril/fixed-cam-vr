#nullable enable

using System.Collections.Generic;
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 段 4 で<b>封印の箱そのものが割れる</b>ための面。単位立方体（±0.5）を細かい quad へ割り、
    /// 各 quad の中心を <c>TEXCOORD1</c> に載せる（回す・縮める・飛ばすの起点）。
    ///
    /// ⚠ <b>ここが破砕の主役。</b> 段 4 の実機の絵（<c>logs/evidence/20260811_200400</c>）を見ると、
    /// 体験者は箱の面から 0.5m ほどの所に立っていて<b>視界のほぼ全部が箱</b>だった。
    /// パススルーだけ割っても、割れているところが画にほとんど無い。
    ///
    /// ⚠ 底面は作らない。<c>Cull Back</c> で外から見るので、床に接した底の外側は見えない。
    ///
    /// 覆いのセル（<see cref="IntroVeilShatterMesh"/>）との違いは<b>実体かどうか</b>。
    /// 覆いのセルは「現実が覗く窓」で中身は動かないが、箱の破片は面そのものなので
    /// <b>模様を持ったまま飛ぶ</b>（シェーダが模様を<b>ホームの座標</b>で引くため）。
    /// </summary>
    public static class SealedBoxShatterMesh
    {
        /// <summary>破片 1 枚のおよその実寸 (m)。0.5m の距離で約 8.6°、1.5m で約 2.9°。</summary>
        public const float CellSizeM = 0.075f;

        /// <summary>1 辺あたりの分割数の上限。頂点数を 16bit index の範囲に収める。</summary>
        public const int MaxDivisions = 48;

        /// <summary>単位立方体を割った面を組む。<paramref name="sizeM"/> は箱の実寸 (幅・高さ・奥行)。</summary>
        public static Mesh Build(Vector3 sizeM, out int cellCount)
        {
            int nx = Divisions(sizeM.x);
            int ny = Divisions(sizeM.y);
            int nz = Divisions(sizeM.z);

            var pos = new List<Vector3>();
            var nrm = new List<Vector3>();
            var cell = new List<Vector3>();
            var tris = new List<int>();

            // cross(u, v) が外向きの法線になる並び（Unity は cross(b-a, c-a) を表面と見る）。
            AddFace(pos, nrm, cell, tris, new Vector3(0.5f, -0.5f, -0.5f),
                    Vector3.up, Vector3.forward, ny, nz);                    // +X
            AddFace(pos, nrm, cell, tris, new Vector3(-0.5f, -0.5f, -0.5f),
                    Vector3.forward, Vector3.up, nz, ny);                    // -X
            AddFace(pos, nrm, cell, tris, new Vector3(-0.5f, -0.5f, 0.5f),
                    Vector3.right, Vector3.up, nx, ny);                      // +Z
            AddFace(pos, nrm, cell, tris, new Vector3(-0.5f, -0.5f, -0.5f),
                    Vector3.up, Vector3.right, ny, nx);                      // -Z
            AddFace(pos, nrm, cell, tris, new Vector3(-0.5f, 0.5f, -0.5f),
                    Vector3.forward, Vector3.right, nz, nx);                 // +Y（天面）

            cellCount = pos.Count / 4;
            var m = new Mesh { name = "SealedBoxShatterCells" };
            m.SetVertices(pos);
            m.SetNormals(nrm);
            m.SetUVs(1, cell);
            m.SetTriangles(tris, 0);
            // 破片は面の外へ飛ぶ。視錐台カリングで消えないよう箱の 3 倍を内包させる。
            m.bounds = new Bounds(Vector3.zero, new Vector3(3f, 3f, 3f));
            m.UploadMeshData(markNoLongerReadable: false);
            return m;
        }

        private static int Divisions(float lengthM) =>
            Mathf.Clamp(Mathf.RoundToInt(Mathf.Abs(lengthM) / CellSizeM), 4, MaxDivisions);

        private static void AddFace(List<Vector3> pos, List<Vector3> nrm, List<Vector3> cell,
                                    List<int> tris, Vector3 origin, Vector3 u, Vector3 v,
                                    int nu, int nv)
        {
            Vector3 n = Vector3.Cross(u, v).normalized;
            Vector3 du = u / nu;
            Vector3 dv = v / nv;
            for (int j = 0; j < nv; j++)
            {
                for (int i = 0; i < nu; i++)
                {
                    Vector3 p0 = origin + du * i + dv * j;
                    Vector3 p1 = p0 + du;
                    Vector3 p2 = p0 + dv;
                    Vector3 p3 = p0 + du + dv;
                    Vector3 c = p0 + du * 0.5f + dv * 0.5f;
                    int b = pos.Count;
                    pos.Add(p0); pos.Add(p1); pos.Add(p2); pos.Add(p3);
                    for (int k = 0; k < 4; k++) { nrm.Add(n); cell.Add(c); }
                    tris.Add(b + 0); tris.Add(b + 1); tris.Add(b + 3);
                    tris.Add(b + 0); tris.Add(b + 3); tris.Add(b + 2);
                }
            }
        }
    }
}
