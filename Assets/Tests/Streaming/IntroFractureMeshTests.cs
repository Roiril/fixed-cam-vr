#nullable enable

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace FixedCamVr.Streaming.Tests
{
    public sealed class IntroFractureMeshTests
    {
        [Test]
        public void Shards_CoverTheFractureAreaWithoutDegeneratePieces()
        {
            Mesh mesh = IntroFractureMesh.Build();
            try
            {
                Vector3[] vertices = mesh.vertices;
                int[] triangles = mesh.triangles;
                List<Vector4> pieceData = ReadUv(mesh, 1);
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
                    totalArea += area;

                    Vector4 key = pieceData[ia];
                    Assert.That(pieceData[ib], Is.EqualTo(key));
                    Assert.That(pieceData[ic], Is.EqualTo(key));
                    pieceAreas.TryGetValue(key, out double pieceArea);
                    pieceAreas[key] = pieceArea + area;
                }

                Assert.That(totalArea, Is.EqualTo(0.36d).Within(0.0002d));
                Assert.That(pieceAreas.Count, Is.EqualTo(IntroFractureMesh.LastPieceCount));
                foreach (KeyValuePair<Vector4, double> piece in pieceAreas)
                    Assert.That(piece.Value, Is.GreaterThan(0d), $"piece {piece.Key} has no area");
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void Shards_ContainEveryMacroGroupAndIrregularPolygons()
        {
            Mesh mesh = IntroFractureMesh.Build();
            try
            {
                List<Vector4> pieceData = ReadUv(mesh, 1);
                List<Vector4> macroData = ReadUv(mesh, 2);
                var macros = new HashSet<int>();
                var verticesPerPiece = new Dictionary<Vector4, int>();
                bool hasNonQuad = false;
                for (int i = 0; i < mesh.vertexCount; i++)
                {
                    macros.Add(Mathf.RoundToInt(macroData[i].w));
                    verticesPerPiece.TryGetValue(pieceData[i], out int count);
                    verticesPerPiece[pieceData[i]] = count + 1;
                }

                foreach (int count in verticesPerPiece.Values)
                    hasNonQuad |= count != 4;

                Assert.That(macros.Count, Is.EqualTo(IntroFractureMesh.MacroCount));
                for (int i = 0; i < IntroFractureMesh.MacroCount; i++)
                    Assert.That(macros.Contains(i), Is.True, $"macro {i} is absent");
                Assert.That(IntroFractureMesh.LastPieceCount, Is.InRange(1000, 2400));
                Assert.That(hasNonQuad, Is.True, "all pieces are quads; Voronoi irregularity was lost");
            }
            finally
            {
                Object.DestroyImmediate(mesh);
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
                int[] triangles = mesh.triangles;

                Assert.That(mesh.name, Is.EqualTo("IntroFractureShards"));
                Assert.That(mesh.indexFormat, Is.EqualTo(IndexFormat.UInt16));
                Assert.That(vertices.Length, Is.LessThanOrEqualTo(ushort.MaxValue));
                Assert.That(uv0.Length, Is.EqualTo(vertices.Length));
                Assert.That(uv1.Count, Is.EqualTo(vertices.Length));
                Assert.That(uv2.Count, Is.EqualTo(vertices.Length));
                Assert.That(triangles.Length % 3, Is.Zero);
                Assert.That(mesh.bounds.center, Is.EqualTo(Vector3.zero));
                Assert.That(mesh.bounds.size, Is.EqualTo(new Vector3(1.2f, 1.2f, 1.2f)));

                for (int i = 0; i < vertices.Length; i++)
                {
                    AssertFinite(vertices[i], $"vertex {i}");
                    AssertFinite(uv0[i], $"uv0 {i}");
                    AssertFinite(uv1[i], $"uv1 {i}");
                    AssertFinite(uv2[i], $"uv2 {i}");
                    Assert.That(vertices[i].z, Is.EqualTo(0f));
                    Assert.That(uv0[i], Is.EqualTo(new Vector2(vertices[i].x + 0.5f, vertices[i].y + 0.5f)));
                    Assert.That(uv1[i].z, Is.GreaterThan(0f));
                    Assert.That(uv1[i].w, Is.EqualTo(1f));
                    Assert.That(uv2[i].w, Is.InRange(0f, IntroFractureMesh.MacroCount - 1f));
                }
                foreach (int index in triangles)
                    Assert.That(index, Is.InRange(0, vertices.Length - 1));
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void MacroAttributes_AreSharedWithinEachGroup()
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
                        Assert.That(data, Is.EqualTo(expected), $"macro {macro} has inconsistent centroid or start offset");
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
                Object.DestroyImmediate(mesh);
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
            }
            finally
            {
                Random.state = original;
                if (first != null) Object.DestroyImmediate(first);
                if (second != null) Object.DestroyImmediate(second);
            }
        }

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
    }
}
