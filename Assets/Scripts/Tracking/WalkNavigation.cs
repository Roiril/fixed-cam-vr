#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using FixedCamVr.Streaming;
using UnityEngine;

namespace FixedCamVr.Tracking
{
    /// <summary>カメラの割当と歩行境界を分け、円形の体を壁・箱に接触させずに運ぶ純ロジック。</summary>
    public sealed class WalkNavigation
    {
        public const float GridStepM = 0.05f;
        private const float Epsilon = 0.00001f;
        private readonly ShowGridDef? _grid;
        private readonly List<Wall> _walls = new();
        private readonly List<Box> _boxes = new();
        private readonly Dictionary<int, Vector2> _stops = new();
        private readonly Rect _centers;
        private readonly Vector2[] _nodes;
        private readonly bool[] _open;
        private readonly int _cols, _rows;

        public Rect Bounds { get; }
        public float BodyRadiusM { get; }
        public float WallMarginM { get; }
        public float BoundaryPaddingM { get; }
        public float HeightM { get; }
        public string LastFailure { get; private set; } = "";

        private readonly struct Wall
        {
            public readonly Vector2 A, B;
            public readonly float Radius;
            public Wall(Vector2 a, Vector2 b, float radius) { A = a; B = b; Radius = radius; }
        }

        private readonly struct Box
        {
            public readonly Vector2 Center, Half;
            private readonly float _cos, _sin;
            public Box(ShowRoomBoxDef b)
            {
                Center = new Vector2(b.x, b.z); Half = new Vector2(b.w, b.d) * 0.5f;
                float angle = b.yawDeg * Mathf.Deg2Rad;
                _cos = Mathf.Cos(angle); _sin = Mathf.Sin(angle);
            }
            public Vector2 Local(Vector2 p)
            {
                p -= Center;
                // Unity の正の yaw は +Z を +X へ向ける。
                return new Vector2(_cos * p.x - _sin * p.y, _sin * p.x + _cos * p.y);
            }
        }

        public WalkNavigation(ShowLayoutDef layout, float bodyRadiusM = 0.24f,
            float wallMarginM = 0.08f, float boundaryPaddingM = 0.5f, float heightM = 1.65f)
        {
            if (layout == null) throw new ArgumentNullException(nameof(layout));
            Require(bodyRadiusM > 0f && Finite(bodyRadiusM), "bodyRadiusM must be positive");
            Require(wallMarginM >= 0f && Finite(wallMarginM), "wallMarginM must be nonnegative");
            Require(boundaryPaddingM >= 0f && Finite(boundaryPaddingM), "boundaryPaddingM must be nonnegative");
            Require(heightM > 0f && Finite(heightM), "heightM must be positive");
            BodyRadiusM = bodyRadiusM; WallMarginM = wallMarginM;
            BoundaryPaddingM = boundaryPaddingM; HeightM = heightM;
            _grid = layout.grid;
            bool hasGrid = _grid != null && _grid.rows > 0 && _grid.cols > 0 &&
                Finite(_grid.tileM) && _grid.tileM > 0f;
            float width = hasGrid ? _grid!.cols * _grid.tileM : layout.room?.floorW ?? layout.floor?.w ?? 0f;
            float depth = hasGrid ? _grid!.rows * _grid.tileM : layout.room?.floorD ?? layout.floor?.d ?? 0f;
            Require(Finite(width) && Finite(depth) && width > 0f && depth > 0f, "layout has no usable floor dimensions");
            Bounds = new Rect(-width * 0.5f - boundaryPaddingM, -depth * 0.5f - boundaryPaddingM,
                width + 2f * boundaryPaddingM, depth + 2f * boundaryPaddingM);
            _centers = new Rect(Bounds.xMin + bodyRadiusM, Bounds.yMin + bodyRadiusM,
                Bounds.width - 2f * bodyRadiusM, Bounds.height - 2f * bodyRadiusM);

            var room = layout.room;
            if (room != null)
            {
                foreach (var w in room.walls ?? Array.Empty<ShowRoomWallDef>())
                {
                    Require(w != null && Finite(w.x1) && Finite(w.z1) && Finite(w.x2) &&
                        Finite(w.z2) && Finite(w.h) && Finite(w.thick) && w.thick >= 0f, "invalid room wall");
                    if (w.h <= 0f) continue;
                    _walls.Add(new Wall(new Vector2(w.x1, w.z1), new Vector2(w.x2, w.z2), w.thick * 0.5f));
                }
                foreach (var b in room.props ?? Array.Empty<ShowRoomBoxDef>())
                {
                    Require(b != null && Finite(b.x) && Finite(b.z) && Finite(b.y) && Finite(b.w) &&
                        Finite(b.d) && Finite(b.h) && Finite(b.yawDeg) && b.w > 0f && b.d > 0f && b.h > 0f,
                        "invalid room prop");
                    // y は床からの高さ。floorY のワールド位置を足す必要は無い。
                    if (b.y > heightM || b.y + b.h < 0f) continue;
                    _boxes.Add(new Box(b));
                }
            }
            _cols = _centers.width > 0f ? Mathf.CeilToInt(_centers.width / GridStepM) + 1 : 0;
            _rows = _centers.height > 0f ? Mathf.CeilToInt(_centers.height / GridStepM) + 1 : 0;
            Require((long)_cols * _rows <= 1000000, "navigation bounds exceed one million grid points");
            _nodes = new Vector2[_cols * _rows]; _open = new bool[_nodes.Length];
            for (int r = 0; r < _rows; r++)
                for (int c = 0; c < _cols; c++)
                {
                    int i = r * _cols + c;
                    _nodes[i] = new Vector2(Mathf.Lerp(_centers.xMin, _centers.xMax, (float)c / (_cols - 1)),
                        Mathf.Lerp(_centers.yMin, _centers.yMax, (float)r / (_rows - 1)));
                    _open[i] = CanTraverse(_nodes[i], _nodes[i]);
                }
        }

