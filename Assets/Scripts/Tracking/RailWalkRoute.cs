#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;

namespace FixedCamVr.Tracking
{
    /// <summary>
    /// 体験者が右手で手すりをたどって歩く道（L の枠の芯から一定距離の閉じた輪）。
    /// 寸法は実物の枠の実測（<c>tools/walk-guide/build_walk_guide.py</c>）。PC の仮想体験の <c>path=rail</c> が使う。
    /// 体の外形で壁を避ける <see cref="WalkNavigation"/> と違い、手すりから離れない。
    /// </summary>
    public static class RailWalkRoute
    {
        public const float WalkOffsetM = 0.24f;   // 枠の芯から歩く道まで
        public const float RailExtM = 0.045f;     // 手すりが支柱より先へ出ている長さ
        private const float StepM = 0.05f;

        /// <summary>
        /// 輪の点列。A の区間の入口（w1 の端を回り終えた所）から順路の向きに 1 周する。閉じていない（先頭を繰り返さない）。
        /// corner は L の角、endX は角から +x の端、endZ は角から -z の端。
        /// </summary>
        public static List<Vector2> Ring(Vector2 corner, Vector2 endX, Vector2 endZ, float d = WalkOffsetM)
        {
            if (endX.x <= corner.x || Mathf.Abs(endX.y - corner.y) > 0.001f || endZ.y >= corner.y || Mathf.Abs(endZ.x - corner.x) > 0.001f)
                throw new ArgumentException("L の向きが想定と違う（角から +x と -z へ腕が伸びる形だけを扱う）");
            float cornerX = corner.x, cornerZ = corner.y;
            float eastX = endX.x + RailExtM, southZ = endZ.y - RailExtM;
            var points = new List<Vector2>();
            Line(points, new Vector2(eastX, cornerZ - d), new Vector2(cornerX + d, cornerZ - d));   // 南面を西へ
            Line(points, new Vector2(cornerX + d, cornerZ - d), new Vector2(cornerX + d, southZ));  // 東面を南へ
            Arc(points, new Vector2(cornerX, southZ), d, 0f, -Mathf.PI);                            // w2 の端を回る
            Line(points, new Vector2(cornerX - d, southZ), new Vector2(cornerX - d, cornerZ));      // 西面を北へ
            Arc(points, new Vector2(cornerX, cornerZ), d, Mathf.PI, -0.5f * Mathf.PI);              // 外の角を回る
            Line(points, new Vector2(cornerX, cornerZ + d), new Vector2(eastX, cornerZ + d));       // 北面を東へ
            Arc(points, new Vector2(eastX, cornerZ), d, 0.5f * Mathf.PI, -Mathf.PI);                // w1 の端を回る
            return points;
        }

        /// <summary>
        /// 道筋。kind は forward（順路）/ reverse（逆走）/ backtrack（A→B→A→C→A）。
        /// 最後の点で dwellSec 止まる。折り返しでは短く止まって向き直す。
        /// </summary>
        public static List<WalkRouteLogic.Waypoint> Build(Vector2 corner, Vector2 endX, Vector2 endZ, string kind, int laps, float dwellSec, out string log)
        {
            List<Vector2> ring = Ring(corner, endX, endZ);
            var path = new List<Vector2>();
            switch (kind)
            {
                case "forward":
                    for (int lap = 0; lap < Mathf.Max(1, laps); lap++) path.AddRange(ring);
                    path.Add(ring[0]);
                    break;
                case "reverse":
                {
                    var back = new List<Vector2>(ring);
                    back.Reverse();
                    path.Add(ring[0]);
                    for (int lap = 0; lap < Mathf.Max(1, laps); lap++) path.AddRange(back);
                    break;
                }
                case "backtrack":
                {
                    // 西面の中ほど（B）まで進んで戻り、逆向きに北面の中ほど（C）まで行って戻る
                    int toB = Nearest(ring, new Vector2(corner.x - WalkOffsetM, (corner.y + endZ.y) * 0.5f));
                    int toC = Nearest(ring, new Vector2((corner.x + endX.x) * 0.5f, corner.y + WalkOffsetM));
                    for (int i = 0; i <= toB; i++) path.Add(ring[i]);
                    for (int i = toB - 1; i >= 0; i--) path.Add(ring[i]);
                    for (int i = ring.Count - 1; i >= toC; i--) path.Add(ring[i]);
                    for (int i = toC + 1; i < ring.Count; i++) path.Add(ring[i]);
                    path.Add(ring[0]);
                    break;
                }
                default:
                    throw new ArgumentException("route は forward / reverse / backtrack");
            }
            var route = new List<WalkRouteLogic.Waypoint>(path.Count);
            for (int i = 0; i < path.Count; i++)
                route.Add(new WalkRouteLogic.Waypoint(path[i], i == path.Count - 1 ? dwellSec : 0f, -1));
            log = $"rail kind={kind} offset={WalkOffsetM:F2} points={path.Count}";
            return route;
        }

        /// <summary>点から L の枠の芯までの距離。</summary>
        public static float DistanceToFrame(Vector2 p, Vector2 corner, Vector2 endX, Vector2 endZ)
            => Mathf.Min(SegmentDistance(p, corner, endX), SegmentDistance(p, corner, endZ));

        private static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
            return Vector2.Distance(p, a + ab * t);
        }

        private static int Nearest(List<Vector2> points, Vector2 p)
        {
            int best = 0;
            float bestD = float.PositiveInfinity;
            for (int i = 0; i < points.Count; i++)
            {
                float d = (points[i] - p).sqrMagnitude;
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        private static void Line(List<Vector2> points, Vector2 a, Vector2 b)
        {
            int n = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) / StepM));
            for (int i = 0; i < n; i++) points.Add(Vector2.Lerp(a, b, i / (float)n));
        }

        private static void Arc(List<Vector2> points, Vector2 center, float r, float start, float sweep)
        {
            int n = Mathf.Max(2, Mathf.CeilToInt(Mathf.Abs(sweep) * r / StepM));
            for (int i = 0; i < n; i++)
            {
                float a = start + sweep * i / n;
                points.Add(center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r);
            }
        }
    }
}
