#nullable enable

using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Random = UnityEngine.Random;

namespace FixedCamVr.Streaming.Tests
{
    public sealed class IntroFractureMeshTests
    {
        [Test]
        public void Shards_FormACompleteNonDegenerateTriangulation()
        {
            Mesh mesh = IntroFractureMesh.Build();
            try
            {
                Vector3[] vertices = mesh.vertices;
                int[] triangles = mesh.triangles;
                List<Vector4> pieceData = ReadUv(mesh, 1);
                List<Vector4> surfaceData = ReadUv(mesh, 3);
                var pieceAreas = new Dictionary<Vector4, double>();
                double totalArea = 0d;
                for (int i = 0; i < triangles.Length; i += 3)
                {
                    int ia = triangles[i];
                    int ib = triangles[i + 1];
                    int ic = triangles[i + 2];
                    Vector3 a = vertices[ia];
                    Vector3 b = vertices[ib];
                    Vector3 c = vertices[ic];
                    double area = Vector3.Cross(b - a, c - a).magnitude * 0.5d;
                    Assert.That(area, Is.GreaterThan(0d), $"triangle {i / 3} has no area");
                    float surface = surfaceData[ia].x;
                    Assert.That(surfaceData[ib].x, Is.EqualTo(surface));
                    Assert.That(surfaceData[ic].x, Is.EqualTo(surface));
                    if (surface != IntroFractureMesh.FrontSurface)
                        continue;

                    totalArea += area;
                    Vector4 key = pieceData[ia];
                    Assert.That(pieceData[ib], Is.EqualTo(key));
                    Assert.That(pieceData[ic], Is.EqualTo(key));
                    pieceAreas.TryGetValue(key, out double pieceArea);
                    pieceAreas[key] = pieceArea + area;
                }

                Assert.That(totalArea, Is.EqualTo(0.36d).Within(0.0002d));
                Assert.That(pieceAreas.Count, Is.EqualTo(IntroFractureMesh.LastPieceCount));
                Assert.That(IntroFractureMesh.LastPieceCount, Is.InRange(200, 500));
                Assert.That(IntroFractureMesh.LastTrianglePieceCount + IntroFractureMesh.LastQuadPieceCount,
                    Is.EqualTo(IntroFractureMesh.LastPieceCount));
                TestContext.WriteLine(
                    $"fracture pieces: triangles={IntroFractureMesh.LastTrianglePieceCount} "
                    + $"quads={IntroFractureMesh.LastQuadPieceCount} total={IntroFractureMesh.LastPieceCount} "
                    + $"vertices={mesh.vertexCount} meshTriangles={triangles.Length / 3}");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void FrontEdges_AreSharedExactlyTwiceExceptAtTheOuterBoundary()
        {
            Mesh mesh = IntroFractureMesh.Build();
            try
            {
                List<Vector4> pieces = ReadUv(mesh, 1);
                List<Vector4> surfaces = ReadUv(mesh, 3);
                Vector3[] vertices = mesh.vertices;
                var verticesByPiece = new Dictionary<Vector4, List<Vector2>>();
                for (int i = 0; i < vertices.Length; i++)
                {
                    if (surfaces[i].x != IntroFractureMesh.FrontSurface)
                        continue;
                    if (!verticesByPiece.TryGetValue(pieces[i], out List<Vector2>? points))
                    {
                        points = new List<Vector2>(3);
                        verticesByPiece.Add(pieces[i], points);
                    }
                    points.Add(vertices[i]);
                }

                var edgeCounts = new Dictionary<EdgeKey, int>();
                foreach (List<Vector2> points in verticesByPiece.Values)
                {
                    Assert.That(points.Count, Is.EqualTo(3).Or.EqualTo(4), "a shard has an unsupported outline");
                    for (int i = 0; i < points.Count; i++)
                        AddEdge(edgeCounts, points[i], points[(i + 1) % points.Count]);
                }

                int outside = 0;
                int inside = 0;
                foreach (KeyValuePair<EdgeKey, int> edge in edgeCounts)
                {
                    if (edge.Value == 1)
                    {
                        outside++;
                        Assert.That(edge.Key.IsOuterBoundary, Is.True,
                            $"unpaired internal edge {edge.Key}");
                    }
                    else
                    {
                        inside++;
                        Assert.That(edge.Value, Is.EqualTo(2), $"edge {edge.Key} is shared by {edge.Value} shards");
                    }
                }
                Assert.That(outside, Is.GreaterThan(0));
                Assert.That(inside, Is.GreaterThan(outside));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void Shards_MixPointedTrianglesWithConvexIrregularQuads()
        {
            Mesh mesh = IntroFractureMesh.Build();
            try
            {
                List<Vector4> pieces = ReadUv(mesh, 1);
                List<Vector4> surfaces = ReadUv(mesh, 3);
                Vector3[] vertices = mesh.vertices;
                var verticesByPiece = new Dictionary<Vector4, List<Vector2>>();
                for (int i = 0; i < vertices.Length; i++)
                {
                    if (surfaces[i].x != IntroFractureMesh.FrontSurface)
                        continue;
                    if (!verticesByPiece.TryGetValue(pieces[i], out List<Vector2>? points))
                    {
                        points = new List<Vector2>(3);
                        verticesByPiece.Add(pieces[i], points);
                    }
                    points.Add(vertices[i]);
                }

                var areas = new List<double>(verticesByPiece.Count);
                int pointed = 0;
                int triangleCount = 0;
                int quadCount = 0;
                foreach (List<Vector2> points in verticesByPiece.Values)
                {
                    double twiceArea = 0d;
                    for (int i = 0; i < points.Count; i++)
                        twiceArea += Cross(points[i], points[(i + 1) % points.Count]);
                    double area = twiceArea * 0.5d;
                    Assert.That(area, Is.GreaterThan(0d));
                    areas.Add(area);
                    if (points.Count == 3)
                    {
                        triangleCount++;
                        double longestSquared = Math.Max(
                            (points[1] - points[0]).sqrMagnitude,
                            Math.Max((points[2] - points[1]).sqrMagnitude, (points[0] - points[2]).sqrMagnitude));
                        if (longestSquared / (2d * area) >= 2d)
                            pointed++;
                    }
                    else
                    {
                        quadCount++;
                        bool allAnglesNearRight = true;
                        for (int i = 0; i < points.Count; i++)
                        {
                            Vector2 a = points[(i + points.Count - 1) % points.Count];
                            Vector2 b = points[i];
                            Vector2 c = points[(i + 1) % points.Count];
                            Assert.That(Cross(c - b, a - b), Is.GreaterThan(0d), "quad is not convex");
                            double cosine = Math.Abs(Vector2.Dot(a - b, c - b))
                                            / ((a - b).magnitude * (c - b).magnitude);
                            allAnglesNearRight &= cosine <= 0.21d;
                        }
                        Assert.That(allAnglesNearRight, Is.False, "square or rectangle-like shard was accepted");
                    }
                }
                areas.Sort();

                double quadRatio = (double)quadCount / (triangleCount + quadCount);
                Assert.That(triangleCount, Is.EqualTo(IntroFractureMesh.LastTrianglePieceCount));
                Assert.That(quadCount, Is.EqualTo(IntroFractureMesh.LastQuadPieceCount));
                Assert.That(quadRatio, Is.InRange(0.25d, 0.40d));
                double lower = areas[areas.Count / 10];
                double upper = areas[areas.Count * 9 / 10];
                Assert.That(upper / lower, Is.GreaterThanOrEqualTo(8d),
                    "the 90th-percentile shard is not eight times the 10th-percentile shard");
                Assert.That(pointed, Is.GreaterThanOrEqualTo(triangleCount / 3),
                    "fewer than one third of the triangle shards are pointed or elongated");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void QuadFaces_UseThePreservedDelaunayEdgeAsTheirDiagonal()
        {
            Mesh mesh = IntroFractureMesh.Build();
            try
            {
                List<Vector4> pieces = ReadUv(mesh, 1);
                List<Vector4> surfaces = ReadUv(mesh, 3);
                Vector3[] vertices = mesh.vertices;
                int[] triangles = mesh.triangles;
                var outlines = new Dictionary<Vector4, List<Vector2>>();
                for (int i = 0; i < vertices.Length; i++)
                {
                    if (surfaces[i].x != IntroFractureMesh.FrontSurface)
                        continue;
                    if (!outlines.TryGetValue(pieces[i], out List<Vector2>? points))
                    {
                        points = new List<Vector2>(4);
                        outlines.Add(pieces[i], points);
                    }
                    points.Add(vertices[i]);
                }

                var faces = new Dictionary<Vector4, List<int[]>>();
                for (int i = 0; i < triangles.Length; i += 3)
                {
                    int first = triangles[i];
                    if (surfaces[first].x != IntroFractureMesh.FrontSurface)
                        continue;
                    Vector4 piece = pieces[first];
                    if (!faces.TryGetValue(piece, out List<int[]>? pieceFaces))
                    {
                        pieceFaces = new List<int[]>(2);
                        faces.Add(piece, pieceFaces);
                    }
                    pieceFaces.Add(new[] { first, triangles[i + 1], triangles[i + 2] });
                }

                foreach (KeyValuePair<Vector4, List<Vector2>> outline in outlines)
                {
                    if (outline.Value.Count != 4)
                        continue;
                    List<int[]> pieceFaces = faces[outline.Key];
                    Assert.That(pieceFaces.Count, Is.EqualTo(2));
                    var common = new List<int>(2);
                    foreach (int first in pieceFaces[0])
                    {
                        foreach (int second in pieceFaces[1])
                        {
                            if (first == second)
                                common.Add(first);
                        }
                    }
                    Assert.That(common.Count, Is.EqualTo(2));
                    var actual = new EdgeKey(vertices[common[0]], vertices[common[1]]);
                    var preserved = new EdgeKey(outline.Value[0], outline.Value[2]);
                    Assert.That(actual, Is.EqualTo(preserved), "quad fan changed the preserved Delaunay edge");
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void BevelDistance_OnlyMarksPolygonBoundaries_NotQuadDiagonals()
        {
            Mesh mesh = IntroFractureMesh.Build();
            try
            {
                List<Vector4> pieces = ReadUv(mesh, 1);
                List<Vector4> surfaces = ReadUv(mesh, 3);
                List<Vector4> distances = ReadUv(mesh, 4);
                Assert.That(distances.Count, Is.EqualTo(mesh.vertexCount));
                var fronts = new Dictionary<Vector4, List<int>>();
                for (int i = 0; i < mesh.vertexCount; i++)
                {
                    if (surfaces[i].x != IntroFractureMesh.FrontSurface) continue;
                    if (!fronts.TryGetValue(pieces[i], out List<int>? ids))
                    {
                        ids = new List<int>();
                        fronts.Add(pieces[i], ids);
                    }
                    ids.Add(i);
                }
                foreach (List<int> ids in fronts.Values)
                {
                    for (int edge = 0; edge < ids.Count; edge++)
                    {
                        Vector4 midpoint = (distances[ids[edge]]
                            + distances[ids[(edge + 1) % ids.Count]]) * 0.5f;
                        Assert.That(midpoint[edge], Is.LessThan(1e-6f), "outline must catch light");
                    }
                    if (ids.Count != 4) continue;
                    Vector4 diagonal = (distances[ids[0]] + distances[ids[2]]) * 0.5f;
                    for (int edge = 0; edge < 4; edge++)
                        Assert.That(diagonal[edge], Is.GreaterThan(1e-6f),
                            "an internal triangulation edge must not appear as a crack");
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void EveryPiece_HasFrontBackAndOutwardSideSurfaces()
        {
            Mesh mesh = IntroFractureMesh.Build();
            try
            {
                List<Vector4> pieces = ReadUv(mesh, 1);
                List<Vector4> surfaces = ReadUv(mesh, 3);
                Vector3[] vertices = mesh.vertices;
                Vector3[] normals = mesh.normals;
                var counts = new Dictionary<Vector4, Vector3Int>();
                for (int i = 0; i < mesh.vertexCount; i++)
                {
                    Vector3Int count = counts.TryGetValue(pieces[i], out Vector3Int found)
                        ? found : Vector3Int.zero;
                    if (surfaces[i].x == IntroFractureMesh.FrontSurface)
                    {
                        count.x++;
                        Assert.That(vertices[i].z, Is.EqualTo(-0.5f));
                        Assert.That(normals[i], Is.EqualTo(Vector3.back));
                    }
                    else if (surfaces[i].x == IntroFractureMesh.BackSurface)
                    {
                        count.y++;
                        Assert.That(vertices[i].z, Is.EqualTo(0.5f));
                        Assert.That(normals[i], Is.EqualTo(Vector3.forward));
                    }
                    else
                    {
                        count.z++;
                        Assert.That(normals[i].z, Is.EqualTo(0f));
                    }
                    counts[pieces[i]] = count;
                }

                Assert.That(counts.Count, Is.EqualTo(IntroFractureMesh.LastPieceCount));
                Assert.That(mesh.vertexCount, Is.EqualTo(IntroFractureMesh.LastVertexCount));
                foreach (KeyValuePair<Vector4, Vector3Int> piece in counts)
                {
                    Assert.That(piece.Value.x, Is.EqualTo(3).Or.EqualTo(4));
                    Assert.That(piece.Value.y, Is.EqualTo(piece.Value.x));
                    Assert.That(piece.Value.z, Is.EqualTo(piece.Value.x * 4));
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void VertexStreams_AreFiniteAndConsistent()
        {
            Mesh mesh = IntroFractureMesh.Build();
            try
            {
                Vector3[] vertices = mesh.vertices;
                Vector2[] uv0 = mesh.uv;
                List<Vector4> uv1 = ReadUv(mesh, 1);
                List<Vector4> uv2 = ReadUv(mesh, 2);
                List<Vector4> uv3 = ReadUv(mesh, 3);
                Vector3[] normals = mesh.normals;
                int[] triangles = mesh.triangles;

                Assert.That(mesh.name, Is.EqualTo("IntroFractureShards"));
                Assert.That(mesh.indexFormat, Is.EqualTo(IndexFormat.UInt32));
                Assert.That(uv0.Length, Is.EqualTo(vertices.Length));
                Assert.That(uv1.Count, Is.EqualTo(vertices.Length));
                Assert.That(uv2.Count, Is.EqualTo(vertices.Length));
                Assert.That(uv3.Count, Is.EqualTo(vertices.Length));
                Assert.That(normals.Length, Is.EqualTo(vertices.Length));
                Assert.That(triangles.Length % 3, Is.Zero);
                Assert.That(mesh.bounds.center, Is.EqualTo(Vector3.zero));
                Assert.That(mesh.bounds.size, Is.EqualTo(Vector3.one * 200f));

                for (int i = 0; i < vertices.Length; i++)
                {
                    AssertFinite(vertices[i], $"vertex {i}");
                    AssertFinite(uv0[i], $"uv0 {i}");
                    AssertFinite(uv1[i], $"uv1 {i}");
                    AssertFinite(uv2[i], $"uv2 {i}");
                    AssertFinite(uv3[i], $"uv3 {i}");
                    AssertFinite(normals[i], $"normal {i}");
                    Assert.That(Mathf.Abs(vertices[i].z), Is.EqualTo(0.5f));
                    Assert.That(uv0[i], Is.EqualTo(new Vector2(vertices[i].x + 0.5f, vertices[i].y + 0.5f)));
                    Assert.That(normals[i].sqrMagnitude, Is.EqualTo(1f).Within(1e-5f));
                    Assert.That(uv3[i].x, Is.EqualTo(IntroFractureMesh.FrontSurface)
                        .Or.EqualTo(IntroFractureMesh.BackSurface)
                        .Or.EqualTo(IntroFractureMesh.SideSurface));
                    Assert.That(uv1[i].z, Is.GreaterThan(0f));
                    Assert.That(uv1[i].w, Is.EqualTo(1f));
                    Assert.That(uv2[i].w, Is.InRange(0f, IntroFractureMesh.MacroCount - 1f));
                }
                foreach (int index in triangles)
                    Assert.That(index, Is.InRange(0, vertices.Length - 1));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void MacroAttributes_CoverEveryGroupAndUseTheFullDelayRange()
        {
            Mesh mesh = IntroFractureMesh.Build();
            try
            {
                List<Vector4> macroData = ReadUv(mesh, 2);
                var byMacro = new Dictionary<int, Vector4>();
                foreach (Vector4 data in macroData)
                {
                    int macro = Mathf.RoundToInt(data.w);
                    if (byMacro.TryGetValue(macro, out Vector4 expected))
                        Assert.That(data, Is.EqualTo(expected), $"macro {macro} has inconsistent center or start offset");
                    else
                        byMacro.Add(macro, data);
                }

                Assert.That(byMacro.Count, Is.EqualTo(IntroFractureMesh.MacroCount));
                float latest = 0f;
                float earliest = float.PositiveInfinity;
                foreach (Vector4 data in byMacro.Values)
                {
                    Assert.That(data.z, Is.InRange(0f, 0.14f));
                    latest = Mathf.Max(latest, data.z);
                    earliest = Mathf.Min(earliest, data.z);
                }
                Assert.That(latest, Is.EqualTo(0.14f).Within(1e-6f));
                Assert.That(earliest, Is.EqualTo(0f).Within(1e-6f), "first crack must align with the audio cue");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void Build_IsDeterministicAndDoesNotTouchUnityRandom()
        {
            Random.State original = Random.state;
            Mesh? first = null;
            Mesh? second = null;
            try
            {
                Random.InitState(918273);
                float expectedFirst = Random.value;
                float expectedSecond = Random.value;
                Random.InitState(918273);
                Assert.That(Random.value, Is.EqualTo(expectedFirst));

                first = IntroFractureMesh.Build();
                Assert.That(Random.value, Is.EqualTo(expectedSecond));
                second = IntroFractureMesh.Build();
                Assert.That(second.vertices, Is.EqualTo(first.vertices));
                Assert.That(second.triangles, Is.EqualTo(first.triangles));
                Assert.That(ReadUv(second, 0), Is.EqualTo(ReadUv(first, 0)));
                Assert.That(ReadUv(second, 1), Is.EqualTo(ReadUv(first, 1)));
                Assert.That(ReadUv(second, 2), Is.EqualTo(ReadUv(first, 2)));
                Assert.That(ReadUv(second, 3), Is.EqualTo(ReadUv(first, 3)));
                Assert.That(second.normals, Is.EqualTo(first.normals));
            }
            finally
            {
                Random.state = original;
                if (first != null) UnityEngine.Object.DestroyImmediate(first);
                if (second != null) UnityEngine.Object.DestroyImmediate(second);
            }
        }

        private static void AddEdge(Dictionary<EdgeKey, int> edges, Vector2 a, Vector2 b)
        {
            var edge = new EdgeKey(a, b);
            edges.TryGetValue(edge, out int count);
            edges[edge] = count + 1;
        }

        private static double Cross(Vector2 a, Vector2 b) => (double)a.x * b.y - (double)a.y * b.x;

        private static List<Vector4> ReadUv(Mesh mesh, int channel)
        {
            var values = new List<Vector4>();
            mesh.GetUVs(channel, values);
            return values;
        }

        private static void AssertFinite(Vector2 value, string label)
        {
            Assert.That(float.IsNaN(value.x) || float.IsInfinity(value.x)
                        || float.IsNaN(value.y) || float.IsInfinity(value.y),
                Is.False, label);
        }

        private static void AssertFinite(Vector3 value, string label)
        {
            Assert.That(float.IsNaN(value.x) || float.IsInfinity(value.x)
                        || float.IsNaN(value.y) || float.IsInfinity(value.y)
                        || float.IsNaN(value.z) || float.IsInfinity(value.z),
                Is.False, label);
        }

        private static void AssertFinite(Vector4 value, string label)
        {
            Assert.That(float.IsNaN(value.x) || float.IsInfinity(value.x)
                        || float.IsNaN(value.y) || float.IsInfinity(value.y)
                        || float.IsNaN(value.z) || float.IsInfinity(value.z)
                        || float.IsNaN(value.w) || float.IsInfinity(value.w),
                Is.False, label);
        }

        private readonly struct EdgeKey : IEquatable<EdgeKey>
        {
            private const long Scale = 10000000L;
            private const long Boundary = 3000000L;
            private readonly PointKey _a;
            private readonly PointKey _b;

            public EdgeKey(Vector2 a, Vector2 b)
            {
                var first = new PointKey(a);
                var second = new PointKey(b);
                if (first.CompareTo(second) <= 0)
                {
                    _a = first;
                    _b = second;
                }
                else
                {
                    _a = second;
                    _b = first;
                }
            }

            public bool IsOuterBoundary =>
                (_a.x == -Boundary && _b.x == -Boundary)
                || (_a.x == Boundary && _b.x == Boundary)
                || (_a.y == -Boundary && _b.y == -Boundary)
                || (_a.y == Boundary && _b.y == Boundary);

            public bool Equals(EdgeKey other) => _a.Equals(other._a) && _b.Equals(other._b);
            public override bool Equals(object? obj) => obj is EdgeKey other && Equals(other);
            public override int GetHashCode() => (_a.GetHashCode() * 397) ^ _b.GetHashCode();
            public override string ToString() => $"{_a}-{_b}";

            private readonly struct PointKey : IEquatable<PointKey>, IComparable<PointKey>
            {
                public readonly long x;
                public readonly long y;

                public PointKey(Vector2 point)
                {
                    x = (long)Math.Round(point.x * Scale);
                    y = (long)Math.Round(point.y * Scale);
                }

                public int CompareTo(PointKey other)
                {
                    int xOrder = x.CompareTo(other.x);
                    return xOrder != 0 ? xOrder : y.CompareTo(other.y);
                }

                public bool Equals(PointKey other) => x == other.x && y == other.y;
                public override bool Equals(object? obj) => obj is PointKey other && Equals(other);
                public override int GetHashCode() => (x.GetHashCode() * 397) ^ y.GetHashCode();
                public override string ToString() => $"({x},{y})";
            }
        }
    }
}