        /// <summary>障害物の表面から体の表面まで。境界と追加の余白は含まない。</summary>
        public float Clearance(Vector2 p)
        {
            if (!Finite(p)) return float.NegativeInfinity;
            float distance = float.PositiveInfinity;
            foreach (var w in _walls) distance = Mathf.Min(distance, PointSegment(p, w.A, w.B) - w.Radius);
            foreach (var b in _boxes)
            {
                Vector2 q = b.Local(p);
                q = new Vector2(Mathf.Abs(q.x), Mathf.Abs(q.y)) - b.Half;
                float signed = new Vector2(Mathf.Max(q.x, 0f), Mathf.Max(q.y, 0f)).magnitude +
                    Mathf.Min(Mathf.Max(q.x, q.y), 0f);
                distance = Mathf.Min(distance, signed);
            }
            return distance - BodyRadiusM;
        }

        public bool CanTraverse(Vector2 a, Vector2 b)
        {
            if (!Inside(a) || !Inside(b)) return false;
            float radius = BodyRadiusM + WallMarginM;
            foreach (var w in _walls)
                if (SegmentDistance(a, b, w.A, w.B) + Epsilon < radius + w.Radius) return false;
            foreach (var box in _boxes)
            {
                Vector2 p = box.Local(a), q = box.Local(b), h = box.Half;
                if (InBox(p, h) || InBox(q, h)) return false;
                Vector2 v0 = new(-h.x, -h.y), v1 = new(h.x, -h.y), v2 = h, v3 = new(-h.x, h.y);
                if (SegmentDistance(p, q, v0, v1) + Epsilon < radius ||
                    SegmentDistance(p, q, v1, v2) + Epsilon < radius ||
                    SegmentDistance(p, q, v2, v3) + Epsilon < radius ||
                    SegmentDistance(p, q, v3, v0) + Epsilon < radius) return false;
            }
            return true;
        }

