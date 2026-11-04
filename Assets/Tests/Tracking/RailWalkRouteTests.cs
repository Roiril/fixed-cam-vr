#nullable enable
using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Tracking.Tests
{
    public sealed class RailWalkRouteTests
    {
        private static readonly Vector2 Corner = new(-0.5f, 0.5f), EndX = new(0.455f, 0.5f), EndZ = new(-0.5f, -0.455f);

        private static float Length(IReadOnlyList<Vector2> points, bool closed)
        {
            float total = 0f;
            for (int i = 1; i < points.Count; i++) total += Vector2.Distance(points[i - 1], points[i]);
            if (closed) total += Vector2.Distance(points[points.Count - 1], points[0]);
            return total;
        }

        [Test]
        public void Ring_KeepsWalkOffsetFromFrame()
        {
            foreach (Vector2 p in RailWalkRoute.Ring(Corner, EndX, EndZ))
                Assert.That(RailWalkRoute.DistanceToFrame(p, Corner, EndX, EndZ), Is.GreaterThanOrEqualTo(RailWalkRoute.WalkOffsetM - 0.002f));
        }

        [Test]
        public void Ring_LengthMatchesWalkGuide()
        {
            // tools/walk-guide の make_path(0.24) と同じ形: 直線 1.0 + 1.0 + 0.76 + 0.76、円弧 0.377 + 0.754 + 0.754
            Assert.That(Length(RailWalkRoute.Ring(Corner, EndX, EndZ), true), Is.EqualTo(5.405f).Within(0.02f));
        }

        [Test]
        public void Ring_StepsAreSmall()
        {
            List<Vector2> ring = RailWalkRoute.Ring(Corner, EndX, EndZ);
            for (int i = 1; i < ring.Count; i++)
                Assert.That(Vector2.Distance(ring[i - 1], ring[i]), Is.LessThanOrEqualTo(0.06f));
        }

        [Test]
        public void Forward_VisitsWestFaceThenNorthFace_WithWallOnRight()
        {
            List<WalkRouteLogic.Waypoint> route = RailWalkRoute.Build(Corner, EndX, EndZ, "forward", 1, 3f, out _);
            int west = route.FindIndex(w => Mathf.Abs(w.Course.x + 0.74f) < 0.01f && Mathf.Abs(w.Course.y) < 0.05f);
            int north = route.FindIndex(w => Mathf.Abs(w.Course.y - 0.74f) < 0.01f && Mathf.Abs(w.Course.x) < 0.05f);
            Assert.That(west, Is.GreaterThan(0));
            Assert.That(north, Is.GreaterThan(west));
            Assert.That(route[route.Count - 1].HoldSec, Is.EqualTo(3f));
            Assert.That(Vector2.Distance(route[0].Course, route[route.Count - 1].Course), Is.LessThan(0.001f));
        }

        [Test]
        public void Reverse_VisitsNorthFaceBeforeWestFace()
        {
            List<WalkRouteLogic.Waypoint> route = RailWalkRoute.Build(Corner, EndX, EndZ, "reverse", 1, 0f, out _);
            int west = route.FindIndex(w => Mathf.Abs(w.Course.x + 0.74f) < 0.01f && Mathf.Abs(w.Course.y) < 0.05f);
            int north = route.FindIndex(w => Mathf.Abs(w.Course.y - 0.74f) < 0.01f && Mathf.Abs(w.Course.x) < 0.05f);
            Assert.That(north, Is.GreaterThan(0));
            Assert.That(west, Is.GreaterThan(north));
        }

        [Test]
        public void Backtrack_ReachesBothFacesAndReturns()
        {
            List<WalkRouteLogic.Waypoint> route = RailWalkRoute.Build(Corner, EndX, EndZ, "backtrack", 1, 0f, out _);
            Assert.That(route.Exists(w => Mathf.Abs(w.Course.x + 0.74f) < 0.01f));
            Assert.That(route.Exists(w => Mathf.Abs(w.Course.y - 0.74f) < 0.01f));
            for (int i = 1; i < route.Count; i++)
                Assert.That(Vector2.Distance(route[i - 1].Course, route[i].Course), Is.LessThanOrEqualTo(0.06f));
            Assert.That(Vector2.Distance(route[0].Course, route[route.Count - 1].Course), Is.LessThan(0.001f));
        }

        [Test]
        public void UnknownOrientation_Throws()
        {
            Assert.Throws<ArgumentException>(() => RailWalkRoute.Ring(Corner, new Vector2(-1f, 0.5f), EndZ));
            Assert.Throws<ArgumentException>(() => RailWalkRoute.Build(Corner, EndX, EndZ, "sideways", 1, 0f, out _));
        }
    }
}
