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
    ///
    /// 形は<b>起点から放射する亀裂と同心の亀裂の網</b>（ガラスの蜘蛛の巣・canon/LEDGER.md 0222）。
    /// 起点近くは細かく、周縁ほど大きい。放射線に沿う細長い片を混ぜる。節は角度も半径も揺らし、
    /// 届かない亀裂も混ぜて、長方形の升目にならないようにする（0215「正方形、長方形は除く」）。
    /// </summary>
    public static class IntroFractureMesh
    {
        public const int MacroCount = 24;
        /// <summary>
        /// 起点（角度空間）。シェーダの起点 tan(-0.16, 0.12) と同じ点（tan = tan(angle × atan 2)）。
        /// 亀裂はここから放射し、破断の波もここから外へ走る（大区分の startOffset）。
        /// </summary>
        public const double ImpactX = -0.143d;
        public const double ImpactY = 0.108d;
        public const int RayCount = 15;
        public const float HalfExtentLocal = 0.30f;
        public const float FrontSurface = 0f;
        public const float BackSurface = 1f;
        public const float SideSurface = 2f;
        public const int EdgeCloserCount = 3;

        private const int MacroColumns = 6;
        private const int MacroRows = 4;
        private const int Seed = 20260915;
        /// <summary>同心の亀裂の半径（角度空間・起点から）。外側 2 つは遠い角にしか届かない。</summary>
        private static readonly double[] RingRadii =
            { 0.055d, 0.115d, 0.19d, 0.29d, 0.42d, 0.58d, 0.78d, 1.02d, 1.32d, 1.70d };
        private const double MinArea = 1e-12;
        private const double DuplicateDistanceSquared = 1e-16;

        public static int LastPieceCount { get; private set; }
        public static int LastTrianglePieceCount { get; private set; }
        public static int LastQuadPieceCount { get; private set; }
        public static int LastVertexCount { get; private set; }

        /// <summary>
        /// 片ごとの情報。光の粒（<see cref="IntroSparkMesh"/>・0235）が放出点と破断時刻を
        /// 同じ片から読むために公開する。<b>メッシュの頂点・順序・属性は 1 ビットも変えない。</b>
        /// </summary>
        public readonly struct PieceInfo
        {
            /// <summary>片の重心（覆いのローカル・±<see cref="HalfExtentLocal"/>）。</summary>
            public readonly Vector2 center;
            /// <summary>sqrt(面積)（覆いのローカル m）。uv1.z と同じ値。</summary>
            public readonly float size;
            /// <summary>枠を閉じる片か（uv1.w ≥ 0.5 と同じ）。</summary>
            public readonly bool edgeCloser;
            /// <summary>大区分の番号（uv2.w と同じ）。</summary>
            public readonly int macroIndex;
            /// <summary>大区分の破断の順（0..0.14・uv2.z と同じ）。</summary>
            public readonly float macroOrder;
            /// <summary>外周（覆いのローカル）。向きは問わない。</summary>
            public readonly Vector2[] outline;

            public PieceInfo(Vector2 center, float size, bool edgeCloser,
                int macroIndex, float macroOrder, Vector2[] outline)
            {
                this.center = center;
                this.size = size;
                this.edgeCloser = edgeCloser;
                this.macroIndex = macroIndex;
                this.macroOrder = macroOrder;
                this.outline = outline;
            }
        }

        /// <summary>直近の <see cref="Build"/> が組んだ片の一覧（順序はメッシュの片の順と同じ）。</summary>
        public static IReadOnlyList<PieceInfo> LastPieces { get; private set; } =
            Array.Empty<PieceInfo>();

        public static Mesh Build()
        {
            var random = new System.Random(Seed);
            Point[] macroSites = BuildGridSites(MacroColumns, MacroRows, 0.34d, random);
            List<Point> points = BuildFracturePoints(macroSites, random);
            List<Triangle> triangles = Triangulate(points);
            List<Piece> shards = BuildPieces(points, triangles, macroSites, random);
            MacroRegion[] macros = BuildMacroRegions(macroSites);
            HashSet<int> edgeClosers = SelectEdgeClosers(shards, points);

            var positions = new List<Vector3>(shards.Count * 20);
            var uv0 = new List<Vector2>(shards.Count * 20);
            var uv1 = new List<Vector4>(shards.Count * 20);
            var uv2 = new List<Vector4>(shards.Count * 20);
            var uv3 = new List<Vector4>(shards.Count * 20);
            var edgeDistances = new List<Vector4>(shards.Count * 20);
            var normals = new List<Vector3>(shards.Count * 20);
            var meshTriangles = new List<int>(shards.Count * 28);
            var pieceInfos = new List<PieceInfo>(shards.Count);

            int trianglePieceCount = 0;
            int quadPieceCount = 0;
            for (int shardIndex = 0; shardIndex < shards.Count; shardIndex++)
            {
                Piece shard = shards[shardIndex];
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
                bool edgeCloser = edgeClosers.Contains(shardIndex);
                AddPiece(positions, uv0, uv1, uv2, uv3, normals, meshTriangles, local, centroid, area,
                    macros[macroIndex], macroIndex, edgeCloser);
                pieceInfos.Add(new PieceInfo(centroid, Mathf.Sqrt(area), edgeCloser, macroIndex,
                    macros[macroIndex].startOffset, local.ToArray()));
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
            LastPieces = pieceInfos;
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

        /// <summary>
        /// 撮影画像の実際の有効範囲に接する大片を、終端を閉じる片として選び直す。
        /// 投影が使えない場合は Build 時の外周選択を残す。
        /// </summary>
        public static int ReselectClosingPieces(
            Mesh mesh,
            Matrix4x4 captureHeadToWorld,
            Matrix4x4 leftWorldToUv,
            Matrix4x4 rightWorldToUv)
        {
            if (mesh == null || !mesh.isReadable) return 0;
            Vector3[] vertices = mesh.vertices;
            var pieces = new List<Vector4>(vertices.Length);
            var surfaces = new List<Vector4>(vertices.Length);
            mesh.GetUVs(1, pieces);
            mesh.GetUVs(3, surfaces);
            if (pieces.Count != vertices.Length || surfaces.Count != vertices.Length) return 0;

            var candidates = new Dictionary<Vector3, ClosingCandidate>();
            for (int i = 0; i < vertices.Length; i++)
            {
                if (surfaces[i].x != FrontSurface) continue;
                Vector4 piece = pieces[i];
                var key = new Vector3(piece.x, piece.y, piece.z);
                if (!candidates.TryGetValue(key, out ClosingCandidate? candidate))
                {
                    Vector3 centerWorld = captureHeadToWorld.MultiplyPoint3x4(ShellPoint(key));
                    bool leftCenterValid = TryProjectFrozenUv(leftWorldToUv, centerWorld, out Vector2 leftCenter);
                    bool rightCenterValid = TryProjectFrozenUv(rightWorldToUv, centerWorld, out Vector2 rightCenter);
                    candidate = new ClosingCandidate(
                        key,
                        piece.z * piece.z,
                        leftCenterValid && rightCenterValid
                            && IsInsidePhoto(leftCenter) && IsInsidePhoto(rightCenter),
                        leftCenterValid && rightCenterValid
                            ? Mathf.Min(PhotoEdgeDistance(leftCenter), PhotoEdgeDistance(rightCenter))
                            : float.PositiveInfinity);
                    candidates.Add(key, candidate);
                }

                Vector3 captureWorld = captureHeadToWorld.MultiplyPoint3x4(ShellPoint(vertices[i]));
                candidate.vertexCount++;
                if (TryProjectFrozenUv(leftWorldToUv, captureWorld, out Vector2 leftUv)
                    && IsInsidePhoto(leftUv))
                {
                    candidate.leftInside++;
                    candidate.edgeDistance = Mathf.Min(candidate.edgeDistance, PhotoEdgeDistance(leftUv));
                }
                if (TryProjectFrozenUv(rightWorldToUv, captureWorld, out Vector2 rightUv)
                    && IsInsidePhoto(rightUv))
                {
                    candidate.rightInside++;
                    candidate.edgeDistance = Mathf.Min(candidate.edgeDistance, PhotoEdgeDistance(rightUv));
                }
            }

            var visible = new List<ClosingCandidate>();
            foreach (ClosingCandidate candidate in candidates.Values)
            {
                if (candidate.centerInsideBoth && candidate.leftInside > 0 && candidate.rightInside > 0)
                    visible.Add(candidate);
            }
            if (visible.Count < EdgeCloserCount) return 0;
            visible.Sort((a, b) =>
            {
                int edgeOrder = b.TouchesPhotoEdge.CompareTo(a.TouchesPhotoEdge);
                if (edgeOrder != 0) return edgeOrder;
                if (a.TouchesPhotoEdge)
                {
                    int areaOrder = b.area.CompareTo(a.area);
                    if (areaOrder != 0) return areaOrder;
                }
                else
                {
                    int distanceOrder = a.edgeDistance.CompareTo(b.edgeDistance);
                    if (distanceOrder != 0) return distanceOrder;
                    int areaOrder = b.area.CompareTo(a.area);
                    if (areaOrder != 0) return areaOrder;
                }
                return ComparePieceKey(a.key, b.key);
            });

            var selected = new HashSet<Vector3>();
            for (int i = 0; i < EdgeCloserCount; i++)
                selected.Add(visible[i].key);
            for (int i = 0; i < pieces.Count; i++)
            {
                Vector4 piece = pieces[i];
                piece.w = selected.Contains(new Vector3(piece.x, piece.y, piece.z)) ? 1f : 0f;
                pieces[i] = piece;
            }
            mesh.SetUVs(1, pieces);
            return selected.Count;
        }

        private static Vector3 ShellPoint(Vector3 local)
        {
            return new Vector3(local.x / 0.15f, local.y / 0.15f, 1f).normalized * 1.6f;
        }

        private static bool TryProjectFrozenUv(Matrix4x4 worldToUv, Vector3 captureWorld, out Vector2 uv)
        {
            Vector4 q = worldToUv * new Vector4(captureWorld.x, captureWorld.y, captureWorld.z, 1f);
            if (!(q.z > 1e-5f) || !(q.w > 1e-5f)
                || float.IsNaN(q.x) || float.IsInfinity(q.x)
                || float.IsNaN(q.y) || float.IsInfinity(q.y))
            {
                uv = default;
                return false;
            }
            uv = new Vector2(q.x / q.w, q.y / q.w);
            return !float.IsNaN(uv.x) && !float.IsInfinity(uv.x)
                && !float.IsNaN(uv.y) && !float.IsInfinity(uv.y);
        }

        private static bool IsInsidePhoto(Vector2 uv)
        {
            return uv.x >= 0f && uv.x <= 1f && uv.y >= 0f && uv.y <= 1f;
        }

        private static float PhotoEdgeDistance(Vector2 uv)
        {
            return Mathf.Min(Mathf.Min(uv.x, uv.y), Mathf.Min(1f - uv.x, 1f - uv.y));
        }

        private static int ComparePieceKey(Vector3 a, Vector3 b)
        {
            int xOrder = a.x.CompareTo(b.x);
            if (xOrder != 0) return xOrder;
            int yOrder = a.y.CompareTo(b.y);
            return yOrder != 0 ? yOrder : a.z.CompareTo(b.z);
        }

        private static HashSet<int> SelectEdgeClosers(List<Piece> shards, List<Point> points)
        {
            var candidates = new List<KeyValuePair<int, float>>();
            for (int shardIndex = 0; shardIndex < shards.Count; shardIndex++)
            {
                Piece shard = shards[shardIndex];
                var local = new List<Vector2>(shard.vertices.Length);
                bool touchesOuterBoundary = false;
                foreach (int vertex in shard.vertices)
                {
                    Vector2 point = ToLocal(points[vertex]);
                    local.Add(point);
                    touchesOuterBoundary |= Mathf.Abs(point.x) >= HalfExtentLocal - 1e-5f
                        || Mathf.Abs(point.y) >= HalfExtentLocal - 1e-5f;
                }
                if (touchesOuterBoundary && TryMeasure(local, out float area, out _))
                    candidates.Add(new KeyValuePair<int, float>(shardIndex, area));
            }
            candidates.Sort((a, b) =>
            {
                int areaOrder = b.Value.CompareTo(a.Value);
                return areaOrder != 0 ? areaOrder : a.Key.CompareTo(b.Key);
            });
            if (candidates.Count < EdgeCloserCount)
                throw new InvalidOperationException("Fracture mesh has too few outer shards for edge closers.");

            var selected = new HashSet<int>();
            for (int i = 0; i < EdgeCloserCount; i++)
                selected.Add(candidates[i].Key);
            return selected;
        }

        private sealed class ClosingCandidate
        {
            public readonly Vector3 key;
            public readonly float area;
            public readonly bool centerInsideBoth;
            public int vertexCount;
            public int leftInside;
            public int rightInside;
            public float edgeDistance;

            public bool TouchesPhotoEdge => leftInside < vertexCount || rightInside < vertexCount;

            public ClosingCandidate(Vector3 key, float area, bool centerInsideBoth, float edgeDistance)
            {
                this.key = key;
                this.area = area;
                this.centerInsideBoth = centerInsideBoth;
                this.edgeDistance = edgeDistance;
            }
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

            // 起点から放射する亀裂と同心の亀裂の網。節は角度（区画の ±36%）も半径（±22%）も揺らし、
            // 2 本目の輪から先は 12% の節を欠かせる（亀裂が届かない）。外周は境界の点が閉じる。
            var impact = new Point(ImpactX, ImpactY);
            AddPoint(points, impact);
            double sector = Math.PI * 2d / RayCount;
            var rayAngles = new double[RayCount];
            var rayDrift = new double[RayCount];
            for (int ray = 0; ray < RayCount; ray++)
            {
                rayAngles[ray] = sector * ray + (random.NextDouble() * 2d - 1d) * sector * 0.30d;
                rayDrift[ray] = (random.NextDouble() * 2d - 1d) * 0.045d;
            }
            for (int ring = 0; ring < RingRadii.Length; ring++)
            {
                for (int ray = 0; ray < RayCount; ray++)
                {
                    double skip = random.NextDouble();
                    double angle = rayAngles[ray] + rayDrift[ray] * ring
                                   + (random.NextDouble() * 2d - 1d) * sector * 0.36d;
                    double radius = RingRadii[ring] * (1d + (random.NextDouble() * 2d - 1d) * 0.22d);
                    if (ring >= 2 && skip < 0.12d)
                        continue;
                    Point point = impact + new Point(Math.Cos(angle), Math.Sin(angle)) * radius;
                    if (Math.Abs(point.x) > 0.985d || Math.Abs(point.y) > 0.985d)
                        continue;
                    AddPoint(points, point);
                }
            }

            // 放射線に沿う細長い片。起点付近の 5 本の亀裂に、線上のわずかにずれた点を並べる。
            for (int ray = 0; ray < RayCount; ray += 3)
                AddSliver(points, impact, rayAngles[ray] + rayDrift[ray] * 1.5d, random);

            foreach (Point site in macroSites)
                AddPoint(points, site);
            return points;
        }

        private static void AddSliver(List<Point> points, Point origin, double angle, System.Random random)
        {
            var along = new Point(Math.Cos(angle), Math.Sin(angle));
            var across = new Point(-along.y, along.x);
            for (int i = 0; i < 6; i++)
            {
                double longitudinal = 0.08d + random.NextDouble() * 0.36d;
                double lateral = (random.NextDouble() * 2d - 1d) * 0.018d;
                Point point = origin + along * longitudinal + across * lateral;
                if (Math.Abs(point.x) > 0.985d || Math.Abs(point.y) > 0.985d)
                    continue;
                AddPoint(points, point);
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
            // 破断の波は亀裂の起点から外へ走る（シェーダの breakAt ＝ CrackEnd + startOffset の順）。
            Vector2 start = ToLocal(new Point(ImpactX, ImpactY));
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
            int macroIndex,
            bool edgeCloser)
        {
            var pieceData = new Vector4(centroid.x, centroid.y, Mathf.Sqrt(area), edgeCloser ? 1f : 0f);
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
