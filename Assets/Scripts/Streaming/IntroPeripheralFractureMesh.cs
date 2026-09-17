#nullable enable

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 撮影画角の外を補う粗い球殻破片。立方体の各面を同じ 2x2 境界で分けて球へ投影するため、
    /// 頭を後ろへ向けても継ぎ目のない閉じた殻になる。
    /// </summary>
    public static class IntroPeripheralFractureMesh
    {
        public const float Radius = 1.6f;
        private const int Seed = 20260918;

        public static int LastPieceCount { get; private set; }
        public static int LastTrianglePieceCount { get; private set; }
        public static int LastQuadPieceCount { get; private set; }

        public static Mesh Build()
        {
            var random = new System.Random(Seed);
            var positions = new List<Vector3>(900);
            var patchUv = new List<Vector2>(900);
            var pieceData = new List<Vector4>(900);
            var timingData = new List<Vector4>(900);
            var edgeDistances = new List<Vector4>(900);
            var normals = new List<Vector3>(900);
            var triangles = new List<int>(2880);
            int pieceIndex = 0;
            int triangleCount = 0;
            int quadCount = 0;

            for (int face = 0; face < 6; face++)
            {
                float centerX = Mathf.Lerp(-0.18f, 0.18f, (float)random.NextDouble());
                float centerY = Mathf.Lerp(-0.18f, 0.18f, (float)random.NextDouble());
                int fanCell = (face + 2) & 3;

                for (int y = 0; y < 2; y++)
                {
                    for (int x = 0; x < 2; x++)
                    {
                        int cell = y * 2 + x;
                        Vector3 a = GridPoint(face, x, y, centerX, centerY);
                        Vector3 b = GridPoint(face, x + 1, y, centerX, centerY);
                        Vector3 c = GridPoint(face, x + 1, y + 1, centerX, centerY);
                        Vector3 d = GridPoint(face, x, y + 1, centerX, centerY);
                        if (cell == fanCell)
                        {
                            float jx = Mathf.Lerp(0.38f, 0.62f, (float)random.NextDouble());
                            float jy = Mathf.Lerp(0.38f, 0.62f, (float)random.NextDouble());
                            Vector3 center = ((a * (1f - jx) + b * jx) * (1f - jy)
                                            + (d * (1f - jx) + c * jx) * jy).normalized * Radius;
                            AddPiece(new[] { a, b, center }, face, pieceIndex++, random,
                                positions, patchUv, pieceData, timingData, edgeDistances, normals, triangles);
                            AddPiece(new[] { b, c, center }, face, pieceIndex++, random,
                                positions, patchUv, pieceData, timingData, edgeDistances, normals, triangles);
                            AddPiece(new[] { c, d, center }, face, pieceIndex++, random,
                                positions, patchUv, pieceData, timingData, edgeDistances, normals, triangles);
                            AddPiece(new[] { d, a, center }, face, pieceIndex++, random,
                                positions, patchUv, pieceData, timingData, edgeDistances, normals, triangles);
                            triangleCount += 4;
                        }
                        else
                        {
                            bool slash = ((face + cell) & 1) == 0;
                            Vector3[] first = slash ? new[] { a, b, c } : new[] { a, b, d };
                            Vector3[] second = slash ? new[] { a, c, d } : new[] { b, c, d };
                            AddPiece(first, face, pieceIndex++, random,
                                positions, patchUv, pieceData, timingData, edgeDistances, normals, triangles);
                            AddPiece(second, face, pieceIndex++, random,
                                positions, patchUv, pieceData, timingData, edgeDistances, normals, triangles);
                            triangleCount += 2;
                        }
                    }
                }
            }

            LastPieceCount = pieceIndex;
            LastTrianglePieceCount = triangleCount;
            LastQuadPieceCount = quadCount;
            var mesh = new Mesh
            {
                name = "IntroPeripheralFractureShell",
                indexFormat = IndexFormat.UInt16,
            };
            mesh.SetVertices(positions);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, patchUv);
            mesh.SetUVs(1, pieceData);
            mesh.SetUVs(2, timingData);
            mesh.SetUVs(3, edgeDistances);
            mesh.SetTriangles(triangles, 0);
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 200f);
            mesh.UploadMeshData(markNoLongerReadable: false);
            return mesh;
        }

        private static void AddPiece(
            Vector3[] polygon,
            int face,
            int pieceIndex,
            System.Random random,
            List<Vector3> positions,
            List<Vector2> patchUv,
            List<Vector4> pieceData,
            List<Vector4> timingData,
            List<Vector4> edgeDistances,
            List<Vector3> normals,
            List<int> triangles)
        {
            Vector3 center = Vector3.zero;
            for (int i = 0; i < polygon.Length; i++) center += polygon[i];
            center = center.normalized * Radius;
            float breakOffset = (float)random.NextDouble();
            float patchX = Mathf.Lerp(0.18f, 0.82f, (float)random.NextDouble());
            float patchY = Mathf.Lerp(0.18f, 0.82f, (float)random.NextDouble());
            float route = (float)random.NextDouble();
            float spin = (float)random.NextDouble();
            Vector3 tangent = Vector3.Cross(Mathf.Abs(center.y) < Radius * 0.85f ? Vector3.up : Vector3.right,
                center).normalized;
            Vector3 bitangent = Vector3.Cross(center.normalized, tangent);
            float extent = 0f;
            for (int i = 0; i < polygon.Length; i++)
                extent = Mathf.Max(extent, Mathf.Abs(Vector3.Dot(polygon[i] - center, tangent)),
                    Mathf.Abs(Vector3.Dot(polygon[i] - center, bitangent)));
            extent = Mathf.Max(extent, 1e-4f);

            if (polygon.Length != 3)
                throw new InvalidOperationException("Peripheral fracture pieces must remain triangles.");

            const int divisions = 4;
            var grid = new int[divisions + 1, divisions + 1];
            for (int row = 0; row <= divisions; row++)
            {
                for (int column = 0; column <= divisions - row; column++)
                {
                    float weightB = column / (float)divisions;
                    float weightC = row / (float)divisions;
                    float weightA = 1f - weightB - weightC;
                    Vector3 point = polygon[0] * weightA + polygon[1] * weightB + polygon[2] * weightC;
                    Vector3 delta = point - center;
                    grid[row, column] = positions.Count;
                    positions.Add(point);
                    patchUv.Add(new Vector2(
                        Vector3.Dot(delta, tangent) / (2f * extent) + 0.5f,
                        Vector3.Dot(delta, bitangent) / (2f * extent) + 0.5f));
                    pieceData.Add(new Vector4(center.x, center.y, center.z, pieceIndex));
                    timingData.Add(new Vector4(
                        breakOffset, patchX, patchY, route + face * 0.01f + spin * 0.001f));
                    normals.Add(-point.normalized);

                    Vector4 distances = Vector4.one * 1000f;
                    for (int edge = 0; edge < polygon.Length; edge++)
                    {
                        Vector3 edgeStart = polygon[edge];
                        Vector3 edgeVector = polygon[(edge + 1) % polygon.Length] - edgeStart;
                        float length = edgeVector.magnitude;
                        if (length <= 1e-5f)
                            throw new InvalidOperationException("Peripheral fracture piece contains an empty edge.");
                        Vector3 inward = Vector3.Cross(center.normalized, edgeVector / length);
                        if (Vector3.Dot(center - edgeStart, inward) < 0f) inward = -inward;
                        distances[edge] = Mathf.Max(0f, Vector3.Dot(point - edgeStart, inward));
                    }
                    edgeDistances.Add(distances);
                }
            }

            for (int row = 0; row < divisions; row++)
            {
                for (int column = 0; column < divisions - row; column++)
                {
                    int a = grid[row, column];
                    int b = grid[row, column + 1];
                    int c = grid[row + 1, column];
                    triangles.Add(a);
                    triangles.Add(b);
                    triangles.Add(c);
                    if (column < divisions - row - 1)
                    {
                        int d = grid[row + 1, column + 1];
                        triangles.Add(b);
                        triangles.Add(d);
                        triangles.Add(c);
                    }
                }
            }
        }

        private static Vector3 OnSphere(int face, float u, float v)
        {
            Vector3 cube = face switch
            {
                0 => new Vector3(1f, v, -u),
                1 => new Vector3(-1f, v, u),
                2 => new Vector3(u, 1f, -v),
                3 => new Vector3(u, -1f, v),
                4 => new Vector3(u, v, 1f),
                _ => new Vector3(-u, v, -1f),
            };
            return cube.normalized * Radius;
        }

        private static Vector3 GridPoint(int face, int x, int y, float centerX, float centerY)
        {
            float u = x - 1f;
            float v = y - 1f;
            // 共有境界は必ず -1/0/1 の同じ点にする。揺らすのは各面の内部交点だけ。
            if (x == 1 && y == 1)
            {
                u = centerX;
                v = centerY;
            }
            return OnSphere(face, u, v);
        }
    }
}
