#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using FixedCamVr.Streaming;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Tracking.Tests
{
    public sealed class WalkNavigationTests
    {
        private static ShowLayoutDef SiteLayout()
        {
            var cells = new string[12];
            for (int r = 0; r < 12; r++) cells[r] = r < 3 ? "111222222222" : "111000000000";
            return new ShowLayoutDef
            {
                grid = new ShowGridDef { rows = 12, cols = 12, tileM = 0.15f, cells = cells },
                hasRoom = true,
                room = new ShowRoomDef
                {
                    walls = new[]
                    {
                        new ShowRoomWallDef { x1 = -0.5f, z1 = 0.5f, x2 = 0.455f, z2 = 0.5f, thick = 0.04f },
                        new ShowRoomWallDef { x1 = -0.5f, z1 = 0.5f, x2 = -0.5f, z2 = -0.455f, thick = 0.04f },
                    },
                },
            };
        }

        private static ShowLayoutDef Floor(float width = 3f, float depth = 3f) => new()
        {
            room = new ShowRoomDef { floorW = width, floorD = depth }, hasRoom = true,
        };

        [Test]
        public void SweptFootprint_DetectsFullBodyCrossingEvenWhenBothPosesAreClear()
        {
            var layout = Floor();
            layout.room!.walls = new[] { new ShowRoomWallDef { x1 = 0f, z1 = -.8f, x2 = 0f, z2 = .8f, thick = .04f } };
            var nav = new WalkNavigation(layout);
            Vector2[] left = { new(-.6f, -.2f), new(-.4f, -.2f), new(-.4f, .2f), new(-.6f, .2f) };
            Vector2[] right = { new(.4f, -.2f), new(.6f, -.2f), new(.6f, .2f), new(.4f, .2f) };
            Assert.That(nav.SweptFootprintClearance(left, left), Is.GreaterThan(.3f));
            Assert.That(nav.SweptFootprintClearance(right, right), Is.GreaterThan(.3f));
            Assert.That(nav.SweptFootprintClearance(left, right), Is.LessThan(0f));
        }

        [TestCase("0,1,2")]
        [TestCase("0,2,1")]
        [TestCase("0,1,0,2")]
        public void MeasuredRemyEnvelope_CanWalkEverySiteRoute(string sequence)
        {
            var nav = new WalkNavigation(SiteLayout(), .39f, .04f);
            var route = nav.BuildRoute(sequence.Split(',').Select(int.Parse).ToArray(), 1, 3f, out string log);
            Assert.That(route, Is.Not.Empty, log);
            for (int i = 1; i < route.Count; i++)
                Assert.That(nav.CanTraverse(route[i - 1].Course, route[i].Course), Is.True);
        }

        [TestCase("0,1,2")]
        [TestCase("0,2,1")]
        [TestCase("0,1,0,2")]
        public void SiteRoutes_VisitStopsIncludingInitialA_AndEverySweptEdgeClearsWalls(string sequence)
        {
            int[] order = sequence.Split(',').Select(int.Parse).ToArray();
            var layout = SiteLayout();
            var nav = new WalkNavigation(layout);
            List<WalkRouteLogic.Waypoint> route = nav.BuildRoute(order, 2, 4f, out string log);
            Assert.That(route, Is.Not.Empty, log);
            int[] expected = order.Concat(order).Concat(new[] { order[0] }).ToArray();
            Assert.That(route.Where(w => w.Camera >= 0).Select(w => w.Camera), Is.EqualTo(expected));
            Assert.That(nav.TryCameraStop(0, out Vector2 first), Is.True);
            Assert.That(route[0].Course, Is.EqualTo(first));
            foreach (var waypoint in route)
            {
                Assert.That(waypoint.HoldSec, Is.EqualTo(waypoint.Camera >= 0 ? 4f : 0f));
                if (waypoint.Camera < 0) continue;
                int col = Mathf.FloorToInt((waypoint.Course.x + 0.9f) / 0.15f);
                int row = Mathf.FloorToInt((0.9f - waypoint.Course.y) / 0.15f);
                Assert.That(row, Is.InRange(0, 11)); Assert.That(col, Is.InRange(0, 11));
                Assert.That(layout.grid!.cells[row][col] - '0', Is.EqualTo(waypoint.Camera));
            }
            for (int i = 1; i < route.Count; i++)
            {
                Vector2 a = route[i - 1].Course, b = route[i].Course;
                Assert.That(nav.CanTraverse(a, b), Is.True, $"edge {i}: {a}->{b}");
                Assert.That(nav.CanTraverse(b, a), Is.True);
                // 別の観測で 1mm 以下の間隔を採り、壁線分からの距離も直接測る。
                int samples = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) / 0.001f));
                for (int sample = 0; sample <= samples; sample++)
                {
                    Vector2 p = Vector2.Lerp(a, b, (float)sample / samples);
                    foreach (var wall in layout.room!.walls)
                    {
                        Vector2 start = new(wall.x1, wall.z1), end = new(wall.x2, wall.z2);
                        Vector2 v = end - start;
                        Vector2 nearest = start + v * Mathf.Clamp01(Vector2.Dot(p - start, v) / v.sqrMagnitude);
                        Assert.That(Vector2.Distance(p, nearest), Is.GreaterThanOrEqualTo(0.33998f));
                    }
                    Assert.That(p.x, Is.InRange(nav.Bounds.xMin + nav.BodyRadiusM - 0.00001f,
                        nav.Bounds.xMax - nav.BodyRadiusM + 0.00001f));
                    Assert.That(p.y, Is.InRange(nav.Bounds.yMin + nav.BodyRadiusM - 0.00001f,
                        nav.Bounds.yMax - nav.BodyRadiusM + 0.00001f));
                }
            }
        }

        [Test]
        public void SiteInterior_CanCrossCameraTilesDirectlyWithoutTreatingThemAsWalls()
        {
            var nav = new WalkNavigation(SiteLayout());
            Vector2 from = new(0f, -0.3f), to = new(0.4f, 0.1f);
            Assert.That(nav.TryPath(from, to, out var path), Is.True, nav.LastFailure);
            Assert.That(path, Is.EqualTo(new[] { from, to }));
        }

        [Test]
        public void WallEndpoint_RejectsSweptBodyEvenWhenBothEndpointsAreClear()
        {
            var layout = Floor();
            layout.room!.walls = new[] { new ShowRoomWallDef { x1 = 0f, z1 = 0f, x2 = 0f, z2 = 0.5f } };
            var nav = new WalkNavigation(layout);
            Vector2 from = new(-0.5f, -0.2f), to = new(0.5f, -0.2f);
            Assert.That(nav.Clearance(from), Is.GreaterThan(nav.WallMarginM));
            Assert.That(nav.Clearance(to), Is.GreaterThan(nav.WallMarginM));
            Assert.That(nav.CanTraverse(from, to), Is.False);
            Assert.That(nav.CanTraverse(to, from), Is.False);
            Assert.That(nav.TryPath(from, to, out var path), Is.True, nav.LastFailure);
            Assert.That(path.Count, Is.GreaterThan(2));
            AssertPath(nav, path);
            Assert.That(nav.Clearance(new Vector2(0f, -0.5f)), Is.EqualTo(0.24f).Within(0.00001f));
        }

        [Test]
        public void DiagonalWall_CrossingBetweenClearEndpointsRequiresDetour()
        {
            var layout = Floor();
            layout.room!.walls = new[] { new ShowRoomWallDef { x1 = -0.3f, z1 = -0.3f, x2 = 0.3f, z2 = 0.3f } };
            var nav = new WalkNavigation(layout);
            Vector2 from = new(-0.7f, 0.6f), to = new(0.7f, -0.6f);
            Assert.That(nav.CanTraverse(from, to), Is.False);
            Assert.That(nav.TryPath(from, to, out var path), Is.True, nav.LastFailure);
            AssertPath(nav, path);
        }

        [Test]
        public void RotatedBox_UsesOrientationAndRoundedBodySweep()
        {
            var layout = Floor();
            layout.room!.props = new[] { new ShowRoomBoxDef { w = 0.8f, d = 0.2f, yawDeg = 45f } };
            var nav = new WalkNavigation(layout, 0.1f, 0.05f);
            float invSqrt2 = 1f / Mathf.Sqrt(2f);
            // local +Z は世界の (+X,+Z)。薄い面から 0.3m の地点。
            Assert.That(nav.Clearance(new Vector2(0.4f, 0.4f) * invSqrt2), Is.EqualTo(0.2f).Within(0.00001f));
            Assert.That(nav.Clearance(new Vector2(0.25f, -0.25f)), Is.LessThan(0f), "長辺は世界の (+X,-Z)");
            Vector2 from = new(-0.8f, -0.8f), to = new(0.8f, 0.8f);
            Assert.That(nav.CanTraverse(from, to), Is.False);
            Assert.That(nav.TryPath(from, to, out var path), Is.True, nav.LastFailure);
            AssertPath(nav, path);
        }

        [TestCase(1.7f, true)]
        [TestCase(1.6f, false)]
        [TestCase(-0.8f, true)]
        public void FloatingBoxes_BlockOnlyWhenTheirHeightIntersectsTheBody(float bottom, bool traversable)
        {
            var layout = Floor();
            layout.room!.floorY = 8f;
            layout.room.props = new[] { new ShowRoomBoxDef { y = bottom, h = 0.5f } };
            var nav = new WalkNavigation(layout);
            Assert.That(nav.CanTraverse(new Vector2(-1f, 0f), new Vector2(1f, 0f)), Is.EqualTo(traversable));
        }

        [Test]
        public void FullBoundaryPartition_ReturnsEmptyPathAndRouteWithReason()
        {
            var layout = Floor(2f, 2f);
            layout.grid = new ShowGridDef { rows = 2, cols = 2, tileM = 1f, cells = new[] { "01", "01" } };
            layout.room!.walls = new[] { new ShowRoomWallDef { x1 = 0f, z1 = -2f, x2 = 0f, z2 = 2f } };
            var nav = new WalkNavigation(layout, boundaryPaddingM: 0f);
            Assert.That(nav.TryPath(new Vector2(-0.6f, 0f), new Vector2(0.6f, 0f), out var path), Is.False);
            Assert.That(path, Is.Empty); Assert.That(nav.LastFailure, Does.Contain("no connected path"));
            Assert.That(nav.BuildRoute(new[] { 0, 1 }, 1, 4f, out string log), Is.Empty);
            Assert.That(log, Does.Contain("failed: 0->1"));
        }

        [Test]
        public void BoundsSmallerThanBody_ReturnsFailureInsteadOfStraightFallback()
        {
            var layout = Floor(0.4f, 1f);
            var nav = new WalkNavigation(layout, boundaryPaddingM: 0f);
            Assert.That(nav.TryPath(Vector2.zero, new Vector2(0f, 0.1f), out var path), Is.False);
            Assert.That(path, Is.Empty);
            Assert.That(nav.CanTraverse(Vector2.zero, Vector2.zero), Is.False);
        }

        [Test]
        public void BoundaryPaddingAndBodyDimensions_AreExplicitAndRecordedWithoutChangingLayout()
        {
            var layout = SiteLayout();
            var nav = new WalkNavigation(layout, 0.25f, 0.07f, 0.6f, 1.7f);
            Assert.That(nav.Bounds.width, Is.EqualTo(3f).Within(0.00001f));
            Assert.That(nav.Bounds.height, Is.EqualTo(3f).Within(0.00001f));
            Assert.That(nav.BodyRadiusM, Is.EqualTo(0.25f));
            Assert.That(nav.WallMarginM, Is.EqualTo(0.07f));
            Assert.That(nav.BoundaryPaddingM, Is.EqualTo(0.6f));
            Assert.That(nav.HeightM, Is.EqualTo(1.7f));
            Assert.That(nav.CanTraverse(new Vector2(1.2f, 0f), new Vector2(1.2f, 0.1f)), Is.True);
            Assert.That(nav.CanTraverse(new Vector2(1.26f, 0f), new Vector2(1.26f, 0.1f)), Is.False);
            Assert.That(nav.BuildRoute(new[] { 0, 1, 2 }, 1, 0f, out string log), Is.Not.Empty, log);
            Assert.That(log, Does.Contain("bodyRadiusM=0.250 wallMarginM=0.070 boundaryPaddingM=0.600 heightM=1.700"));
            Assert.That(layout.room!.floorW, Is.EqualTo(1.8f));
            Assert.That(layout.grid!.rows, Is.EqualTo(12));
        }

        [Test]
        public void ConsecutiveIdenticalStops_PreserveEachDwellWithoutAddingTravelCorners()
        {
            var nav = new WalkNavigation(SiteLayout());
            var route = nav.BuildRoute(new[] { 0, 0 }, 1, 2f, out string log);
            Assert.That(route.Count, Is.EqualTo(3), log);
            Assert.That(route.Select(w => w.Camera), Is.EqualTo(new[] { 0, 0, 0 }));
            Assert.That(route.All(w => w.Course == route[0].Course && w.HoldSec == 2f), Is.True);
            Assert.That(nav.TryPath(route[0].Course, route[0].Course, out var path), Is.True);
            Assert.That(path.Count, Is.EqualTo(1));
        }

        [Test]
        public void MissingOrBlockedCameraStops_InvalidateEntireRoute()
        {
            var layout = SiteLayout();
            var nav = new WalkNavigation(layout);
            Assert.That(nav.BuildRoute(new[] { 0, 7 }, 1, 0f, out string missing), Is.Empty);
            Assert.That(missing, Does.Contain("camera 7 has no assigned cells"));
            layout.room!.props = new[] { new ShowRoomBoxDef { w = 5f, d = 5f } };
            nav = new WalkNavigation(layout);
            Assert.That(nav.BuildRoute(new[] { 0, 1 }, 1, 0f, out string blocked), Is.Empty);
            Assert.That(blocked, Does.Contain("no body-clear stop"));
        }

        [Test]
        public void InvalidInputs_FailClosed()
        {
            var nav = new WalkNavigation(Floor());
            Assert.That(nav.TryPath(new Vector2(float.NaN, 0f), Vector2.zero, out var path), Is.False);
            Assert.That(path, Is.Empty);
            Assert.That(nav.BuildRoute(Array.Empty<int>(), 1, 0f, out _), Is.Empty);
            Assert.That(nav.BuildRoute(new[] { 0 }, 0, 0f, out _), Is.Empty);
            Assert.Throws<ArgumentException>(() => new WalkNavigation(Floor(), bodyRadiusM: float.NaN));
            var invalid = Floor();
            invalid.room!.walls = new[] { new ShowRoomWallDef { x1 = float.NaN } };
            Assert.Throws<ArgumentException>(() => new WalkNavigation(invalid));
        }

        private static void AssertPath(WalkNavigation nav, List<Vector2> path)
        {
            Assert.That(path.Count, Is.GreaterThanOrEqualTo(2));
            for (int i = 1; i < path.Count; i++)
                Assert.That(nav.CanTraverse(path[i - 1], path[i]), Is.True, $"edge {i}");
        }
    }
}