        /// <summary>前後の実スキン凸包の和を掃引の保守的な包絡として、全身と障害物の余白を測る。</summary>
        public float SweptFootprintClearance(Vector2[] previous, Vector2[] current)
        {
            var points = new List<Vector2>(previous.Length + current.Length);
            points.AddRange(previous); points.AddRange(current);
            points.Sort((a, b) => a.x == b.x ? a.y.CompareTo(b.y) : a.x.CompareTo(b.x));
            var hull = new List<Vector2>();
            foreach (Vector2 point in points)
            {
                if (!Finite(point)) return float.NegativeInfinity;
                while (hull.Count > 1 && Cross(hull[hull.Count - 1] - hull[hull.Count - 2], point - hull[hull.Count - 1]) <= 0f)
                    hull.RemoveAt(hull.Count - 1);
                hull.Add(point);
            }
            int lower = hull.Count;
            for (int i = points.Count - 2; i >= 0; i--)
            {
                Vector2 point = points[i];
                while (hull.Count > lower && Cross(hull[hull.Count - 1] - hull[hull.Count - 2], point - hull[hull.Count - 1]) <= 0f)
                    hull.RemoveAt(hull.Count - 1);
                hull.Add(point);
            }
            if (hull.Count < 4) return float.NegativeInfinity;
            hull.RemoveAt(hull.Count - 1);
            float clearance = float.PositiveInfinity;
            foreach (Vector2 p in hull)
                clearance = Mathf.Min(clearance, p.x - Bounds.xMin, Bounds.xMax - p.x, p.y - Bounds.yMin, Bounds.yMax - p.y);
            foreach (Wall wall in _walls)
            {
                float distance = Contains(hull, wall.A) || Contains(hull, wall.B) ? 0f : float.PositiveInfinity;
                for (int i = 0; i < hull.Count; i++)
                    distance = Mathf.Min(distance, SegmentDistance(hull[i], hull[(i + 1) % hull.Count], wall.A, wall.B));
                clearance = Mathf.Min(clearance, distance - wall.Radius);
            }
            foreach (Box box in _boxes)
            {
                var local = hull.ConvertAll(box.Local);
                Vector2 h = box.Half;
                Vector2[] corners = { new(-h.x, -h.y), new(h.x, -h.y), h, new(-h.x, h.y) };
                float distance = float.PositiveInfinity;
                foreach (Vector2 p in local) if (InBox(p, h)) distance = 0f;
                foreach (Vector2 p in corners) if (Contains(local, p)) distance = 0f;
                for (int i = 0; i < local.Count; i++) for (int j = 0; j < 4; j++)
                    distance = Mathf.Min(distance, SegmentDistance(local[i], local[(i + 1) % local.Count], corners[j], corners[(j + 1) % 4]));
                clearance = Mathf.Min(clearance, distance);
            }
            return clearance;
        }

        private static bool Contains(List<Vector2> hull, Vector2 p)
        {
            for (int i = 0; i < hull.Count; i++)
                if (Cross(hull[(i + 1) % hull.Count] - hull[i], p - hull[i]) < -Epsilon) return false;
            return true;
        }

        public bool TryPath(Vector2 from, Vector2 to, out List<Vector2> path)
        {
            path = new List<Vector2>(); LastFailure = "";
            if (!CanTraverse(from, from) || !CanTraverse(to, to)) return Fail("endpoint lacks body clearance or lies outside bounds");
            if (CanTraverse(from, to))
            {
                path.Add(from);
                if (!from.Equals(to)) path.Add(to);
                return true;
            }
            var costs = new float[_nodes.Length]; var prev = new int[_nodes.Length];
            var closed = new bool[_nodes.Length]; var goalVisible = new bool[_nodes.Length];
            var heap = new MinHeap();
            for (int i = 0; i < _nodes.Length; i++)
            {
                costs[i] = float.PositiveInfinity; prev[i] = -1;
                if (!_open[i]) continue;
                goalVisible[i] = CanTraverse(_nodes[i], to);
                if (!CanTraverse(from, _nodes[i])) continue;
                costs[i] = Vector2.Distance(from, _nodes[i]);
                heap.Push(i, costs[i] + Vector2.Distance(_nodes[i], to));
            }
            int goal = -1;
            while (heap.Count > 0)
            {
                int i = heap.Pop();
                if (closed[i]) continue;
                closed[i] = true;
                if (goalVisible[i]) { goal = i; break; }
                int row = i / _cols, col = i % _cols;
                for (int dr = -1; dr <= 1; dr++)
                    for (int dc = -1; dc <= 1; dc++)
                    {
                        int nr = row + dr, nc = col + dc;
                        if ((dr == 0 && dc == 0) || nr < 0 || nr >= _rows || nc < 0 || nc >= _cols) continue;
                        int j = nr * _cols + nc;
                        if (!_open[j] || closed[j] || !CanTraverse(_nodes[i], _nodes[j])) continue;
                        float cost = costs[i] + Vector2.Distance(_nodes[i], _nodes[j]);
                        if (cost >= costs[j]) continue;
                        costs[j] = cost; prev[j] = i;
                        heap.Push(j, cost + Vector2.Distance(_nodes[j], to));
                    }
            }
            if (goal < 0) return Fail("no connected path at 0.05m navigation resolution");
            var raw = new List<Vector2> { to };
            for (int i = goal; i >= 0; i = prev[i]) raw.Add(_nodes[i]);
            raw.Add(from); raw.Reverse(); path.Add(from);
            for (int i = 0; i < raw.Count - 1;)
            {
                int next = raw.Count - 1;
                while (next > i + 1 && !CanTraverse(raw[i], raw[next])) next--;
                path.Add(raw[next]); i = next;
            }
            return true;
        }

