#nullable enable

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 導入で覆いを割る不規則な破片を組む。角度空間で作った小さい Voronoi 面を
    /// 24 枚の大面で切り、大面の境界を隣り合う小片で共有する。
    /// </summary>
    public static class IntroFractureMesh
    {
        public const int MacroCount = 24;
        public const int MicroSide = 40;
        public const float HalfExtentLocal = 0.30f;
        public const float FrontSurface = 0f;
        public const float BackSurface = 1f;
        public const float SideSurface = 2f;

        private const int MacroColumns = 6;
        private const int MacroRows = 4;
        private const int Seed = 20260913;
        private const double MinArea = 1e-12;

        private static readonly Point[] Domain =
        {
            new Point(-1d, -1d),
            new Point(1d, -1d),
            new Point(1d, 1d),
            new Point(-1d, 1d)
        };

        public static int LastPieceCount { get; private set; }
        public static int LastVertexCount { get; private set; }

        public static Mesh Build()
        {
            var random = new System.Random(Seed);
            Point[] macroSites = BuildSites(MacroColumns, MacroRows, random);
            Point[] microSites = BuildSites(MicroSide, MicroSide, random);
            MacroRegion[] macros = BuildMacroRegions(macroSites);

            var positions = new List<Vector3>(12000);
            var uv0 = new List<Vector2>(12000);
            var uv1 = new List<Vector4>(12000);
            var uv2 = new List<Vector4>(12000);
            var uv3 = new List<Vector4>(12000);
            var normals = new List<Vector3>(12000);
            var triangles = new List<int>(48000);
            int pieceCount = 0;

            for (int y = 0; y < MicroSide; y++)
            {
                for (int x = 0; x < MicroSide; x++)
                {
                    int microIndex = y * MicroSide + x;
                    List<Point> micro = BuildMicroCell(microSites, x, y, microIndex);
                    if (micro.Count < 3)
                        continue;

                    for (int macroIndex = 0; macroIndex < macros.Length; macroIndex++)
                    {
                        List<Point> piece = IntersectConvex(micro, macros[macroIndex].polygon);
                        if (piece.Count < 3)
                            continue;

                        var local = new List<Vector2>(piece.Count);
                        for (int i = 0; i < piece.Count; i++)
                            local.Add(ToLocal(piece[i]));

                        if (!TryMeasure(local, out float area, out Vector2 centroid) || area <= MinArea)
                            continue;

                        AddPiece(positions, uv0, uv1, uv2, uv3, normals, triangles, local, centroid, area,
                            macros[macroIndex], macroIndex);
                        pieceCount++;
                    }
                }
            }

            LastPieceCount = pieceCount;
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
            mesh.SetTriangles(triangles, 0);
            // 頂点シェーダで撮影時の頭位置から現在のスクリーンまで運ぶ。
            // 元の平面だけから求めた bounds では、頭を動かした瞬間に全破片がカリングされる。
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 200f);
            mesh.UploadMeshData(markNoLongerReadable: false);
            return mesh;
        }

        private static Point[] BuildSites(int columns, int rows, System.Random random)
        {
            var sites = new Point[columns * rows];
            double width = 2d / columns;
            double height = 2d / rows;
            const double jitter = 0.34d;
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

        private static MacroRegion[] BuildMacroRegions(Point[] sites)
        {
            var polygons = new List<Point>[MacroCount];
            var localCentroids = new Vector2[MacroCount];
            var distances = new float[MacroCount];
            var start = new Point(-0.45d, 0.30d);
            float maxDistance = 0f;
            float minDistance = float.PositiveInfinity;
            for (int i = 0; i < sites.Length; i++)
            {
                List<Point> polygon = BuildCell(sites, i, 0, sites.Length);
                Point angularCentroid = Centroid(polygon);
                var local = new List<Vector2>(polygon.Count);
                for (int p = 0; p < polygon.Count; p++)
                    local.Add(ToLocal(polygon[p]));
                if (!TryMeasure(local, out _, out Vector2 localCentroid))
                    throw new InvalidOperationException($"Macro Voronoi cell {i} has no area.");

                float distance = (float)Math.Sqrt(
                    Square(angularCentroid.x - start.x) + Square(angularCentroid.y - start.y));
                polygons[i] = polygon;
                localCentroids[i] = localCentroid;
                distances[i] = distance;
                maxDistance = Mathf.Max(maxDistance, distance);
                minDistance = Mathf.Min(minDistance, distance);
            }

            var regions = new MacroRegion[MacroCount];
            for (int i = 0; i < regions.Length; i++)
                regions[i] = new MacroRegion(
                    polygons[i], localCentroids[i], (distances[i] - minDistance) / (maxDistance - minDistance) * 0.14f);
            return regions;
        }

        private static List<Point> BuildMicroCell(Point[] sites, int siteX, int siteY, int siteIndex)
        {
            var polygon = new List<Point>(Domain);
            int minX = Math.Max(0, siteX - 2);
            int maxX = Math.Min(MicroSide - 1, siteX + 2);
            int minY = Math.Max(0, siteY - 2);
            int maxY = Math.Min(MicroSide - 1, siteY + 2);
            Point site = sites[siteIndex];
            for (int y = minY; y <= maxY && polygon.Count >= 3; y++)
            {
                for (int x = minX; x <= maxX && polygon.Count >= 3; x++)
                {
                    int otherIndex = y * MicroSide + x;
                    if (otherIndex == siteIndex)
                        continue;
                    polygon = ClipNearer(polygon, site, sites[otherIndex]);
                }
            }
            return polygon;
        }

        private static List<Point> BuildCell(Point[] sites, int siteIndex, int first, int end)
        {
            var polygon = new List<Point>(Domain);
            Point site = sites[siteIndex];
            for (int other = first; other < end && polygon.Count >= 3; other++)
            {
                if (other == siteIndex)
                    continue;
                polygon = ClipNearer(polygon, site, sites[other]);
            }
            return polygon;
        }

        private static List<Point> ClipNearer(List<Point> polygon, Point site, Point other)
        {
            Point normal = other - site;
            double limit = (Square(other.x) + Square(other.y) - Square(site.x) - Square(site.y)) * 0.5d;
            return Clip(polygon, p => limit - Dot(p, normal));
        }

        private static List<Point> IntersectConvex(List<Point> subject, List<Point> clipPolygon)
        {
            var result = new List<Point>(subject);
            for (int i = 0; i < clipPolygon.Count && result.Count >= 3; i++)
            {
                Point a = clipPolygon[i];
                Point b = clipPolygon[(i + 1) % clipPolygon.Count];
                Point edge = b - a;
                result = Clip(result, p => Cross(edge, p - a));
            }
            return result;
        }

        private static List<Point> Clip(List<Point> polygon, Func<Point, double> signedDistance)
        {
            var output = new List<Point>(polygon.Count + 1);
            if (polygon.Count == 0)
                return output;

            Point previous = polygon[polygon.Count - 1];
            double previousDistance = signedDistance(previous);
            bool previousInside = previousDistance >= 0d;
            for (int i = 0; i < polygon.Count; i++)
            {
                Point current = polygon[i];
                double currentDistance = signedDistance(current);
                bool currentInside = currentDistance >= 0d;
                if (currentInside != previousInside)
                {
                    double t = previousDistance / (previousDistance - currentDistance);
                    AddDistinct(output, previous + (current - previous) * t);
                }
                if (currentInside)
                    AddDistinct(output, current);

                previous = current;
                previousDistance = currentDistance;
                previousInside = currentInside;
            }

            if (output.Count > 1 && DistanceSquared(output[0], output[output.Count - 1]) < 1e-24)
                output.RemoveAt(output.Count - 1);
            return output;
        }

        private static void AddDistinct(List<Point> points, Point point)
        {
            if (points.Count == 0 || DistanceSquared(points[points.Count - 1], point) >= 1e-24)
                points.Add(point);
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
            var macroData = new Vector4(macro.localCentroid.x, macro.localCentroid.y,
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
                // Voronoi の交点には 1e-5m 未満の辺もある。Vector2.normalized はそれを
                // ゼロへ丸めるため、側面の照明へ不正な法線を渡してしまう。
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
            public readonly List<Point> polygon;
            public readonly Vector2 localCentroid;
            public readonly float startOffset;

            public MacroRegion(List<Point> polygon, Vector2 localCentroid, float startOffset)
            {
                this.polygon = polygon;
                this.localCentroid = localCentroid;
                this.startOffset = startOffset;
            }
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
