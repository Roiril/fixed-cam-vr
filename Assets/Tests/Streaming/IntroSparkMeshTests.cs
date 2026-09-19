#nullable enable

using System.Collections.Generic;
using FixedCamVr.Streaming;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 光の粒（canon/LEDGER.md 0235）のメッシュを固定する。粒は CPU のパーティクルではなく
    /// 時計から決定的に動く静的メッシュなので、<b>同じ版は必ず同じ粒を出す</b>。
    /// 頂点属性の並びは <c>Assets/Resources/IntroSpark.shader</c> と対（片方だけ直すと黙って食い違う）。
    /// </summary>
    public sealed class IntroSparkMeshTests
    {
        private static IReadOnlyList<IntroFractureMesh.PieceInfo> Pieces()
        {
            Mesh fracture = IntroFractureMesh.Build();
            try
            {
                return IntroFractureMesh.LastPieces;
            }
            finally
            {
                Object.DestroyImmediate(fracture);
            }
        }

        [Test]
        public void Mesh_HasOneQuadPerSpark()
        {
            IReadOnlyList<IntroFractureMesh.PieceInfo> pieces = Pieces();
            Mesh mesh = IntroSparkMesh.Build(pieces);
            try
            {
                Assert.That(IntroSparkMesh.LastFlowCount, Is.EqualTo(IntroSparkMesh.FlowSparkCount),
                    "流れる粒の総数は固定（頂点数の上限を決めている）");
                Assert.That(IntroSparkMesh.LastSparkCount,
                    Is.EqualTo(IntroSparkMesh.FlowSparkCount + IntroSparkMesh.HeroSparkCount));
                Assert.That(mesh.vertexCount, Is.EqualTo(IntroSparkMesh.LastSparkCount * 4),
                    "1 粒 = 四角 1 枚（4 頂点）");
                Assert.That(mesh.triangles.Length, Is.EqualTo(IntroSparkMesh.LastSparkCount * 6),
                    "1 粒 = 2 三角");
                Assert.That(mesh.indexFormat, Is.EqualTo(UnityEngine.Rendering.IndexFormat.UInt16),
                    "2072 頂点は UInt16 で足りる");

                var corners = new List<Vector2>();
                mesh.GetUVs(0, corners);
                Assert.That(corners.Count, Is.EqualTo(mesh.vertexCount));
                foreach (Vector2 corner in corners)
                {
                    Assert.That(Mathf.Abs(corner.x), Is.EqualTo(1f).Within(1e-5f));
                    Assert.That(Mathf.Abs(corner.y), Is.EqualTo(1f).Within(1e-5f));
                }

                var seeds = new List<Vector4>();
                mesh.GetUVs(1, seeds);
                int heroVertices = 0;
                for (int i = 0; i < seeds.Count; i++)
                {
                    if (seeds[i].z < 0.5f) continue;
                    heroVertices++;
                    Assert.That(seeds[i].w, Is.InRange(0f, IntroSparkMesh.HeroSparkCount - 1f),
                        "代表の粒の番号が IntroSpark.hlsl の HeroSparkCount を越えている");
                }
                Assert.That(heroVertices, Is.EqualTo(IntroSparkMesh.HeroSparkCount * 4));
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void Mesh_IsDeterministic()
        {
            IReadOnlyList<IntroFractureMesh.PieceInfo> pieces = Pieces();
            Mesh first = IntroSparkMesh.Build(pieces);
            Mesh second = IntroSparkMesh.Build(pieces);
            try
            {
                Assert.That(second.vertices, Is.EqualTo(first.vertices),
                    "同じ版で違う粒が出ている（乱数の種が固定されていない）");
                var firstSeeds = new List<Vector4>();
                var secondSeeds = new List<Vector4>();
                first.GetUVs(1, firstSeeds);
                second.GetUVs(1, secondSeeds);
                Assert.That(secondSeeds, Is.EqualTo(firstSeeds));
                var firstLook = new List<Vector4>();
                var secondLook = new List<Vector4>();
                first.GetUVs(3, firstLook);
                second.GetUVs(3, secondLook);
                Assert.That(secondLook, Is.EqualTo(firstLook));
            }
            finally
            {
                Object.DestroyImmediate(first);
                Object.DestroyImmediate(second);
            }
        }

        [Test]
        public void FlowSparks_AreBornOnTheOutlineOfTheirPiece()
        {
            IReadOnlyList<IntroFractureMesh.PieceInfo> pieces = Pieces();
            Mesh mesh = IntroSparkMesh.Build(pieces);
            try
            {
                Vector3[] vertices = mesh.vertices;
                var seeds = new List<Vector4>();
                var pieceData = new List<Vector4>();
                mesh.GetUVs(1, seeds);
                mesh.GetUVs(2, pieceData);
                int flowVertices = 0;
                for (int i = 0; i < vertices.Length; i++)
                {
                    if (seeds[i].z > 0.5f) continue;   // 代表の粒は放出点を持たない
                    int pieceIndex = Mathf.RoundToInt(pieceData[i].y);
                    Assert.That(pieceIndex, Is.InRange(0, pieces.Count - 1));
                    IntroFractureMesh.PieceInfo piece = pieces[pieceIndex];
                    Assert.That(vertices[i].z, Is.EqualTo(0f).Within(1e-6f));
                    float distance = DistanceToOutline(piece.outline, vertices[i]);
                    Assert.That(distance, Is.LessThan(1e-4f),
                        $"頂点 {i} の放出点が片 {pieceIndex} の外周の辺の上に無い（{distance:F6} m）");
                    Assert.That(pieceData[i].x,
                        Is.EqualTo(piece.macroOrder).Within(1e-5f), "大区分の破断の順が片と食い違う");
                    Assert.That(pieceData[i].w, Is.EqualTo(piece.size).Within(1e-5f));
                    Assert.That(pieceData[i].z, Is.InRange(0f, 1f));
                    flowVertices++;
                }
                Assert.That(flowVertices, Is.EqualTo(IntroSparkMesh.FlowSparkCount * 4));
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        /// <summary>
        /// 割れた片は 1 枚残らず粒を出す（粒は「破片と一緒に飛ぶ」もの）。
        /// 総数は 512 で固定で、余りは sqrt(面積) に比例するので大きい片ほど多い。
        /// </summary>
        [Test]
        public void EveryPiece_EmitsAtLeastOneSpark()
        {
            IReadOnlyList<IntroFractureMesh.PieceInfo> pieces = Pieces();
            Mesh mesh = IntroSparkMesh.Build(pieces);
            try
            {
                var seeds = new List<Vector4>();
                var pieceData = new List<Vector4>();
                mesh.GetUVs(1, seeds);
                mesh.GetUVs(2, pieceData);
                var counts = new Dictionary<int, int>();
                for (int i = 0; i < seeds.Count; i += 4)
                {
                    if (seeds[i].z > 0.5f) continue;
                    int pieceIndex = Mathf.RoundToInt(pieceData[i].y);
                    counts.TryGetValue(pieceIndex, out int count);
                    counts[pieceIndex] = count + 1;
                }
                Assert.That(counts.Count, Is.EqualTo(IntroSparkMesh.LastEmittingPieceCount));
                Assert.That(counts.Count, Is.EqualTo(pieces.Count),
                    "粒を出していない片がある（破片と一緒に飛ぶ粒なので、割れた片は全部が出す）");
                int total = 0;
                foreach (KeyValuePair<int, int> entry in counts)
                {
                    Assert.That(entry.Value, Is.GreaterThanOrEqualTo(IntroSparkMesh.MinSparksPerPiece),
                        $"片 {entry.Key} が {entry.Value} 本しか出していない");
                    total += entry.Value;
                }
                Assert.That(total, Is.EqualTo(IntroSparkMesh.FlowSparkCount),
                    "流れる粒の合計が 512 でない（頂点数の上限を決めている）");

                // 大きい片ほど多い（sqrt(面積) に比例配分）。
                int largest = -1;
                int smallest = -1;
                foreach (KeyValuePair<int, int> entry in counts)
                {
                    if (largest < 0 || pieces[entry.Key].size > pieces[largest].size) largest = entry.Key;
                    if (smallest < 0 || pieces[entry.Key].size < pieces[smallest].size) smallest = entry.Key;
                }
                Assert.That(counts[largest], Is.GreaterThanOrEqualTo(counts[smallest]));
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void DeathTimes_StayInsideTheConvergenceWindow()
        {
            IReadOnlyList<IntroFractureMesh.PieceInfo> pieces = Pieces();
            Mesh mesh = IntroSparkMesh.Build(pieces);
            try
            {
                var look = new List<Vector4>();
                mesh.GetUVs(3, look);
                Assert.That(look.Count, Is.EqualTo(mesh.vertexCount));
                for (int i = 0; i < look.Count; i++)
                {
                    Assert.That(look[i].z, Is.InRange(0.62f, 0.80f),
                        $"頂点 {i} の消える時刻が集結の窓の外（集結 .56〜.90 の内側で消えること）");
                    Assert.That(look[i].x == 0f || look[i].x == 1f, Is.True,
                        $"頂点 {i} の star は 0 か 1（実値 {look[i].x}）");
                    Assert.That(look[i].y, Is.InRange(0.6f, 1.8f));
                    Assert.That(look[i].w, Is.InRange(0f, 1f));
                }
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        private static float DistanceToOutline(Vector2[] outline, Vector2 point)
        {
            float best = float.PositiveInfinity;
            for (int i = 0; i < outline.Length; i++)
            {
                Vector2 a = outline[i];
                Vector2 b = outline[(i + 1) % outline.Length];
                Vector2 edge = b - a;
                float lengthSquared = edge.sqrMagnitude;
                float t = lengthSquared > 0f
                    ? Mathf.Clamp01(Vector2.Dot(point - a, edge) / lengthSquared) : 0f;
                best = Mathf.Min(best, Vector2.Distance(point, a + edge * t));
            }
            return best;
        }
    }
}