        public bool TryCameraStop(int camera, out Vector2 point)
        {
            LastFailure = "";
            if (_stops.TryGetValue(camera, out point)) return true;
            point = default;
            if (_grid == null || !_grid.HasData()) return Fail("camera stops require a usable camera grid");
            int[,] cells = WalkRouteLogic.BuildCells(_grid, out int rows, out int cols);
            Vector2 centroid = Vector2.zero; int count = 0;
            for (int r = 0; r < rows; r++) for (int c = 0; c < cols; c++)
                if (cells[r, c] == camera) { centroid += WalkRouteLogic.CellCenter(r, c, rows, cols, _grid.tileM); count++; }
            if (count == 0) return Fail($"camera {camera} has no assigned cells");
            centroid /= count;
            float best = float.NegativeInfinity;
            int subdivisions = Mathf.Max(1, Mathf.CeilToInt(_grid.tileM / (GridStepM * 0.5f)));
            for (int r = 0; r < rows; r++) for (int c = 0; c < cols; c++)
            {
                if (cells[r, c] != camera) continue;
                Vector2 center = WalkRouteLogic.CellCenter(r, c, rows, cols, _grid.tileM);
                for (int z = 0; z < subdivisions; z++) for (int x = 0; x < subdivisions; x++)
                {
                    Vector2 p = center + new Vector2((x + 0.5f) / subdivisions - 0.5f,
                        (z + 0.5f) / subdivisions - 0.5f) * _grid.tileM;
                    if (!CanTraverse(p, p)) continue;
                    float room = Mathf.Min(Clearance(p), BoundaryClearance(p));
                    float score = Mathf.Min(room, WallMarginM + 0.1f) - Vector2.Distance(p, centroid) * 0.15f;
                    if (score <= best) continue;
                    best = score; point = p;
                }
            }
            if (float.IsNegativeInfinity(best)) return Fail($"camera {camera} has no body-clear stop within its assigned cells");
            _stops[camera] = point; LastFailure = ""; return true;
        }

