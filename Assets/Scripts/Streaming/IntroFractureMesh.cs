#nullable enable

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 導入で覆いを割る大小の三角形片と四角形片を組む。角度空間の Delaunay 分割を全片で共有し、
    /// 隣接三角形の一部を凸な四角形へまとめる。
    /// </summary>
    public static class IntroFractureMesh
    {
        public const int MacroCount = 24;
        public const float HalfExtentLocal = 0.30f;
        public const float FrontSurface = 0f;
        public const float BackSurface = 1f;
        public const float SideSurface = 2f;

        private const int MacroColumns = 6;
        private const int MacroRows = 4;
        private const int Seed = 20260913;
        private const double MinArea = 1e-12;
        private const double DuplicateDistanceSquared = 1e-16;

        public static int LastPieceCount { get; private set; }
        public static int LastTrianglePieceCount { get; private set; }
        public static int LastQuadPieceCount { get; private set; }
        public static int LastVertexCount { get; private set; }

        public static Mesh Build()
        {
            var random = new System.Random(Seed);
            Point[] macroSites = BuildGridSites(MacroColumns, MacroRows, 0.34d, random);
            List<Point> points = BuildFracturePoints(macroSites, random);
            List<Triangle> triangles = Triangulate(points);
            List<Piece> shards = BuildPieces(points, triangles, macroSites, random);
            MacroRegion[] macros = BuildMacroRegions(macroSites);

            var positions = new List<Vector3>(shards.Count * 20);
            var uv0 = new List<Vector2>(shards.Count * 20);
            var uv1 = new List<Vector4>(shards.Count * 20);
            var uv2 = new List<Vector4>(shards.Count * 20);
            var uv3 = new List<Vector4>(shards.Count * 20);
            var edgeDistances = new List<Vector4>(shards.Count * 20);
            var normals = new List<Vector3>(shards.Count * 20);
            var meshTriangles = new List<int>(shards.Count * 28);

            int trianglePieceCount = 0;
            int quadPieceCount = 0;
            foreach (Piece shard in shards)
            {
                var angular = new List<Point>(shard.vertices.Length);
                var local = new List<Vector2>(shard.vertices.Length);
                foreach (int vertex in shard.vertices)
                {
                    angular.Add(points[vertex]);
                    local.Add(ToLocal(points[vertex]));
                }
                Point angularCentroid = Centroid(angular);
                int macroIndex = FindNearestMacro(angularCentroid, macroSites);
                if (!TryMeasure(local, out float area, out Vector2 centroid))
                    throw new InvalidOperationException("Fracture shard became degenerate after projection.");

                int firstVertex = positions.Count;
                AddPiece(positions, uv0, uv1, uv2, uv3, normals, meshTriangles, local, centroid, area,
                    macros[macroIndex], macroIndex);
                // 各外周辺までの符号付き距離。四角片の内部対角線を光らせない。
                // 距離はアフィンなので、面の中でも頂点からの補間で正確に復元できる。
                for (int vertex = firstVertex; vertex < positions.Count; vertex++)
                {
                    Vector2 point = positions[vertex];
                    Vector4 distances = Vector4.one;
                    for (int edge = 0; edge < local.Count; edge++)
                    {
                        Vector2 a = local[edge];
                        Vector2 direction = local[(edge + 1) % local.Count] - a;
                        Vector2 delta = point - a;
                        distances[edge] = Mathf.Max(0f,
                            (direction.x * delta.y - direction.y * delta.x) / direction.magnitude);
                    }
                    edgeDistances.Add(distances);
                }
                if (shard.vertices.Length == 3) trianglePieceCount++;
                else quadPieceCount++;
            }

            LastPieceCount = shards.Count;
            LastTrianglePieceCount = trianglePieceCount;
            LastQuadPieceCount = quadPieceCount;
            LastVertexCount = positions.Count;
            var mesh = new Mesh
            {
                name = "IntroFractureShards",
                indexFormat = IndexFormat.UInt32,
            };
            mesh.SetVertices(positions);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uv0);
            mesh.SetUVs(1, uv1);
            mesh.SetUVs(2, uv2);
            mesh.SetUVs(3, uv3);
            mesh.SetUVs(4, edgeDistances);
            mesh.SetTriangles(meshTriangles, 0);
            // 頂点シェーダで撮影時の頭位置から現在のスクリーンまで運ぶ。
            // 元の平面だけから求めた bounds では、頭を動かした瞬間に全破片がカリングされる。
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 200f);
            mesh.UploadMeshData(markNoLongerReadable: false);
            return mesh;
        }

        private static List<Point> BuildFracturePoints(Point[] macroSites, System.Random random)
        {
            var points = new List<Point>(220);
            AddPoint(points, new Point(-1d, -1d));
            AddPoint(points, new Point(1d, -1d));
            AddPoint(points, new Point(1d, 1d));
            AddPoint(points, new Point(-1d, 1d));

            double[] horizontal = { -0.91d, -0.78d, -0.59d, -0.34d, -0.08d, 0.13d, 0.47d, 0.72d, 0.93d };
            double[] vertical = { -0.88d, -0.63d, -0.29d, 0.04d, 0.38d, 0.79d };
            foreach (double x in horizontal)
            {
                AddPoint(points, new Point(x, -1d));
                AddPoint(points, new Point(x, 1d));
            }
            foreach (double y in vertical)
            {
                AddPoint(points, new Point(-1d, y));
                AddPoint(points, new Point(1d, y));
            }

            foreach (Point site in BuildGridSites(9, 7, 0.42d, random))
                AddPoint(points, site);

            Point[] clusterCenters =
            {
                new Point(-0.64d, 0.52d),
                new Point(0.53d, 0.58d),
                new Point(-0.48d, -0.51d),
                new Point(0.49d, -0.43d),
                new Point(0.03d, 0.02d),
            };
            double[] clusterAngles = { 0.22d, -0.63d, 0.87d, 0.38d, -0.91d };
            for (int cluster = 0; cluster < clusterCenters.Length; cluster++)
                AddCluster(points, clusterCenters[cluster], clusterAngles[cluster], random);

            foreach (Point site in macroSites)
                AddPoint(points, site);
            return points;
        }

        private static void AddCluster(List<Point> points, Point center, double angle, System.Random random)
        {
            var along = new Point(Math.Cos(angle), Math.Sin(angle));
            var across = new Point(-along.y, along.x);
            for (int i = 0; i < 18; i++)
            {
                double longitudinal = (random.NextDouble() * 2d - 1d) * 0.16d;
                double lateral = (random.NextDouble() * 2d - 1d) * 0.022d;
                AddPoint(points, center + along * longitudinal + across * lateral);
            }
        }

        private static void AddPoint(List<Point> points, Point point)
        {
            for (int i = 0; i < points.Count; i++)
            {
                if (DistanceSquared(points[i], point) < DuplicateDistanceSquared)
                    return;
            }
            points.Add(point);
        }

        private static Point[] BuildGridSites(int columns, int rows, double jitter, System.Random random)
        {
            var sites = new Point[columns * rows];
            double width = 2d / columns;
            double height = 2d / rows;
            for (int y = 0; y < rows; y++)
            {
                for (int x = 0; x < columns; x++)
                {
                    double centerX = -1d + (x + 0.5d) * width;
                    double centerY = -1d + (y + 0.5d) * height;
                    double offsetX = (random.NextDouble() * 2d - 1d) * width * jitter;
                    double offsetY = (random.NextDouble() * 2d - 1d) * height * jitter;
                    sites[y * columns + x] = new Point(centerX + offsetX, centerY + offsetY);
                }
            }
            return sites;
        }

        private static List<Triangle> Triangulate(List<Point> points)
        {
            int pointCount = points.Count;
            var working = new List<Point>(points)
            {
                new Point(-16d, -8d),
                new Point(16d, -8d),
                new Point(0d, 16d),
            };
            var triangles = new List<Triangle> { new Triangle(pointCount, pointCount + 1, pointCount + 2) };

            for (int pointIndex = 0; pointIndex < pointCount; pointIndex++)
            {
                var boundary = new List<Edge>();
                for (int triangleIndex = triangles.Count - 1; triangleIndex >= 0; triangleIndex--)
                {
                    Triangle triangle = triangles[triangleIndex];
                    if (!CircumcircleContains(working[triangle.a], working[triangle.b], working[triangle.c],
                            working[pointIndex]))
                        continue;

                    ToggleBoundary(boundary, new Edge(triangle.a, triangle.b));
                    ToggleBoundary(boundary, new Edge(triangle.b, triangle.c));
                    ToggleBoundary(boundary, new Edge(triangle.c, triangle.a));
                    triangles.RemoveAt(triangleIndex);
                }

                foreach (Edge edge in boundary)
                {
                    double cross = Cross(working[edge.b] - working[edge.a], working[pointIndex] - working[edge.a]);
                    if (Math.Abs(cross) <= MinArea)
                        continue;
                    triangles.Add(cross > 0d
                        ? new Triangle(edge.a, edge.b, pointIndex)
                        : new Triangle(edge.b, edge.a, pointIndex));
                }
            }

            triangles.RemoveAll(triangle => triangle.a >= pointCount || triangle.b >= pointCount || triangle.c >= pointCount);
            return triangles;
        }

        private static List<Piece> BuildPieces(
            List<Point> points,
            List<Triangle> triangles,
            Point[] macroSites,
            System.Random random)
        {
            var owners = new Dictionary<Edge, int>();
            var candidates = new List<MergeCandidate>(triangles.Count);
            for (int triangleIndex = 0; triangleIndex < triangles.Count; triangleIndex++)
            {
                Triangle triangle = triangles[triangleIndex];
                AddMergeCandidate(owners, candidates, new Edge(triangle.a, triangle.b), triangleIndex);
                AddMergeCandidate(owners, candidates, new Edge(triangle.b, triangle.c), triangleIndex);
                AddMergeCandidate(owners, candidates, new Edge(triangle.c, triangle.a), triangleIndex);
            }
            for (int i = candidates.Count - 1; i > 0; i--)
            {
                int swap = random.Next(i + 1);
                MergeCandidate candidate = candidates[i];
                candidates[i] = candidates[swap];
                candidates[swap] = candidate;
            }

            int targetQuadCount = triangles.Count / 4;
            int quadCount = 0;
            var consumed = new bool[triangles.Count];
            var mergedAt = new Piece[triangles.Count];
            var hasMergedAt = new bool[triangles.Count];
            foreach (MergeCandidate candidate in candidates)
            {
                if (quadCount >= targetQuadCount)
                    break;
                if (consumed[candidate.firstTriangle] || consumed[candidate.secondTriangle])
                    continue;

                Triangle first = triangles[candidate.firstTriangle];
                Triangle second = triangles[candidate.secondTriangle];
                if (FindNearestMacro(TriangleCentroid(first, points), macroSites)
                    != FindNearestMacro(TriangleCentroid(second, points), macroSites))
                    continue;
                if (!TryBuildQuad(first, second, candidate.edge, points, out Piece quad))
                    continue;

                consumed[candidate.firstTriangle] = true;
                consumed[candidate.secondTriangle] = true;
                int outputIndex = Math.Min(candidate.firstTriangle, candidate.secondTriangle);
                mergedAt[outputIndex] = quad;
                hasMergedAt[outputIndex] = true;
                quadCount++;
            }
            if (quadCount < targetQuadCount)
                throw new InvalidOperationException($"Only {quadCount} valid fracture quads were available; expected {targetQuadCount}.");

            var pieces = new List<Piece>(triangles.Count - quadCount);
            for (int i = 0; i < triangles.Count; i++)
            {
                if (hasMergedAt[i])
                    pieces.Add(mergedAt[i]);
                else if (!consumed[i])
                    pieces.Add(new Piece(triangles[i].a, triangles[i].b, triangles[i].c));
            }
            return pieces;
        }

        private static void AddMergeCandidate(
            Dictionary<Edge, int> owners,
            List<MergeCandidate> candidates,
            Edge edge,
            int triangleIndex)
        {
            if (owners.TryGetValue(edge, out int owner))
                candidates.Add(new MergeCandidate(owner, triangleIndex, edge));
            else
                owners.Add(edge, triangleIndex);
        }

        private static bool TryBuildQuad(
            Triangle first,
            Triangle second,
            Edge shared,
            List<Point> points,
            out Piece quad)
        {
            int firstOpposite = OppositeVertex(first, shared);
            int secondOpposite = OppositeVertex(second, shared);
            int[] vertices = { shared.a, firstOpposite, shared.b, secondOpposite };
            var angular = new List<Point>(4)
            {
                points[vertices[0]], points[vertices[1]], points[vertices[2]], points[vertices[3]],
            };
            if (TwiceArea(angular) < 0d)
            {
                vertices[1] = secondOpposite;
                vertices[3] = firstOpposite;
                angular[1] = points[vertices[1]];
                angular[3] = points[vertices[3]];
            }

            if (!IsConvex(angular) || IsRectangleLike(angular))
            {
                quad = default;
                return false;
            }

            var local = new List<Point>(4);
            foreach (int vertex in vertices)
            {
                Vector2 point = ToLocal(points[vertex]);
                local.Add(new Point(point.x, point.y));
            }
            if (!IsConvex(local) || IsRectangleLike(local))
            {
                quad = default;
                return false;
            }

            // 0-2 は元の Delaunay 共有辺。AddPiece の fan も同じ対角線を使う。
            quad = new Piece(vertices);
            return true;
        }

        private static int OppositeVertex(Triangle triangle, Edge edge)
        {
            if (triangle.a != edge.a && triangle.a != edge.b) return triangle.a;
            if (triangle.b != edge.a && triangle.b != edge.b) return triangle.b;
            return triangle.c;
        }

        private static Point TriangleCentroid(Triangle triangle, List<Point> points) =>
            (points[triangle.a] + points[triangle.b] + points[triangle.c]) * (1d / 3d);

        private static bool IsConvex(List<Point> polygon)
        {
            for (int i = 0; i < polygon.Count; i++)
            {
                Point a = polygon[i];
                Point b = polygon[(i + 1) % polygon.Count];
                Point c = polygon[(i + 2) % polygon.Count];
                if (Cross(b - a, c - b) <= MinArea)
                    return false;
            }
            return true;
        }

        private static bool IsRectangleLike(List<Point> polygon)
        {
            const double maxRightAngleCosine = 0.21d;
            for (int i = 0; i < polygon.Count; i++)
            {
                Point incoming = polygon[(i + polygon.Count - 1) % polygon.Count] - polygon[i];
                Point outgoing = polygon[(i + 1) % polygon.Count] - polygon[i];
                double cosine = Math.Abs(Dot(incoming, outgoing))
                                / Math.Sqrt(Dot(incoming, incoming) * Dot(outgoing, outgoing));
                if (cosine > maxRightAngleCosine)
                    return false;
            }
            return true;
        }

        private static double TwiceArea(List<Point> polygon)
        {
            double twiceArea = 0d;
            for (int i = 0; i < polygon.Count; i++)
                twiceArea += Cross(polygon[i], polygon[(i + 1) % polygon.Count]);
            return twiceArea;
        }

        private static void ToggleBoundary(List<Edge> boundary, Edge edge)
        {
            int existing = boundary.IndexOf(edge);
            if (existing >= 0)
                boundary.RemoveAt(existing);
            else
                boundary.Add(edge);
        }

        private static bool CircumcircleContains(Point a, Point b, Point c, Point point)
        {
            double ax = a.x - point.x;
            double ay = a.y - point.y;
            double bx = b.x - point.x;
            double by = b.y - point.y;
            double cx = c.x - point.x;
            double cy = c.y - point.y;
            double determinant = (ax * ax + ay * ay) * (bx * cy - by * cx)
                                 - (bx * bx + by * by) * (ax * cy - ay * cx)
                                 + (cx * cx + cy * cy) * (ax * by - ay * bx);
            return determinant > 1e-13;
        }

        private static MacroRegion[] BuildMacroRegions(Point[] sites)
        {
            var centers = new Vector2[MacroCount];
            var distances = new float[MacroCount];
            Vector2 start = ToLocal(new Point(-0.12d, 0.08d));
            float maxDistance = 0f;
            float minDistance = float.PositiveInfinity;
            for (int i = 0; i < sites.Length; i++)
            {
                centers[i] = ToLocal(sites[i]);
                distances[i] = Vector2.Distance(centers[i], start);
                maxDistance = Mathf.Max(maxDistance, distances[i]);
                minDistance = Mathf.Min(minDistance, distances[i]);
            }

            var regions = new MacroRegion[MacroCount];
            for (int i = 0; i < regions.Length; i++)
                regions[i] = new MacroRegion(
                    centers[i], (distances[i] - minDistance) / (maxDistance - minDistance) * 0.14f);
            return regions;
        }

        private static int FindNearestMacro(Point point, Point[] macroSites)
        {
            int nearest = 0;
            double nearestDistance = double.PositiveInfinity;
            for (int i = 0; i < macroSites.Length; i++)
            {
                double distance = DistanceSquared(point, macroSites[i]);
                if (distance < nearestDistance)
                {
                    nearest = i;
                    nearestDistance = distance;
                }
            }
            return nearest;
        }

        private static void AddPiece(
            List<Vector3> positions,
            List<Vector2> uv0,
            List<Vector4> uv1,
            List<Vector4> uv2,
            List<Vector4> uv3,
            List<Vector3> normals,
            List<int> triangles,
            List<Vector2> polygon,
            Vector2 centroid,
            float area,
            MacroRegion macro,
            int macroIndex)
        {
            var pieceData = new Vector4(centroid.x, centroid.y, Mathf.Sqrt(area), 1f);
            var macroData = new Vector4(macro.localCenter.x, macro.localCenter.y,
                macro.startOffset, macroIndex);
            int front = positions.Count;
            for (int i = 0; i < polygon.Count; i++)
            {
                Vector2 point = polygon[i];
                AddVertex(positions, uv0, uv1, uv2, uv3, normals,
                    new Vector3(point.x, point.y, -0.5f), point, pieceData, macroData,
                    new Vector3(0f, 0f, -1f), FrontSurface);
            }

            // 覆いは -Z 側から見る。表は従来と同じ winding、裏は逆向きにする。
            for (int i = 1; i < polygon.Count - 1; i++)
            {
                triangles.Add(front);
                triangles.Add(front + i + 1);
                triangles.Add(front + i);
            }

            int back = positions.Count;
            for (int i = 0; i < polygon.Count; i++)
            {
                Vector2 point = polygon[i];
                AddVertex(positions, uv0, uv1, uv2, uv3, normals,
                    new Vector3(point.x, point.y, 0.5f), point, pieceData, macroData,
                    new Vector3(0f, 0f, 1f), BackSurface);
            }
            for (int i = 1; i < polygon.Count - 1; i++)
            {
                triangles.Add(back);
                triangles.Add(back + i);
                triangles.Add(back + i + 1);
            }

            for (int i = 0; i < polygon.Count; i++)
            {
                Vector2 a = polygon[i];
                Vector2 b = polygon[(i + 1) % polygon.Count];
                Vector2 edge = b - a;
                float edgeLength = edge.magnitude;
                if (edgeLength <= 0f)
                    throw new InvalidOperationException("Fracture polygon contains an empty edge.");
                Vector2 outward = new Vector2(edge.y, -edge.x) / edgeLength;
                Vector3 normal = new Vector3(outward.x, outward.y, 0f);
                int side = positions.Count;
                AddVertex(positions, uv0, uv1, uv2, uv3, normals,
                    new Vector3(a.x, a.y, -0.5f), a, pieceData, macroData, normal, SideSurface);
                AddVertex(positions, uv0, uv1, uv2, uv3, normals,
                    new Vector3(b.x, b.y, -0.5f), b, pieceData, macroData, normal, SideSurface);
                AddVertex(positions, uv0, uv1, uv2, uv3, normals,
                    new Vector3(a.x, a.y, 0.5f), a, pieceData, macroData, normal, SideSurface);
                AddVertex(positions, uv0, uv1, uv2, uv3, normals,
                    new Vector3(b.x, b.y, 0.5f), b, pieceData, macroData, normal, SideSurface);
                triangles.Add(side);
                triangles.Add(side + 1);
                triangles.Add(side + 2);
                triangles.Add(side + 1);
                triangles.Add(side + 3);
                triangles.Add(side + 2);
            }
        }

        private static void AddVertex(
            List<Vector3> positions,
            List<Vector2> uv0,
            List<Vector4> uv1,
            List<Vector4> uv2,
            List<Vector4> uv3,
            List<Vector3> normals,
            Vector3 position,
            Vector2 flatPosition,
            Vector4 pieceData,
            Vector4 macroData,
            Vector3 normal,
            float surface)
        {
            positions.Add(position);
            uv0.Add(flatPosition + Vector2.one * 0.5f);
            uv1.Add(pieceData);
            uv2.Add(macroData);
            uv3.Add(new Vector4(surface, 0f, 0f, 0f));
            normals.Add(normal);
        }

        private static Vector2 ToLocal(Point angle)
        {
            double halfAngle = Math.Atan(2d);
            return new Vector2(
                (float)(Math.Tan(angle.x * halfAngle) * 0.5d * HalfExtentLocal),
                (float)(Math.Tan(angle.y * halfAngle) * 0.5d * HalfExtentLocal));
        }

        private static bool TryMeasure(List<Vector2> polygon, out float area, out Vector2 centroid)
        {
            double twiceArea = 0d;
            double sumX = 0d;
            double sumY = 0d;
            for (int i = 0; i < polygon.Count; i++)
            {
                Vector2 a = polygon[i];
                Vector2 b = polygon[(i + 1) % polygon.Count];
                double cross = (double)a.x * b.y - (double)b.x * a.y;
                twiceArea += cross;
                sumX += (a.x + b.x) * cross;
                sumY += (a.y + b.y) * cross;
            }

            if (twiceArea <= MinArea * 2d)
            {
                area = 0f;
                centroid = default;
                return false;
            }

            area = (float)(twiceArea * 0.5d);
            centroid = new Vector2(
                (float)(sumX / (3d * twiceArea)),
                (float)(sumY / (3d * twiceArea)));
            return true;
        }

        private static Point Centroid(List<Point> polygon)
        {
            double twiceArea = 0d;
            double sumX = 0d;
            double sumY = 0d;
            for (int i = 0; i < polygon.Count; i++)
            {
                Point a = polygon[i];
                Point b = polygon[(i + 1) % polygon.Count];
                double cross = Cross(a, b);
                twiceArea += cross;
                sumX += (a.x + b.x) * cross;
                sumY += (a.y + b.y) * cross;
            }
            return new Point(sumX / (3d * twiceArea), sumY / (3d * twiceArea));
        }

        private static double Dot(Point a, Point b) => a.x * b.x + a.y * b.y;
        private static double Cross(Point a, Point b) => a.x * b.y - a.y * b.x;
        private static double Square(double value) => value * value;
        private static double DistanceSquared(Point a, Point b) => Square(a.x - b.x) + Square(a.y - b.y);

        private readonly struct MacroRegion
        {
            public readonly Vector2 localCenter;
            public readonly float startOffset;

            public MacroRegion(Vector2 localCenter, float startOffset)
            {
                this.localCenter = localCenter;
                this.startOffset = startOffset;
            }
        }

        private readonly struct Triangle
        {
            public readonly int a;
            public readonly int b;
            public readonly int c;

            public Triangle(int a, int b, int c)
            {
                this.a = a;
                this.b = b;
                this.c = c;
            }
        }

        private readonly struct Piece
        {
            public readonly int[] vertices;

            public Piece(params int[] vertices)
            {
                this.vertices = vertices;
            }
        }

        private readonly struct MergeCandidate
        {
            public readonly int firstTriangle;
            public readonly int secondTriangle;
            public readonly Edge edge;

            public MergeCandidate(int firstTriangle, int secondTriangle, Edge edge)
            {
                this.firstTriangle = firstTriangle;
                this.secondTriangle = secondTriangle;
                this.edge = edge;
            }
        }

        private readonly struct Edge : IEquatable<Edge>
        {
            public readonly int a;
            public readonly int b;

            public Edge(int a, int b)
            {
                this.a = Math.Min(a, b);
                this.b = Math.Max(a, b);
            }

            public bool Equals(Edge other) => a == other.a && b == other.b;
            public override bool Equals(object? obj) => obj is Edge other && Equals(other);
            public override int GetHashCode() => (a * 397) ^ b;
        }

        private readonly struct Point
        {
            public readonly double x;
            public readonly double y;

            public Point(double x, double y)
            {
                this.x = x;
                this.y = y;
            }

            public static Point operator +(Point a, Point b) => new Point(a.x + b.x, a.y + b.y);
            public static Point operator -(Point a, Point b) => new Point(a.x - b.x, a.y - b.y);
            public static Point operator *(Point point, double scale) => new Point(point.x * scale, point.y * scale);
        }
    }
}