        public List<WalkRouteLogic.Waypoint> BuildRoute(int[] order, int laps, float dwellSec, out string log)
        {
            var route = new List<WalkRouteLogic.Waypoint>();
            string settings = string.Format(CultureInfo.InvariantCulture,
                "bodyRadiusM={0:F3} wallMarginM={1:F3} boundaryPaddingM={2:F3} heightM={3:F3} bounds=[{4:F3},{5:F3},{6:F3},{7:F3}]",
                BodyRadiusM, WallMarginM, BoundaryPaddingM, HeightM, Bounds.xMin, Bounds.yMin, Bounds.xMax, Bounds.yMax);
            log = settings;
            if (order == null || order.Length == 0 || laps < 1 || !Finite(dwellSec) || dwellSec < 0f)
            { Fail("invalid camera order, laps or dwell"); log += " failed: " + LastFailure; return route; }
            var stops = new Dictionary<int, Vector2>();
            foreach (int camera in order)
            {
                if (stops.ContainsKey(camera)) continue;
                if (!TryCameraStop(camera, out Vector2 p)) { log += " failed: " + LastFailure; return route; }
                stops.Add(camera, p);
            }
            int previous = order[0]; Vector2 current = stops[previous];
            route.Add(new WalkRouteLogic.Waypoint(current, dwellSec, previous));
            var trace = new System.Text.StringBuilder(settings);
            long visits = (long)laps * order.Length;
            for (long visit = 1; visit <= visits; visit++)
            {
                int camera = order[(int)(visit % order.Length)]; Vector2 target = stops[camera];
                if (!TryPath(current, target, out var path))
                { log = trace + $" failed: {previous}->{camera}: " + LastFailure; return new List<WalkRouteLogic.Waypoint>(); }
                for (int i = 1; i < path.Count - 1; i++) route.Add(new WalkRouteLogic.Waypoint(path[i], 0f, -1));
                route.Add(new WalkRouteLogic.Waypoint(target, dwellSec, camera));
                trace.Append(' ').Append(previous).Append("->").Append(camera);
                previous = camera; current = target;
            }
            log = trace.ToString(); return route;
        }

        private bool Fail(string reason) { LastFailure = reason; return false; }
        private bool Inside(Vector2 p) => Finite(p) && _centers.width > 0f && _centers.height > 0f &&
            p.x >= _centers.xMin && p.x <= _centers.xMax && p.y >= _centers.yMin && p.y <= _centers.yMax;
        private float BoundaryClearance(Vector2 p) => Mathf.Min(Mathf.Min(p.x - Bounds.xMin, Bounds.xMax - p.x),
            Mathf.Min(p.y - Bounds.yMin, Bounds.yMax - p.y)) - BodyRadiusM;
        private static bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
        private static bool Finite(Vector2 p) => Finite(p.x) && Finite(p.y);
        private static void Require(bool condition, string reason) { if (!condition) throw new ArgumentException(reason); }
        private static bool InBox(Vector2 p, Vector2 h) => Mathf.Abs(p.x) <= h.x && Mathf.Abs(p.y) <= h.y;
        private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;
        private static float PointSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 d = b - a;
            return Vector2.Distance(p, a + d * (d.sqrMagnitude > 0f ? Mathf.Clamp01(Vector2.Dot(p - a, d) / d.sqrMagnitude) : 0f));
        }
        private static float SegmentDistance(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            Vector2 ab = b - a, cd = d - c;
            float denominator = Cross(ab, cd);
            if (Mathf.Abs(denominator) > 0f)
            {
                float t = Cross(c - a, cd) / denominator, u = Cross(c - a, ab) / denominator;
                if (t >= 0f && t <= 1f && u >= 0f && u <= 1f) return 0f;
            }
            return Mathf.Min(Mathf.Min(PointSegment(a, c, d), PointSegment(b, c, d)),
                Mathf.Min(PointSegment(c, a, b), PointSegment(d, a, b)));
        }

        private sealed class MinHeap
        {
            private readonly List<(int index, float score)> _items = new();
            public int Count => _items.Count;
            public void Push(int index, float score)
            {
                int i = _items.Count; _items.Add((index, score));
                while (i > 0)
                {
                    int parent = (i - 1) / 2;
                    if (_items[parent].score <= score) break;
                    _items[i] = _items[parent]; i = parent;
                }
                _items[i] = (index, score);
            }
            public int Pop()
            {
                int result = _items[0].index;
                var last = _items[_items.Count - 1]; _items.RemoveAt(_items.Count - 1);
                if (_items.Count == 0) return result;
                int i = 0;
                while (i * 2 + 1 < _items.Count)
                {
                    int child = i * 2 + 1;
                    if (child + 1 < _items.Count && _items[child + 1].score < _items[child].score) child++;
                    if (last.score <= _items[child].score) break;
                    _items[i] = _items[child]; i = child;
                }
                _items[i] = last; return result;
            }
        }
    }
}
